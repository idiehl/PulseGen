#include "pulsegen/pulsegen.h"
#include "wav_writer.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <exception>
#include <filesystem>
#include <limits>
#include <numbers>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace {

constexpr std::string_view engine_version = "0.1.0-alpha.1";
constexpr double minimum_effective_sound_speed = 40.0;

struct vec3 {
    double x{};
    double y{};
    double z{};
};

vec3 from_c(pg_vec3 value) noexcept { return { value.x, value.y, value.z }; }
vec3 operator+(vec3 left, vec3 right) noexcept { return { left.x + right.x, left.y + right.y, left.z + right.z }; }
vec3 operator-(vec3 left, vec3 right) noexcept { return { left.x - right.x, left.y - right.y, left.z - right.z }; }
vec3 operator*(vec3 value, double scalar) noexcept { return { value.x * scalar, value.y * scalar, value.z * scalar }; }
double dot(vec3 left, vec3 right) noexcept { return (left.x * right.x) + (left.y * right.y) + (left.z * right.z); }
double length(vec3 value) noexcept { return std::sqrt(dot(value, value)); }

vec3 normalized(vec3 value) noexcept
{
    const double value_length = length(value);
    return value_length > 1.0e-12 ? value * (1.0 / value_length) : vec3{ 0.0, 0.0, 1.0 };
}

vec3 rotate_yaw(vec3 value, double yaw_degrees) noexcept
{
    const double radians = yaw_degrees * std::numbers::pi / 180.0;
    const double cosine = std::cos(radians);
    const double sine = std::sin(radians);
    return { (value.x * cosine) + (value.z * sine), value.y, (-value.x * sine) + (value.z * cosine) };
}

std::string safe_name(const char* value, std::string_view fallback)
{
    std::string result = value == nullptr ? std::string(fallback) : std::string(value);
    if (result.empty()) {
        result = std::string(fallback);
    }
    if (result.size() >= PG_MAX_NAME_BYTES) {
        result.resize(PG_MAX_NAME_BYTES - 1U);
    }
    for (char& character : result) {
        const bool allowed = (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z') ||
            (character >= '0' && character <= '9') || character == '-' || character == '_';
        if (!allowed) {
            character = '_';
        }
    }
    return result;
}

bool finite(vec3 value) noexcept
{
    return std::isfinite(value.x) && std::isfinite(value.y) && std::isfinite(value.z);
}

std::filesystem::path path_from_utf8(const char* value)
{
#if defined(_WIN32)
    return std::filesystem::path(std::u8string(reinterpret_cast<const char8_t*>(value)));
#else
    return std::filesystem::path(value);
#endif
}

template <typename T>
bool valid_struct(const T* value) noexcept
{
    return value != nullptr && value->size >= sizeof(T) && value->version == PG_ABI_VERSION;
}

struct atmosphere_state {
    double temperature_celsius{ 20.0 };
    double relative_humidity_percent{ 50.0 };
    double pressure_kilopascals{ 101.325 };
    vec3 wind{};
    double turbulence{};
    std::uint64_t noise_seed{ 1U };
};

struct room_state {
    vec3 centre{};
    vec3 dimensions{ 20.0, 8.0, 12.0 };
    double reflection_low{ 0.75 };
    double reflection_mid{ 0.65 };
    double reflection_high{ 0.5 };
    double scattering{ 0.1 };
    bool enabled{ true };
};

struct keyframe_state {
    double time_seconds{};
    vec3 position{};
};

struct emitter_state {
    std::string name;
    double frequency_hz{ 440.0 };
    double amplitude{ 1.0 };
    double gain_linear{ 1.0 };
    double min_distance_metres{ 1.0 };
    std::vector<keyframe_state> keyframes;
    bool enabled{ true };
};

struct receiver_state {
    std::string name;
    pg_receiver_kind kind{ PG_RECEIVER_BINAURAL_HEAD };
    vec3 centre{};
    double yaw_degrees{};
    double spacing_metres{ 0.18 };
    double head_radius_metres{ 0.09 };
    double microphone_angle_degrees{};
    pg_polar_pattern polar_pattern{ PG_POLAR_OMNI };
    double sensitivity_linear{ 1.0 };
    double self_noise_db_spl{};
    bool enabled{ true };
};

struct channel_filter_state {
    double left{};
    double right{};
};

} // namespace

struct pg_engine {
    pg_engine_config config{};
    atmosphere_state atmosphere{};
    room_state room{};
    std::vector<emitter_state> emitters;
    std::vector<receiver_state> receivers;
    std::vector<channel_filter_state> receiver_filters;
    std::vector<std::uint64_t> receiver_next_frames;
    std::string last_error;
};

namespace {

pg_status fail(pg_engine* engine, pg_status status, std::string message)
{
    if (engine != nullptr) {
        engine->last_error = std::move(message);
    }
    return status;
}

double speed_of_sound(const atmosphere_state& atmosphere) noexcept
{
    // Temperature dominates the first-order model. Humidity adds a deliberately
    // bounded correction so project values affect propagation without claiming
    // a full atmospheric absorption standard implementation.
    const double temperature_speed = 331.3 + (0.606 * atmosphere.temperature_celsius);
    const double humidity_correction = std::clamp(atmosphere.relative_humidity_percent, 0.0, 100.0) * 0.0124;
    return temperature_speed + humidity_correction;
}

double air_absorption_per_metre(const atmosphere_state& atmosphere, double frequency_hz) noexcept
{
    const double normalized_frequency = std::clamp(frequency_hz / 1000.0, 0.02, 20.0);
    const double dryness = 1.0 - (std::clamp(atmosphere.relative_humidity_percent, 0.0, 100.0) / 100.0);
    const double pressure_factor = 101.325 / std::clamp(atmosphere.pressure_kilopascals, 20.0, 120.0);
    return (0.000015 + (0.000018 * dryness)) * normalized_frequency * normalized_frequency * pressure_factor;
}

vec3 sample_motion(const emitter_state& emitter, double time_seconds) noexcept
{
    if (emitter.keyframes.empty()) {
        return {};
    }
    if (time_seconds <= emitter.keyframes.front().time_seconds) {
        return emitter.keyframes.front().position;
    }
    if (time_seconds >= emitter.keyframes.back().time_seconds) {
        return emitter.keyframes.back().position;
    }

    const auto upper = std::upper_bound(
        emitter.keyframes.begin(),
        emitter.keyframes.end(),
        time_seconds,
        [](double time, const keyframe_state& keyframe) { return time < keyframe.time_seconds; });
    const auto lower = upper - 1;
    const double duration = upper->time_seconds - lower->time_seconds;
    const double amount = duration > 0.0 ? (time_seconds - lower->time_seconds) / duration : 0.0;
    return lower->position + ((upper->position - lower->position) * amount);
}

double effective_sound_speed(const atmosphere_state& atmosphere, vec3 source, vec3 receiver) noexcept
{
    const vec3 propagation = normalized(receiver - source);
    return std::max(minimum_effective_sound_speed, speed_of_sound(atmosphere) + dot(atmosphere.wind, propagation));
}

struct delayed_path_sample {
    double value{};
    double distance{};
    vec3 source_position{};
};

delayed_path_sample sample_delayed_path(
    const pg_engine& engine,
    const emitter_state& emitter,
    vec3 receiver_position,
    double receive_time,
    bool reflected,
    int reflection_axis,
    bool reflection_high_side) noexcept
{
    double emission_time = receive_time;
    vec3 source_position = sample_motion(emitter, emission_time);
    vec3 apparent_source = source_position;

    for (int iteration = 0; iteration < 6; ++iteration) {
        source_position = sample_motion(emitter, emission_time);
        apparent_source = source_position;
        if (reflected && engine.room.enabled) {
            const std::array<double, 3> centre{ engine.room.centre.x, engine.room.centre.y, engine.room.centre.z };
            const std::array<double, 3> dimensions{ engine.room.dimensions.x, engine.room.dimensions.y, engine.room.dimensions.z };
            std::array<double, 3> coordinates{ apparent_source.x, apparent_source.y, apparent_source.z };
            const double wall = centre[static_cast<std::size_t>(reflection_axis)] +
                (reflection_high_side ? dimensions[static_cast<std::size_t>(reflection_axis)] * 0.5 : -dimensions[static_cast<std::size_t>(reflection_axis)] * 0.5);
            coordinates[static_cast<std::size_t>(reflection_axis)] = (2.0 * wall) - coordinates[static_cast<std::size_t>(reflection_axis)];
            apparent_source = { coordinates[0], coordinates[1], coordinates[2] };
        }

        const double distance = length(receiver_position - apparent_source);
        const double propagation_speed = effective_sound_speed(engine.atmosphere, apparent_source, receiver_position);
        emission_time = receive_time - (distance / propagation_speed);
    }

    const double distance = length(receiver_position - apparent_source);
    const double attenuation_distance = std::max(distance, emitter.min_distance_metres);
    const double absorption = std::exp(-air_absorption_per_metre(engine.atmosphere, emitter.frequency_hz) * distance);
    double reflection = 1.0;
    if (reflected) {
        const double frequency_mix = std::clamp((emitter.frequency_hz - 250.0) / 3750.0, 0.0, 1.0);
        reflection = std::lerp(engine.room.reflection_mid, engine.room.reflection_high, frequency_mix);
        reflection *= 1.0 - (0.35 * engine.room.scattering);
    }

    const double phase = 2.0 * std::numbers::pi * emitter.frequency_hz * emission_time;
    const double sample = std::sin(phase) * emitter.amplitude * emitter.gain_linear * absorption * reflection / attenuation_distance;
    return { sample, distance, source_position };
}

double microphone_pattern_gain(pg_polar_pattern pattern, vec3 capsule_forward, vec3 source_direction) noexcept
{
    const double cosine = std::clamp(dot(normalized(capsule_forward), normalized(source_direction)), -1.0, 1.0);
    switch (pattern) {
    case PG_POLAR_CARDIOID:
        return 0.5 * (1.0 + cosine);
    case PG_POLAR_SUPERCARDIOID:
        return std::max(0.0, (0.37 + (0.63 * cosine)));
    case PG_POLAR_FIGURE_EIGHT:
        return cosine;
    case PG_POLAR_OMNI:
    default:
        return 1.0;
    }
}

std::pair<float, float> render_frame(
    pg_engine& engine,
    std::size_t receiver_index,
    double receive_time,
    std::uint32_t sample_rate) noexcept
{
    const receiver_state& receiver = engine.receivers[receiver_index];
    const vec3 right = normalized(rotate_yaw({ 1.0, 0.0, 0.0 }, receiver.yaw_degrees));
    const vec3 forward = normalized(rotate_yaw({ 0.0, 0.0, 1.0 }, receiver.yaw_degrees));
    const vec3 left_position = receiver.centre - (right * (receiver.spacing_metres * 0.5));
    const vec3 right_position = receiver.centre + (right * (receiver.spacing_metres * 0.5));

    double left_total = 0.0;
    double right_total = 0.0;
    double source_lateral = 0.0;
    for (const emitter_state& emitter : engine.emitters) {
        if (!emitter.enabled) {
            continue;
        }

        const auto left_direct = sample_delayed_path(engine, emitter, left_position, receive_time, false, 0, false);
        const auto right_direct = sample_delayed_path(engine, emitter, right_position, receive_time, false, 0, false);
        double left_sample = left_direct.value;
        double right_sample = right_direct.value;
        source_lateral += dot(normalized(left_direct.source_position - receiver.centre), right);

        if (engine.room.enabled) {
            for (int axis = 0; axis < 3; ++axis) {
                for (int side = 0; side < 2; ++side) {
                    left_sample += sample_delayed_path(engine, emitter, left_position, receive_time, true, axis, side != 0).value;
                    right_sample += sample_delayed_path(engine, emitter, right_position, receive_time, true, axis, side != 0).value;
                }
            }
        }

        if (receiver.kind == PG_RECEIVER_STEREO_MICROPHONE) {
            const vec3 left_forward = rotate_yaw(forward, -receiver.microphone_angle_degrees * 0.5);
            const vec3 right_forward = rotate_yaw(forward, receiver.microphone_angle_degrees * 0.5);
            left_sample *= microphone_pattern_gain(receiver.polar_pattern, left_forward, left_direct.source_position - left_position);
            right_sample *= microphone_pattern_gain(receiver.polar_pattern, right_forward, right_direct.source_position - right_position);
        }

        left_total += left_sample;
        right_total += right_sample;
    }

    if (receiver.kind == PG_RECEIVER_BINAURAL_HEAD) {
        channel_filter_state& filter = engine.receiver_filters[receiver_index];
        const double obstruction = std::clamp(std::abs(source_lateral), 0.0, 1.0);
        const double cutoff_hz = std::lerp(18000.0, 1400.0, obstruction * std::clamp(receiver.head_radius_metres / 0.12, 0.25, 1.5));
        const double coefficient = std::exp((-2.0 * std::numbers::pi * cutoff_hz) / static_cast<double>(sample_rate));
        if (source_lateral > 0.0) {
            filter.left = ((1.0 - coefficient) * left_total) + (coefficient * filter.left);
            left_total = filter.left;
            filter.right = right_total;
        } else {
            filter.right = ((1.0 - coefficient) * right_total) + (coefficient * filter.right);
            right_total = filter.right;
            filter.left = left_total;
        }
    }

    left_total *= receiver.sensitivity_linear;
    right_total *= receiver.sensitivity_linear;
    return {
        static_cast<float>(std::clamp(left_total, -4.0, 4.0)),
        static_cast<float>(std::clamp(right_total, -4.0, 4.0))
    };
}

pg_status validate_scene(pg_engine* engine)
{
    if (engine->emitters.empty()) {
        return fail(engine, PG_STATUS_INVALID_SCENE, "Scene has no emitters.");
    }
    if (engine->receivers.empty()) {
        return fail(engine, PG_STATUS_INVALID_SCENE, "Scene has no receiver rigs.");
    }
    return PG_STATUS_OK;
}

} // namespace

extern "C" {

uint32_t PG_CALL pg_get_abi_version(void) { return PG_ABI_VERSION; }

const char* PG_CALL pg_get_engine_version(void) { return engine_version.data(); }

const char* PG_CALL pg_get_last_error(const pg_engine* engine)
{
    return engine == nullptr ? "PulseGen engine handle is null." : engine->last_error.c_str();
}

pg_status PG_CALL pg_engine_create(const pg_engine_config* config, pg_engine** out_engine)
{
    if (out_engine == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    *out_engine = nullptr;
    if (!valid_struct(config)) {
        return config == nullptr ? PG_STATUS_INVALID_ARGUMENT : PG_STATUS_ABI_MISMATCH;
    }
    if (config->max_emitters == 0U || config->max_receivers == 0U || config->max_keyframes_per_path < 2U) {
        return PG_STATUS_INVALID_ARGUMENT;
    }

    try {
        auto* engine = new pg_engine{};
        engine->config = *config;
        engine->emitters.reserve(config->max_emitters);
        engine->receivers.reserve(config->max_receivers);
        engine->receiver_filters.reserve(config->max_receivers);
        engine->receiver_next_frames.reserve(config->max_receivers);
        *out_engine = engine;
        return PG_STATUS_OK;
    } catch (...) {
        return PG_STATUS_INTERNAL_ERROR;
    }
}

void PG_CALL pg_engine_destroy(pg_engine* engine) { delete engine; }

pg_status PG_CALL pg_engine_clear_scene(pg_engine* engine)
{
    if (engine == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    engine->emitters.clear();
    engine->receivers.clear();
    engine->receiver_filters.clear();
    engine->receiver_next_frames.clear();
    engine->last_error.clear();
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_set_atmosphere(pg_engine* engine, const pg_atmosphere_desc* atmosphere)
{
    if (engine == nullptr || atmosphere == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(atmosphere)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Atmosphere structure version or size is incompatible.");
    }
    const vec3 wind = from_c(atmosphere->uniform_wind_metres_per_second);
    if (!finite(wind) || !std::isfinite(atmosphere->temperature_celsius) || atmosphere->temperature_celsius < -80.0 || atmosphere->temperature_celsius > 80.0 ||
        atmosphere->relative_humidity_percent < 0.0 || atmosphere->relative_humidity_percent > 100.0 || atmosphere->turbulence < 0.0 || atmosphere->turbulence > 1.0) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Atmosphere values are outside bounded acoustic ranges.");
    }
    engine->atmosphere = {
        atmosphere->temperature_celsius,
        atmosphere->relative_humidity_percent,
        atmosphere->pressure_kilopascals,
        wind,
        atmosphere->turbulence,
        atmosphere->noise_seed
    };
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_set_room(pg_engine* engine, const pg_room_desc* room)
{
    if (engine == nullptr || room == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(room)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Room structure version or size is incompatible.");
    }
    const vec3 dimensions = from_c(room->dimensions_metres);
    if (!finite(dimensions) || dimensions.x <= 0.0 || dimensions.y <= 0.0 || dimensions.z <= 0.0) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Room dimensions must be finite and positive.");
    }
    const auto coefficient_valid = [](double value) { return std::isfinite(value) && value >= 0.0 && value <= 1.0; };
    if (!coefficient_valid(room->wall_reflection_low) || !coefficient_valid(room->wall_reflection_mid) ||
        !coefficient_valid(room->wall_reflection_high) || !coefficient_valid(room->scattering)) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Room reflection and scattering values must be in 0..1.");
    }
    engine->room = {
        from_c(room->centre_metres),
        dimensions,
        room->wall_reflection_low,
        room->wall_reflection_mid,
        room->wall_reflection_high,
        room->scattering,
        room->enabled != 0U
    };
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_add_sine_emitter(pg_engine* engine, const pg_sine_emitter_desc* emitter, uint32_t* out_index)
{
    if (engine == nullptr || emitter == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(emitter)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Emitter structure version or size is incompatible.");
    }
    if (engine->emitters.size() >= engine->config.max_emitters) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Emitter capacity exceeded.");
    }
    if (emitter->keyframes == nullptr || emitter->keyframe_count < 2U || emitter->keyframe_count > engine->config.max_keyframes_per_path ||
        !std::isfinite(emitter->frequency_hz) || emitter->frequency_hz <= 0.0 || !std::isfinite(emitter->amplitude) ||
        !std::isfinite(emitter->gain_linear) || emitter->gain_linear < 0.0 || emitter->min_distance_metres <= 0.0) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Emitter frequency, gain, minimum distance, or path is invalid.");
    }

    emitter_state state{};
    state.name = safe_name(emitter->name_utf8, "Emitter");
    state.frequency_hz = emitter->frequency_hz;
    state.amplitude = emitter->amplitude;
    state.gain_linear = emitter->gain_linear;
    state.min_distance_metres = emitter->min_distance_metres;
    state.enabled = emitter->enabled != 0U;
    state.keyframes.reserve(emitter->keyframe_count);
    for (std::uint32_t index = 0; index < emitter->keyframe_count; ++index) {
        const auto& keyframe = emitter->keyframes[index];
        if (!std::isfinite(keyframe.time_seconds) || !finite(from_c(keyframe.position_metres)) ||
            (index > 0U && keyframe.time_seconds <= emitter->keyframes[index - 1U].time_seconds)) {
            return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Emitter keyframes must be finite and strictly increasing in time.");
        }
        state.keyframes.push_back({ keyframe.time_seconds, from_c(keyframe.position_metres) });
    }

    engine->emitters.push_back(std::move(state));
    if (out_index != nullptr) {
        *out_index = static_cast<std::uint32_t>(engine->emitters.size() - 1U);
    }
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_add_receiver(pg_engine* engine, const pg_receiver_rig_desc* receiver, uint32_t* out_index)
{
    if (engine == nullptr || receiver == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(receiver)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Receiver structure version or size is incompatible.");
    }
    if (engine->receivers.size() >= engine->config.max_receivers) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Receiver capacity exceeded.");
    }
    if (!finite(from_c(receiver->centre_metres)) || !std::isfinite(receiver->spacing_metres) || receiver->spacing_metres < 0.01 || receiver->spacing_metres > 5.0 ||
        receiver->head_radius_metres < 0.04 || receiver->head_radius_metres > 0.2 || !std::isfinite(receiver->sensitivity_db)) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Receiver position, spacing, head radius, or sensitivity is invalid.");
    }

    receiver_state state{};
    state.name = safe_name(receiver->name_utf8, "Receiver");
    state.kind = receiver->kind;
    state.centre = from_c(receiver->centre_metres);
    state.yaw_degrees = receiver->yaw_degrees;
    state.spacing_metres = receiver->spacing_metres;
    state.head_radius_metres = receiver->head_radius_metres;
    state.microphone_angle_degrees = receiver->microphone_angle_degrees;
    state.polar_pattern = receiver->polar_pattern;
    state.sensitivity_linear = std::pow(10.0, receiver->sensitivity_db / 20.0);
    state.self_noise_db_spl = receiver->self_noise_db_spl;
    state.enabled = receiver->enabled != 0U;
    engine->receivers.push_back(std::move(state));
    engine->receiver_filters.push_back({});
    engine->receiver_next_frames.push_back(0U);
    if (out_index != nullptr) {
        *out_index = static_cast<std::uint32_t>(engine->receivers.size() - 1U);
    }
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_render_receiver_block(
    pg_engine* engine,
    uint32_t receiver_index,
    uint64_t start_frame,
    uint32_t frame_count,
    uint32_t sample_rate,
    float* interleaved_stereo,
    size_t interleaved_sample_capacity)
{
    if (engine == nullptr || interleaved_stereo == nullptr || sample_rate == 0U) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (receiver_index >= engine->receivers.size()) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Receiver index is out of range.");
    }
    if (interleaved_sample_capacity < static_cast<std::size_t>(frame_count) * 2U) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Output buffer is too small for requested stereo frames.");
    }
    if (validate_scene(engine) != PG_STATUS_OK) {
        return PG_STATUS_INVALID_SCENE;
    }

    if (start_frame == 0U || engine->receiver_next_frames[receiver_index] != start_frame) {
        engine->receiver_filters[receiver_index] = {};
    }
    for (std::uint32_t frame = 0; frame < frame_count; ++frame) {
        const double receive_time = static_cast<double>(start_frame + frame) / static_cast<double>(sample_rate);
        const auto [left, right] = render_frame(*engine, receiver_index, receive_time, sample_rate);
        interleaved_stereo[static_cast<std::size_t>(frame) * 2U] = left;
        interleaved_stereo[(static_cast<std::size_t>(frame) * 2U) + 1U] = right;
    }
    engine->receiver_next_frames[receiver_index] = start_frame + frame_count;
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_render_offline(pg_engine* engine, const pg_render_request* request, pg_render_receipt* receipt)
{
    if (engine == nullptr || request == nullptr || receipt == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(request) || receipt->size < sizeof(pg_render_receipt) || receipt->version != PG_ABI_VERSION) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Render request or receipt structure is incompatible.");
    }
    if (validate_scene(engine) != PG_STATUS_OK) {
        return PG_STATUS_INVALID_SCENE;
    }
    if (request->output_directory_utf8 == nullptr || request->duration_seconds <= 0.0 || request->sample_rate == 0U ||
        !std::isfinite(request->duration_seconds)) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Render directory, duration, or sample rate is invalid.");
    }

    try {
        const std::filesystem::path output_directory = path_from_utf8(request->output_directory_utf8);
        std::filesystem::create_directories(output_directory);
        const std::string prefix = safe_name(request->file_prefix_utf8, "PulseGen");
        const auto frame_count = static_cast<std::uint64_t>(std::llround(request->duration_seconds * static_cast<double>(request->sample_rate)));
        if (frame_count == 0U || frame_count > (std::numeric_limits<std::size_t>::max() / (2U * sizeof(float)))) {
            return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Render frame count is outside addressable bounds.");
        }
        std::vector<float> buffer(static_cast<std::size_t>(frame_count) * 2U);
        double peak_left = 0.0;
        double peak_right = 0.0;
        long double sum_square_left = 0.0;
        long double sum_square_right = 0.0;
        std::uint32_t files_written = 0U;
        std::uint64_t measured_frames = 0U;
        std::vector<std::pair<std::filesystem::path, std::filesystem::path>> staged_files;
        staged_files.reserve(engine->receivers.size());

        for (std::size_t receiver_index = 0; receiver_index < engine->receivers.size(); ++receiver_index) {
            if (!engine->receivers[receiver_index].enabled) {
                continue;
            }
            engine->receiver_filters[receiver_index] = {};
            for (std::uint64_t frame = 0; frame < frame_count; ++frame) {
                if (request->cancellation_flag != nullptr && *request->cancellation_flag != 0U) {
                    for (const auto& [staged, unused_final] : staged_files) {
                        (void)unused_final;
                        std::error_code cleanup_error;
                        std::filesystem::remove(staged, cleanup_error);
                    }
                    return fail(engine, PG_STATUS_CANCELLED, "Render cancelled before output commit.");
                }
                const double receive_time = static_cast<double>(frame) / static_cast<double>(request->sample_rate);
                const auto [left, right] = render_frame(*engine, receiver_index, receive_time, request->sample_rate);
                const std::size_t sample_index = static_cast<std::size_t>(frame) * 2U;
                buffer[sample_index] = left;
                buffer[sample_index + 1U] = right;
                peak_left = std::max(peak_left, std::abs(static_cast<double>(left)));
                peak_right = std::max(peak_right, std::abs(static_cast<double>(right)));
                sum_square_left += static_cast<long double>(left) * static_cast<long double>(left);
                sum_square_right += static_cast<long double>(right) * static_cast<long double>(right);
            }
            measured_frames += frame_count;

            const std::string file_name = prefix + "_" + engine->receivers[receiver_index].name + ".wav";
            const std::filesystem::path final_path = output_directory / file_name;
            const std::filesystem::path temporary_path = final_path.string() + ".tmp";
            std::string error;
            if (!pulsegen::write_stereo_wav(temporary_path, buffer, request->sample_rate, request->sample_format, request->seed + receiver_index, error)) {
                for (const auto& [staged, unused_final] : staged_files) {
                    (void)unused_final;
                    std::error_code cleanup_error;
                    std::filesystem::remove(staged, cleanup_error);
                }
                return fail(engine, PG_STATUS_IO_ERROR, std::move(error));
            }
            staged_files.emplace_back(temporary_path, final_path);
        }

        if (request->cancellation_flag != nullptr && *request->cancellation_flag != 0U) {
            for (const auto& [staged, unused_final] : staged_files) {
                (void)unused_final;
                std::error_code cleanup_error;
                std::filesystem::remove(staged, cleanup_error);
            }
            return fail(engine, PG_STATUS_CANCELLED, "Render cancelled before output commit.");
        }

        for (const auto& [temporary_path, final_path] : staged_files) {
            std::error_code move_error;
            std::filesystem::remove(final_path, move_error);
            move_error.clear();
            std::filesystem::rename(temporary_path, final_path, move_error);
            if (move_error) {
                std::filesystem::remove(temporary_path, move_error);
                return fail(engine, PG_STATUS_IO_ERROR, "Unable to atomically commit rendered WAV.");
            }
            ++files_written;
        }

        receipt->frames_rendered = frame_count;
        receipt->files_written = files_written;
        receipt->sample_rate = request->sample_rate;
        receipt->peak_left = peak_left;
        receipt->peak_right = peak_right;
        receipt->rms_left = std::sqrt(static_cast<double>(sum_square_left / static_cast<long double>(std::max<std::uint64_t>(measured_frames, 1U))));
        receipt->rms_right = std::sqrt(static_cast<double>(sum_square_right / static_cast<long double>(std::max<std::uint64_t>(measured_frames, 1U))));
        receipt->speed_of_sound_metres_per_second = speed_of_sound(engine->atmosphere);
        receipt->deterministic_seed = request->seed;
        return PG_STATUS_OK;
    } catch (const std::exception& exception) {
        return fail(engine, PG_STATUS_INTERNAL_ERROR, exception.what());
    } catch (...) {
        return fail(engine, PG_STATUS_INTERNAL_ERROR, "Unknown failure while rendering offline.");
    }
}

double PG_CALL pg_calculate_speed_of_sound(double temperature_celsius)
{
    return 331.3 + (0.606 * temperature_celsius);
}

double PG_CALL pg_calculate_observed_frequency(
    double source_frequency_hz,
    double speed_of_sound_metres_per_second,
    double source_radial_velocity_metres_per_second,
    double receiver_radial_velocity_metres_per_second)
{
    if (!std::isfinite(source_frequency_hz) || !std::isfinite(speed_of_sound_metres_per_second) || source_frequency_hz <= 0.0 || speed_of_sound_metres_per_second <= 0.0) {
        return std::numeric_limits<double>::quiet_NaN();
    }
    const double numerator = speed_of_sound_metres_per_second + receiver_radial_velocity_metres_per_second;
    const double denominator = speed_of_sound_metres_per_second - source_radial_velocity_metres_per_second;
    if (numerator <= 0.0 || denominator <= speed_of_sound_metres_per_second * 0.01) {
        return std::numeric_limits<double>::quiet_NaN();
    }
    return source_frequency_hz * numerator / denominator;
}

} // extern "C"

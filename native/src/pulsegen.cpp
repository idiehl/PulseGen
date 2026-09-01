#include "pulsegen/pulsegen.h"
#include "wav_writer.h"
#include "miniaudio.h"

#include <assimp/Importer.hpp>
#include <assimp/IOSystem.hpp>
#include <assimp/postprocess.h>
#include <assimp/scene.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cctype>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <exception>
#include <filesystem>
#include <limits>
#include <new>
#include <numbers>
#include <numeric>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace {

constexpr std::string_view engine_version = "0.1.0-alpha.15";
constexpr double minimum_effective_sound_speed = 40.0;
constexpr std::uint32_t maximum_atmosphere_integration_segments = 4096U;
constexpr std::uint32_t maximum_supported_sample_rate = 192000U;
constexpr std::uint64_t maximum_buffer_preview_seconds = 600U;
constexpr std::size_t maximum_acoustic_materials = 256U;
constexpr std::size_t maximum_acoustic_geometries = 256U;
constexpr std::size_t maximum_imported_mesh_geometries = 64U;
constexpr std::size_t maximum_imported_mesh_triangles_total = 1000000U;
constexpr std::uint64_t maximum_mesh_asset_bytes = 512ULL * 1024ULL * 1024ULL;
constexpr std::uint32_t mesh_bvh_leaf_triangles = 8U;
constexpr std::size_t maximum_mesh_reflection_patches = 256U;
constexpr std::size_t maximum_cached_mesh_reflection_paths = 8U;
constexpr std::size_t maximum_second_order_candidate_patches = 12U;
constexpr std::size_t maximum_cached_mesh_second_order_paths = 4U;
constexpr std::uint32_t mesh_reflection_cache_interval_frames = 256U;
constexpr std::array<double, 4> late_delay_seconds{ 0.0297, 0.0371, 0.0411, 0.0437 };
constexpr std::array<double, 8> diffuse_delay_seconds{ 0.0113, 0.0179, 0.0231, 0.0317, 0.0433, 0.0599, 0.0731, 0.0897 };

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
vec3 cross(vec3 left, vec3 right) noexcept
{
    return {
        (left.y * right.z) - (left.z * right.y),
        (left.z * right.x) - (left.x * right.z),
        (left.x * right.y) - (left.y * right.x)
    };
}
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

vec3 rotate_euler(vec3 value, vec3 rotation_degrees) noexcept
{
    const double x_radians = rotation_degrees.x * std::numbers::pi / 180.0;
    const double y_radians = rotation_degrees.y * std::numbers::pi / 180.0;
    const double z_radians = rotation_degrees.z * std::numbers::pi / 180.0;

    const double x_cosine = std::cos(x_radians);
    const double x_sine = std::sin(x_radians);
    value = { value.x, (value.y * x_cosine) - (value.z * x_sine), (value.y * x_sine) + (value.z * x_cosine) };

    const double y_cosine = std::cos(y_radians);
    const double y_sine = std::sin(y_radians);
    value = { (value.x * y_cosine) + (value.z * y_sine), value.y, (-value.x * y_sine) + (value.z * y_cosine) };

    const double z_cosine = std::cos(z_radians);
    const double z_sine = std::sin(z_radians);
    return { (value.x * z_cosine) - (value.y * z_sine), (value.x * z_sine) + (value.y * z_cosine), value.z };
}

vec3 inverse_rotate_euler(vec3 value, vec3 rotation_degrees) noexcept
{
    const double z_radians = -rotation_degrees.z * std::numbers::pi / 180.0;
    const double z_cosine = std::cos(z_radians);
    const double z_sine = std::sin(z_radians);
    value = { (value.x * z_cosine) - (value.y * z_sine), (value.x * z_sine) + (value.y * z_cosine), value.z };

    const double y_radians = -rotation_degrees.y * std::numbers::pi / 180.0;
    const double y_cosine = std::cos(y_radians);
    const double y_sine = std::sin(y_radians);
    value = { (value.x * y_cosine) + (value.z * y_sine), value.y, (-value.x * y_sine) + (value.z * y_cosine) };

    const double x_radians = -rotation_degrees.x * std::numbers::pi / 180.0;
    const double x_cosine = std::cos(x_radians);
    const double x_sine = std::sin(x_radians);
    return { value.x, (value.y * x_cosine) - (value.z * x_sine), (value.y * x_sine) + (value.z * x_cosine) };
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

class memory_only_io_system final : public Assimp::IOSystem {
public:
    bool Exists(const char*) const override { return false; }
    char getOsSeparator() const override { return '/'; }
    Assimp::IOStream* Open(const char*, const char* = "rb") override { return nullptr; }
    void Close(Assimp::IOStream*) override { }
};

struct atmosphere_layer_state {
    double base_altitude_metres{};
    double top_altitude_metres{};
    double temperature_celsius{ 20.0 };
    double relative_humidity_percent{ 50.0 };
    double pressure_kilopascals{ 101.325 };
};

struct wind_zone_state {
    vec3 centre{};
    vec3 dimensions{ 20.0, 20.0, 20.0 };
    vec3 rotation_degrees{};
    vec3 velocity{};
    double turbulence{};
    std::uint64_t noise_seed{ 1U };
};

struct atmosphere_state {
    double temperature_celsius{ 20.0 };
    double relative_humidity_percent{ 50.0 };
    double pressure_kilopascals{ 101.325 };
    vec3 wind{};
    double turbulence{};
    std::uint64_t noise_seed{ 1U };
    std::vector<atmosphere_layer_state> layers;
    std::vector<wind_zone_state> wind_zones;
    std::uint32_t integration_segments{ 1U };
};

struct room_state {
    vec3 centre{};
    vec3 dimensions{ 20.0, 8.0, 12.0 };
    double reflection_low{ 0.75 };
    double reflection_mid{ 0.65 };
    double reflection_high{ 0.5 };
    double scattering{ 0.1 };
    double decay_time_low_seconds{ 1.5 };
    double decay_time_mid_seconds{ 1.5 };
    double decay_time_high_seconds{ 1.5 };
    bool enabled{ true };
};

struct acoustic_material_state {
    std::string name;
    std::array<double, PG_ACOUSTIC_BAND_COUNT> octave_band_centres_hz{};
    std::array<double, PG_ACOUSTIC_BAND_COUNT> absorption{};
    std::array<double, PG_ACOUSTIC_BAND_COUNT> scattering{};
    std::array<double, PG_ACOUSTIC_BAND_COUNT> transmission{};
    double roughness{};
};

struct geometry_state {
    std::string name;
    pg_geometry_kind kind{ PG_GEOMETRY_PLANE };
    vec3 centre{};
    vec3 dimensions{ 1.0, 1.0, 1.0 };
    vec3 rotation_degrees{};
    std::uint32_t material_index{};
    bool occludes{ true };
    bool reflects{ true };
    bool enabled{ true };
};

struct axis_aligned_bounds {
    vec3 minimum{
        std::numeric_limits<double>::infinity(),
        std::numeric_limits<double>::infinity(),
        std::numeric_limits<double>::infinity()
    };
    vec3 maximum{
        -std::numeric_limits<double>::infinity(),
        -std::numeric_limits<double>::infinity(),
        -std::numeric_limits<double>::infinity()
    };
};

struct mesh_triangle {
    vec3 a{};
    vec3 b{};
    vec3 c{};
    axis_aligned_bounds bounds{};
    vec3 centroid{};
    std::uint32_t source_mesh_index{};
    std::uint32_t source_material_index{};
    std::uint32_t material_index{};
    std::uint32_t reflection_patch_index{ std::numeric_limits<std::uint32_t>::max() };
};

struct mesh_reflection_patch {
    vec3 point{};
    vec3 normal{ 0.0, 0.0, 1.0 };
    axis_aligned_bounds bounds{};
    double area_square_metres{};
    std::uint32_t source_mesh_index{};
    std::uint32_t source_material_index{};
    std::uint32_t material_index{};
};

struct mesh_material_region_state {
    std::uint32_t source_mesh_index{};
    std::uint32_t source_material_index{};
    std::uint32_t material_index{};
    std::uint32_t triangle_count{};
    std::string source_mesh_name;
    std::string source_material_name;
};

struct mesh_bvh_node {
    axis_aligned_bounds bounds{};
    std::uint32_t left{};
    std::uint32_t right{};
    std::uint32_t first{};
    std::uint32_t count{};
};

struct mesh_geometry_state {
    std::string name;
    std::uint32_t material_index{};
    bool occludes{ true };
    bool reflects{ true };
    bool enabled{ true };
    std::vector<mesh_triangle> triangles;
    std::vector<std::uint32_t> triangle_indices;
    std::vector<mesh_bvh_node> bvh;
    std::vector<mesh_reflection_patch> reflection_patches;
    std::vector<mesh_material_region_state> material_regions;
    axis_aligned_bounds bounds{};
};

struct mesh_reflection_cache {
    std::array<std::uint32_t, maximum_cached_mesh_reflection_paths> patch_indices{};
    std::uint32_t count{};
};

struct mesh_patch_reference {
    std::uint32_t mesh_index{};
    std::uint32_t patch_index{};
};

struct mesh_second_order_reflection_cache {
    std::array<mesh_patch_reference, maximum_cached_mesh_second_order_paths> first{};
    std::array<mesh_patch_reference, maximum_cached_mesh_second_order_paths> second{};
    std::uint32_t count{};
};

struct keyframe_state {
    double time_seconds{};
    vec3 position{};
};

struct additive_partial_state {
    double frequency_ratio{ 1.0 };
    double amplitude{ 1.0 };
    double phase_radians{};
};

struct emitter_state {
    std::string name;
    pg_waveform_kind waveform{ PG_WAVE_SINE };
    pg_motion_path_kind motion_path_kind{ PG_PATH_LINEAR };
    double frequency_hz{ 440.0 };
    double amplitude{ 1.0 };
    double phase_radians{};
    double pulse_width{ 0.5 };
    double gain_linear{ 1.0 };
    double min_distance_metres{ 1.0 };
    double attack_seconds{};
    double release_seconds{};
    double nonlinear_drive{};
    std::uint64_t noise_seed{ 1U };
    std::vector<additive_partial_state> additive_partials;
    std::vector<keyframe_state> keyframes;
    bool enabled{ true };
};

struct microphone_capsule_state {
    vec3 relative_position{};
    vec3 relative_rotation{};
    pg_polar_pattern polar_pattern{ PG_POLAR_OMNI };
    double sensitivity_linear{ 1.0 };
    double self_noise_db_spl{};
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
    microphone_capsule_state left_capsule{};
    microphone_capsule_state right_capsule{};
    bool has_explicit_capsules{};
    pg_motion_path_kind motion_path_kind{ PG_PATH_LINEAR };
    std::vector<keyframe_state> keyframes;
    bool enabled{ true };
};

struct channel_filter_state {
    double left{};
    double right{};
};

struct room_response_state {
    std::array<std::array<std::vector<float>, late_delay_seconds.size()>, 3> delay_lines;
    std::array<std::array<std::size_t, late_delay_seconds.size()>, 3> delay_indices{};
    std::array<std::array<double, late_delay_seconds.size()>, 3> delay_feedbacks{};
    std::array<double, 3> input_gains{};
    std::array<double, 3> modal_previous_one{};
    std::array<double, 3> modal_previous_two{};
    double lowpass_low{};
    double lowpass_high{};
    double lowpass_low_coefficient{};
    double lowpass_high_coefficient{};
    double modal_radius{ 0.9 };
    std::uint32_t sample_rate{};
};

struct diffuse_response_state {
    std::array<std::vector<float>, diffuse_delay_seconds.size()> delay_lines;
    std::array<std::size_t, diffuse_delay_seconds.size()> delay_indices{};
    std::uint32_t sample_rate{};
    std::uint32_t ray_count{};
    std::uint32_t seed{};
    double decay_time_seconds{};
};

} // namespace

struct pg_engine {
    pg_engine_config config{};
    atmosphere_state atmosphere{};
    room_state room{};
    std::vector<acoustic_material_state> acoustic_materials;
    std::vector<geometry_state> geometries;
    std::vector<mesh_geometry_state> mesh_geometries;
    std::vector<emitter_state> emitters;
    std::vector<receiver_state> receivers;
    std::vector<channel_filter_state> receiver_filters;
    std::vector<std::uint64_t> receiver_next_frames;
    std::vector<room_response_state> receiver_room_responses;
    std::vector<diffuse_response_state> receiver_diffuse_responses;
    std::vector<mesh_reflection_cache> mesh_reflection_caches;
    std::vector<mesh_second_order_reflection_cache> mesh_second_order_reflection_caches;
    ma_device device{};
    bool device_initialized{};
    std::atomic_bool device_running{};
    std::atomic_bool device_recovery_available{};
    std::atomic_uint64_t device_frame_cursor{};
    std::atomic_uint64_t device_frames_processed{};
    std::atomic_uint64_t device_callback_count{};
    std::atomic_uint64_t device_callback_duration_nanoseconds_total{};
    std::atomic_uint64_t device_callback_duration_nanoseconds_maximum{};
    std::atomic_uint64_t device_callback_deadline_miss_count{};
    std::atomic_uint32_t device_interruption_count{};
    std::atomic_uint32_t device_recovery_count{};
    std::atomic_uint32_t device_callback_load_parts_per_million_maximum{};
    std::atomic_uint32_t device_callback_frames_maximum{};
    std::uint32_t device_sample_rate{};
    std::uint32_t device_period_frames{};
    std::uint32_t device_receiver_index{};
    std::uint32_t device_reflection_order{ 1U };
    std::uint32_t device_ray_count{ 1024U };
    std::uint32_t device_seed{ 1U };
    double device_impulse_response_seconds{ 2.0 };
    bool device_exclusive_mode{};
    std::vector<float> device_buffer;
    bool device_buffer_mode{};
    bool device_buffer_loop{};
    std::string last_error;
};

namespace {

static_assert(std::atomic<std::uint64_t>::is_always_lock_free, "PulseGen requires lock-free 64-bit audio telemetry atomics.");
static_assert(std::atomic<std::uint32_t>::is_always_lock_free, "PulseGen requires lock-free 32-bit audio telemetry atomics.");

using device_callback_clock = std::chrono::steady_clock;

template <typename T>
void store_atomic_maximum(std::atomic<T>& destination, T value) noexcept
{
    T observed = destination.load(std::memory_order_relaxed);
    while (observed < value &&
        !destination.compare_exchange_weak(observed, value, std::memory_order_relaxed, std::memory_order_relaxed)) {
    }
}

void record_device_callback_timing(
    pg_engine* engine,
    device_callback_clock::time_point started,
    std::uint32_t frame_count) noexcept
{
    const auto elapsed = std::chrono::duration_cast<std::chrono::nanoseconds>(device_callback_clock::now() - started).count();
    const std::uint64_t elapsed_nanoseconds = elapsed > 0 ? static_cast<std::uint64_t>(elapsed) : 0U;
    engine->device_callback_duration_nanoseconds_total.fetch_add(elapsed_nanoseconds, std::memory_order_relaxed);
    store_atomic_maximum(engine->device_callback_duration_nanoseconds_maximum, elapsed_nanoseconds);
    store_atomic_maximum(engine->device_callback_frames_maximum, frame_count);

    const std::uint32_t sample_rate = engine->device_sample_rate;
    if (frame_count > 0U && sample_rate > 0U) {
        const std::uint64_t deadline_nanoseconds =
            (static_cast<std::uint64_t>(frame_count) * 1000000000ULL) / sample_rate;
        if (elapsed_nanoseconds > deadline_nanoseconds) {
            engine->device_callback_deadline_miss_count.fetch_add(1U, std::memory_order_relaxed);
        }
        const std::uint64_t load_parts_per_million = std::min<std::uint64_t>(
            (elapsed_nanoseconds * sample_rate) / (static_cast<std::uint64_t>(frame_count) * 1000ULL),
            std::numeric_limits<std::uint32_t>::max());
        store_atomic_maximum(
            engine->device_callback_load_parts_per_million_maximum,
            static_cast<std::uint32_t>(load_parts_per_million));
    }
    // Publish the completed callback count last so readers never count work
    // whose timing fields have not yet been recorded.
    engine->device_callback_count.fetch_add(1U, std::memory_order_release);
}

void reset_device_callback_timing(pg_engine* engine) noexcept
{
    engine->device_callback_count.store(0U, std::memory_order_relaxed);
    engine->device_callback_duration_nanoseconds_total.store(0U, std::memory_order_relaxed);
    engine->device_callback_duration_nanoseconds_maximum.store(0U, std::memory_order_relaxed);
    engine->device_callback_deadline_miss_count.store(0U, std::memory_order_relaxed);
    engine->device_callback_load_parts_per_million_maximum.store(0U, std::memory_order_relaxed);
    engine->device_callback_frames_maximum.store(0U, std::memory_order_relaxed);
}

bool device_running(const pg_engine* engine) noexcept
{
    return engine != nullptr && engine->device_running.load(std::memory_order_acquire);
}

pg_status fail(pg_engine* engine, pg_status status, std::string message)
{
    if (engine != nullptr) {
        engine->last_error = std::move(message);
    }
    return status;
}

pg_status require_stopped_device(pg_engine* engine, std::string_view operation)
{
    if (engine == nullptr || (!device_running(engine) && !engine->device_initialized &&
        !engine->device_recovery_available.load(std::memory_order_acquire))) {
        return PG_STATUS_OK;
    }
    return fail(engine, PG_STATUS_INVALID_ARGUMENT, std::string(operation) + " requires stopping real-time preview first.");
}

double smooth_hashed_noise(std::uint64_t seed, double position) noexcept;

struct atmosphere_sample {
    double temperature_celsius{ 20.0 };
    double relative_humidity_percent{ 50.0 };
    double pressure_kilopascals{ 101.325 };
    vec3 wind{};
};

double speed_of_sound(double temperature_celsius, double relative_humidity_percent) noexcept
{
    // Temperature dominates the first-order model. Humidity adds a deliberately
    // bounded correction so project values affect propagation without claiming
    // a full atmospheric absorption standard implementation.
    const double temperature_speed = 331.3 + (0.606 * temperature_celsius);
    const double humidity_correction = std::clamp(relative_humidity_percent, 0.0, 100.0) * 0.0124;
    return temperature_speed + humidity_correction;
}

double speed_of_sound(const atmosphere_state& atmosphere) noexcept
{
    return speed_of_sound(atmosphere.temperature_celsius, atmosphere.relative_humidity_percent);
}

double air_absorption_per_metre(
    double relative_humidity_percent,
    double pressure_kilopascals,
    double frequency_hz) noexcept
{
    const double normalized_frequency = std::clamp(frequency_hz / 1000.0, 0.02, 20.0);
    const double dryness = 1.0 - (std::clamp(relative_humidity_percent, 0.0, 100.0) / 100.0);
    const double pressure_factor = 101.325 / std::clamp(pressure_kilopascals, 20.0, 120.0);
    return (0.000015 + (0.000018 * dryness)) * normalized_frequency * normalized_frequency * pressure_factor;
}

double smoothstep(double amount) noexcept
{
    const double bounded = std::clamp(amount, 0.0, 1.0);
    return bounded * bounded * (3.0 - (2.0 * bounded));
}

double wind_zone_weight(const wind_zone_state& zone, vec3 point) noexcept
{
    const vec3 local = inverse_rotate_euler(point - zone.centre, zone.rotation_degrees);
    const vec3 half = zone.dimensions * 0.5;
    const double normalized_x = std::abs(local.x) / half.x;
    const double normalized_y = std::abs(local.y) / half.y;
    const double normalized_z = std::abs(local.z) / half.z;
    const double edge = std::max({ normalized_x, normalized_y, normalized_z });
    if (edge >= 1.0) {
        return 0.0;
    }
    // The inner 80% is uniform. The outer 20% is feathered so a moving path
    // cannot cross from still air to full wind in one sample.
    return edge <= 0.8 ? 1.0 : smoothstep((1.0 - edge) / 0.2);
}

vec3 turbulence_vector(
    std::uint64_t seed,
    double turbulence,
    double reference_speed,
    vec3 point,
    double time_seconds) noexcept
{
    if (turbulence <= 0.0) {
        return {};
    }
    const double amplitude = std::max(reference_speed, 1.0) * turbulence * 0.12;
    const double spatial = (point.x * 0.071) + (point.y * 0.047) + (point.z * 0.059);
    return {
        smooth_hashed_noise(seed, spatial + (time_seconds * 0.61)) * amplitude,
        smooth_hashed_noise(seed + 1U, (spatial * 1.31) + (time_seconds * 0.43)) * amplitude,
        smooth_hashed_noise(seed + 2U, (spatial * 0.83) + (time_seconds * 0.79)) * amplitude
    };
}

atmosphere_sample sample_atmosphere(
    const atmosphere_state& atmosphere,
    vec3 point,
    double time_seconds) noexcept
{
    atmosphere_sample result{
        atmosphere.temperature_celsius,
        atmosphere.relative_humidity_percent,
        atmosphere.pressure_kilopascals,
        atmosphere.wind
    };
    for (const atmosphere_layer_state& layer : atmosphere.layers) {
        if (point.y >= layer.base_altitude_metres && point.y < layer.top_altitude_metres) {
            result.temperature_celsius = layer.temperature_celsius;
            result.relative_humidity_percent = layer.relative_humidity_percent;
            result.pressure_kilopascals = layer.pressure_kilopascals;
            break;
        }
    }

    result.wind = result.wind + turbulence_vector(
        atmosphere.noise_seed,
        atmosphere.turbulence,
        length(atmosphere.wind),
        point,
        time_seconds);
    for (const wind_zone_state& zone : atmosphere.wind_zones) {
        const double weight = wind_zone_weight(zone, point);
        if (weight <= 0.0) {
            continue;
        }
        const vec3 local_wind = zone.velocity + turbulence_vector(
            zone.noise_seed,
            zone.turbulence,
            length(zone.velocity),
            point,
            time_seconds);
        result.wind = result.wind + (local_wind * weight);
    }
    return result;
}

struct propagation_integral {
    double travel_time_seconds{};
    double absorption_exponent{};
    double distance_metres{};
};

propagation_integral integrate_propagation_segment(
    const atmosphere_state& atmosphere,
    vec3 start,
    vec3 end,
    double frequency_hz,
    double receive_time,
    std::uint32_t segment_count) noexcept
{
    const vec3 delta = end - start;
    const double distance = length(delta);
    if (distance <= 1.0e-12) {
        return {};
    }
    const vec3 direction = delta * (1.0 / distance);
    const std::uint32_t segments = std::clamp(segment_count, 1U, maximum_atmosphere_integration_segments);
    const double segment_length = distance / static_cast<double>(segments);
    propagation_integral result{};
    result.distance_metres = distance;
    for (std::uint32_t index = 0U; index < segments; ++index) {
        const double amount = (static_cast<double>(index) + 0.5) / static_cast<double>(segments);
        const vec3 point = start + (delta * amount);
        const atmosphere_sample local = sample_atmosphere(atmosphere, point, receive_time);
        const double local_speed = speed_of_sound(local.temperature_celsius, local.relative_humidity_percent);
        const double effective_speed = std::clamp(
            local_speed + dot(local.wind, direction),
            minimum_effective_sound_speed,
            local_speed * 1.8);
        result.travel_time_seconds += segment_length / effective_speed;
        result.absorption_exponent += air_absorption_per_metre(
            local.relative_humidity_percent,
            local.pressure_kilopascals,
            frequency_hz) * segment_length;
    }
    return result;
}

template <std::size_t PointCount>
propagation_integral integrate_propagation_path(
    const atmosphere_state& atmosphere,
    const std::array<vec3, PointCount>& points,
    double frequency_hz,
    double receive_time) noexcept
{
    static_assert(PointCount >= 2U);
    std::array<double, PointCount - 1U> distances{};
    double total_distance = 0.0;
    for (std::size_t index = 0U; index + 1U < PointCount; ++index) {
        distances[index] = length(points[index + 1U] - points[index]);
        total_distance += distances[index];
    }
    if (total_distance <= 1.0e-12) {
        return {};
    }

    propagation_integral result{};
    for (std::size_t index = 0U; index + 1U < PointCount; ++index) {
        const auto allocated = static_cast<std::uint32_t>(std::max(
            1.0,
            std::round(static_cast<double>(atmosphere.integration_segments) * distances[index] / total_distance)));
        const propagation_integral segment = integrate_propagation_segment(
            atmosphere,
            points[index],
            points[index + 1U],
            frequency_hz,
            receive_time,
            allocated);
        result.travel_time_seconds += segment.travel_time_seconds;
        result.absorption_exponent += segment.absorption_exponent;
        result.distance_metres += segment.distance_metres;
    }
    return result;
}

template <std::size_t PointCount>
propagation_integral integrate_propagation_path_preview(
    const atmosphere_state& atmosphere,
    const std::array<vec3, PointCount>& points,
    double frequency_hz,
    double receive_time,
    const atmosphere_sample* shared_atmosphere = nullptr) noexcept
{
    static_assert(PointCount >= 2U);
    std::array<double, PointCount - 1U> distances{};
    vec3 weighted_midpoint{};
    double total_distance = 0.0;
    for (std::size_t index = 0U; index + 1U < PointCount; ++index) {
        const vec3 delta = points[index + 1U] - points[index];
        const double distance = length(delta);
        distances[index] = distance;
        total_distance += distance;
        weighted_midpoint = weighted_midpoint + ((points[index] + (delta * 0.5)) * distance);
    }
    if (total_distance <= 1.0e-12) {
        return {};
    }

    const atmosphere_sample local = shared_atmosphere != nullptr
        ? *shared_atmosphere
        : sample_atmosphere(
            atmosphere,
            weighted_midpoint * (1.0 / total_distance),
            receive_time);
    const double local_speed = speed_of_sound(local.temperature_celsius, local.relative_humidity_percent);
    propagation_integral result{};
    result.distance_metres = total_distance;
    result.absorption_exponent = air_absorption_per_metre(
        local.relative_humidity_percent,
        local.pressure_kilopascals,
        frequency_hz) * total_distance;
    for (std::size_t index = 0U; index + 1U < PointCount; ++index) {
        const double distance = distances[index];
        if (distance <= 1.0e-12) {
            continue;
        }
        const vec3 direction = (points[index + 1U] - points[index]) * (1.0 / distance);
        const double effective_speed = std::clamp(
            local_speed + dot(local.wind, direction),
            minimum_effective_sound_speed,
            local_speed * 1.8);
        result.travel_time_seconds += distance / effective_speed;
    }
    return result;
}

double material_band_value(
    const acoustic_material_state& material,
    const std::array<double, PG_ACOUSTIC_BAND_COUNT>& values,
    double frequency_hz) noexcept
{
    const double frequency = std::max(frequency_hz, 1.0);
    if (frequency <= material.octave_band_centres_hz.front()) {
        return values.front();
    }
    if (frequency >= material.octave_band_centres_hz.back()) {
        return values.back();
    }
    for (std::size_t index = 1U; index < material.octave_band_centres_hz.size(); ++index) {
        const double upper = material.octave_band_centres_hz[index];
        if (frequency <= upper) {
            const double lower = material.octave_band_centres_hz[index - 1U];
            const double amount = (std::log2(frequency) - std::log2(lower)) / (std::log2(upper) - std::log2(lower));
            return std::lerp(values[index - 1U], values[index], std::clamp(amount, 0.0, 1.0));
        }
    }
    return values.back();
}

double component(vec3 value, std::size_t axis) noexcept
{
    return axis == 0U ? value.x : (axis == 1U ? value.y : value.z);
}

void expand(axis_aligned_bounds& bounds, vec3 point) noexcept
{
    bounds.minimum.x = std::min(bounds.minimum.x, point.x);
    bounds.minimum.y = std::min(bounds.minimum.y, point.y);
    bounds.minimum.z = std::min(bounds.minimum.z, point.z);
    bounds.maximum.x = std::max(bounds.maximum.x, point.x);
    bounds.maximum.y = std::max(bounds.maximum.y, point.y);
    bounds.maximum.z = std::max(bounds.maximum.z, point.z);
}

void expand(axis_aligned_bounds& bounds, const axis_aligned_bounds& other) noexcept
{
    expand(bounds, other.minimum);
    expand(bounds, other.maximum);
}

axis_aligned_bounds triangle_bounds(vec3 a, vec3 b, vec3 c) noexcept
{
    axis_aligned_bounds bounds{};
    expand(bounds, a);
    expand(bounds, b);
    expand(bounds, c);
    return bounds;
}

using mesh_plane_key = std::array<std::int64_t, 6>;

struct triangle_plane_record {
    mesh_plane_key key{};
    std::uint32_t triangle_index{};
    double area_square_metres{};
};

struct mesh_plane_group {
    mesh_plane_key key{};
    std::size_t first{};
    std::size_t count{};
    vec3 normal{ 0.0, 0.0, 1.0 };
    double plane_offset{};
    axis_aligned_bounds bounds{};
    double area_square_metres{};
    std::uint32_t source_mesh_index{};
    std::uint32_t source_material_index{};
    std::uint32_t material_index{};
};

std::int64_t quantized_plane_component(double value, double scale) noexcept
{
    const double scaled = value * scale;
    constexpr auto integer_limit = std::numeric_limits<std::int64_t>::max() / 2;
    const double bounded = std::clamp(
        scaled,
        -static_cast<double>(integer_limit),
        static_cast<double>(integer_limit));
    return static_cast<std::int64_t>(std::llround(bounded));
}

mesh_plane_key triangle_plane_key(
    const mesh_triangle& triangle,
    vec3& canonical_normal,
    double& plane_offset,
    double& area_square_metres) noexcept
{
    const vec3 area_normal = cross(triangle.b - triangle.a, triangle.c - triangle.a);
    const double twice_area = length(area_normal);
    canonical_normal = normalized(area_normal);
    constexpr double orientation_tolerance = 1.0e-12;
    const bool reverse = canonical_normal.x < -orientation_tolerance ||
        (std::abs(canonical_normal.x) <= orientation_tolerance && canonical_normal.y < -orientation_tolerance) ||
        (std::abs(canonical_normal.x) <= orientation_tolerance &&
            std::abs(canonical_normal.y) <= orientation_tolerance && canonical_normal.z < 0.0);
    if (reverse) {
        canonical_normal = canonical_normal * -1.0;
    }
    plane_offset = dot(canonical_normal, triangle.a);
    area_square_metres = twice_area * 0.5;
    return {
        quantized_plane_component(canonical_normal.x, 1000000.0),
        quantized_plane_component(canonical_normal.y, 1000000.0),
        quantized_plane_component(canonical_normal.z, 1000000.0),
        quantized_plane_component(plane_offset, 10000.0),
        static_cast<std::int64_t>(triangle.source_mesh_index),
        static_cast<std::int64_t>(triangle.material_index)
    };
}

void build_mesh_reflection_patches(mesh_geometry_state& mesh)
{
    std::vector<triangle_plane_record> records;
    records.reserve(mesh.triangles.size());
    for (std::size_t index = 0U; index < mesh.triangles.size(); ++index) {
        vec3 normal{};
        double plane_offset = 0.0;
        double area_square_metres = 0.0;
        records.push_back({
            triangle_plane_key(mesh.triangles[index], normal, plane_offset, area_square_metres),
            static_cast<std::uint32_t>(index),
            area_square_metres
        });
    }
    std::stable_sort(records.begin(), records.end(), [](const auto& left, const auto& right) {
        if (left.key != right.key) {
            return left.key < right.key;
        }
        return left.triangle_index < right.triangle_index;
    });

    std::vector<mesh_plane_group> groups;
    groups.reserve(std::min(records.size(), maximum_mesh_reflection_patches));
    for (std::size_t first = 0U; first < records.size();) {
        std::size_t end = first + 1U;
        while (end < records.size() && records[end].key == records[first].key) {
            ++end;
        }
        const mesh_triangle& first_triangle = mesh.triangles[records[first].triangle_index];
        vec3 normal{};
        double plane_offset = 0.0;
        double ignored_area = 0.0;
        (void)triangle_plane_key(first_triangle, normal, plane_offset, ignored_area);
        mesh_plane_group group{};
        group.key = records[first].key;
        group.first = first;
        group.count = end - first;
        group.normal = normal;
        group.plane_offset = plane_offset;
        group.source_mesh_index = first_triangle.source_mesh_index;
        group.source_material_index = first_triangle.source_material_index;
        group.material_index = first_triangle.material_index;
        for (std::size_t record_index = first; record_index < end; ++record_index) {
            const triangle_plane_record& record = records[record_index];
            group.area_square_metres += record.area_square_metres;
            expand(group.bounds, mesh.triangles[record.triangle_index].bounds);
        }
        groups.push_back(group);
        first = end;
    }

    std::vector<std::size_t> ranked_groups(groups.size());
    std::iota(ranked_groups.begin(), ranked_groups.end(), 0U);
    std::stable_sort(ranked_groups.begin(), ranked_groups.end(), [&groups](std::size_t left, std::size_t right) {
        if (groups[left].area_square_metres != groups[right].area_square_metres) {
            return groups[left].area_square_metres > groups[right].area_square_metres;
        }
        return groups[left].key < groups[right].key;
    });

    std::vector<std::size_t> selected_groups;
    if (groups.size() <= maximum_mesh_reflection_patches) {
        selected_groups = std::move(ranked_groups);
    } else {
        constexpr std::size_t largest_surface_quota = maximum_mesh_reflection_patches / 2U;
        std::vector<bool> selected(groups.size());
        selected_groups.reserve(maximum_mesh_reflection_patches);
        for (std::size_t index = 0U; index < largest_surface_quota; ++index) {
            selected[ranked_groups[index]] = true;
            selected_groups.push_back(ranked_groups[index]);
        }

        std::vector<std::size_t> remaining_groups;
        remaining_groups.reserve(groups.size() - largest_surface_quota);
        for (std::size_t index = 0U; index < groups.size(); ++index) {
            if (!selected[index]) {
                remaining_groups.push_back(index);
            }
        }
        const std::size_t coverage_quota = maximum_mesh_reflection_patches - selected_groups.size();
        for (std::size_t slot = 0U; slot < coverage_quota; ++slot) {
            const std::size_t sample_index = std::min(
                ((2U * slot + 1U) * remaining_groups.size()) / (2U * coverage_quota),
                remaining_groups.size() - 1U);
            selected_groups.push_back(remaining_groups[sample_index]);
        }
    }
    std::sort(selected_groups.begin(), selected_groups.end());

    mesh.reflection_patches.reserve(selected_groups.size());
    for (std::size_t selected_index = 0U; selected_index < selected_groups.size(); ++selected_index) {
        const mesh_plane_group& group = groups[selected_groups[selected_index]];
        mesh.reflection_patches.push_back({
            group.normal * group.plane_offset,
            group.normal,
            group.bounds,
            group.area_square_metres,
            group.source_mesh_index,
            group.source_material_index,
            group.material_index
        });
        for (std::size_t record_index = group.first; record_index < group.first + group.count; ++record_index) {
            mesh.triangles[records[record_index].triangle_index].reflection_patch_index =
                static_cast<std::uint32_t>(selected_index);
        }
    }
}

std::string normalized_format_hint(const char* value)
{
    std::string result = value == nullptr ? std::string{} : std::string(value);
    if (!result.empty() && result.front() == '.') {
        result.erase(result.begin());
    }
    std::transform(result.begin(), result.end(), result.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    return result;
}

std::uint32_t build_mesh_bvh(mesh_geometry_state& mesh, std::uint32_t first, std::uint32_t count)
{
    const std::uint32_t node_index = static_cast<std::uint32_t>(mesh.bvh.size());
    mesh.bvh.emplace_back();

    axis_aligned_bounds bounds{};
    axis_aligned_bounds centroid_bounds{};
    for (std::uint32_t offset = 0U; offset < count; ++offset) {
        const mesh_triangle& triangle = mesh.triangles[mesh.triangle_indices[first + offset]];
        expand(bounds, triangle.bounds);
        expand(centroid_bounds, triangle.centroid);
    }
    mesh.bvh[node_index].bounds = bounds;

    if (count <= mesh_bvh_leaf_triangles) {
        mesh.bvh[node_index].first = first;
        mesh.bvh[node_index].count = count;
        return node_index;
    }

    const vec3 extent = centroid_bounds.maximum - centroid_bounds.minimum;
    std::size_t split_axis = 0U;
    if (extent.y > extent.x) {
        split_axis = 1U;
    }
    if (component(extent, 2U) > component(extent, split_axis)) {
        split_axis = 2U;
    }
    const std::uint32_t left_count = count / 2U;
    auto begin = mesh.triangle_indices.begin() + first;
    auto middle = begin + left_count;
    auto end = begin + count;
    std::nth_element(begin, middle, end, [&mesh, split_axis](std::uint32_t left, std::uint32_t right) {
        return component(mesh.triangles[left].centroid, split_axis) < component(mesh.triangles[right].centroid, split_axis);
    });

    const std::uint32_t left = build_mesh_bvh(mesh, first, left_count);
    const std::uint32_t right = build_mesh_bvh(mesh, first + left_count, count - left_count);
    mesh.bvh[node_index].left = left;
    mesh.bvh[node_index].right = right;
    return node_index;
}

bool segment_intersects_bounds(const axis_aligned_bounds& bounds, vec3 start, vec3 end) noexcept
{
    const std::array<double, 3> origin{ start.x, start.y, start.z };
    const std::array<double, 3> direction{ end.x - start.x, end.y - start.y, end.z - start.z };
    const std::array<double, 3> minimum{ bounds.minimum.x, bounds.minimum.y, bounds.minimum.z };
    const std::array<double, 3> maximum{ bounds.maximum.x, bounds.maximum.y, bounds.maximum.z };
    double minimum_amount = 1.0e-7;
    double maximum_amount = 1.0 - 1.0e-7;
    for (std::size_t axis = 0U; axis < 3U; ++axis) {
        if (std::abs(direction[axis]) <= 1.0e-12) {
            if (origin[axis] < minimum[axis] || origin[axis] > maximum[axis]) {
                return false;
            }
            continue;
        }
        double near_amount = (minimum[axis] - origin[axis]) / direction[axis];
        double far_amount = (maximum[axis] - origin[axis]) / direction[axis];
        if (near_amount > far_amount) {
            std::swap(near_amount, far_amount);
        }
        minimum_amount = std::max(minimum_amount, near_amount);
        maximum_amount = std::min(maximum_amount, far_amount);
        if (minimum_amount > maximum_amount) {
            return false;
        }
    }
    return maximum_amount >= minimum_amount;
}

bool segment_intersects_triangle(
    const mesh_triangle& triangle,
    vec3 start,
    vec3 end,
    double& intersection_amount) noexcept
{
    const vec3 direction = end - start;
    const vec3 edge_one = triangle.b - triangle.a;
    const vec3 edge_two = triangle.c - triangle.a;
    const vec3 perpendicular = cross(direction, edge_two);
    const double determinant = dot(edge_one, perpendicular);
    if (std::abs(determinant) <= 1.0e-12) {
        return false;
    }
    const double inverse_determinant = 1.0 / determinant;
    const vec3 offset = start - triangle.a;
    const double u = dot(offset, perpendicular) * inverse_determinant;
    if (u < 0.0 || u > 1.0) {
        return false;
    }
    const vec3 cross_offset = cross(offset, edge_one);
    const double v = dot(direction, cross_offset) * inverse_determinant;
    if (v < 0.0 || u + v > 1.0) {
        return false;
    }
    const double amount = dot(edge_two, cross_offset) * inverse_determinant;
    if (amount <= 1.0e-7 || amount >= 1.0 - 1.0e-7) {
        return false;
    }
    intersection_amount = amount;
    return true;
}

bool segment_mesh_material(
    const mesh_geometry_state& mesh,
    vec3 start,
    vec3 end,
    std::uint32_t& material_index) noexcept
{
    if (mesh.bvh.empty() || !segment_intersects_bounds(mesh.bounds, start, end)) {
        return false;
    }

    std::array<std::uint32_t, 128> stack{};
    std::size_t stack_size = 1U;
    stack[0] = 0U;
    bool intersected = false;
    double nearest_amount = std::numeric_limits<double>::infinity();
    while (stack_size > 0U) {
        const mesh_bvh_node& node = mesh.bvh[stack[--stack_size]];
        if (!segment_intersects_bounds(node.bounds, start, end)) {
            continue;
        }
        if (node.count > 0U) {
            for (std::uint32_t offset = 0U; offset < node.count; ++offset) {
                const mesh_triangle& triangle = mesh.triangles[mesh.triangle_indices[node.first + offset]];
                double amount = 0.0;
                if (segment_intersects_triangle(triangle, start, end, amount) && amount < nearest_amount) {
                    intersected = true;
                    nearest_amount = amount;
                    material_index = triangle.material_index;
                }
            }
            continue;
        }
        if (stack_size + 2U > stack.size()) {
            // The builder creates a balanced tree whose normal depth is far
            // below this fixed audio-thread stack. Treat unexpected overflow
            // conservatively as occluded instead of allocating or recursing.
            material_index = mesh.material_index;
            return true;
        }
        stack[stack_size++] = node.left;
        stack[stack_size++] = node.right;
    }
    return intersected;
}

bool bounds_contain_point(const axis_aligned_bounds& bounds, vec3 point, double tolerance = 2.0e-4) noexcept
{
    return point.x >= bounds.minimum.x - tolerance && point.x <= bounds.maximum.x + tolerance &&
        point.y >= bounds.minimum.y - tolerance && point.y <= bounds.maximum.y + tolerance &&
        point.z >= bounds.minimum.z - tolerance && point.z <= bounds.maximum.z + tolerance;
}

bool point_within_triangle(const mesh_triangle& triangle, vec3 point) noexcept
{
    const vec3 edge_zero = triangle.b - triangle.a;
    const vec3 edge_one = triangle.c - triangle.a;
    const vec3 offset = point - triangle.a;
    const vec3 plane_normal = normalized(cross(edge_zero, edge_one));
    if (std::abs(dot(offset, plane_normal)) > 2.0e-4) {
        return false;
    }
    const double dot_zero_zero = dot(edge_zero, edge_zero);
    const double dot_zero_one = dot(edge_zero, edge_one);
    const double dot_one_one = dot(edge_one, edge_one);
    const double dot_offset_zero = dot(offset, edge_zero);
    const double dot_offset_one = dot(offset, edge_one);
    const double denominator = (dot_zero_zero * dot_one_one) - (dot_zero_one * dot_zero_one);
    if (std::abs(denominator) <= 1.0e-18) {
        return false;
    }
    const double u = ((dot_one_one * dot_offset_zero) - (dot_zero_one * dot_offset_one)) / denominator;
    const double v = ((dot_zero_zero * dot_offset_one) - (dot_zero_one * dot_offset_zero)) / denominator;
    constexpr double edge_tolerance = 1.0e-8;
    return u >= -edge_tolerance && v >= -edge_tolerance && u + v <= 1.0 + edge_tolerance;
}

bool point_on_mesh_reflection_patch(
    const mesh_geometry_state& mesh,
    std::uint32_t patch_index,
    vec3 point) noexcept
{
    if (patch_index >= mesh.reflection_patches.size() || mesh.bvh.empty() ||
        !bounds_contain_point(mesh.reflection_patches[patch_index].bounds, point)) {
        return false;
    }

    std::array<std::uint32_t, 128> stack{};
    std::size_t stack_size = 1U;
    stack[0] = 0U;
    while (stack_size > 0U) {
        const mesh_bvh_node& node = mesh.bvh[stack[--stack_size]];
        if (!bounds_contain_point(node.bounds, point)) {
            continue;
        }
        if (node.count > 0U) {
            for (std::uint32_t offset = 0U; offset < node.count; ++offset) {
                const mesh_triangle& triangle = mesh.triangles[mesh.triangle_indices[node.first + offset]];
                if (triangle.reflection_patch_index == patch_index &&
                    bounds_contain_point(triangle.bounds, point) && point_within_triangle(triangle, point)) {
                    return true;
                }
            }
            continue;
        }
        if (stack_size + 2U > stack.size()) {
            return false;
        }
        stack[stack_size++] = node.left;
        stack[stack_size++] = node.right;
    }
    return false;
}

bool segment_intersects_plane(const geometry_state& geometry, vec3 start, vec3 end) noexcept
{
    const vec3 local_start = inverse_rotate_euler(start - geometry.centre, geometry.rotation_degrees);
    const vec3 local_end = inverse_rotate_euler(end - geometry.centre, geometry.rotation_degrees);
    const vec3 direction = local_end - local_start;
    if (std::abs(direction.z) <= 1.0e-12) {
        return false;
    }
    const double amount = -local_start.z / direction.z;
    if (amount <= 1.0e-7 || amount >= 1.0 - 1.0e-7) {
        return false;
    }
    const vec3 point = local_start + (direction * amount);
    return std::abs(point.x) <= (geometry.dimensions.x * 0.5) + 1.0e-9 &&
        std::abs(point.y) <= (geometry.dimensions.y * 0.5) + 1.0e-9;
}

bool segment_intersects_box(const geometry_state& geometry, vec3 start, vec3 end) noexcept
{
    const vec3 local_start = inverse_rotate_euler(start - geometry.centre, geometry.rotation_degrees);
    const vec3 local_end = inverse_rotate_euler(end - geometry.centre, geometry.rotation_degrees);
    const std::array<double, 3> origin{ local_start.x, local_start.y, local_start.z };
    const std::array<double, 3> direction{ local_end.x - local_start.x, local_end.y - local_start.y, local_end.z - local_start.z };
    const std::array<double, 3> half_dimensions{ geometry.dimensions.x * 0.5, geometry.dimensions.y * 0.5, geometry.dimensions.z * 0.5 };
    double minimum_amount = 1.0e-7;
    double maximum_amount = 1.0 - 1.0e-7;
    for (std::size_t axis = 0U; axis < 3U; ++axis) {
        if (std::abs(direction[axis]) <= 1.0e-12) {
            if (origin[axis] < -half_dimensions[axis] || origin[axis] > half_dimensions[axis]) {
                return false;
            }
            continue;
        }
        double near_amount = (-half_dimensions[axis] - origin[axis]) / direction[axis];
        double far_amount = (half_dimensions[axis] - origin[axis]) / direction[axis];
        if (near_amount > far_amount) {
            std::swap(near_amount, far_amount);
        }
        minimum_amount = std::max(minimum_amount, near_amount);
        maximum_amount = std::min(maximum_amount, far_amount);
        if (minimum_amount > maximum_amount) {
            return false;
        }
    }
    return maximum_amount >= minimum_amount;
}

bool segment_intersects_geometry(const geometry_state& geometry, vec3 start, vec3 end) noexcept
{
    return geometry.kind == PG_GEOMETRY_BOX
        ? segment_intersects_box(geometry, start, end)
        : segment_intersects_plane(geometry, start, end);
}

double occlusion_transmission_gain(
    const pg_engine& engine,
    vec3 start,
    vec3 end,
    double frequency_hz,
    std::size_t excluded_geometry = std::numeric_limits<std::size_t>::max()) noexcept
{
    double gain = 1.0;
    for (std::size_t index = 0U; index < engine.geometries.size(); ++index) {
        const geometry_state& geometry = engine.geometries[index];
        if (index == excluded_geometry || !geometry.enabled || !geometry.occludes ||
            geometry.material_index >= engine.acoustic_materials.size() || !segment_intersects_geometry(geometry, start, end)) {
            continue;
        }
        const acoustic_material_state& material = engine.acoustic_materials[geometry.material_index];
        const double transmission = material_band_value(material, material.transmission, frequency_hz);
        gain *= std::sqrt(std::clamp(transmission, 0.0, 1.0));
        if (gain <= 1.0e-12) {
            return 0.0;
        }
    }
    for (const mesh_geometry_state& geometry : engine.mesh_geometries) {
        std::uint32_t material_index = geometry.material_index;
        if (!geometry.enabled || !geometry.occludes ||
            !segment_mesh_material(geometry, start, end, material_index) ||
            material_index >= engine.acoustic_materials.size()) {
            continue;
        }
        const acoustic_material_state& material = engine.acoustic_materials[material_index];
        const double transmission = material_band_value(material, material.transmission, frequency_hz);
        gain *= std::sqrt(std::clamp(transmission, 0.0, 1.0));
        if (gain <= 1.0e-12) {
            return 0.0;
        }
    }
    return gain;
}

struct reflection_surface {
    vec3 point{};
    vec3 normal{ 0.0, 0.0, 1.0 };
    vec3 tangent_u{ 1.0, 0.0, 0.0 };
    vec3 tangent_v{ 0.0, 1.0, 0.0 };
    double half_extent_u{ 0.5 };
    double half_extent_v{ 0.5 };
};

std::size_t reflection_surface_count(const geometry_state& geometry) noexcept
{
    return geometry.kind == PG_GEOMETRY_BOX ? 6U : 1U;
}

reflection_surface geometry_reflection_surface(const geometry_state& geometry, std::size_t surface_index) noexcept
{
    const std::array<vec3, 3> axes{
        rotate_euler({ 1.0, 0.0, 0.0 }, geometry.rotation_degrees),
        rotate_euler({ 0.0, 1.0, 0.0 }, geometry.rotation_degrees),
        rotate_euler({ 0.0, 0.0, 1.0 }, geometry.rotation_degrees)
    };
    const std::array<double, 3> half_dimensions{
        geometry.dimensions.x * 0.5,
        geometry.dimensions.y * 0.5,
        geometry.dimensions.z * 0.5
    };
    if (geometry.kind == PG_GEOMETRY_PLANE) {
        return { geometry.centre, axes[2], axes[0], axes[1], half_dimensions[0], half_dimensions[1] };
    }

    const std::size_t axis = std::min<std::size_t>(surface_index / 2U, 2U);
    const double side = (surface_index % 2U) == 0U ? -1.0 : 1.0;
    const vec3 normal = axes[axis] * side;
    const std::size_t tangent_u_axis = (axis + 1U) % 3U;
    const std::size_t tangent_v_axis = (axis + 2U) % 3U;
    return {
        geometry.centre + (normal * half_dimensions[axis]),
        normal,
        axes[tangent_u_axis],
        axes[tangent_v_axis],
        half_dimensions[tangent_u_axis],
        half_dimensions[tangent_v_axis]
    };
}

bool point_within_surface(const reflection_surface& surface, vec3 point) noexcept
{
    const vec3 offset = point - surface.point;
    return std::abs(dot(offset, surface.tangent_u)) <= surface.half_extent_u + 1.0e-8 &&
        std::abs(dot(offset, surface.tangent_v)) <= surface.half_extent_v + 1.0e-8;
}

vec3 sample_motion_path(
    pg_motion_path_kind motion_path_kind,
    const std::vector<keyframe_state>& keyframes,
    double time_seconds,
    vec3 fallback = {}) noexcept
{
    if (keyframes.empty()) {
        return fallback;
    }
    if (time_seconds <= keyframes.front().time_seconds) {
        return keyframes.front().position;
    }
    if (time_seconds >= keyframes.back().time_seconds) {
        return keyframes.back().position;
    }

    const auto upper = std::upper_bound(
        keyframes.begin(),
        keyframes.end(),
        time_seconds,
        [](double time, const keyframe_state& keyframe) { return time < keyframe.time_seconds; });
    const auto lower = upper - 1;
    const double duration = upper->time_seconds - lower->time_seconds;
    const double amount = duration > 0.0 ? (time_seconds - lower->time_seconds) / duration : 0.0;
    if (motion_path_kind == PG_PATH_CATMULL_ROM || motion_path_kind == PG_PATH_CUBIC_BEZIER_APPROXIMATION) {
        const std::size_t right_index = static_cast<std::size_t>(std::distance(keyframes.begin(), upper));
        const vec3 p0 = right_index >= 2U ? keyframes[right_index - 2U].position : lower->position;
        const vec3 p1 = lower->position;
        const vec3 p2 = upper->position;
        const vec3 p3 = right_index + 1U < keyframes.size() ? keyframes[right_index + 1U].position : upper->position;
        const double amount_squared = amount * amount;
        const double amount_cubed = amount_squared * amount;
        return (p1 * 2.0 +
            (p2 - p0) * amount +
            ((p0 * 2.0) - (p1 * 5.0) + (p2 * 4.0) - p3) * amount_squared +
            ((p1 * 3.0) - p0 - (p2 * 3.0) + p3) * amount_cubed) * 0.5;
    }
    return lower->position + ((upper->position - lower->position) * amount);
}

vec3 sample_motion(const emitter_state& emitter, double time_seconds) noexcept
{
    return sample_motion_path(emitter.motion_path_kind, emitter.keyframes, time_seconds);
}

vec3 sample_motion(const receiver_state& receiver, double time_seconds) noexcept
{
    return sample_motion_path(receiver.motion_path_kind, receiver.keyframes, time_seconds, receiver.centre);
}

double fractional(double value) noexcept
{
    return value - std::floor(value);
}

double poly_blep(double phase, double phase_increment) noexcept
{
    if (phase_increment <= 0.0) {
        return 0.0;
    }
    if (phase < phase_increment) {
        const double amount = phase / phase_increment;
        return amount + amount - (amount * amount) - 1.0;
    }
    if (phase > 1.0 - phase_increment) {
        const double amount = (phase - 1.0) / phase_increment;
        return (amount * amount) + amount + amount + 1.0;
    }
    return 0.0;
}

std::uint64_t mix_hash(std::uint64_t value) noexcept
{
    value ^= value >> 30U;
    value *= 0xbf58476d1ce4e5b9ULL;
    value ^= value >> 27U;
    value *= 0x94d049bb133111ebULL;
    return value ^ (value >> 31U);
}

double hashed_noise(std::uint64_t seed, std::int64_t index) noexcept
{
    const std::uint64_t mixed = mix_hash(seed ^ std::bit_cast<std::uint64_t>(index));
    constexpr double scale = 1.0 / static_cast<double>(std::numeric_limits<std::uint64_t>::max());
    return (static_cast<double>(mixed) * scale * 2.0) - 1.0;
}

double smooth_hashed_noise(std::uint64_t seed, double position) noexcept
{
    const auto left_index = static_cast<std::int64_t>(std::floor(position));
    const double amount = fractional(position);
    const double smooth_amount = amount * amount * (3.0 - (2.0 * amount));
    return std::lerp(hashed_noise(seed, left_index), hashed_noise(seed, left_index + 1), smooth_amount);
}

double primitive_sample(const emitter_state& emitter, double emission_time, std::uint32_t sample_rate) noexcept
{
    const double start_time = emitter.keyframes.empty() ? 0.0 : emitter.keyframes.front().time_seconds;
    const double end_time = emitter.keyframes.empty() ? std::numeric_limits<double>::infinity() : emitter.keyframes.back().time_seconds;
    if (emission_time < start_time || emission_time > end_time) {
        return 0.0;
    }

    double envelope = 1.0;
    if (emitter.attack_seconds > 0.0) {
        envelope *= std::clamp((emission_time - start_time) / emitter.attack_seconds, 0.0, 1.0);
    }
    if (emitter.release_seconds > 0.0) {
        envelope *= std::clamp((end_time - emission_time) / emitter.release_seconds, 0.0, 1.0);
    }

    const double local_time = emission_time - start_time;
    const double cycles = (local_time * emitter.frequency_hz) + (emitter.phase_radians / (2.0 * std::numbers::pi));
    const double phase = fractional(cycles);
    const double increment = std::clamp(emitter.frequency_hz / static_cast<double>(sample_rate), 1.0e-9, 0.49);
    double value = 0.0;
    switch (emitter.waveform) {
    case PG_WAVE_TRIANGLE:
        value = (2.0 / std::numbers::pi) * std::asin(std::sin(2.0 * std::numbers::pi * phase));
        break;
    case PG_WAVE_SAW:
        value = (2.0 * phase) - 1.0 - poly_blep(phase, increment);
        break;
    case PG_WAVE_SQUARE:
        value = phase < 0.5 ? 1.0 : -1.0;
        value += poly_blep(phase, increment);
        value -= poly_blep(fractional(phase + 0.5), increment);
        break;
    case PG_WAVE_PULSE:
        value = phase < emitter.pulse_width ? 1.0 : -1.0;
        value += poly_blep(phase, increment);
        value -= poly_blep(fractional(phase + (1.0 - emitter.pulse_width)), increment);
        break;
    case PG_WAVE_WHITE_NOISE: {
        const auto sample_index = static_cast<std::int64_t>(std::floor(local_time * static_cast<double>(sample_rate)));
        value = hashed_noise(emitter.noise_seed, sample_index);
        break;
    }
    case PG_WAVE_PINK_NOISE: {
        const auto sample_index = static_cast<std::int64_t>(std::floor(local_time * static_cast<double>(sample_rate)));
        double weight = 1.0;
        double weight_sum = 0.0;
        for (int octave = 0; octave < 7; ++octave) {
            value += hashed_noise(emitter.noise_seed + static_cast<std::uint64_t>(octave), sample_index >> octave) * weight;
            weight_sum += weight;
            weight *= 0.72;
        }
        value /= weight_sum;
        break;
    }
    case PG_WAVE_BROWN_NOISE:
        value = (0.68 * smooth_hashed_noise(emitter.noise_seed, local_time * static_cast<double>(sample_rate) / 64.0)) +
            (0.32 * smooth_hashed_noise(emitter.noise_seed + 1U, local_time * static_cast<double>(sample_rate) / 257.0));
        break;
    case PG_WAVE_IMPULSE:
        value = phase < increment ? 1.0 : 0.0;
        break;
    case PG_WAVE_ADDITIVE: {
        double normalization = 0.0;
        for (const auto& partial : emitter.additive_partials) {
            value += partial.amplitude * std::sin((2.0 * std::numbers::pi * cycles * partial.frequency_ratio) + partial.phase_radians);
            normalization += std::abs(partial.amplitude);
        }
        value /= std::max(1.0, normalization);
        break;
    }
    case PG_WAVE_TURBULENCE:
        value = (0.55 * smooth_hashed_noise(emitter.noise_seed, local_time * 19.0)) +
            (0.28 * smooth_hashed_noise(emitter.noise_seed + 1U, local_time * 53.0)) +
            (0.12 * smooth_hashed_noise(emitter.noise_seed + 2U, local_time * 131.0)) +
            (0.05 * hashed_noise(emitter.noise_seed + 3U, static_cast<std::int64_t>(local_time * sample_rate)));
        break;
    case PG_WAVE_SINE:
    default:
        value = std::sin(2.0 * std::numbers::pi * phase);
        break;
    }

    value *= emitter.amplitude * envelope;
    if (emitter.nonlinear_drive > 0.0) {
        value = std::tanh(value * emitter.nonlinear_drive) / std::tanh(emitter.nonlinear_drive);
    }
    return value;
}

struct delayed_path_sample {
    double value{};
    double distance{};
    vec3 source_position{};
    vec3 arrival_direction{ 0.0, 0.0, 1.0 };
    double diffuse_value{};
};

bool room_reflection_path(
    const room_state& room,
    vec3 source,
    vec3 receiver,
    int reflection_axis,
    bool reflection_high_side,
    vec3& image_source,
    vec3& reflection_point) noexcept
{
    const std::size_t axis = static_cast<std::size_t>(reflection_axis);
    const std::array<double, 3> centre{ room.centre.x, room.centre.y, room.centre.z };
    const std::array<double, 3> dimensions{ room.dimensions.x, room.dimensions.y, room.dimensions.z };
    const std::array<double, 3> source_coordinates{ source.x, source.y, source.z };
    const std::array<double, 3> receiver_coordinates{ receiver.x, receiver.y, receiver.z };
    std::array<double, 3> image_coordinates = source_coordinates;
    const double wall = centre[axis] +
        (reflection_high_side ? dimensions[axis] * 0.5 : -dimensions[axis] * 0.5);
    image_coordinates[axis] = (2.0 * wall) - image_coordinates[axis];
    image_source = { image_coordinates[0], image_coordinates[1], image_coordinates[2] };

    const double denominator = image_coordinates[axis] - receiver_coordinates[axis];
    if (std::abs(denominator) <= 1.0e-12) {
        reflection_point = image_source;
        return false;
    }
    const double amount = (wall - receiver_coordinates[axis]) / denominator;
    if (amount <= 0.0 || amount >= 1.0) {
        reflection_point = image_source;
        return false;
    }
    reflection_point = receiver + ((image_source - receiver) * amount);
    return true;
}

double axis_value(vec3 value, std::size_t axis) noexcept
{
    if (axis == 0U) {
        return value.x;
    }
    if (axis == 1U) {
        return value.y;
    }
    return value.z;
}

vec3 with_axis_value(vec3 value, std::size_t axis, double coordinate) noexcept
{
    if (axis == 0U) {
        value.x = coordinate;
    } else if (axis == 1U) {
        value.y = coordinate;
    } else {
        value.z = coordinate;
    }
    return value;
}

bool point_inside_room(const room_state& room, vec3 point) noexcept
{
    constexpr double tolerance = 1.0e-7;
    for (std::size_t axis = 0U; axis < 3U; ++axis) {
        const double half_extent = axis_value(room.dimensions, axis) * 0.5;
        const double coordinate = axis_value(point, axis);
        const double centre = axis_value(room.centre, axis);
        if (coordinate < centre - half_extent - tolerance || coordinate > centre + half_extent + tolerance) {
            return false;
        }
    }
    return true;
}

bool room_second_order_reflection_path(
    const room_state& room,
    vec3 source,
    vec3 receiver,
    int reflection_axis,
    bool first_high_side,
    vec3& image_source,
    vec3& first_reflection_point,
    vec3& second_reflection_point) noexcept
{
    if (!point_inside_room(room, source) || !point_inside_room(room, receiver)) {
        return false;
    }

    const std::size_t axis = static_cast<std::size_t>(reflection_axis);
    const double centre = axis_value(room.centre, axis);
    const double dimension = axis_value(room.dimensions, axis);
    const double low_wall = centre - (dimension * 0.5);
    const double high_wall = centre + (dimension * 0.5);
    const double source_coordinate = axis_value(source, axis);
    const double receiver_coordinate = axis_value(receiver, axis);
    const double image_coordinate = source_coordinate + (first_high_side ? -2.0 * dimension : 2.0 * dimension);
    image_source = with_axis_value(source, axis, image_coordinate);

    const double denominator = image_coordinate - receiver_coordinate;
    if (std::abs(denominator) <= 1.0e-12) {
        return false;
    }
    const double near_boundary = first_high_side ? low_wall : high_wall;
    const double far_boundary = first_high_side ? low_wall - dimension : high_wall + dimension;
    const double near_amount = (near_boundary - receiver_coordinate) / denominator;
    const double far_amount = (far_boundary - receiver_coordinate) / denominator;
    if (near_amount <= 0.0 || far_amount <= near_amount || far_amount >= 1.0) {
        return false;
    }

    const vec3 near_unfolded = receiver + ((image_source - receiver) * near_amount);
    const vec3 far_unfolded = receiver + ((image_source - receiver) * far_amount);
    first_reflection_point = with_axis_value(far_unfolded, axis, first_high_side ? high_wall : low_wall);
    second_reflection_point = with_axis_value(near_unfolded, axis, first_high_side ? low_wall : high_wall);
    return point_inside_room(room, first_reflection_point) && point_inside_room(room, second_reflection_point);
}

double room_reflection_gain(const room_state& room, double frequency_hz) noexcept
{
    const double frequency = std::max(frequency_hz, 1.0);
    if (frequency <= 250.0) {
        return room.reflection_low;
    }
    if (frequency <= 1000.0) {
        const double amount = std::log2(frequency / 250.0) / 2.0;
        return std::lerp(room.reflection_low, room.reflection_mid, std::clamp(amount, 0.0, 1.0));
    }
    const double amount = std::log2(frequency / 1000.0) / 3.0;
    return std::lerp(room.reflection_mid, room.reflection_high, std::clamp(amount, 0.0, 1.0));
}

bool surface_reflection_point(
    vec3 image_source,
    vec3 receiver,
    const reflection_surface& surface,
    vec3& reflection_point) noexcept
{
    const vec3 image_direction = image_source - receiver;
    const double denominator = dot(image_direction, surface.normal);
    if (std::abs(denominator) <= 1.0e-12) {
        return false;
    }
    const double amount = dot(surface.point - receiver, surface.normal) / denominator;
    if (amount <= 1.0e-7 || amount >= 1.0 - 1.0e-7) {
        return false;
    }
    reflection_point = receiver + (image_direction * amount);
    return true;
}

bool mesh_patch_reflection_point(
    const mesh_geometry_state& mesh,
    std::uint32_t patch_index,
    vec3 source,
    vec3 receiver,
    vec3& image_source,
    vec3& reflection_point,
    bool require_triangle_membership) noexcept
{
    if (patch_index >= mesh.reflection_patches.size()) {
        return false;
    }
    const mesh_reflection_patch& patch = mesh.reflection_patches[patch_index];
    const double source_side = dot(source - patch.point, patch.normal);
    const double receiver_side = dot(receiver - patch.point, patch.normal);
    if (source_side * receiver_side <= 1.0e-10) {
        return false;
    }
    image_source = source - (patch.normal * (2.0 * source_side));
    const reflection_surface unbounded_surface{ patch.point, patch.normal };
    if (!surface_reflection_point(image_source, receiver, unbounded_surface, reflection_point)) {
        return false;
    }
    return !require_triangle_membership || point_on_mesh_reflection_patch(mesh, patch_index, reflection_point);
}

bool mesh_second_order_reflection_points(
    const mesh_geometry_state& first_mesh,
    std::uint32_t first_patch_index,
    const mesh_geometry_state& second_mesh,
    std::uint32_t second_patch_index,
    vec3 source,
    vec3 receiver,
    vec3& first_reflection_point,
    vec3& second_reflection_point,
    bool require_triangle_membership) noexcept
{
    if (first_patch_index >= first_mesh.reflection_patches.size() ||
        second_patch_index >= second_mesh.reflection_patches.size()) {
        return false;
    }
    const mesh_reflection_patch& first_patch = first_mesh.reflection_patches[first_patch_index];
    const mesh_reflection_patch& second_patch = second_mesh.reflection_patches[second_patch_index];
    const double parallel_amount = std::abs(dot(first_patch.normal, second_patch.normal));
    if (parallel_amount > 1.0 - 1.0e-8 &&
        std::abs(dot(second_patch.point - first_patch.point, first_patch.normal)) <= 2.0e-4) {
        return false;
    }

    const double first_source_side = dot(source - first_patch.point, first_patch.normal);
    const vec3 first_image = source - (first_patch.normal * (2.0 * first_source_side));
    const double second_image_side = dot(first_image - second_patch.point, second_patch.normal);
    const vec3 second_image = first_image - (second_patch.normal * (2.0 * second_image_side));
    const reflection_surface second_surface{ second_patch.point, second_patch.normal };
    if (!surface_reflection_point(second_image, receiver, second_surface, second_reflection_point)) {
        return false;
    }
    const reflection_surface first_surface{ first_patch.point, first_patch.normal };
    if (!surface_reflection_point(first_image, second_reflection_point, first_surface, first_reflection_point)) {
        return false;
    }

    const double first_next_side = dot(second_reflection_point - first_patch.point, first_patch.normal);
    const double second_previous_side = dot(first_reflection_point - second_patch.point, second_patch.normal);
    const double second_receiver_side = dot(receiver - second_patch.point, second_patch.normal);
    if (first_source_side * first_next_side <= 1.0e-10 ||
        second_previous_side * second_receiver_side <= 1.0e-10 ||
        length(first_reflection_point - second_reflection_point) <= 1.0e-7) {
        return false;
    }
    return !require_triangle_membership ||
        (point_on_mesh_reflection_patch(first_mesh, first_patch_index, first_reflection_point) &&
            point_on_mesh_reflection_patch(second_mesh, second_patch_index, second_reflection_point));
}

pg_status prepare_mesh_reflection_caches(pg_engine* engine)
{
    const std::size_t mesh_count = engine->mesh_geometries.size();
    if (mesh_count > 0U && engine->emitters.size() > engine->mesh_reflection_caches.max_size() / mesh_count) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Imported-mesh reflection cache exceeds addressable memory.");
    }
    const std::size_t required = engine->emitters.size() * mesh_count;
    try {
        engine->mesh_reflection_caches.assign(required, {});
        engine->mesh_second_order_reflection_caches.assign(engine->emitters.size(), {});
        return PG_STATUS_OK;
    } catch (const std::bad_alloc&) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Unable to allocate the bounded imported-mesh reflection cache.");
    } catch (...) {
        return fail(engine, PG_STATUS_INTERNAL_ERROR, "Unable to prepare imported-mesh reflection paths.");
    }
}

void update_mesh_reflection_caches(
    pg_engine& engine,
    vec3 receiver_position,
    double receive_time,
    std::uint32_t requested_path_limit,
    bool include_second_order) noexcept
{
    const std::size_t mesh_count = engine.mesh_geometries.size();
    if (mesh_count == 0U || engine.mesh_reflection_caches.size() != engine.emitters.size() * mesh_count ||
        engine.mesh_second_order_reflection_caches.size() != engine.emitters.size()) {
        return;
    }
    const std::uint32_t path_limit = std::clamp<std::uint32_t>(
        requested_path_limit,
        1U,
        static_cast<std::uint32_t>(maximum_cached_mesh_reflection_paths));
    const double base_sound_speed = std::max(speed_of_sound(engine.atmosphere), minimum_effective_sound_speed);
    for (std::size_t emitter_index = 0U; emitter_index < engine.emitters.size(); ++emitter_index) {
        const emitter_state& emitter = engine.emitters[emitter_index];
        const vec3 receive_time_source_position = sample_motion(emitter, receive_time);
        for (std::size_t mesh_index = 0U; mesh_index < mesh_count; ++mesh_index) {
            const mesh_geometry_state& mesh = engine.mesh_geometries[mesh_index];
            mesh_reflection_cache& cache = engine.mesh_reflection_caches[(emitter_index * mesh_count) + mesh_index];
            cache.count = 0U;
            if (!emitter.enabled || !mesh.enabled || !mesh.reflects) {
                continue;
            }
            std::array<double, maximum_cached_mesh_reflection_paths> distances{};
            distances.fill(std::numeric_limits<double>::infinity());
            for (std::uint32_t patch_index = 0U;
                patch_index < static_cast<std::uint32_t>(mesh.reflection_patches.size());
                ++patch_index) {
                vec3 image_source{};
                vec3 reflection_point{};
                if (!mesh_patch_reflection_point(
                        mesh,
                        patch_index,
                        receive_time_source_position,
                        receiver_position,
                        image_source,
                        reflection_point,
                        true)) {
                    continue;
                }
                const double initial_distance = length(receive_time_source_position - reflection_point) +
                    length(reflection_point - receiver_position);
                const vec3 retarded_source_position = sample_motion(
                    emitter,
                    receive_time - (initial_distance / base_sound_speed));
                if (!mesh_patch_reflection_point(
                        mesh,
                        patch_index,
                        retarded_source_position,
                        receiver_position,
                        image_source,
                        reflection_point,
                        true)) {
                    continue;
                }
                const double distance = length(retarded_source_position - reflection_point) +
                    length(reflection_point - receiver_position);
                std::uint32_t insert_at = cache.count;
                if (cache.count < path_limit) {
                    ++cache.count;
                } else {
                    insert_at = path_limit - 1U;
                    const double last_distance = distances[insert_at];
                    const std::uint32_t last_patch = cache.patch_indices[insert_at];
                    if (distance > last_distance || (distance == last_distance && patch_index >= last_patch)) {
                        continue;
                    }
                }
                while (insert_at > 0U) {
                    const double previous_distance = distances[insert_at - 1U];
                    const std::uint32_t previous_patch = cache.patch_indices[insert_at - 1U];
                    if (distance > previous_distance || (distance == previous_distance && patch_index >= previous_patch)) {
                        break;
                    }
                    distances[insert_at] = previous_distance;
                    cache.patch_indices[insert_at] = previous_patch;
                    --insert_at;
                }
                distances[insert_at] = distance;
                cache.patch_indices[insert_at] = patch_index;
            }
        }

        mesh_second_order_reflection_cache& second_order_cache =
            engine.mesh_second_order_reflection_caches[emitter_index];
        second_order_cache.count = 0U;
        if (!include_second_order || !emitter.enabled) {
            continue;
        }
        std::array<mesh_patch_reference, maximum_second_order_candidate_patches> candidates{};
        std::array<double, maximum_second_order_candidate_patches> candidate_distances{};
        candidate_distances.fill(std::numeric_limits<double>::infinity());
        std::uint32_t candidate_count = 0U;
        for (std::size_t mesh_index = 0U; mesh_index < mesh_count; ++mesh_index) {
            const mesh_reflection_cache& cache = engine.mesh_reflection_caches[(emitter_index * mesh_count) + mesh_index];
            const mesh_geometry_state& mesh = engine.mesh_geometries[mesh_index];
            for (std::uint32_t path_index = 0U; path_index < cache.count; ++path_index) {
                const std::uint32_t patch_index = cache.patch_indices[path_index];
                vec3 image_source{};
                vec3 point{};
                if (!mesh_patch_reflection_point(
                        mesh, patch_index, receive_time_source_position, receiver_position, image_source, point, true)) {
                    continue;
                }
                const double distance = length(receive_time_source_position - point) + length(point - receiver_position);
                std::uint32_t insert_at = candidate_count;
                if (candidate_count < candidates.size()) {
                    ++candidate_count;
                } else {
                    insert_at = static_cast<std::uint32_t>(candidates.size() - 1U);
                    if (distance >= candidate_distances[insert_at]) {
                        continue;
                    }
                }
                while (insert_at > 0U && distance < candidate_distances[insert_at - 1U]) {
                    candidate_distances[insert_at] = candidate_distances[insert_at - 1U];
                    candidates[insert_at] = candidates[insert_at - 1U];
                    --insert_at;
                }
                candidate_distances[insert_at] = distance;
                candidates[insert_at] = { static_cast<std::uint32_t>(mesh_index), patch_index };
            }
        }

        std::array<double, maximum_cached_mesh_second_order_paths> second_order_distances{};
        second_order_distances.fill(std::numeric_limits<double>::infinity());
        const std::uint32_t second_order_limit = std::min<std::uint32_t>(
            static_cast<std::uint32_t>(maximum_cached_mesh_second_order_paths),
            std::max(1U, (path_limit + 1U) / 2U));
        for (std::uint32_t first_index = 0U; first_index < candidate_count; ++first_index) {
            for (std::uint32_t second_index = 0U; second_index < candidate_count; ++second_index) {
                const mesh_patch_reference first = candidates[first_index];
                const mesh_patch_reference second = candidates[second_index];
                if (first.mesh_index == second.mesh_index && first.patch_index == second.patch_index) {
                    continue;
                }
                const mesh_geometry_state& first_mesh = engine.mesh_geometries[first.mesh_index];
                const mesh_geometry_state& second_mesh = engine.mesh_geometries[second.mesh_index];
                vec3 first_point{};
                vec3 second_point{};
                if (!mesh_second_order_reflection_points(
                        first_mesh,
                        first.patch_index,
                        second_mesh,
                        second.patch_index,
                        receive_time_source_position,
                        receiver_position,
                        first_point,
                        second_point,
                        true)) {
                    continue;
                }
                const double initial_distance = length(receive_time_source_position - first_point) +
                    length(first_point - second_point) + length(second_point - receiver_position);
                const vec3 retarded_source_position = sample_motion(
                    emitter,
                    receive_time - (initial_distance / base_sound_speed));
                if (!mesh_second_order_reflection_points(
                        first_mesh,
                        first.patch_index,
                        second_mesh,
                        second.patch_index,
                        retarded_source_position,
                        receiver_position,
                        first_point,
                        second_point,
                        true)) {
                    continue;
                }
                const double distance = length(retarded_source_position - first_point) +
                    length(first_point - second_point) + length(second_point - receiver_position);
                std::uint32_t insert_at = second_order_cache.count;
                if (second_order_cache.count < second_order_limit) {
                    ++second_order_cache.count;
                } else {
                    insert_at = second_order_limit - 1U;
                    if (distance >= second_order_distances[insert_at]) {
                        continue;
                    }
                }
                while (insert_at > 0U && distance < second_order_distances[insert_at - 1U]) {
                    second_order_distances[insert_at] = second_order_distances[insert_at - 1U];
                    second_order_cache.first[insert_at] = second_order_cache.first[insert_at - 1U];
                    second_order_cache.second[insert_at] = second_order_cache.second[insert_at - 1U];
                    --insert_at;
                }
                second_order_distances[insert_at] = distance;
                second_order_cache.first[insert_at] = first;
                second_order_cache.second[insert_at] = second;
            }
        }
    }
}

std::uint32_t mesh_reflection_path_limit(std::uint32_t ray_count, bool realtime_approximation) noexcept
{
    if (realtime_approximation) {
        return 2U;
    }
    return std::clamp<std::uint32_t>(
        std::max(1U, (ray_count + 1023U) / 1024U),
        1U,
        static_cast<std::uint32_t>(maximum_cached_mesh_reflection_paths));
}

delayed_path_sample sample_delayed_path(
    const pg_engine& engine,
    const emitter_state& emitter,
    vec3 initial_source_position,
    vec3 receiver_position,
    double receive_time,
    std::uint32_t sample_rate,
    std::uint32_t reflection_bounces,
    int reflection_axis,
    bool reflection_high_side,
    double room_pressure_reflection = 1.0,
    std::uint32_t solver_iterations = 6U,
    const atmosphere_sample* preview_atmosphere = nullptr) noexcept
{
    double emission_time = receive_time;
    vec3 source_position = initial_source_position;
    vec3 image_source = source_position;
    vec3 first_reflection_point = source_position;
    vec3 second_reflection_point = source_position;
    propagation_integral propagation{};
    const std::uint32_t bounded_solver_iterations = std::clamp(solver_iterations, 1U, 6U);

    const auto integrate_path = [&](const auto& points) noexcept {
        return bounded_solver_iterations == 1U
            ? integrate_propagation_path_preview(
                engine.atmosphere,
                points,
                emitter.frequency_hz,
                receive_time,
                preview_atmosphere)
            : integrate_propagation_path(
                engine.atmosphere,
                points,
                emitter.frequency_hz,
                receive_time);
    };

    const auto update_propagation = [&]() noexcept {
        if (reflection_bounces == 0U) {
            propagation = integrate_path(std::array{ source_position, receiver_position });
            return true;
        }
        if (!engine.room.enabled) {
            return false;
        }
        if (reflection_bounces == 1U) {
            if (!room_reflection_path(
                    engine.room,
                    source_position,
                    receiver_position,
                    reflection_axis,
                    reflection_high_side,
                    image_source,
                    first_reflection_point)) {
                return false;
            }
            propagation = integrate_path(std::array{ source_position, first_reflection_point, receiver_position });
            return true;
        }
        if (reflection_bounces == 2U) {
            if (!room_second_order_reflection_path(
                    engine.room,
                    source_position,
                    receiver_position,
                    reflection_axis,
                    reflection_high_side,
                    image_source,
                    first_reflection_point,
                    second_reflection_point)) {
                return false;
            }
            propagation = integrate_path(
                std::array{ source_position, first_reflection_point, second_reflection_point, receiver_position });
            return true;
        }
        return false;
    };

    for (std::uint32_t iteration = 0U; iteration < bounded_solver_iterations; ++iteration) {
        if (iteration > 0U) {
            source_position = sample_motion(emitter, emission_time);
        }
        if (!update_propagation()) {
            return {};
        }
        emission_time = receive_time - propagation.travel_time_seconds;
    }

    source_position = sample_motion(emitter, emission_time);
    if (bounded_solver_iterations > 1U && !update_propagation()) {
        return {};
    }

    const double distance = propagation.distance_metres;
    const double attenuation_distance = std::max(distance, emitter.min_distance_metres);
    const double absorption = std::exp(-propagation.absorption_exponent);
    const double transmission = reflection_bounces > 0U
        ? 1.0
        : occlusion_transmission_gain(engine, source_position, receiver_position, emitter.frequency_hz);
    const double incident_sample = primitive_sample(emitter, emission_time, sample_rate) * emitter.gain_linear *
        absorption * transmission / attenuation_distance;
    if (reflection_bounces == 0U) {
        return { incident_sample, distance, source_position, normalized(source_position - receiver_position) };
    }

    const double reflection_per_bounce = std::clamp(room_pressure_reflection, 0.0, 1.0);
    const double specular_energy_per_bounce = 1.0 - std::clamp(engine.room.scattering, 0.0, 1.0);
    const double combined_reflection = reflection_bounces == 2U
        ? reflection_per_bounce * reflection_per_bounce
        : reflection_per_bounce;
    const double specular_energy_fraction = reflection_bounces == 2U
        ? specular_energy_per_bounce * specular_energy_per_bounce
        : specular_energy_per_bounce;
    const double specular_gain = combined_reflection * std::sqrt(specular_energy_fraction);
    const double diffuse_gain = combined_reflection * std::sqrt(std::max(0.0, 1.0 - specular_energy_fraction));
    const vec3 arrival_point = reflection_bounces == 1U ? first_reflection_point : second_reflection_point;
    return {
        incident_sample * specular_gain,
        distance,
        source_position,
        normalized(arrival_point - receiver_position),
        incident_sample * diffuse_gain
    };
}

delayed_path_sample sample_geometry_reflection(
    const pg_engine& engine,
    const emitter_state& emitter,
    vec3 initial_source_position,
    vec3 receiver_position,
    double receive_time,
    std::uint32_t sample_rate,
    std::size_t geometry_index,
    const reflection_surface& surface,
    std::uint32_t solver_iterations = 6U) noexcept
{
    if (geometry_index >= engine.geometries.size()) {
        return {};
    }
    const geometry_state& geometry = engine.geometries[geometry_index];
    if (!geometry.enabled || !geometry.reflects || geometry.material_index >= engine.acoustic_materials.size()) {
        return {};
    }

    double emission_time = receive_time;
    vec3 source_position = initial_source_position;
    vec3 image_source = source_position;
    vec3 reflection_point = source_position;
    propagation_integral propagation{};
    const std::uint32_t bounded_solver_iterations = std::clamp(solver_iterations, 1U, 6U);
    const auto integrate_path = [&](const auto& points) noexcept {
        return bounded_solver_iterations == 1U
            ? integrate_propagation_path_preview(
                engine.atmosphere,
                points,
                emitter.frequency_hz,
                receive_time)
            : integrate_propagation_path(
                engine.atmosphere,
                points,
                emitter.frequency_hz,
                receive_time);
    };
    for (std::uint32_t iteration = 0U; iteration < bounded_solver_iterations; ++iteration) {
        if (iteration > 0U) {
            source_position = sample_motion(emitter, emission_time);
        }
        const double source_plane_distance = dot(source_position - surface.point, surface.normal);
        image_source = source_position - (surface.normal * (2.0 * source_plane_distance));
        const bool has_reflection_point = surface_reflection_point(image_source, receiver_position, surface, reflection_point);
        propagation = has_reflection_point
            ? integrate_path(std::array{ source_position, reflection_point, receiver_position })
            : integrate_path(std::array{ image_source, receiver_position });
        emission_time = receive_time - propagation.travel_time_seconds;
    }

    source_position = sample_motion(emitter, emission_time);
    const double source_plane_distance = dot(source_position - surface.point, surface.normal);
    image_source = source_position - (surface.normal * (2.0 * source_plane_distance));

    const double source_side = dot(source_position - surface.point, surface.normal);
    const double receiver_side = dot(receiver_position - surface.point, surface.normal);
    if (source_side * receiver_side <= 1.0e-10) {
        return {};
    }
    if (!surface_reflection_point(image_source, receiver_position, surface, reflection_point)) {
        return {};
    }
    if (!point_within_surface(surface, reflection_point)) {
        return {};
    }
    if (geometry.kind == PG_GEOMETRY_BOX &&
        (segment_intersects_box(geometry, source_position, reflection_point) ||
            segment_intersects_box(geometry, reflection_point, receiver_position))) {
        return {};
    }

    if (bounded_solver_iterations > 1U) {
        propagation = integrate_propagation_path(
            engine.atmosphere,
            std::array{ source_position, reflection_point, receiver_position },
            emitter.frequency_hz,
            receive_time);
    }
    const double distance = propagation.distance_metres;
    const double attenuation_distance = std::max(distance, emitter.min_distance_metres);
    const double absorption = std::exp(-propagation.absorption_exponent);
    const acoustic_material_state& material = engine.acoustic_materials[geometry.material_index];
    const double material_absorption = material_band_value(material, material.absorption, emitter.frequency_hz);
    const double material_transmission = material_band_value(material, material.transmission, emitter.frequency_hz);
    const double material_scattering = material_band_value(material, material.scattering, emitter.frequency_hz);
    const double reflection_energy = std::max(0.0, 1.0 - material_absorption - material_transmission);
    const double high_frequency_amount = std::clamp(std::log2(std::max(emitter.frequency_hz, 125.0) / 125.0) / 7.0, 0.0, 1.0);
    const double specular_gain = std::sqrt(reflection_energy * (1.0 - material_scattering)) *
        (1.0 - (0.5 * material.roughness * high_frequency_amount));
    const double diffuse_gain = std::sqrt(reflection_energy * material_scattering) *
        (1.0 - (0.65 * material.roughness * high_frequency_amount));
    const double visibility = occlusion_transmission_gain(
        engine,
        source_position,
        reflection_point,
        emitter.frequency_hz,
        geometry_index) * occlusion_transmission_gain(
            engine,
            reflection_point,
            receiver_position,
            emitter.frequency_hz,
            geometry_index);
    const double incident_sample = primitive_sample(emitter, emission_time, sample_rate) * emitter.gain_linear *
        absorption * visibility / attenuation_distance;
    return {
        incident_sample * specular_gain,
        distance,
        source_position,
        normalized(reflection_point - receiver_position),
        incident_sample * diffuse_gain
    };
}

delayed_path_sample sample_mesh_geometry_reflection(
    const pg_engine& engine,
    const emitter_state& emitter,
    vec3 initial_source_position,
    vec3 receiver_position,
    double receive_time,
    std::uint32_t sample_rate,
    std::size_t mesh_index,
    std::uint32_t patch_index,
    std::uint32_t solver_iterations = 6U) noexcept
{
    if (mesh_index >= engine.mesh_geometries.size()) {
        return {};
    }
    const mesh_geometry_state& mesh = engine.mesh_geometries[mesh_index];
    if (!mesh.enabled || !mesh.reflects || patch_index >= mesh.reflection_patches.size() ||
        mesh.reflection_patches[patch_index].material_index >= engine.acoustic_materials.size()) {
        return {};
    }

    double emission_time = receive_time;
    vec3 source_position = initial_source_position;
    vec3 image_source = source_position;
    vec3 reflection_point = source_position;
    propagation_integral propagation{};
    const std::uint32_t bounded_solver_iterations = std::clamp(solver_iterations, 1U, 6U);
    const auto integrate_path = [&](const auto& points) noexcept {
        return bounded_solver_iterations == 1U
            ? integrate_propagation_path_preview(
                engine.atmosphere,
                points,
                emitter.frequency_hz,
                receive_time)
            : integrate_propagation_path(
                engine.atmosphere,
                points,
                emitter.frequency_hz,
                receive_time);
    };
    for (std::uint32_t iteration = 0U; iteration < bounded_solver_iterations; ++iteration) {
        if (iteration > 0U) {
            source_position = sample_motion(emitter, emission_time);
        }
        if (!mesh_patch_reflection_point(
                mesh,
                patch_index,
                source_position,
                receiver_position,
                image_source,
                reflection_point,
                false)) {
            return {};
        }
        propagation = integrate_path(std::array{ source_position, reflection_point, receiver_position });
        emission_time = receive_time - propagation.travel_time_seconds;
    }

    source_position = sample_motion(emitter, emission_time);
    if (!mesh_patch_reflection_point(
            mesh,
            patch_index,
            source_position,
            receiver_position,
            image_source,
            reflection_point,
            true)) {
        return {};
    }
    if (bounded_solver_iterations > 1U) {
        propagation = integrate_propagation_path(
            engine.atmosphere,
            std::array{ source_position, reflection_point, receiver_position },
            emitter.frequency_hz,
            receive_time);
    }

    const double distance = propagation.distance_metres;
    const double attenuation_distance = std::max(distance, emitter.min_distance_metres);
    const double air_absorption = std::exp(-propagation.absorption_exponent);
    const acoustic_material_state& material =
        engine.acoustic_materials[mesh.reflection_patches[patch_index].material_index];
    const double material_absorption = material_band_value(material, material.absorption, emitter.frequency_hz);
    const double material_transmission = material_band_value(material, material.transmission, emitter.frequency_hz);
    const double material_scattering = material_band_value(material, material.scattering, emitter.frequency_hz);
    const double reflection_energy = std::max(0.0, 1.0 - material_absorption - material_transmission);
    const double high_frequency_amount = std::clamp(
        std::log2(std::max(emitter.frequency_hz, 125.0) / 125.0) / 7.0,
        0.0,
        1.0);
    const double specular_gain = std::sqrt(reflection_energy * (1.0 - material_scattering)) *
        (1.0 - (0.5 * material.roughness * high_frequency_amount));
    const double diffuse_gain = std::sqrt(reflection_energy * material_scattering) *
        (1.0 - (0.65 * material.roughness * high_frequency_amount));
    const double visibility = occlusion_transmission_gain(
        engine,
        source_position,
        reflection_point,
        emitter.frequency_hz) * occlusion_transmission_gain(
            engine,
            reflection_point,
            receiver_position,
            emitter.frequency_hz);
    const double incident_sample = primitive_sample(emitter, emission_time, sample_rate) * emitter.gain_linear *
        air_absorption * visibility / attenuation_distance;
    return {
        incident_sample * specular_gain,
        distance,
        source_position,
        normalized(reflection_point - receiver_position),
        incident_sample * diffuse_gain
    };
}

delayed_path_sample sample_mesh_second_order_reflection(
    const pg_engine& engine,
    const emitter_state& emitter,
    vec3 initial_source_position,
    vec3 receiver_position,
    double receive_time,
    std::uint32_t sample_rate,
    mesh_patch_reference first_reference,
    mesh_patch_reference second_reference,
    std::uint32_t solver_iterations = 6U) noexcept
{
    if (first_reference.mesh_index >= engine.mesh_geometries.size() ||
        second_reference.mesh_index >= engine.mesh_geometries.size()) {
        return {};
    }
    const mesh_geometry_state& first_mesh = engine.mesh_geometries[first_reference.mesh_index];
    const mesh_geometry_state& second_mesh = engine.mesh_geometries[second_reference.mesh_index];
    if (!first_mesh.enabled || !first_mesh.reflects || !second_mesh.enabled || !second_mesh.reflects ||
        first_reference.patch_index >= first_mesh.reflection_patches.size() ||
        second_reference.patch_index >= second_mesh.reflection_patches.size()) {
        return {};
    }
    const mesh_reflection_patch& first_patch = first_mesh.reflection_patches[first_reference.patch_index];
    const mesh_reflection_patch& second_patch = second_mesh.reflection_patches[second_reference.patch_index];
    if (first_patch.material_index >= engine.acoustic_materials.size() ||
        second_patch.material_index >= engine.acoustic_materials.size()) {
        return {};
    }

    double emission_time = receive_time;
    vec3 source_position = initial_source_position;
    vec3 first_point = source_position;
    vec3 second_point = source_position;
    propagation_integral propagation{};
    const std::uint32_t bounded_solver_iterations = std::clamp(solver_iterations, 1U, 6U);
    const auto integrate_path = [&](const auto& points) noexcept {
        return bounded_solver_iterations == 1U
            ? integrate_propagation_path_preview(engine.atmosphere, points, emitter.frequency_hz, receive_time)
            : integrate_propagation_path(engine.atmosphere, points, emitter.frequency_hz, receive_time);
    };
    for (std::uint32_t iteration = 0U; iteration < bounded_solver_iterations; ++iteration) {
        if (iteration > 0U) {
            source_position = sample_motion(emitter, emission_time);
        }
        if (!mesh_second_order_reflection_points(
                first_mesh,
                first_reference.patch_index,
                second_mesh,
                second_reference.patch_index,
                source_position,
                receiver_position,
                first_point,
                second_point,
                false)) {
            return {};
        }
        propagation = integrate_path(std::array{ source_position, first_point, second_point, receiver_position });
        emission_time = receive_time - propagation.travel_time_seconds;
    }
    source_position = sample_motion(emitter, emission_time);
    if (!mesh_second_order_reflection_points(
            first_mesh,
            first_reference.patch_index,
            second_mesh,
            second_reference.patch_index,
            source_position,
            receiver_position,
            first_point,
            second_point,
            true)) {
        return {};
    }
    if (bounded_solver_iterations > 1U) {
        propagation = integrate_propagation_path(
            engine.atmosphere,
            std::array{ source_position, first_point, second_point, receiver_position },
            emitter.frequency_hz,
            receive_time);
    }

    const acoustic_material_state& first_material = engine.acoustic_materials[first_patch.material_index];
    const acoustic_material_state& second_material = engine.acoustic_materials[second_patch.material_index];
    const auto reflected_energy = [&](const acoustic_material_state& material) noexcept {
        const double absorption = material_band_value(material, material.absorption, emitter.frequency_hz);
        const double transmission = material_band_value(material, material.transmission, emitter.frequency_hz);
        return std::max(0.0, 1.0 - absorption - transmission);
    };
    const double first_energy = reflected_energy(first_material);
    const double second_energy = reflected_energy(second_material);
    const double first_scattering = material_band_value(first_material, first_material.scattering, emitter.frequency_hz);
    const double second_scattering = material_band_value(second_material, second_material.scattering, emitter.frequency_hz);
    const double total_energy = first_energy * second_energy;
    const double specular_energy = total_energy * (1.0 - first_scattering) * (1.0 - second_scattering);
    const double high_frequency_amount = std::clamp(
        std::log2(std::max(emitter.frequency_hz, 125.0) / 125.0) / 7.0,
        0.0,
        1.0);
    const double roughness_gain =
        (1.0 - (0.5 * first_material.roughness * high_frequency_amount)) *
        (1.0 - (0.5 * second_material.roughness * high_frequency_amount));
    const double specular_gain = std::sqrt(std::max(0.0, specular_energy)) * roughness_gain;
    const double diffuse_gain = std::sqrt(std::max(0.0, total_energy - specular_energy)) *
        (1.0 - (0.325 * (first_material.roughness + second_material.roughness) * high_frequency_amount));
    const double visibility =
        occlusion_transmission_gain(engine, source_position, first_point, emitter.frequency_hz) *
        occlusion_transmission_gain(engine, first_point, second_point, emitter.frequency_hz) *
        occlusion_transmission_gain(engine, second_point, receiver_position, emitter.frequency_hz);
    const double distance = propagation.distance_metres;
    const double attenuation_distance = std::max(distance, emitter.min_distance_metres);
    const double incident_sample = primitive_sample(emitter, emission_time, sample_rate) * emitter.gain_linear *
        std::exp(-propagation.absorption_exponent) * visibility / attenuation_distance;
    return {
        incident_sample * specular_gain,
        distance,
        source_position,
        normalized(second_point - receiver_position),
        incident_sample * diffuse_gain
    };
}

double microphone_pattern_gain(pg_polar_pattern pattern, vec3 capsule_forward, vec3 source_direction) noexcept
{
    const double cosine = std::clamp(dot(capsule_forward, source_direction), -1.0, 1.0);
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

room_response_state make_room_response_state()
{
    room_response_state state{};
    for (auto& band : state.delay_lines) {
        for (std::size_t index = 0; index < late_delay_seconds.size(); ++index) {
            const auto capacity = static_cast<std::size_t>(std::ceil(late_delay_seconds[index] * maximum_supported_sample_rate)) + 2U;
            band[index].resize(capacity);
        }
    }
    return state;
}

void reset_room_response(
    room_response_state& state,
    std::uint32_t sample_rate,
    const room_state& room,
    double impulse_response_seconds) noexcept
{
    for (auto& band : state.delay_lines) {
        for (auto& line : band) {
            std::fill(line.begin(), line.end(), 0.0F);
        }
    }
    state.delay_indices = {};
    state.modal_previous_one = {};
    state.modal_previous_two = {};
    state.lowpass_low = 0.0;
    state.lowpass_high = 0.0;
    state.lowpass_low_coefficient = std::exp(
        (-2.0 * std::numbers::pi * 500.0) / static_cast<double>(sample_rate));
    state.lowpass_high_coefficient = std::exp(
        (-2.0 * std::numbers::pi * 4000.0) / static_cast<double>(sample_rate));
    const std::array<double, 3> room_reflections{
        std::clamp(room.reflection_low, 0.0, 0.999),
        std::clamp(room.reflection_mid, 0.0, 0.999),
        std::clamp(room.reflection_high, 0.0, 0.999)
    };
    const std::array<double, 3> room_decay_times{
        room.decay_time_low_seconds,
        room.decay_time_mid_seconds,
        room.decay_time_high_seconds
    };
    for (std::size_t band = 0U; band < state.delay_lines.size(); ++band) {
        const double decay_time = std::clamp(
            std::min(room_decay_times[band], impulse_response_seconds),
            0.05,
            30.0);
        state.input_gains[band] = 0.08 + (0.18 * room.scattering) + (0.12 * room_reflections[band]);
        for (std::size_t index = 0U; index < late_delay_seconds.size(); ++index) {
            state.delay_feedbacks[band][index] = std::pow(0.001, late_delay_seconds[index] / decay_time);
        }
        if (band == 0U) {
            state.modal_radius = std::clamp(
                std::pow(0.001, 1.0 / (decay_time * static_cast<double>(sample_rate))),
                0.9,
                0.99999);
        }
    }
    state.sample_rate = sample_rate;
}

diffuse_response_state make_diffuse_response_state()
{
    diffuse_response_state state{};
    for (std::size_t index = 0; index < diffuse_delay_seconds.size(); ++index) {
        const auto capacity = static_cast<std::size_t>(
            std::ceil((diffuse_delay_seconds[index] + 0.0015) * maximum_supported_sample_rate)) + 2U;
        state.delay_lines[index].resize(capacity);
    }
    return state;
}

void reset_diffuse_response(
    diffuse_response_state& state,
    std::uint32_t sample_rate,
    std::uint32_t ray_count = 0U,
    double decay_time_seconds = 0.0,
    std::uint32_t seed = 0U) noexcept
{
    for (auto& line : state.delay_lines) {
        std::fill(line.begin(), line.end(), 0.0F);
    }
    state.delay_indices = {};
    state.sample_rate = sample_rate;
    state.ray_count = ray_count;
    state.decay_time_seconds = decay_time_seconds;
    state.seed = seed;
}

std::size_t diffuse_line_count(std::uint32_t ray_count) noexcept
{
    if (ray_count == 0U) {
        return 0U;
    }
    if (ray_count < 64U) {
        return 2U;
    }
    if (ray_count < 1024U) {
        return 4U;
    }
    if (ray_count < 4096U) {
        return 6U;
    }
    return diffuse_delay_seconds.size();
}

std::pair<double, double> process_diffuse_response(
    pg_engine& engine,
    std::size_t receiver_index,
    double input,
    std::uint32_t sample_rate,
    std::uint32_t reflection_order,
    std::uint32_t ray_count,
    double impulse_response_seconds,
    std::uint32_t seed) noexcept
{
    const std::size_t line_count = diffuse_line_count(ray_count);
    if (reflection_order == 0U || line_count == 0U || impulse_response_seconds <= 0.0) {
        return {};
    }

    diffuse_response_state& state = engine.receiver_diffuse_responses[receiver_index];
    const double decay_time = std::clamp(impulse_response_seconds, 0.05, 30.0);
    if (state.sample_rate != sample_rate || state.ray_count != ray_count ||
        state.seed != seed || std::abs(state.decay_time_seconds - decay_time) > 1.0e-12) {
        reset_diffuse_response(state, sample_rate, ray_count, decay_time, seed);
    }

    constexpr std::array<double, diffuse_delay_seconds.size()> left_signs{ 1, -1, 1, 1, -1, 1, -1, -1 };
    constexpr std::array<double, diffuse_delay_seconds.size()> right_signs{ 1, 1, -1, 1, 1, -1, -1, 1 };
    double diffuse_left = 0.0;
    double diffuse_right = 0.0;
    for (std::size_t index = 0; index < line_count; ++index) {
        auto& line = state.delay_lines[index];
        const double jitter_seconds = hashed_noise(seed + 0x9e3779b9U, static_cast<std::int64_t>(index)) * 0.0012;
        const double delay_seconds = diffuse_delay_seconds[index] + jitter_seconds;
        const auto delay_length = std::clamp(
            static_cast<std::size_t>(std::llround(delay_seconds * sample_rate)),
            std::size_t{ 2U },
            line.size());
        std::size_t& cursor = state.delay_indices[index];
        if (cursor >= delay_length) {
            cursor = 0U;
        }
        const double delayed = line[cursor];
        const double feedback = std::pow(0.001, delay_seconds / decay_time);
        line[cursor] = static_cast<float>(std::clamp(input + (delayed * feedback), -8.0, 8.0));
        cursor = (cursor + 1U) % delay_length;

        const double energy_normalization = std::sqrt(
            std::max(0.0, 1.0 - (feedback * feedback)) /
            (2.0 * static_cast<double>(line_count)));
        diffuse_left += delayed * energy_normalization * left_signs[index];
        diffuse_right += delayed * energy_normalization * right_signs[index];
    }
    return { diffuse_left, diffuse_right };
}

std::pair<double, double> process_room_response(
    pg_engine& engine,
    std::size_t receiver_index,
    double input,
    std::uint32_t sample_rate,
    std::uint32_t reflection_order,
    double impulse_response_seconds) noexcept
{
    if (!engine.room.enabled || reflection_order == 0U || impulse_response_seconds <= 0.0) {
        return {};
    }
    room_response_state& state = engine.receiver_room_responses[receiver_index];
    if (state.sample_rate != sample_rate) {
        reset_room_response(state, sample_rate, engine.room, impulse_response_seconds);
    }

    state.lowpass_low = ((1.0 - state.lowpass_low_coefficient) * input) +
        (state.lowpass_low_coefficient * state.lowpass_low);
    state.lowpass_high = ((1.0 - state.lowpass_high_coefficient) * input) +
        (state.lowpass_high_coefficient * state.lowpass_high);
    const std::array<double, 3> band_inputs{
        state.lowpass_low,
        state.lowpass_high - state.lowpass_low,
        input - state.lowpass_high
    };
    constexpr std::array<double, 4> left_mix{ 0.24, 0.19, -0.17, 0.15 };
    constexpr std::array<double, 4> right_mix{ 0.17, -0.23, 0.15, 0.19 };
    double late_left = 0.0;
    double late_right = 0.0;
    for (std::size_t band = 0U; band < state.delay_lines.size(); ++band) {
        for (std::size_t index = 0; index < late_delay_seconds.size(); ++index) {
            auto& line = state.delay_lines[band][index];
            const auto delay_length = std::clamp(
                static_cast<std::size_t>(std::llround(late_delay_seconds[index] * sample_rate)),
                std::size_t{ 2U },
                line.size());
            std::size_t& cursor = state.delay_indices[band][index];
            if (cursor >= delay_length) {
                cursor = 0U;
            }
            const double delayed = line[cursor];
            line[cursor] = static_cast<float>(std::clamp(
                (band_inputs[band] * state.input_gains[band]) +
                    (delayed * state.delay_feedbacks[band][index]),
                -8.0,
                8.0));
            cursor = (cursor + 1U) % delay_length;
            late_left += delayed * left_mix[index];
            late_right += delayed * right_mix[index];
        }
    }

    const std::array<double, 3> dimensions{
        std::max(engine.room.dimensions.x, 0.5),
        std::max(engine.room.dimensions.y, 0.5),
        std::max(engine.room.dimensions.z, 0.5)
    };
    constexpr std::array<double, 3> mode_numbers{ 5.0, 3.0, 7.0 };
    const double local_speed = speed_of_sound(engine.atmosphere);
    const double radius = state.modal_radius;
    double modal = 0.0;
    for (std::size_t mode = 0; mode < dimensions.size(); ++mode) {
        const double frequency = std::min(local_speed * mode_numbers[mode] / (2.0 * dimensions[mode]), sample_rate * 0.45);
        const double coefficient = 2.0 * radius * std::cos(2.0 * std::numbers::pi * frequency / sample_rate);
        const double output = (band_inputs[0] * 0.0035) + (coefficient * state.modal_previous_one[mode]) -
            (radius * radius * state.modal_previous_two[mode]);
        state.modal_previous_two[mode] = state.modal_previous_one[mode];
        state.modal_previous_one[mode] = std::clamp(output, -4.0, 4.0);
        modal += output * 0.16;
    }
    return { late_left + modal, late_right + (modal * 0.94) };
}

std::pair<float, float> render_frame(
    pg_engine& engine,
    std::size_t receiver_index,
    double receive_time,
    std::uint32_t sample_rate,
    std::uint32_t reflection_order,
    std::uint32_t ray_count,
    double impulse_response_seconds,
    std::uint32_t seed,
    bool realtime_approximation = false) noexcept
{
    const receiver_state& receiver = engine.receivers[receiver_index];
    const vec3 receiver_centre = sample_motion(receiver, receive_time);
    const vec3 right = normalized(rotate_yaw({ 1.0, 0.0, 0.0 }, receiver.yaw_degrees));
    const vec3 forward = normalized(rotate_yaw({ 0.0, 0.0, 1.0 }, receiver.yaw_degrees));
    const bool explicit_microphones = receiver.kind == PG_RECEIVER_STEREO_MICROPHONE && receiver.has_explicit_capsules;
    const vec3 left_position = explicit_microphones
        ? receiver_centre + rotate_yaw(receiver.left_capsule.relative_position, receiver.yaw_degrees)
        : receiver_centre - (right * (receiver.spacing_metres * 0.5));
    const vec3 right_position = explicit_microphones
        ? receiver_centre + rotate_yaw(receiver.right_capsule.relative_position, receiver.yaw_degrees)
        : receiver_centre + (right * (receiver.spacing_metres * 0.5));
    const vec3 left_forward = explicit_microphones
        ? normalized(rotate_yaw(rotate_euler({ 0.0, 0.0, 1.0 }, receiver.left_capsule.relative_rotation), receiver.yaw_degrees))
        : rotate_yaw(forward, -receiver.microphone_angle_degrees * 0.5);
    const vec3 right_forward = explicit_microphones
        ? normalized(rotate_yaw(rotate_euler({ 0.0, 0.0, 1.0 }, receiver.right_capsule.relative_rotation), receiver.yaw_degrees))
        : rotate_yaw(forward, receiver.microphone_angle_degrees * 0.5);
    const pg_polar_pattern left_pattern = explicit_microphones ? receiver.left_capsule.polar_pattern : receiver.polar_pattern;
    const pg_polar_pattern right_pattern = explicit_microphones ? receiver.right_capsule.polar_pattern : receiver.polar_pattern;
    const auto receiver_path_sample = [&](const delayed_path_sample& path, vec3 capsule_forward, pg_polar_pattern pattern) noexcept {
        return receiver.kind == PG_RECEIVER_STEREO_MICROPHONE
            ? path.value * microphone_pattern_gain(pattern, capsule_forward, path.arrival_direction)
            : path.value;
    };
    const auto receiver_diffuse_sample = [&](const delayed_path_sample& path, vec3 capsule_forward, pg_polar_pattern pattern) noexcept {
        return receiver.kind == PG_RECEIVER_STEREO_MICROPHONE
            ? path.diffuse_value * microphone_pattern_gain(pattern, capsule_forward, path.arrival_direction)
            : path.diffuse_value;
    };

    double left_total = 0.0;
    double right_total = 0.0;
    double diffuse_input_left = 0.0;
    double diffuse_input_right = 0.0;
    double source_lateral = 0.0;
    for (std::size_t emitter_index = 0U; emitter_index < engine.emitters.size(); ++emitter_index) {
        const emitter_state& emitter = engine.emitters[emitter_index];
        if (!emitter.enabled) {
            continue;
        }

        const std::uint32_t solver_iterations = realtime_approximation ? 1U : 6U;
        const vec3 initial_source_position = sample_motion(emitter, receive_time);
        atmosphere_sample preview_direct_atmosphere{};
        const atmosphere_sample* preview_direct_atmosphere_pointer = nullptr;
        if (realtime_approximation) {
            // The two direct routes differ only by the receiver spacing. Preview
            // shares their centre-path field sample while retaining each route's
            // own distance and wind projection for stereo timing.
            preview_direct_atmosphere = sample_atmosphere(
                engine.atmosphere,
                initial_source_position + ((receiver_centre - initial_source_position) * 0.5),
                receive_time);
            preview_direct_atmosphere_pointer = &preview_direct_atmosphere;
        }
        const auto left_direct = sample_delayed_path(
            engine,
            emitter,
            initial_source_position,
            left_position,
            receive_time,
            sample_rate,
            0U,
            0,
            false,
            1.0,
            solver_iterations,
            preview_direct_atmosphere_pointer);
        const auto right_direct = sample_delayed_path(
            engine,
            emitter,
            initial_source_position,
            right_position,
            receive_time,
            sample_rate,
            0U,
            0,
            false,
            1.0,
            solver_iterations,
            preview_direct_atmosphere_pointer);
        double left_sample = receiver_path_sample(left_direct, left_forward, left_pattern);
        double right_sample = receiver_path_sample(right_direct, right_forward, right_pattern);
        source_lateral += dot(normalized(left_direct.source_position - receiver_centre), right);
        const double room_pressure_reflection = engine.room.enabled && reflection_order > 0U
            ? room_reflection_gain(engine.room, emitter.frequency_hz)
            : 1.0;

        if (engine.room.enabled && reflection_order > 0U) {
            for (int axis = 0; axis < 3; ++axis) {
                for (int side = 0; side < 2; ++side) {
                    if (realtime_approximation) {
                        const auto reflection = sample_delayed_path(
                            engine,
                            emitter,
                            initial_source_position,
                            receiver_centre,
                            receive_time,
                            sample_rate,
                            1U,
                            axis,
                            side != 0,
                            room_pressure_reflection,
                            solver_iterations);
                        left_sample += receiver_path_sample(reflection, left_forward, left_pattern);
                        right_sample += receiver_path_sample(reflection, right_forward, right_pattern);
                        diffuse_input_left += receiver_diffuse_sample(reflection, left_forward, left_pattern);
                        diffuse_input_right += receiver_diffuse_sample(reflection, right_forward, right_pattern);
                    } else {
                        const auto left_reflection = sample_delayed_path(
                            engine,
                            emitter,
                            initial_source_position,
                            left_position,
                            receive_time,
                            sample_rate,
                            1U,
                            axis,
                            side != 0,
                            room_pressure_reflection);
                        const auto right_reflection = sample_delayed_path(
                            engine,
                            emitter,
                            initial_source_position,
                            right_position,
                            receive_time,
                            sample_rate,
                            1U,
                            axis,
                            side != 0,
                            room_pressure_reflection);
                        left_sample += receiver_path_sample(left_reflection, left_forward, left_pattern);
                        right_sample += receiver_path_sample(right_reflection, right_forward, right_pattern);
                        diffuse_input_left += receiver_diffuse_sample(left_reflection, left_forward, left_pattern);
                        diffuse_input_right += receiver_diffuse_sample(right_reflection, right_forward, right_pattern);
                    }
                }
            }
        }

        if (engine.room.enabled && reflection_order > 1U) {
            for (int axis = 0; axis < 3; ++axis) {
                for (int first_side = 0; first_side < 2; ++first_side) {
                    if (realtime_approximation) {
                        const auto reflection = sample_delayed_path(
                            engine,
                            emitter,
                            initial_source_position,
                            receiver_centre,
                            receive_time,
                            sample_rate,
                            2U,
                            axis,
                            first_side != 0,
                            room_pressure_reflection,
                            solver_iterations);
                        left_sample += receiver_path_sample(reflection, left_forward, left_pattern);
                        right_sample += receiver_path_sample(reflection, right_forward, right_pattern);
                        diffuse_input_left += receiver_diffuse_sample(reflection, left_forward, left_pattern);
                        diffuse_input_right += receiver_diffuse_sample(reflection, right_forward, right_pattern);
                    } else {
                        const auto left_reflection = sample_delayed_path(
                            engine,
                            emitter,
                            initial_source_position,
                            left_position,
                            receive_time,
                            sample_rate,
                            2U,
                            axis,
                            first_side != 0,
                            room_pressure_reflection);
                        const auto right_reflection = sample_delayed_path(
                            engine,
                            emitter,
                            initial_source_position,
                            right_position,
                            receive_time,
                            sample_rate,
                            2U,
                            axis,
                            first_side != 0,
                            room_pressure_reflection);
                        left_sample += receiver_path_sample(left_reflection, left_forward, left_pattern);
                        right_sample += receiver_path_sample(right_reflection, right_forward, right_pattern);
                        diffuse_input_left += receiver_diffuse_sample(left_reflection, left_forward, left_pattern);
                        diffuse_input_right += receiver_diffuse_sample(right_reflection, right_forward, right_pattern);
                    }
                }
            }
        }

        if (reflection_order > 0U) {
            for (std::size_t geometry_index = 0U; geometry_index < engine.geometries.size(); ++geometry_index) {
                const geometry_state& geometry = engine.geometries[geometry_index];
                if (!geometry.enabled || !geometry.reflects) {
                    continue;
                }
                const std::size_t surface_count = reflection_surface_count(geometry);
                for (std::size_t surface_index = 0U; surface_index < surface_count; ++surface_index) {
                    const reflection_surface surface = geometry_reflection_surface(geometry, surface_index);
                    if (realtime_approximation) {
                        const auto reflection = sample_geometry_reflection(
                            engine,
                            emitter,
                            initial_source_position,
                            receiver_centre,
                            receive_time,
                            sample_rate,
                            geometry_index,
                            surface,
                            solver_iterations);
                        left_sample += receiver_path_sample(reflection, left_forward, left_pattern);
                        right_sample += receiver_path_sample(reflection, right_forward, right_pattern);
                        diffuse_input_left += receiver_diffuse_sample(reflection, left_forward, left_pattern);
                        diffuse_input_right += receiver_diffuse_sample(reflection, right_forward, right_pattern);
                        continue;
                    }
                    const auto left_reflection = sample_geometry_reflection(
                        engine,
                        emitter,
                        initial_source_position,
                        left_position,
                        receive_time,
                        sample_rate,
                        geometry_index,
                        surface);
                    const auto right_reflection = sample_geometry_reflection(
                        engine,
                        emitter,
                        initial_source_position,
                        right_position,
                        receive_time,
                        sample_rate,
                        geometry_index,
                        surface);
                    left_sample += receiver_path_sample(left_reflection, left_forward, left_pattern);
                    right_sample += receiver_path_sample(right_reflection, right_forward, right_pattern);
                    diffuse_input_left += receiver_diffuse_sample(left_reflection, left_forward, left_pattern);
                    diffuse_input_right += receiver_diffuse_sample(right_reflection, right_forward, right_pattern);
                }
            }

            const std::size_t mesh_count = engine.mesh_geometries.size();
            if (engine.mesh_reflection_caches.size() == engine.emitters.size() * mesh_count) {
                for (std::size_t mesh_index = 0U; mesh_index < mesh_count; ++mesh_index) {
                    const mesh_reflection_cache& cache =
                        engine.mesh_reflection_caches[(emitter_index * mesh_count) + mesh_index];
                    for (std::uint32_t path_index = 0U; path_index < cache.count; ++path_index) {
                        const std::uint32_t patch_index = cache.patch_indices[path_index];
                        if (realtime_approximation) {
                            const auto reflection = sample_mesh_geometry_reflection(
                                engine,
                                emitter,
                                initial_source_position,
                                receiver_centre,
                                receive_time,
                                sample_rate,
                                mesh_index,
                                patch_index,
                                solver_iterations);
                            left_sample += receiver_path_sample(reflection, left_forward, left_pattern);
                            right_sample += receiver_path_sample(reflection, right_forward, right_pattern);
                            diffuse_input_left += receiver_diffuse_sample(reflection, left_forward, left_pattern);
                            diffuse_input_right += receiver_diffuse_sample(reflection, right_forward, right_pattern);
                            continue;
                        }
                        const auto left_reflection = sample_mesh_geometry_reflection(
                            engine,
                            emitter,
                            initial_source_position,
                            left_position,
                            receive_time,
                            sample_rate,
                            mesh_index,
                            patch_index);
                        const auto right_reflection = sample_mesh_geometry_reflection(
                            engine,
                            emitter,
                            initial_source_position,
                            right_position,
                            receive_time,
                            sample_rate,
                            mesh_index,
                            patch_index);
                        left_sample += receiver_path_sample(left_reflection, left_forward, left_pattern);
                        right_sample += receiver_path_sample(right_reflection, right_forward, right_pattern);
                        diffuse_input_left += receiver_diffuse_sample(left_reflection, left_forward, left_pattern);
                        diffuse_input_right += receiver_diffuse_sample(right_reflection, right_forward, right_pattern);
                    }
                }
                if (reflection_order > 1U &&
                    engine.mesh_second_order_reflection_caches.size() == engine.emitters.size()) {
                    const mesh_second_order_reflection_cache& second_order_cache =
                        engine.mesh_second_order_reflection_caches[emitter_index];
                    for (std::uint32_t path_index = 0U; path_index < second_order_cache.count; ++path_index) {
                        if (realtime_approximation) {
                            const auto reflection = sample_mesh_second_order_reflection(
                                engine,
                                emitter,
                                initial_source_position,
                                receiver_centre,
                                receive_time,
                                sample_rate,
                                second_order_cache.first[path_index],
                                second_order_cache.second[path_index],
                                solver_iterations);
                            left_sample += receiver_path_sample(reflection, left_forward, left_pattern);
                            right_sample += receiver_path_sample(reflection, right_forward, right_pattern);
                            diffuse_input_left += receiver_diffuse_sample(reflection, left_forward, left_pattern);
                            diffuse_input_right += receiver_diffuse_sample(reflection, right_forward, right_pattern);
                            continue;
                        }
                        const auto left_reflection = sample_mesh_second_order_reflection(
                            engine,
                            emitter,
                            initial_source_position,
                            left_position,
                            receive_time,
                            sample_rate,
                            second_order_cache.first[path_index],
                            second_order_cache.second[path_index]);
                        const auto right_reflection = sample_mesh_second_order_reflection(
                            engine,
                            emitter,
                            initial_source_position,
                            right_position,
                            receive_time,
                            sample_rate,
                            second_order_cache.first[path_index],
                            second_order_cache.second[path_index]);
                        left_sample += receiver_path_sample(left_reflection, left_forward, left_pattern);
                        right_sample += receiver_path_sample(right_reflection, right_forward, right_pattern);
                        diffuse_input_left += receiver_diffuse_sample(left_reflection, left_forward, left_pattern);
                        diffuse_input_right += receiver_diffuse_sample(right_reflection, right_forward, right_pattern);
                    }
                }
            }
        }

        left_total += left_sample;
        right_total += right_sample;
    }

    const auto [diffuse_left, diffuse_right] = process_diffuse_response(
        engine,
        receiver_index,
        0.5 * (diffuse_input_left + diffuse_input_right),
        sample_rate,
        reflection_order,
        ray_count,
        impulse_response_seconds,
        seed);
    left_total += diffuse_left;
    right_total += diffuse_right;

    const auto [room_left, room_right] = process_room_response(
        engine,
        receiver_index,
        0.5 * (left_total + right_total),
        sample_rate,
        reflection_order,
        impulse_response_seconds);
    left_total += room_left;
    right_total += room_right;

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

    left_total *= explicit_microphones ? receiver.left_capsule.sensitivity_linear : receiver.sensitivity_linear;
    right_total *= explicit_microphones ? receiver.right_capsule.sensitivity_linear : receiver.sensitivity_linear;
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

void device_data_callback(ma_device* device, void* output, const void* input, ma_uint32 frame_count) noexcept
{
    (void)input;
    auto* engine = static_cast<pg_engine*>(device->pUserData);
    auto* samples = static_cast<float*>(output);
    if (engine == nullptr || samples == nullptr || !engine->device_running.load(std::memory_order_relaxed)) {
        if (samples != nullptr) {
            std::fill_n(samples, static_cast<std::size_t>(frame_count) * 2U, 0.0F);
        }
        return;
    }
    const device_callback_clock::time_point callback_started = device_callback_clock::now();

    const std::uint64_t start_frame = engine->device_frame_cursor.fetch_add(frame_count, std::memory_order_relaxed);
    engine->device_frames_processed.fetch_add(frame_count, std::memory_order_relaxed);
    if (engine->device_buffer_mode) {
        const std::uint64_t buffer_frames = static_cast<std::uint64_t>(engine->device_buffer.size() / 2U);
        for (ma_uint32 frame = 0; frame < frame_count; ++frame) {
            std::uint64_t source_frame = start_frame + frame;
            if (engine->device_buffer_loop && buffer_frames > 0U) {
                source_frame %= buffer_frames;
            }
            const std::size_t output_index = static_cast<std::size_t>(frame) * 2U;
            if (source_frame < buffer_frames) {
                const std::size_t source_index = static_cast<std::size_t>(source_frame) * 2U;
                samples[output_index] = std::clamp(engine->device_buffer[source_index], -1.0F, 1.0F);
                samples[output_index + 1U] = std::clamp(engine->device_buffer[source_index + 1U], -1.0F, 1.0F);
            } else {
                samples[output_index] = 0.0F;
                samples[output_index + 1U] = 0.0F;
            }
        }
        record_device_callback_timing(engine, callback_started, frame_count);
        return;
    }
    const std::uint32_t receiver_index = engine->device_receiver_index;
    const std::uint32_t sample_rate = engine->device_sample_rate;
    const std::uint32_t reflection_order = engine->device_reflection_order;
    if (reflection_order > 0U && !engine->mesh_geometries.empty()) {
        const double receive_time = static_cast<double>(start_frame) / static_cast<double>(sample_rate);
        update_mesh_reflection_caches(
            *engine,
            sample_motion(engine->receivers[receiver_index], receive_time),
            receive_time,
            mesh_reflection_path_limit(engine->device_ray_count, true),
            reflection_order > 1U);
    }
    for (ma_uint32 frame = 0; frame < frame_count; ++frame) {
        const double receive_time = static_cast<double>(start_frame + frame) / static_cast<double>(sample_rate);
        const auto [left, right] = render_frame(
            *engine,
            receiver_index,
            receive_time,
            sample_rate,
            reflection_order,
            engine->device_ray_count,
            engine->device_impulse_response_seconds,
            engine->device_seed,
            true);
        samples[static_cast<std::size_t>(frame) * 2U] = left;
        samples[(static_cast<std::size_t>(frame) * 2U) + 1U] = right;
    }
    record_device_callback_timing(engine, callback_started, frame_count);
}

void device_notification_callback(const ma_device_notification* notification) noexcept
{
    if (notification == nullptr || notification->pDevice == nullptr) {
        return;
    }
    auto* engine = static_cast<pg_engine*>(notification->pDevice->pUserData);
    if (engine != nullptr && notification->type == ma_device_notification_type_stopped) {
        if (engine->device_running.exchange(false, std::memory_order_acq_rel)) {
            engine->device_interruption_count.fetch_add(1U, std::memory_order_relaxed);
        }
    }
}

pg_status initialize_playback_device(pg_engine* engine)
{
    ma_device_config device_config = ma_device_config_init(ma_device_type_playback);
    device_config.sampleRate = engine->device_sample_rate;
    device_config.periodSizeInFrames = engine->device_period_frames;
    device_config.performanceProfile = ma_performance_profile_low_latency;
    device_config.playback.format = ma_format_f32;
    device_config.playback.channels = 2U;
    device_config.playback.shareMode = engine->device_exclusive_mode ? ma_share_mode_exclusive : ma_share_mode_shared;
    device_config.dataCallback = device_data_callback;
    device_config.notificationCallback = device_notification_callback;
    device_config.pUserData = engine;

#if defined(_WIN32)
    constexpr ma_backend backends[]{ ma_backend_wasapi };
    const ma_result init_result = ma_device_init_ex(backends, 1U, nullptr, &device_config, &engine->device);
#else
    const ma_result init_result = ma_device_init(nullptr, &device_config, &engine->device);
#endif
    if (init_result != MA_SUCCESS) {
        engine->device_initialized = false;
        engine->device_running.store(false, std::memory_order_release);
        return fail(engine, PG_STATUS_INTERNAL_ERROR, std::string("Unable to initialize the audio device: ") + ma_result_description(init_result));
    }
    engine->device_initialized = true;
    engine->device_sample_rate = engine->device.sampleRate;
    if (engine->device.playback.internalPeriodSizeInFrames > 0U) {
        engine->device_period_frames = engine->device.playback.internalPeriodSizeInFrames;
    }

    if (!engine->device_buffer_mode) {
        const std::uint32_t receiver_index = engine->device_receiver_index;
        engine->receiver_filters[receiver_index] = {};
        reset_room_response(
            engine->receiver_room_responses[receiver_index],
            engine->device_sample_rate,
            engine->room,
            engine->device_impulse_response_seconds);
        const double diffuse_decay_time = engine->device_impulse_response_seconds > 0.0
            ? std::clamp(engine->device_impulse_response_seconds, 0.05, 30.0)
            : 0.0;
        reset_diffuse_response(
            engine->receiver_diffuse_responses[receiver_index],
            engine->device_sample_rate,
            engine->device_ray_count,
            diffuse_decay_time,
            engine->device_seed);
    }

    engine->device_running.store(true, std::memory_order_release);
    const ma_result start_result = ma_device_start(&engine->device);
    if (start_result != MA_SUCCESS) {
        engine->device_running.store(false, std::memory_order_release);
        ma_device_uninit(&engine->device);
        engine->device_initialized = false;
        return fail(engine, PG_STATUS_INTERNAL_ERROR, std::string("Unable to start the audio device: ") + ma_result_description(start_result));
    }
    engine->last_error.clear();
    return PG_STATUS_OK;
}

double mesh_material_specular_gain(
    const acoustic_material_state& material,
    double frequency_hz) noexcept
{
    const double absorption = material_band_value(material, material.absorption, frequency_hz);
    const double transmission = material_band_value(material, material.transmission, frequency_hz);
    const double scattering = material_band_value(material, material.scattering, frequency_hz);
    const double reflection_energy = std::max(0.0, 1.0 - absorption - transmission);
    const double high_frequency_amount = std::clamp(
        std::log2(std::max(frequency_hz, 125.0) / 125.0) / 7.0,
        0.0,
        1.0);
    return std::sqrt(reflection_energy * (1.0 - scattering)) *
        (1.0 - (0.5 * material.roughness * high_frequency_amount));
}

bool solve_mesh_first_order_diagnostic(
    const pg_engine& engine,
    const emitter_state& emitter,
    const mesh_geometry_state& mesh,
    std::uint32_t patch_index,
    vec3 receiver_position,
    double receive_time,
    vec3& source_position,
    vec3& reflection_point,
    propagation_integral& propagation) noexcept
{
    double emission_time = receive_time;
    vec3 image_source{};
    source_position = sample_motion(emitter, receive_time);
    for (std::uint32_t iteration = 0U; iteration < 6U; ++iteration) {
        if (iteration > 0U) {
            source_position = sample_motion(emitter, emission_time);
        }
        if (!mesh_patch_reflection_point(
                mesh, patch_index, source_position, receiver_position, image_source, reflection_point, false)) {
            return false;
        }
        propagation = integrate_propagation_path(
            engine.atmosphere,
            std::array{ source_position, reflection_point, receiver_position },
            emitter.frequency_hz,
            receive_time);
        emission_time = receive_time - propagation.travel_time_seconds;
    }
    source_position = sample_motion(emitter, emission_time);
    if (!mesh_patch_reflection_point(
            mesh, patch_index, source_position, receiver_position, image_source, reflection_point, true)) {
        return false;
    }
    propagation = integrate_propagation_path(
        engine.atmosphere,
        std::array{ source_position, reflection_point, receiver_position },
        emitter.frequency_hz,
        receive_time);
    return true;
}

bool solve_mesh_second_order_diagnostic(
    const pg_engine& engine,
    const emitter_state& emitter,
    const mesh_geometry_state& first_mesh,
    std::uint32_t first_patch_index,
    const mesh_geometry_state& second_mesh,
    std::uint32_t second_patch_index,
    vec3 receiver_position,
    double receive_time,
    vec3& source_position,
    vec3& first_point,
    vec3& second_point,
    propagation_integral& propagation) noexcept
{
    double emission_time = receive_time;
    source_position = sample_motion(emitter, receive_time);
    for (std::uint32_t iteration = 0U; iteration < 6U; ++iteration) {
        if (iteration > 0U) {
            source_position = sample_motion(emitter, emission_time);
        }
        if (!mesh_second_order_reflection_points(
                first_mesh,
                first_patch_index,
                second_mesh,
                second_patch_index,
                source_position,
                receiver_position,
                first_point,
                second_point,
                false)) {
            return false;
        }
        propagation = integrate_propagation_path(
            engine.atmosphere,
            std::array{ source_position, first_point, second_point, receiver_position },
            emitter.frequency_hz,
            receive_time);
        emission_time = receive_time - propagation.travel_time_seconds;
    }
    source_position = sample_motion(emitter, emission_time);
    if (!mesh_second_order_reflection_points(
            first_mesh,
            first_patch_index,
            second_mesh,
            second_patch_index,
            source_position,
            receiver_position,
            first_point,
            second_point,
            true)) {
        return false;
    }
    propagation = integrate_propagation_path(
        engine.atmosphere,
        std::array{ source_position, first_point, second_point, receiver_position },
        emitter.frequency_hz,
        receive_time);
    return true;
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
        engine->acoustic_materials.reserve(maximum_acoustic_materials);
        engine->geometries.reserve(maximum_acoustic_geometries);
        engine->mesh_geometries.reserve(maximum_imported_mesh_geometries);
        engine->emitters.reserve(config->max_emitters);
        engine->receivers.reserve(config->max_receivers);
        engine->receiver_filters.reserve(config->max_receivers);
        engine->receiver_next_frames.reserve(config->max_receivers);
        engine->receiver_room_responses.reserve(config->max_receivers);
        engine->receiver_diffuse_responses.reserve(config->max_receivers);
        *out_engine = engine;
        return PG_STATUS_OK;
    } catch (...) {
        return PG_STATUS_INTERNAL_ERROR;
    }
}

void PG_CALL pg_engine_destroy(pg_engine* engine)
{
    if (engine == nullptr) {
        return;
    }
    (void)pg_engine_stop_device(engine);
    delete engine;
}

pg_status PG_CALL pg_engine_clear_scene(pg_engine* engine)
{
    if (engine == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (require_stopped_device(engine, "Clearing the scene") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    engine->acoustic_materials.clear();
    engine->geometries.clear();
    engine->mesh_geometries.clear();
    engine->emitters.clear();
    engine->receivers.clear();
    engine->receiver_filters.clear();
    engine->receiver_next_frames.clear();
    engine->receiver_room_responses.clear();
    engine->receiver_diffuse_responses.clear();
    engine->mesh_reflection_caches.clear();
    engine->mesh_second_order_reflection_caches.clear();
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
    if (require_stopped_device(engine, "Changing the atmosphere") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    const vec3 wind = from_c(atmosphere->uniform_wind_metres_per_second);
    if (!finite(wind) || !std::isfinite(atmosphere->temperature_celsius) || atmosphere->temperature_celsius < -80.0 || atmosphere->temperature_celsius > 80.0 ||
        !std::isfinite(atmosphere->relative_humidity_percent) || atmosphere->relative_humidity_percent < 0.0 || atmosphere->relative_humidity_percent > 100.0 ||
        !std::isfinite(atmosphere->pressure_kilopascals) || atmosphere->pressure_kilopascals < 20.0 || atmosphere->pressure_kilopascals > 120.0 ||
        !std::isfinite(atmosphere->turbulence) || atmosphere->turbulence < 0.0 || atmosphere->turbulence > 1.0) {
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

pg_status PG_CALL pg_engine_set_atmosphere_field(pg_engine* engine, const pg_atmosphere_field_desc* atmosphere)
{
    if (engine == nullptr || atmosphere == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(atmosphere)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Atmosphere field structure version or size is incompatible.");
    }
    if (require_stopped_device(engine, "Changing the atmosphere field") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!std::isfinite(atmosphere->temperature_celsius) || atmosphere->temperature_celsius < -80.0 || atmosphere->temperature_celsius > 80.0 ||
        !std::isfinite(atmosphere->relative_humidity_percent) || atmosphere->relative_humidity_percent < 0.0 || atmosphere->relative_humidity_percent > 100.0 ||
        !std::isfinite(atmosphere->pressure_kilopascals) || atmosphere->pressure_kilopascals < 20.0 || atmosphere->pressure_kilopascals > 120.0 ||
        atmosphere->layer_count > PG_MAX_ATMOSPHERE_LAYERS || atmosphere->wind_zone_count > PG_MAX_WIND_ZONES ||
        (atmosphere->layer_count > 0U && atmosphere->layers == nullptr) ||
        (atmosphere->wind_zone_count > 0U && atmosphere->wind_zones == nullptr) ||
        atmosphere->integration_segments < 1U || atmosphere->integration_segments > maximum_atmosphere_integration_segments) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Atmosphere field values or bounded counts are invalid.");
    }

    try {
        std::vector<atmosphere_layer_state> layers;
        layers.reserve(atmosphere->layer_count);
        double minimum_sound_speed = speed_of_sound(
            atmosphere->temperature_celsius,
            atmosphere->relative_humidity_percent);
        for (std::uint32_t index = 0U; index < atmosphere->layer_count; ++index) {
            const pg_atmosphere_layer_desc& layer = atmosphere->layers[index];
            if (!valid_struct(&layer) ||
                !std::isfinite(layer.base_altitude_metres) || !std::isfinite(layer.top_altitude_metres) ||
                layer.top_altitude_metres <= layer.base_altitude_metres ||
                !std::isfinite(layer.temperature_celsius) || layer.temperature_celsius < -80.0 || layer.temperature_celsius > 80.0 ||
                !std::isfinite(layer.relative_humidity_percent) || layer.relative_humidity_percent < 0.0 || layer.relative_humidity_percent > 100.0 ||
                !std::isfinite(layer.pressure_kilopascals) || layer.pressure_kilopascals < 20.0 || layer.pressure_kilopascals > 120.0 ||
                (index > 0U && layer.base_altitude_metres < atmosphere->layers[index - 1U].top_altitude_metres)) {
                return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Atmosphere layers must be versioned, finite, altitude-ordered, and non-overlapping.");
            }
            layers.push_back({
                layer.base_altitude_metres,
                layer.top_altitude_metres,
                layer.temperature_celsius,
                layer.relative_humidity_percent,
                layer.pressure_kilopascals
            });
            minimum_sound_speed = std::min(
                minimum_sound_speed,
                speed_of_sound(layer.temperature_celsius, layer.relative_humidity_percent));
        }

        std::vector<wind_zone_state> wind_zones;
        wind_zones.reserve(atmosphere->wind_zone_count);
        for (std::uint32_t index = 0U; index < atmosphere->wind_zone_count; ++index) {
            const pg_wind_zone_desc& zone = atmosphere->wind_zones[index];
            const vec3 centre = from_c(zone.centre_metres);
            const vec3 dimensions = from_c(zone.dimensions_metres);
            const vec3 rotation = from_c(zone.rotation_degrees);
            const vec3 velocity = from_c(zone.velocity_metres_per_second);
            if (!valid_struct(&zone) || !finite(centre) || !finite(dimensions) || !finite(rotation) || !finite(velocity) ||
                dimensions.x <= 0.0 || dimensions.y <= 0.0 || dimensions.z <= 0.0 ||
                !std::isfinite(zone.turbulence) || zone.turbulence < 0.0 || zone.turbulence > 1.0 ||
                length(velocity) >= minimum_sound_speed * 0.8) {
                return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Wind zones must be versioned, finite, positively bounded, and subsonic.");
            }
            wind_zones.push_back({ centre, dimensions, rotation, velocity, zone.turbulence, zone.noise_seed });
        }

        atmosphere_state state{};
        state.temperature_celsius = atmosphere->temperature_celsius;
        state.relative_humidity_percent = atmosphere->relative_humidity_percent;
        state.pressure_kilopascals = atmosphere->pressure_kilopascals;
        state.layers = std::move(layers);
        state.wind_zones = std::move(wind_zones);
        state.integration_segments = atmosphere->integration_segments;
        engine->atmosphere = std::move(state);
        return PG_STATUS_OK;
    } catch (const std::exception& exception) {
        return fail(engine, PG_STATUS_INTERNAL_ERROR, std::string("Atmosphere field setup failed: ") + exception.what());
    } catch (...) {
        return fail(engine, PG_STATUS_INTERNAL_ERROR, "Atmosphere field setup failed with an unknown error.");
    }
}

pg_status PG_CALL pg_engine_set_room(pg_engine* engine, const pg_room_desc* room)
{
    if (engine == nullptr || room == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(room)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Room structure version or size is incompatible.");
    }
    if (require_stopped_device(engine, "Changing the acoustic room") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
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
    const auto decay_valid = [](double value) { return std::isfinite(value) && value >= 0.0 && value <= 30.0; };
    if (!decay_valid(room->decay_time_seconds) || !decay_valid(room->decay_time_low_seconds) ||
        !decay_valid(room->decay_time_high_seconds)) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Room decay times must be zero for a fallback or bounded values no greater than 30 seconds.");
    }
    const double mid_decay_time = room->decay_time_seconds > 0.0 ? room->decay_time_seconds : 1.5;
    engine->room = {
        from_c(room->centre_metres),
        dimensions,
        room->wall_reflection_low,
        room->wall_reflection_mid,
        room->wall_reflection_high,
        room->scattering,
        room->decay_time_low_seconds > 0.0 ? room->decay_time_low_seconds : mid_decay_time,
        mid_decay_time,
        room->decay_time_high_seconds > 0.0 ? room->decay_time_high_seconds : mid_decay_time,
        room->enabled != 0U
    };
    for (auto& response : engine->receiver_room_responses) {
        response.sample_rate = 0U;
    }
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_add_acoustic_material(
    pg_engine* engine,
    const pg_acoustic_material_desc* material,
    uint32_t* out_index)
{
    if (engine == nullptr || material == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(material)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Acoustic material structure version or size is incompatible.");
    }
    if (require_stopped_device(engine, "Adding an acoustic material") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (engine->acoustic_materials.size() >= maximum_acoustic_materials) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Acoustic material capacity exceeded.");
    }
    if (!std::isfinite(material->roughness) || material->roughness < 0.0 || material->roughness > 1.0) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Acoustic material roughness must be in 0..1.");
    }

    acoustic_material_state state{};
    state.name = safe_name(material->name_utf8, "Material");
    state.roughness = material->roughness;
    for (std::size_t index = 0U; index < PG_ACOUSTIC_BAND_COUNT; ++index) {
        const double centre = material->octave_band_centres_hz[index];
        const double absorption = material->absorption[index];
        const double scattering = material->scattering[index];
        const double transmission = material->transmission[index];
        if (!std::isfinite(centre) || centre <= 0.0 ||
            (index > 0U && centre <= material->octave_band_centres_hz[index - 1U]) ||
            !std::isfinite(absorption) || !std::isfinite(scattering) || !std::isfinite(transmission) ||
            absorption < 0.0 || absorption > 1.0 || scattering < 0.0 || scattering > 1.0 ||
            transmission < 0.0 || transmission > 1.0 || absorption + transmission > 1.0 + 1.0e-12) {
            return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Acoustic material bands must be ordered, bounded, and conserve incident energy.");
        }
        state.octave_band_centres_hz[index] = centre;
        state.absorption[index] = absorption;
        state.scattering[index] = scattering;
        state.transmission[index] = transmission;
    }

    engine->acoustic_materials.push_back(std::move(state));
    if (out_index != nullptr) {
        *out_index = static_cast<std::uint32_t>(engine->acoustic_materials.size() - 1U);
    }
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_add_geometry(pg_engine* engine, const pg_geometry_desc* geometry, uint32_t* out_index)
{
    if (engine == nullptr || geometry == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(geometry)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Acoustic geometry structure version or size is incompatible.");
    }
    if (require_stopped_device(engine, "Adding acoustic geometry") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (engine->geometries.size() >= maximum_acoustic_geometries) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Acoustic geometry capacity exceeded.");
    }
    if (geometry->material_index >= engine->acoustic_materials.size()) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Acoustic geometry references an unknown material index.");
    }
    const vec3 centre = from_c(geometry->centre_metres);
    const vec3 dimensions = from_c(geometry->dimensions_metres);
    const vec3 rotation = from_c(geometry->rotation_degrees);
    const bool plane_dimensions_valid = dimensions.x > 0.0 && dimensions.y > 0.0 && dimensions.z >= 0.0;
    const bool box_dimensions_valid = dimensions.x > 0.0 && dimensions.y > 0.0 && dimensions.z > 0.0;
    if (!finite(centre) || !finite(dimensions) || !finite(rotation) ||
        (geometry->kind == PG_GEOMETRY_PLANE && !plane_dimensions_valid) ||
        (geometry->kind == PG_GEOMETRY_BOX && !box_dimensions_valid) ||
        (geometry->kind != PG_GEOMETRY_PLANE && geometry->kind != PG_GEOMETRY_BOX)) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Acoustic geometry transform, dimensions, or kind is invalid.");
    }

    geometry_state state{};
    state.name = safe_name(geometry->name_utf8, "Geometry");
    state.kind = geometry->kind;
    state.centre = centre;
    state.dimensions = dimensions;
    state.rotation_degrees = rotation;
    state.material_index = geometry->material_index;
    state.occludes = geometry->occludes != 0U;
    state.reflects = geometry->reflects != 0U;
    state.enabled = geometry->enabled != 0U;
    engine->geometries.push_back(std::move(state));
    if (out_index != nullptr) {
        *out_index = static_cast<std::uint32_t>(engine->geometries.size() - 1U);
    }
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_add_mesh_geometry(
    pg_engine* engine,
    const pg_mesh_geometry_desc* geometry,
    uint32_t* out_index,
    pg_mesh_import_receipt* out_receipt)
{
    if (engine == nullptr || geometry == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(geometry) || (out_receipt != nullptr && !valid_struct(out_receipt))) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Mesh geometry or import receipt structure version or size is incompatible.");
    }
    if (require_stopped_device(engine, "Importing acoustic mesh geometry") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (engine->mesh_geometries.size() >= maximum_imported_mesh_geometries) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Imported acoustic mesh capacity exceeded.");
    }
    if (geometry->material_index >= engine->acoustic_materials.size()) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Imported acoustic mesh references an unknown material index.");
    }
    if (geometry->material_binding_count > PG_MAX_MESH_MATERIAL_REGIONS ||
        (geometry->material_binding_count > 0U && geometry->material_bindings == nullptr)) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Imported mesh material bindings exceed the bounded region capacity or are missing.");
    }
    for (std::uint32_t index = 0U; index < geometry->material_binding_count; ++index) {
        const pg_mesh_material_binding& binding = geometry->material_bindings[index];
        if (!valid_struct(&binding) || binding.material_index >= engine->acoustic_materials.size()) {
            return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Imported mesh material binding is incompatible or references an unknown acoustic material.");
        }
        for (std::uint32_t previous = 0U; previous < index; ++previous) {
            if (geometry->material_bindings[previous].source_mesh_index == binding.source_mesh_index) {
                return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Imported mesh material bindings must identify each source submesh at most once.");
            }
        }
    }
    if (geometry->encoded_data == nullptr || geometry->encoded_size_bytes == 0U ||
        geometry->encoded_size_bytes > maximum_mesh_asset_bytes ||
        geometry->encoded_size_bytes > static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max())) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Imported mesh data must contain between 1 byte and 512 MiB.");
    }

    const vec3 centre = from_c(geometry->centre_metres);
    const vec3 scale = from_c(geometry->scale);
    const vec3 rotation = from_c(geometry->rotation_degrees);
    if (!finite(centre) || !finite(scale) || !finite(rotation) ||
        std::abs(scale.x) <= 1.0e-12 || std::abs(scale.y) <= 1.0e-12 || std::abs(scale.z) <= 1.0e-12) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Imported mesh transform must be finite with non-zero scale components.");
    }

    const std::string format = normalized_format_hint(geometry->format_hint_utf8);
    if (format != "obj" && format != "gltf" && format != "glb") {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Imported mesh format must be OBJ, glTF, or GLB.");
    }
    const std::uint32_t triangle_limit = geometry->max_triangles == 0U
        ? PG_MAX_IMPORTED_MESH_TRIANGLES
        : geometry->max_triangles;
    if (triangle_limit > PG_MAX_IMPORTED_MESH_TRIANGLES) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Imported mesh triangle limit exceeds the engine safety cap.");
    }

    std::size_t existing_triangle_count = 0U;
    for (const mesh_geometry_state& existing : engine->mesh_geometries) {
        existing_triangle_count += existing.triangles.size();
    }

    try {
        Assimp::Importer importer;
        importer.SetIOHandler(new memory_only_io_system{});
        constexpr unsigned int postprocess_flags =
            aiProcess_Triangulate |
            aiProcess_JoinIdenticalVertices |
            aiProcess_PreTransformVertices |
            aiProcess_SortByPType |
            aiProcess_ValidateDataStructure |
            aiProcess_FindDegenerates;
        const aiScene* scene = importer.ReadFileFromMemory(
            geometry->encoded_data,
            static_cast<std::size_t>(geometry->encoded_size_bytes),
            postprocess_flags,
            format.c_str());
        if (scene == nullptr || scene->mNumMeshes == 0U || scene->mMeshes == nullptr) {
            const char* detail = importer.GetErrorString();
            return fail(engine, PG_STATUS_INVALID_SCENE,
                std::string("Assimp could not decode the bounded ") + format + " mesh: " +
                (detail == nullptr || detail[0] == '\0' ? "no triangle meshes were found." : detail));
        }
        if (scene->mNumMeshes > PG_MAX_MESH_MATERIAL_REGIONS) {
            return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Imported mesh contains more source submeshes than the material-region safety cap.");
        }
        for (std::uint32_t index = 0U; index < geometry->material_binding_count; ++index) {
            const pg_mesh_material_binding& binding = geometry->material_bindings[index];
            if (binding.source_mesh_index >= scene->mNumMeshes) {
                return fail(engine, PG_STATUS_INVALID_SCENE, "Imported mesh material binding references a source submesh that is not present in the verified asset.");
            }
            const aiMesh* source_mesh = scene->mMeshes[binding.source_mesh_index];
            if (source_mesh == nullptr || binding.source_material_index != source_mesh->mMaterialIndex) {
                return fail(engine, PG_STATUS_INVALID_SCENE, "Imported mesh source material changed after the project binding was authored.");
            }
        }

        std::uint64_t source_vertex_count = 0U;
        std::uint64_t candidate_triangle_count = 0U;
        for (unsigned int mesh_index = 0U; mesh_index < scene->mNumMeshes; ++mesh_index) {
            const aiMesh* source_mesh = scene->mMeshes[mesh_index];
            if (source_mesh == nullptr) {
                continue;
            }
            source_vertex_count += source_mesh->mNumVertices;
            for (unsigned int face_index = 0U; face_index < source_mesh->mNumFaces; ++face_index) {
                if (source_mesh->mFaces[face_index].mNumIndices == 3U) {
                    ++candidate_triangle_count;
                }
            }
        }
        if (candidate_triangle_count == 0U) {
            return fail(engine, PG_STATUS_INVALID_SCENE, "Imported mesh contains no triangle primitives.");
        }
        if (source_vertex_count > std::numeric_limits<std::uint32_t>::max() ||
            candidate_triangle_count > triangle_limit ||
            candidate_triangle_count + existing_triangle_count > maximum_imported_mesh_triangles_total) {
            return fail(engine, PG_STATUS_CAPACITY_EXCEEDED,
                "Imported mesh exceeds the per-asset or total triangle safety cap.");
        }

        mesh_geometry_state state{};
        state.name = safe_name(geometry->name_utf8, "ImportedMesh");
        state.material_index = geometry->material_index;
        state.occludes = geometry->occludes != 0U;
        state.reflects = geometry->reflects != 0U;
        state.enabled = geometry->enabled != 0U;
        state.triangles.reserve(static_cast<std::size_t>(candidate_triangle_count));
        state.material_regions.reserve(scene->mNumMeshes);

        for (unsigned int mesh_index = 0U; mesh_index < scene->mNumMeshes; ++mesh_index) {
            const aiMesh* source_mesh = scene->mMeshes[mesh_index];
            if (source_mesh == nullptr || source_mesh->mVertices == nullptr) {
                continue;
            }
            std::uint32_t resolved_material_index = geometry->material_index;
            for (std::uint32_t binding_index = 0U; binding_index < geometry->material_binding_count; ++binding_index) {
                const pg_mesh_material_binding& binding = geometry->material_bindings[binding_index];
                if (binding.source_mesh_index == mesh_index) {
                    resolved_material_index = binding.material_index;
                    break;
                }
            }
            aiString material_name;
            if (scene->mMaterials != nullptr && source_mesh->mMaterialIndex < scene->mNumMaterials &&
                scene->mMaterials[source_mesh->mMaterialIndex] != nullptr) {
                (void)scene->mMaterials[source_mesh->mMaterialIndex]->Get(AI_MATKEY_NAME, material_name);
            }
            const std::size_t region_index = state.material_regions.size();
            state.material_regions.push_back({
                mesh_index,
                source_mesh->mMaterialIndex,
                resolved_material_index,
                0U,
                safe_name(source_mesh->mName.C_Str(), "Submesh"),
                safe_name(material_name.C_Str(), "SourceMaterial")
            });
            for (unsigned int face_index = 0U; face_index < source_mesh->mNumFaces; ++face_index) {
                const aiFace& face = source_mesh->mFaces[face_index];
                if (face.mNumIndices != 3U || face.mIndices == nullptr ||
                    face.mIndices[0] >= source_mesh->mNumVertices || face.mIndices[1] >= source_mesh->mNumVertices ||
                    face.mIndices[2] >= source_mesh->mNumVertices) {
                    continue;
                }
                const auto transformed_vertex = [source_mesh, &face, scale, rotation, centre](unsigned int index) {
                    const aiVector3D& source = source_mesh->mVertices[face.mIndices[index]];
                    const vec3 scaled{
                        static_cast<double>(source.x) * scale.x,
                        static_cast<double>(source.y) * scale.y,
                        static_cast<double>(source.z) * scale.z
                    };
                    return rotate_euler(scaled, rotation) + centre;
                };
                const vec3 a = transformed_vertex(0U);
                const vec3 b = transformed_vertex(1U);
                const vec3 c = transformed_vertex(2U);
                if (!finite(a) || !finite(b) || !finite(c)) {
                    return fail(engine, PG_STATUS_INVALID_SCENE, "Imported mesh contains a non-finite transformed vertex.");
                }
                const vec3 area_normal = cross(b - a, c - a);
                const double twice_area = length(area_normal);
                if (!finite(area_normal) || !std::isfinite(twice_area)) {
                    return fail(engine, PG_STATUS_INVALID_SCENE, "Imported mesh produces non-finite transformed triangle area.");
                }
                if (twice_area <= 1.0e-12) {
                    continue;
                }
                const axis_aligned_bounds bounds = triangle_bounds(a, b, c);
                state.triangles.push_back({
                    a,
                    b,
                    c,
                    bounds,
                    (a + b + c) * (1.0 / 3.0),
                    mesh_index,
                    source_mesh->mMaterialIndex,
                    resolved_material_index,
                    std::numeric_limits<std::uint32_t>::max()
                });
                ++state.material_regions[region_index].triangle_count;
                expand(state.bounds, bounds);
            }
            if (state.material_regions[region_index].triangle_count == 0U) {
                state.material_regions.pop_back();
            }
        }
        if (state.triangles.empty()) {
            return fail(engine, PG_STATUS_INVALID_SCENE, "Imported mesh contains no non-degenerate triangles.");
        }

        if (state.reflects) {
            build_mesh_reflection_patches(state);
        }

        state.triangle_indices.resize(state.triangles.size());
        std::iota(state.triangle_indices.begin(), state.triangle_indices.end(), 0U);
        state.bvh.reserve(state.triangles.size() * 2U);
        (void)build_mesh_bvh(state, 0U, static_cast<std::uint32_t>(state.triangles.size()));

        const std::uint32_t triangle_count = static_cast<std::uint32_t>(state.triangles.size());
        const std::uint32_t bvh_node_count = static_cast<std::uint32_t>(state.bvh.size());
        const axis_aligned_bounds imported_bounds = state.bounds;
        engine->mesh_geometries.push_back(std::move(state));
        if (out_index != nullptr) {
            *out_index = static_cast<std::uint32_t>(engine->mesh_geometries.size() - 1U);
        }
        if (out_receipt != nullptr) {
            out_receipt->source_mesh_count = scene->mNumMeshes;
            out_receipt->source_vertex_count = static_cast<std::uint32_t>(source_vertex_count);
            out_receipt->triangle_count = triangle_count;
            out_receipt->bvh_node_count = bvh_node_count;
            out_receipt->bounds_min_metres = {
                imported_bounds.minimum.x,
                imported_bounds.minimum.y,
                imported_bounds.minimum.z
            };
            out_receipt->bounds_max_metres = {
                imported_bounds.maximum.x,
                imported_bounds.maximum.y,
                imported_bounds.maximum.z
            };
            out_receipt->source_material_count = scene->mNumMaterials;
            out_receipt->material_region_count = static_cast<std::uint32_t>(engine->mesh_geometries.back().material_regions.size());
        }
        return PG_STATUS_OK;
    } catch (const std::exception& exception) {
        return fail(engine, PG_STATUS_INTERNAL_ERROR, std::string("Imported mesh processing failed: ") + exception.what());
    } catch (...) {
        return fail(engine, PG_STATUS_INTERNAL_ERROR, "Imported mesh processing failed with an unknown error.");
    }
}

pg_status PG_CALL pg_engine_copy_mesh_triangles(
    pg_engine* engine,
    uint32_t mesh_index,
    pg_vec3* out_triangle_vertices,
    uint32_t vertex_capacity,
    uint32_t* out_vertex_count)
{
    if (engine == nullptr || out_vertex_count == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (require_stopped_device(engine, "Copying imported mesh triangles") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (mesh_index >= engine->mesh_geometries.size()) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Imported mesh index is out of range.");
    }

    const mesh_geometry_state& mesh = engine->mesh_geometries[mesh_index];
    const std::size_t required_size = mesh.triangles.size() * 3U;
    if (required_size > std::numeric_limits<std::uint32_t>::max()) {
        return fail(engine, PG_STATUS_INTERNAL_ERROR, "Imported mesh vertex count exceeds the public copy range.");
    }
    const std::uint32_t required = static_cast<std::uint32_t>(required_size);
    *out_vertex_count = required;
    if (out_triangle_vertices == nullptr) {
        return vertex_capacity == 0U
            ? PG_STATUS_OK
            : fail(engine, PG_STATUS_INVALID_ARGUMENT, "A non-zero mesh copy capacity requires an output buffer.");
    }
    if (vertex_capacity < required) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Imported mesh triangle output buffer is too small.");
    }

    std::uint32_t output_index = 0U;
    for (const mesh_triangle& triangle : mesh.triangles) {
        out_triangle_vertices[output_index++] = { triangle.a.x, triangle.a.y, triangle.a.z };
        out_triangle_vertices[output_index++] = { triangle.b.x, triangle.b.y, triangle.b.z };
        out_triangle_vertices[output_index++] = { triangle.c.x, triangle.c.y, triangle.c.z };
    }
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_copy_mesh_material_regions(
    pg_engine* engine,
    uint32_t mesh_index,
    pg_mesh_material_region* out_regions,
    uint32_t region_capacity,
    uint32_t* out_region_count)
{
    if (engine == nullptr || out_region_count == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (require_stopped_device(engine, "Copying imported mesh material regions") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (mesh_index >= engine->mesh_geometries.size()) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Imported mesh index is out of range.");
    }
    const mesh_geometry_state& mesh = engine->mesh_geometries[mesh_index];
    const std::uint32_t required = static_cast<std::uint32_t>(mesh.material_regions.size());
    *out_region_count = required;
    if (out_regions == nullptr) {
        return region_capacity == 0U
            ? PG_STATUS_OK
            : fail(engine, PG_STATUS_INVALID_ARGUMENT, "A non-zero material-region capacity requires an output buffer.");
    }
    if (region_capacity < required) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Imported mesh material-region output buffer is too small.");
    }

    for (std::uint32_t index = 0U; index < required; ++index) {
        const mesh_material_region_state& source = mesh.material_regions[index];
        pg_mesh_material_region& destination = out_regions[index];
        std::memset(&destination, 0, sizeof(destination));
        destination.size = sizeof(destination);
        destination.version = PG_ABI_VERSION;
        destination.source_mesh_index = source.source_mesh_index;
        destination.source_material_index = source.source_material_index;
        destination.material_index = source.material_index;
        destination.triangle_count = source.triangle_count;
        std::memcpy(
            destination.source_mesh_name_utf8,
            source.source_mesh_name.data(),
            std::min(source.source_mesh_name.size(), sizeof(destination.source_mesh_name_utf8) - 1U));
        std::memcpy(
            destination.source_material_name_utf8,
            source.source_material_name.data(),
            std::min(source.source_material_name.size(), sizeof(destination.source_material_name_utf8) - 1U));
    }
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_query_reflection_paths(
    pg_engine* engine,
    const pg_reflection_path_query* query,
    pg_reflection_path* out_paths,
    uint32_t path_capacity,
    uint32_t* out_path_count)
{
    if (engine == nullptr || query == nullptr || out_path_count == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(query)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Reflection-path query structure version or size is incompatible.");
    }
    if (require_stopped_device(engine, "Querying imported-mesh reflection paths") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (query->receiver_index >= engine->receivers.size() || query->reflection_order > 2U ||
        !std::isfinite(query->receive_time_seconds) || query->receive_time_seconds < 0.0 ||
        query->sample_rate < 8000U || query->sample_rate > maximum_supported_sample_rate) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Reflection-path query receiver, time, order, or sample rate is invalid.");
    }
    if (out_paths == nullptr && path_capacity != 0U) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "A non-zero reflection-path capacity requires an output buffer.");
    }
    *out_path_count = 0U;
    if (query->reflection_order == 0U || engine->mesh_geometries.empty() || engine->emitters.empty()) {
        return PG_STATUS_OK;
    }

    const pg_status cache_status = prepare_mesh_reflection_caches(engine);
    if (cache_status != PG_STATUS_OK) {
        return cache_status;
    }
    const receiver_state& receiver = engine->receivers[query->receiver_index];
    if (!receiver.enabled) {
        return PG_STATUS_OK;
    }
    const vec3 receiver_position = sample_motion(receiver, query->receive_time_seconds);
    update_mesh_reflection_caches(
        *engine,
        receiver_position,
        query->receive_time_seconds,
        mesh_reflection_path_limit(query->ray_count, false),
        query->reflection_order > 1U);

    std::uint32_t required_count = 0U;
    const auto append_path = [&](const pg_reflection_path& path) noexcept {
        if (out_paths != nullptr && required_count < path_capacity) {
            out_paths[required_count] = path;
        }
        if (required_count < std::numeric_limits<std::uint32_t>::max()) {
            ++required_count;
        }
    };
    const std::size_t mesh_count = engine->mesh_geometries.size();
    for (std::size_t emitter_index = 0U; emitter_index < engine->emitters.size(); ++emitter_index) {
        const emitter_state& emitter = engine->emitters[emitter_index];
        if (!emitter.enabled) {
            continue;
        }
        for (std::size_t mesh_index = 0U; mesh_index < mesh_count; ++mesh_index) {
            const mesh_geometry_state& mesh = engine->mesh_geometries[mesh_index];
            const mesh_reflection_cache& cache =
                engine->mesh_reflection_caches[(emitter_index * mesh_count) + mesh_index];
            for (std::uint32_t path_index = 0U; path_index < cache.count; ++path_index) {
                const std::uint32_t patch_index = cache.patch_indices[path_index];
                if (patch_index >= mesh.reflection_patches.size()) {
                    continue;
                }
                const mesh_reflection_patch& patch = mesh.reflection_patches[patch_index];
                if (patch.material_index >= engine->acoustic_materials.size()) {
                    continue;
                }
                vec3 source_position{};
                vec3 reflection_point{};
                propagation_integral propagation{};
                if (!solve_mesh_first_order_diagnostic(
                        *engine,
                        emitter,
                        mesh,
                        patch_index,
                        receiver_position,
                        query->receive_time_seconds,
                        source_position,
                        reflection_point,
                        propagation)) {
                    continue;
                }
                const double visibility =
                    occlusion_transmission_gain(*engine, source_position, reflection_point, emitter.frequency_hz) *
                    occlusion_transmission_gain(*engine, reflection_point, receiver_position, emitter.frequency_hz);
                pg_reflection_path path{};
                path.size = sizeof(path);
                path.version = PG_ABI_VERSION;
                path.kind = PG_REFLECTION_IMPORTED_MESH;
                path.reflection_order = 1U;
                path.emitter_index = static_cast<std::uint32_t>(emitter_index);
                path.receiver_index = query->receiver_index;
                path.first_mesh_index = static_cast<std::uint32_t>(mesh_index);
                path.second_mesh_index = std::numeric_limits<std::uint32_t>::max();
                path.first_patch_index = patch_index;
                path.second_patch_index = std::numeric_limits<std::uint32_t>::max();
                path.first_material_index = patch.material_index;
                path.second_material_index = std::numeric_limits<std::uint32_t>::max();
                path.point_count = 3U;
                path.points[0] = { source_position.x, source_position.y, source_position.z };
                path.points[1] = { reflection_point.x, reflection_point.y, reflection_point.z };
                path.points[2] = { receiver_position.x, receiver_position.y, receiver_position.z };
                path.distance_metres = propagation.distance_metres;
                path.travel_time_seconds = propagation.travel_time_seconds;
                path.pressure_gain = emitter.gain_linear * std::exp(-propagation.absorption_exponent) * visibility *
                    mesh_material_specular_gain(engine->acoustic_materials[patch.material_index], emitter.frequency_hz) /
                    std::max(propagation.distance_metres, emitter.min_distance_metres);
                append_path(path);
            }
        }

        if (query->reflection_order < 2U ||
            engine->mesh_second_order_reflection_caches.size() != engine->emitters.size()) {
            continue;
        }
        const mesh_second_order_reflection_cache& cache = engine->mesh_second_order_reflection_caches[emitter_index];
        for (std::uint32_t path_index = 0U; path_index < cache.count; ++path_index) {
            const mesh_patch_reference first_reference = cache.first[path_index];
            const mesh_patch_reference second_reference = cache.second[path_index];
            if (first_reference.mesh_index >= mesh_count || second_reference.mesh_index >= mesh_count) {
                continue;
            }
            const mesh_geometry_state& first_mesh = engine->mesh_geometries[first_reference.mesh_index];
            const mesh_geometry_state& second_mesh = engine->mesh_geometries[second_reference.mesh_index];
            if (first_reference.patch_index >= first_mesh.reflection_patches.size() ||
                second_reference.patch_index >= second_mesh.reflection_patches.size()) {
                continue;
            }
            const mesh_reflection_patch& first_patch = first_mesh.reflection_patches[first_reference.patch_index];
            const mesh_reflection_patch& second_patch = second_mesh.reflection_patches[second_reference.patch_index];
            if (first_patch.material_index >= engine->acoustic_materials.size() ||
                second_patch.material_index >= engine->acoustic_materials.size()) {
                continue;
            }
            vec3 source_position{};
            vec3 first_point{};
            vec3 second_point{};
            propagation_integral propagation{};
            if (!solve_mesh_second_order_diagnostic(
                    *engine,
                    emitter,
                    first_mesh,
                    first_reference.patch_index,
                    second_mesh,
                    second_reference.patch_index,
                    receiver_position,
                    query->receive_time_seconds,
                    source_position,
                    first_point,
                    second_point,
                    propagation)) {
                continue;
            }
            const double visibility =
                occlusion_transmission_gain(*engine, source_position, first_point, emitter.frequency_hz) *
                occlusion_transmission_gain(*engine, first_point, second_point, emitter.frequency_hz) *
                occlusion_transmission_gain(*engine, second_point, receiver_position, emitter.frequency_hz);
            pg_reflection_path path{};
            path.size = sizeof(path);
            path.version = PG_ABI_VERSION;
            path.kind = PG_REFLECTION_IMPORTED_MESH;
            path.reflection_order = 2U;
            path.emitter_index = static_cast<std::uint32_t>(emitter_index);
            path.receiver_index = query->receiver_index;
            path.first_mesh_index = first_reference.mesh_index;
            path.second_mesh_index = second_reference.mesh_index;
            path.first_patch_index = first_reference.patch_index;
            path.second_patch_index = second_reference.patch_index;
            path.first_material_index = first_patch.material_index;
            path.second_material_index = second_patch.material_index;
            path.point_count = 4U;
            path.points[0] = { source_position.x, source_position.y, source_position.z };
            path.points[1] = { first_point.x, first_point.y, first_point.z };
            path.points[2] = { second_point.x, second_point.y, second_point.z };
            path.points[3] = { receiver_position.x, receiver_position.y, receiver_position.z };
            path.distance_metres = propagation.distance_metres;
            path.travel_time_seconds = propagation.travel_time_seconds;
            path.pressure_gain = emitter.gain_linear * std::exp(-propagation.absorption_exponent) * visibility *
                mesh_material_specular_gain(engine->acoustic_materials[first_patch.material_index], emitter.frequency_hz) *
                mesh_material_specular_gain(engine->acoustic_materials[second_patch.material_index], emitter.frequency_hz) /
                std::max(propagation.distance_metres, emitter.min_distance_metres);
            append_path(path);
        }
    }

    *out_path_count = required_count;
    if (out_paths != nullptr && path_capacity < required_count) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Reflection-path output buffer is too small.");
    }
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
    pg_signal_emitter_desc signal{};
    signal.size = sizeof(signal);
    signal.version = PG_ABI_VERSION;
    signal.name_utf8 = emitter->name_utf8;
    signal.waveform = PG_WAVE_SINE;
    signal.frequency_hz = emitter->frequency_hz;
    signal.amplitude = emitter->amplitude;
    signal.pulse_width = 0.5;
    signal.gain_linear = emitter->gain_linear;
    signal.min_distance_metres = emitter->min_distance_metres;
    signal.motion_path_kind = PG_PATH_LINEAR;
    signal.keyframes = emitter->keyframes;
    signal.keyframe_count = emitter->keyframe_count;
    signal.noise_seed = 1U;
    signal.enabled = emitter->enabled;
    return pg_engine_add_signal_emitter(engine, &signal, out_index);
}

pg_status PG_CALL pg_engine_add_signal_emitter(pg_engine* engine, const pg_signal_emitter_desc* emitter, uint32_t* out_index)
{
    if (engine == nullptr || emitter == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(emitter)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Signal emitter structure version or size is incompatible.");
    }
    if (require_stopped_device(engine, "Adding an emitter") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (engine->emitters.size() >= engine->config.max_emitters) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Emitter capacity exceeded.");
    }
    if (emitter->keyframes == nullptr || emitter->keyframe_count < 2U || emitter->keyframe_count > engine->config.max_keyframes_per_path ||
        !std::isfinite(emitter->frequency_hz) || emitter->frequency_hz <= 0.0 || !std::isfinite(emitter->amplitude) ||
        !std::isfinite(emitter->gain_linear) || emitter->gain_linear < 0.0 || emitter->min_distance_metres <= 0.0 ||
        !std::isfinite(emitter->phase_radians) ||
        emitter->waveform < PG_WAVE_SINE || emitter->waveform > PG_WAVE_TURBULENCE ||
        emitter->motion_path_kind < PG_PATH_LINEAR || emitter->motion_path_kind > PG_PATH_RECORDED_GESTURE ||
        emitter->pulse_width <= 0.01 || emitter->pulse_width >= 0.99 || emitter->attack_seconds < 0.0 ||
        emitter->release_seconds < 0.0 || emitter->nonlinear_drive < 0.0 || emitter->nonlinear_drive > 100.0 ||
        emitter->additive_partial_count > 1024U || (emitter->additive_partial_count > 0U && emitter->additive_partials == nullptr)) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Emitter frequency, gain, minimum distance, or path is invalid.");
    }

    emitter_state state{};
    state.name = safe_name(emitter->name_utf8, "Emitter");
    state.waveform = emitter->waveform;
    state.motion_path_kind = emitter->motion_path_kind;
    state.frequency_hz = emitter->frequency_hz;
    state.amplitude = emitter->amplitude;
    state.phase_radians = emitter->phase_radians;
    state.pulse_width = emitter->pulse_width;
    state.gain_linear = emitter->gain_linear;
    state.min_distance_metres = emitter->min_distance_metres;
    state.attack_seconds = emitter->attack_seconds;
    state.release_seconds = emitter->release_seconds;
    state.nonlinear_drive = emitter->nonlinear_drive;
    state.noise_seed = emitter->noise_seed == 0U ? 1U : emitter->noise_seed;
    state.enabled = emitter->enabled != 0U;
    state.additive_partials.reserve(emitter->additive_partial_count);
    for (std::uint32_t index = 0; index < emitter->additive_partial_count; ++index) {
        const auto& partial = emitter->additive_partials[index];
        if (!std::isfinite(partial.frequency_ratio) || partial.frequency_ratio <= 0.0 ||
            !std::isfinite(partial.amplitude) || !std::isfinite(partial.phase_radians)) {
            return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Additive partials must contain finite positive frequency ratios.");
        }
        state.additive_partials.push_back({ partial.frequency_ratio, partial.amplitude, partial.phase_radians });
    }
    if (state.waveform == PG_WAVE_ADDITIVE && state.additive_partials.empty()) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Additive emitters require at least one partial.");
    }
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
    if (require_stopped_device(engine, "Adding a receiver") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (engine->receivers.size() >= engine->config.max_receivers) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Receiver capacity exceeded.");
    }
    if (!finite(from_c(receiver->centre_metres)) || !std::isfinite(receiver->yaw_degrees) ||
        !std::isfinite(receiver->spacing_metres) || receiver->spacing_metres < 0.01 || receiver->spacing_metres > 5.0 ||
        !std::isfinite(receiver->head_radius_metres) || receiver->head_radius_metres < 0.04 || receiver->head_radius_metres > 0.2 ||
        !std::isfinite(receiver->microphone_angle_degrees) || !std::isfinite(receiver->sensitivity_db) ||
        !std::isfinite(receiver->self_noise_db_spl) || receiver->self_noise_db_spl < 0.0 || receiver->self_noise_db_spl > 200.0 ||
        (receiver->kind != PG_RECEIVER_BINAURAL_HEAD && receiver->kind != PG_RECEIVER_STEREO_MICROPHONE) ||
        receiver->polar_pattern < PG_POLAR_OMNI || receiver->polar_pattern > PG_POLAR_FIGURE_EIGHT) {
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
    engine->receiver_room_responses.push_back(make_room_response_state());
    engine->receiver_diffuse_responses.push_back(make_diffuse_response_state());
    if (out_index != nullptr) {
        *out_index = static_cast<std::uint32_t>(engine->receivers.size() - 1U);
    }
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_set_receiver_capsules(
    pg_engine* engine,
    uint32_t receiver_index,
    const pg_microphone_capsule_desc* left_capsule,
    const pg_microphone_capsule_desc* right_capsule)
{
    if (engine == nullptr || left_capsule == nullptr || right_capsule == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(left_capsule) || !valid_struct(right_capsule)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Microphone capsule structure version or size is incompatible.");
    }
    if (require_stopped_device(engine, "Setting receiver capsules") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (receiver_index >= engine->receivers.size()) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Receiver index does not exist.");
    }
    receiver_state& receiver = engine->receivers[receiver_index];
    if (receiver.kind != PG_RECEIVER_STEREO_MICROPHONE) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Independent capsules apply only to stereo microphone receivers.");
    }
    const auto valid_capsule = [](const pg_microphone_capsule_desc& capsule) noexcept {
        const vec3 position = from_c(capsule.relative_position_metres);
        return finite(position) && length(position) <= 5.0 && finite(from_c(capsule.relative_rotation_degrees)) &&
            capsule.polar_pattern >= PG_POLAR_OMNI && capsule.polar_pattern <= PG_POLAR_FIGURE_EIGHT &&
            std::isfinite(capsule.sensitivity_db) && std::isfinite(capsule.self_noise_db_spl) &&
            capsule.self_noise_db_spl >= 0.0 && capsule.self_noise_db_spl <= 200.0;
    };
    if (!valid_capsule(*left_capsule) || !valid_capsule(*right_capsule)) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Microphone capsule position, orientation, pattern, sensitivity, or self-noise is invalid.");
    }
    const auto copy_capsule = [](const pg_microphone_capsule_desc& source) noexcept {
        microphone_capsule_state result{};
        result.relative_position = from_c(source.relative_position_metres);
        result.relative_rotation = from_c(source.relative_rotation_degrees);
        result.polar_pattern = source.polar_pattern;
        result.sensitivity_linear = std::pow(10.0, source.sensitivity_db / 20.0);
        result.self_noise_db_spl = source.self_noise_db_spl;
        return result;
    };
    receiver.left_capsule = copy_capsule(*left_capsule);
    receiver.right_capsule = copy_capsule(*right_capsule);
    receiver.has_explicit_capsules = true;
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_set_receiver_motion(pg_engine* engine, const pg_receiver_motion_desc* motion)
{
    if (engine == nullptr || motion == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(motion)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Receiver motion structure version or size is incompatible.");
    }
    if (require_stopped_device(engine, "Changing receiver motion") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (motion->receiver_index >= engine->receivers.size()) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Receiver motion index is out of range.");
    }
    if (motion->motion_path_kind < PG_PATH_LINEAR || motion->motion_path_kind > PG_PATH_RECORDED_GESTURE ||
        motion->keyframes == nullptr || motion->keyframe_count < 2U ||
        motion->keyframe_count > engine->config.max_keyframes_per_path) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Receiver motion requires a supported path with at least two bounded keyframes.");
    }

    try {
        std::vector<keyframe_state> keyframes;
        keyframes.reserve(motion->keyframe_count);
        for (std::uint32_t index = 0U; index < motion->keyframe_count; ++index) {
            const pg_motion_keyframe& keyframe = motion->keyframes[index];
            const vec3 position = from_c(keyframe.position_metres);
            if (!std::isfinite(keyframe.time_seconds) || !finite(position) ||
                (index > 0U && keyframe.time_seconds <= motion->keyframes[index - 1U].time_seconds)) {
                return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Receiver motion keyframes must be finite and strictly increasing in time.");
            }
            keyframes.push_back({ keyframe.time_seconds, position });
        }

        receiver_state& receiver = engine->receivers[motion->receiver_index];
        receiver.motion_path_kind = motion->motion_path_kind;
        receiver.keyframes = std::move(keyframes);
        return PG_STATUS_OK;
    } catch (const std::exception& exception) {
        return fail(engine, PG_STATUS_INTERNAL_ERROR, std::string("Receiver motion setup failed: ") + exception.what());
    } catch (...) {
        return fail(engine, PG_STATUS_INTERNAL_ERROR, "Receiver motion setup failed with an unknown error.");
    }
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
    if (engine == nullptr || interleaved_stereo == nullptr || sample_rate == 0U || sample_rate > maximum_supported_sample_rate) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (receiver_index >= engine->receivers.size()) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Receiver index is out of range.");
    }
    if (require_stopped_device(engine, "Block rendering") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (interleaved_sample_capacity < static_cast<std::size_t>(frame_count) * 2U) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Output buffer is too small for requested stereo frames.");
    }
    if (validate_scene(engine) != PG_STATUS_OK) {
        return PG_STATUS_INVALID_SCENE;
    }
    const pg_status cache_status = prepare_mesh_reflection_caches(engine);
    if (cache_status != PG_STATUS_OK) {
        return cache_status;
    }

    if (start_frame == 0U || engine->receiver_next_frames[receiver_index] != start_frame) {
        engine->receiver_filters[receiver_index] = {};
        reset_room_response(engine->receiver_room_responses[receiver_index], sample_rate, engine->room, 2.0);
        reset_diffuse_response(engine->receiver_diffuse_responses[receiver_index], sample_rate);
    }
    for (std::uint32_t frame = 0; frame < frame_count; ++frame) {
        const double receive_time = static_cast<double>(start_frame + frame) / static_cast<double>(sample_rate);
        if (!engine->mesh_geometries.empty() && frame % mesh_reflection_cache_interval_frames == 0U) {
            update_mesh_reflection_caches(
                *engine,
                sample_motion(engine->receivers[receiver_index], receive_time),
                receive_time,
                mesh_reflection_path_limit(1024U, false),
                false);
        }
        const auto [left, right] = render_frame(*engine, receiver_index, receive_time, sample_rate, 1U, 1024U, 2.0, 1U);
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
    if (require_stopped_device(engine, "Offline rendering") != PG_STATUS_OK) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (validate_scene(engine) != PG_STATUS_OK) {
        return PG_STATUS_INVALID_SCENE;
    }
    if (request->output_directory_utf8 == nullptr || request->duration_seconds <= 0.0 || request->sample_rate == 0U ||
        request->sample_rate > maximum_supported_sample_rate ||
        !std::isfinite(request->duration_seconds) || request->reflection_order > 2U ||
        request->ray_count > 1000000U || !std::isfinite(request->impulse_response_seconds) ||
        request->impulse_response_seconds < 0.0 || request->impulse_response_seconds > 30.0) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Render directory, duration, sample rate, or echo-quality settings are invalid.");
    }
    const pg_status cache_status = prepare_mesh_reflection_caches(engine);
    if (cache_status != PG_STATUS_OK) {
        return cache_status;
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
            reset_room_response(
                engine->receiver_room_responses[receiver_index],
                request->sample_rate,
                engine->room,
                request->impulse_response_seconds);
            reset_diffuse_response(engine->receiver_diffuse_responses[receiver_index], request->sample_rate);
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
                if (request->reflection_order > 0U && !engine->mesh_geometries.empty() &&
                    frame % mesh_reflection_cache_interval_frames == 0U) {
                    update_mesh_reflection_caches(
                        *engine,
                        sample_motion(engine->receivers[receiver_index], receive_time),
                        receive_time,
                        mesh_reflection_path_limit(request->ray_count, false),
                        request->reflection_order > 1U);
                }
                const auto [left, right] = render_frame(
                    *engine,
                    receiver_index,
                    receive_time,
                    request->sample_rate,
                    request->reflection_order,
                    request->ray_count,
                    request->impulse_response_seconds,
                    request->seed);
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

pg_status PG_CALL pg_engine_start_device(pg_engine* engine, const pg_device_config* config)
{
    if (engine == nullptr || config == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(config)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Device configuration structure is incompatible.");
    }
    if (engine->device_initialized || device_running(engine) ||
        engine->device_recovery_available.load(std::memory_order_acquire)) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Real-time preview is already initialized.");
    }
    if (validate_scene(engine) != PG_STATUS_OK) {
        return PG_STATUS_INVALID_SCENE;
    }
    if (config->receiver_index >= engine->receivers.size() || !engine->receivers[config->receiver_index].enabled) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Preview receiver index is out of range or disabled.");
    }
    if (config->sample_rate < 8000U || config->sample_rate > maximum_supported_sample_rate ||
        config->period_frames < 32U || config->period_frames > 8192U || config->reflection_order > 2U ||
        config->exclusive_mode > 1U ||
        config->ray_count > 1000000U || !std::isfinite(config->impulse_response_seconds) ||
        config->impulse_response_seconds < 0.0 || config->impulse_response_seconds > 30.0) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Preview sample rate, period, or echo-quality settings are outside supported bounds.");
    }
    const pg_status cache_status = prepare_mesh_reflection_caches(engine);
    if (cache_status != PG_STATUS_OK) {
        return cache_status;
    }

    engine->device_sample_rate = config->sample_rate;
    engine->device_period_frames = config->period_frames;
    engine->device_receiver_index = config->receiver_index;
    engine->device_reflection_order = config->reflection_order;
    engine->device_ray_count = config->ray_count;
    engine->device_seed = config->seed;
    engine->device_impulse_response_seconds = config->impulse_response_seconds;
    engine->device_exclusive_mode = config->exclusive_mode != 0U;
    engine->device_buffer_mode = false;
    engine->device_buffer_loop = false;
    engine->device_buffer.clear();
    engine->device_frame_cursor.store(config->start_frame, std::memory_order_relaxed);
    engine->device_frames_processed.store(0U, std::memory_order_relaxed);
    reset_device_callback_timing(engine);
    engine->device_interruption_count.store(0U, std::memory_order_relaxed);
    engine->device_recovery_count.store(0U, std::memory_order_relaxed);
    engine->device_recovery_available.store(true, std::memory_order_release);
    const pg_status start_status = initialize_playback_device(engine);
    if (start_status != PG_STATUS_OK) {
        engine->device_recovery_available.store(false, std::memory_order_release);
    }
    return start_status;
}

pg_status PG_CALL pg_engine_start_buffer_device(pg_engine* engine, const pg_buffer_device_config* config)
{
    if (engine == nullptr || config == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!valid_struct(config)) {
        return fail(engine, PG_STATUS_ABI_MISMATCH, "Buffer device configuration structure is incompatible.");
    }
    if (engine->device_initialized || device_running(engine) ||
        engine->device_recovery_available.load(std::memory_order_acquire)) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Real-time preview is already initialized.");
    }
    if (config->interleaved_stereo == nullptr || config->frame_count == 0U ||
        config->sample_rate < 8000U || config->sample_rate > maximum_supported_sample_rate ||
        config->period_frames < 32U || config->period_frames > 8192U ||
        config->exclusive_mode > 1U || config->loop > 1U ||
        config->frame_count > static_cast<std::uint64_t>(config->sample_rate) * maximum_buffer_preview_seconds) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Stereo preview buffer or device settings are outside supported bounds.");
    }
    if (config->frame_count > static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max() / 2U)) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Stereo preview buffer exceeds addressable memory.");
    }

    const std::size_t sample_count = static_cast<std::size_t>(config->frame_count) * 2U;
    if (!std::all_of(config->interleaved_stereo, config->interleaved_stereo + sample_count,
            [](float sample) { return std::isfinite(sample); })) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Stereo preview samples must all be finite.");
    }
    try {
        engine->device_buffer.assign(config->interleaved_stereo, config->interleaved_stereo + sample_count);
    } catch (const std::bad_alloc&) {
        return fail(engine, PG_STATUS_CAPACITY_EXCEEDED, "Unable to reserve the bounded stereo preview buffer.");
    }

    engine->device_buffer_mode = true;
    engine->device_buffer_loop = config->loop != 0U;
    engine->device_sample_rate = config->sample_rate;
    engine->device_period_frames = config->period_frames;
    engine->device_receiver_index = std::numeric_limits<std::uint32_t>::max();
    engine->device_exclusive_mode = config->exclusive_mode != 0U;
    engine->device_frame_cursor.store(0U, std::memory_order_relaxed);
    engine->device_frames_processed.store(0U, std::memory_order_relaxed);
    reset_device_callback_timing(engine);
    engine->device_interruption_count.store(0U, std::memory_order_relaxed);
    engine->device_recovery_count.store(0U, std::memory_order_relaxed);
    engine->device_recovery_available.store(true, std::memory_order_release);
    const pg_status start_status = initialize_playback_device(engine);
    if (start_status != PG_STATUS_OK) {
        engine->device_recovery_available.store(false, std::memory_order_release);
        engine->device_buffer.clear();
        engine->device_buffer_mode = false;
    }
    return start_status;
}

pg_status PG_CALL pg_engine_recover_device(pg_engine* engine, uint64_t resume_frame)
{
    if (engine == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    if (!engine->device_recovery_available.load(std::memory_order_acquire)) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "The preview has no retained device state to recover.");
    }
    if (engine->device_buffer_mode && !engine->device_buffer_loop &&
        resume_frame > static_cast<std::uint64_t>(engine->device_buffer.size() / 2U)) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "The recovery frame is beyond the retained stereo preview buffer.");
    }

    engine->device_running.store(false, std::memory_order_release);
    if (engine->device_initialized) {
        (void)ma_device_stop(&engine->device);
        ma_device_uninit(&engine->device);
        engine->device_initialized = false;
    }
    engine->device_frame_cursor.store(resume_frame, std::memory_order_relaxed);
    engine->device_frames_processed.store(0U, std::memory_order_relaxed);
    reset_device_callback_timing(engine);

    const pg_status recovery_status = initialize_playback_device(engine);
    if (recovery_status == PG_STATUS_OK) {
        engine->device_recovery_count.fetch_add(1U, std::memory_order_relaxed);
    }
    return recovery_status;
}

pg_status PG_CALL pg_engine_stop_device(pg_engine* engine)
{
    if (engine == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    engine->device_recovery_available.store(false, std::memory_order_release);
    engine->device_running.store(false, std::memory_order_release);
    if (!engine->device_initialized) {
        engine->device_buffer.clear();
        engine->device_buffer_mode = false;
        engine->device_buffer_loop = false;
        return PG_STATUS_OK;
    }

    const ma_result stop_result = ma_device_stop(&engine->device);
    ma_device_uninit(&engine->device);
    engine->device_initialized = false;
    engine->device_buffer.clear();
    engine->device_buffer_mode = false;
    engine->device_buffer_loop = false;
    if (stop_result != MA_SUCCESS && stop_result != MA_DEVICE_NOT_STARTED) {
        return fail(engine, PG_STATUS_INTERNAL_ERROR, std::string("Unable to stop the audio device cleanly: ") + ma_result_description(stop_result));
    }
    return PG_STATUS_OK;
}

pg_status PG_CALL pg_engine_get_device_status(const pg_engine* engine, pg_device_status* status)
{
    if (engine == nullptr || status == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    constexpr std::size_t legacy_status_size = offsetof(pg_device_status, recoverable);
    if (status->size < legacy_status_size || status->version != PG_ABI_VERSION) {
        return PG_STATUS_ABI_MISMATCH;
    }
    status->running = device_running(engine) ? 1U : 0U;
    status->sample_rate = engine->device_sample_rate;
    status->period_frames = engine->device_period_frames;
    status->receiver_index = engine->device_receiver_index;
    status->frames_processed = engine->device_frames_processed.load(std::memory_order_relaxed);
    status->callback_count = engine->device_callback_count.load(std::memory_order_acquire);
    constexpr std::size_t recovery_status_size = offsetof(pg_device_status, callback_duration_nanoseconds_total);
    if (status->size >= recovery_status_size) {
        status->recoverable = engine->device_recovery_available.load(std::memory_order_acquire) ? 1U : 0U;
        status->interruption_count = engine->device_interruption_count.load(std::memory_order_relaxed);
        status->recovery_count = engine->device_recovery_count.load(std::memory_order_relaxed);
        status->reserved = 0U;
    }
    if (status->size >= sizeof(pg_device_status)) {
        status->callback_duration_nanoseconds_total = engine->device_callback_duration_nanoseconds_total.load(std::memory_order_relaxed);
        status->callback_duration_nanoseconds_maximum = engine->device_callback_duration_nanoseconds_maximum.load(std::memory_order_relaxed);
        status->callback_deadline_miss_count = engine->device_callback_deadline_miss_count.load(std::memory_order_relaxed);
        status->callback_load_parts_per_million_maximum = engine->device_callback_load_parts_per_million_maximum.load(std::memory_order_relaxed);
        status->callback_frames_maximum = engine->device_callback_frames_maximum.load(std::memory_order_relaxed);
    }
    return PG_STATUS_OK;
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

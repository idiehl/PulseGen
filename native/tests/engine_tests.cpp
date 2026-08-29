#include "pulsegen/pulsegen.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <numeric>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

namespace {

void require(bool condition, std::string_view expression, int line)
{
    if (!condition) {
        throw std::runtime_error("Native requirement failed at line " + std::to_string(line) + ": " + std::string(expression));
    }
}

#define REQUIRE(expression) require(static_cast<bool>(expression), #expression, __LINE__)

pg_engine* create_engine()
{
    pg_engine_config config{};
    config.size = sizeof(config);
    config.version = PG_ABI_VERSION;
    config.max_emitters = 8;
    config.max_receivers = 4;
    config.max_keyframes_per_path = 64;
    pg_engine* engine = nullptr;
    REQUIRE(pg_engine_create(&config, &engine) == PG_STATUS_OK);
    REQUIRE(engine != nullptr);
    return engine;
}

void configure_scene(pg_engine* engine, double emitter_amplitude = 0.5)
{
    pg_atmosphere_desc atmosphere{};
    atmosphere.size = sizeof(atmosphere);
    atmosphere.version = PG_ABI_VERSION;
    atmosphere.temperature_celsius = 20;
    atmosphere.relative_humidity_percent = 50;
    atmosphere.pressure_kilopascals = 101.325;
    atmosphere.uniform_wind_metres_per_second = { 0, 0, 2 };
    atmosphere.turbulence = 0.05;
    atmosphere.noise_seed = 42;
    REQUIRE(pg_engine_set_atmosphere(engine, &atmosphere) == PG_STATUS_OK);

    pg_room_desc room{};
    room.size = sizeof(room);
    room.version = PG_ABI_VERSION;
    room.centre_metres = { 0, 4, 0 };
    room.dimensions_metres = { 64, 8, 18 };
    room.wall_reflection_low = 0.85;
    room.wall_reflection_mid = 0.72;
    room.wall_reflection_high = 0.55;
    room.scattering = 0.15;
    room.enabled = 1;
    REQUIRE(pg_engine_set_room(engine, &room) == PG_STATUS_OK);

    const std::array<pg_motion_keyframe, 4> keyframes{
        pg_motion_keyframe{ 0.0, { -28, 1.7, 4 } },
        pg_motion_keyframe{ 2.5, { -4, 1.7, 1.5 } },
        pg_motion_keyframe{ 3.5, { 4, 1.7, 1.5 } },
        pg_motion_keyframe{ 6.0, { 28, 1.7, 4 } }
    };
    pg_sine_emitter_desc emitter{};
    emitter.size = sizeof(emitter);
    emitter.version = PG_ABI_VERSION;
    emitter.name_utf8 = "Sine_Flyby";
    emitter.frequency_hz = 440;
    emitter.amplitude = emitter_amplitude;
    emitter.gain_linear = 0.8;
    emitter.min_distance_metres = 1;
    emitter.keyframes = keyframes.data();
    emitter.keyframe_count = static_cast<std::uint32_t>(keyframes.size());
    emitter.enabled = 1;
    REQUIRE(pg_engine_add_sine_emitter(engine, &emitter, nullptr) == PG_STATUS_OK);

    pg_receiver_rig_desc receiver{};
    receiver.size = sizeof(receiver);
    receiver.version = PG_ABI_VERSION;
    receiver.name_utf8 = "Head";
    receiver.kind = PG_RECEIVER_BINAURAL_HEAD;
    receiver.centre_metres = { 0, 1.7, 0 };
    receiver.spacing_metres = 0.18;
    receiver.head_radius_metres = 0.09;
    receiver.polar_pattern = PG_POLAR_OMNI;
    receiver.enabled = 1;
    REQUIRE(pg_engine_add_receiver(engine, &receiver, nullptr) == PG_STATUS_OK);
}

std::vector<float> render_primitive(pg_waveform_kind waveform)
{
    pg_engine* engine = create_engine();
    pg_room_desc room{};
    room.size = sizeof(room);
    room.version = PG_ABI_VERSION;
    room.dimensions_metres = { 20, 8, 12 };
    room.wall_reflection_low = 0.8;
    room.wall_reflection_mid = 0.7;
    room.wall_reflection_high = 0.5;
    room.scattering = 0.1;
    room.enabled = 0;
    REQUIRE(pg_engine_set_room(engine, &room) == PG_STATUS_OK);

    const std::array<pg_motion_keyframe, 2> keyframes{
        pg_motion_keyframe{ 0.0, { 0, 1.7, 2 } },
        pg_motion_keyframe{ 1.0, { 0, 1.7, 2 } }
    };
    const std::array<pg_additive_partial, 3> partials{
        pg_additive_partial{ 1.0, 1.0, 0.0 },
        pg_additive_partial{ 2.0, 0.4, 0.2 },
        pg_additive_partial{ 3.0, 0.2, -0.1 }
    };
    pg_signal_emitter_desc emitter{};
    emitter.size = sizeof(emitter);
    emitter.version = PG_ABI_VERSION;
    emitter.name_utf8 = "Primitive";
    emitter.waveform = waveform;
    emitter.frequency_hz = 220;
    emitter.amplitude = 0.4;
    emitter.pulse_width = 0.3;
    emitter.gain_linear = 0.7;
    emitter.min_distance_metres = 1;
    emitter.attack_seconds = 0.002;
    emitter.release_seconds = 0.01;
    emitter.nonlinear_drive = 0.3;
    emitter.additive_partials = waveform == PG_WAVE_ADDITIVE ? partials.data() : nullptr;
    emitter.additive_partial_count = waveform == PG_WAVE_ADDITIVE ? static_cast<std::uint32_t>(partials.size()) : 0U;
    emitter.motion_path_kind = PG_PATH_LINEAR;
    emitter.keyframes = keyframes.data();
    emitter.keyframe_count = static_cast<std::uint32_t>(keyframes.size());
    emitter.noise_seed = 1234;
    emitter.enabled = 1;
    REQUIRE(pg_engine_add_signal_emitter(engine, &emitter, nullptr) == PG_STATUS_OK);

    pg_receiver_rig_desc receiver{};
    receiver.size = sizeof(receiver);
    receiver.version = PG_ABI_VERSION;
    receiver.name_utf8 = "Test_Head";
    receiver.kind = PG_RECEIVER_BINAURAL_HEAD;
    receiver.centre_metres = { 0, 1.7, 0 };
    receiver.spacing_metres = 0.18;
    receiver.head_radius_metres = 0.09;
    receiver.polar_pattern = PG_POLAR_OMNI;
    receiver.enabled = 1;
    REQUIRE(pg_engine_add_receiver(engine, &receiver, nullptr) == PG_STATUS_OK);

    constexpr std::uint32_t frames = 4096;
    std::vector<float> output(frames * 2U);
    REQUIRE(pg_engine_render_receiver_block(engine, 0, 0, frames, 48000, output.data(), output.size()) == PG_STATUS_OK);
    pg_engine_destroy(engine);
    return output;
}

struct static_render_options {
    pg_waveform_kind waveform{ PG_WAVE_SINE };
    double frequency_hz{ 440.0 };
    pg_vec3 source_position{ 0.0, 0.0, 2.0 };
    pg_polar_pattern polar_pattern{ PG_POLAR_OMNI };
    double receiver_spacing_metres{ 0.01 };
    double microphone_angle_degrees{};
    double relative_humidity_percent{ 50.0 };
    double pressure_kilopascals{ 101.325 };
    std::uint64_t start_frame{};
    std::uint32_t frame_count{ 4096U };
};

std::vector<float> render_static_source(const static_render_options& options)
{
    constexpr std::uint32_t sample_rate = 48000U;
    pg_engine* engine = create_engine();

    pg_atmosphere_desc atmosphere{};
    atmosphere.size = sizeof(atmosphere);
    atmosphere.version = PG_ABI_VERSION;
    atmosphere.temperature_celsius = 20.0;
    atmosphere.relative_humidity_percent = options.relative_humidity_percent;
    atmosphere.pressure_kilopascals = options.pressure_kilopascals;
    REQUIRE(pg_engine_set_atmosphere(engine, &atmosphere) == PG_STATUS_OK);

    pg_room_desc room{};
    room.size = sizeof(room);
    room.version = PG_ABI_VERSION;
    room.dimensions_metres = { 20.0, 8.0, 12.0 };
    room.enabled = 0U;
    REQUIRE(pg_engine_set_room(engine, &room) == PG_STATUS_OK);

    const double render_end_seconds = static_cast<double>(options.start_frame + options.frame_count) / sample_rate;
    const std::array<pg_motion_keyframe, 2> keyframes{
        pg_motion_keyframe{ 0.0, options.source_position },
        pg_motion_keyframe{ std::max(2.0, render_end_seconds + 1.0), options.source_position }
    };
    pg_signal_emitter_desc emitter{};
    emitter.size = sizeof(emitter);
    emitter.version = PG_ABI_VERSION;
    emitter.name_utf8 = "Static_Source";
    emitter.waveform = options.waveform;
    emitter.frequency_hz = options.frequency_hz;
    emitter.amplitude = 1.0;
    emitter.pulse_width = 0.5;
    emitter.gain_linear = 1.0;
    emitter.min_distance_metres = 0.01;
    emitter.motion_path_kind = PG_PATH_LINEAR;
    emitter.keyframes = keyframes.data();
    emitter.keyframe_count = static_cast<std::uint32_t>(keyframes.size());
    emitter.noise_seed = 99U;
    emitter.enabled = 1U;
    REQUIRE(pg_engine_add_signal_emitter(engine, &emitter, nullptr) == PG_STATUS_OK);

    pg_receiver_rig_desc receiver{};
    receiver.size = sizeof(receiver);
    receiver.version = PG_ABI_VERSION;
    receiver.name_utf8 = "Static_Microphones";
    receiver.kind = PG_RECEIVER_STEREO_MICROPHONE;
    receiver.centre_metres = { 0.0, 0.0, 0.0 };
    receiver.spacing_metres = options.receiver_spacing_metres;
    receiver.head_radius_metres = 0.09;
    receiver.microphone_angle_degrees = options.microphone_angle_degrees;
    receiver.polar_pattern = options.polar_pattern;
    receiver.enabled = 1U;
    REQUIRE(pg_engine_add_receiver(engine, &receiver, nullptr) == PG_STATUS_OK);

    std::vector<float> output(static_cast<std::size_t>(options.frame_count) * 2U);
    REQUIRE(pg_engine_render_receiver_block(
        engine,
        0U,
        options.start_frame,
        options.frame_count,
        sample_rate,
        output.data(),
        output.size()) == PG_STATUS_OK);
    pg_engine_destroy(engine);
    return output;
}

struct surface_render_options {
    bool add_surface{ true };
    pg_geometry_kind geometry_kind{ PG_GEOMETRY_PLANE };
    pg_waveform_kind waveform{ PG_WAVE_SINE };
    double frequency_hz{ 440.0 };
    pg_vec3 source_position{ 0.0, 0.0, 2.0 };
    pg_vec3 geometry_centre{ 0.0, 0.0, 1.0 };
    pg_vec3 geometry_dimensions{ 10.0, 10.0, 0.0 };
    pg_vec3 geometry_rotation{};
    std::array<double, PG_ACOUSTIC_BAND_COUNT> absorption{};
    std::array<double, PG_ACOUSTIC_BAND_COUNT> scattering{};
    std::array<double, PG_ACOUSTIC_BAND_COUNT> transmission{};
    double roughness{};
    bool occludes{ true };
    bool reflects{};
    pg_polar_pattern polar_pattern{ PG_POLAR_OMNI };
    std::uint64_t start_frame{ 24000U };
    std::uint32_t frame_count{ 4800U };
};

std::vector<float> render_surface_source(const surface_render_options& options)
{
    constexpr std::uint32_t sample_rate = 48000U;
    constexpr std::array<double, PG_ACOUSTIC_BAND_COUNT> band_centres{ 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0 };
    pg_engine* engine = create_engine();

    pg_atmosphere_desc atmosphere{};
    atmosphere.size = sizeof(atmosphere);
    atmosphere.version = PG_ABI_VERSION;
    atmosphere.temperature_celsius = 20.0;
    atmosphere.relative_humidity_percent = 50.0;
    atmosphere.pressure_kilopascals = 101.325;
    REQUIRE(pg_engine_set_atmosphere(engine, &atmosphere) == PG_STATUS_OK);

    pg_room_desc room{};
    room.size = sizeof(room);
    room.version = PG_ABI_VERSION;
    room.dimensions_metres = { 20.0, 8.0, 12.0 };
    room.enabled = 0U;
    REQUIRE(pg_engine_set_room(engine, &room) == PG_STATUS_OK);

    if (options.add_surface) {
        pg_acoustic_material_desc material{};
        material.size = sizeof(material);
        material.version = PG_ABI_VERSION;
        material.name_utf8 = "Test_Surface";
        for (std::size_t index = 0U; index < band_centres.size(); ++index) {
            material.octave_band_centres_hz[index] = band_centres[index];
            material.absorption[index] = options.absorption[index];
            material.scattering[index] = options.scattering[index];
            material.transmission[index] = options.transmission[index];
        }
        material.roughness = options.roughness;
        std::uint32_t material_index = 0U;
        REQUIRE(pg_engine_add_acoustic_material(engine, &material, &material_index) == PG_STATUS_OK);

        pg_geometry_desc geometry{};
        geometry.size = sizeof(geometry);
        geometry.version = PG_ABI_VERSION;
        geometry.name_utf8 = "Bounded_Surface";
        geometry.kind = options.geometry_kind;
        geometry.centre_metres = options.geometry_centre;
        geometry.dimensions_metres = options.geometry_dimensions;
        geometry.rotation_degrees = options.geometry_rotation;
        geometry.material_index = material_index;
        geometry.occludes = options.occludes ? 1U : 0U;
        geometry.reflects = options.reflects ? 1U : 0U;
        geometry.enabled = 1U;
        REQUIRE(pg_engine_add_geometry(engine, &geometry, nullptr) == PG_STATUS_OK);
    }

    const double render_end_seconds = static_cast<double>(options.start_frame + options.frame_count) / sample_rate;
    const std::array<pg_motion_keyframe, 2> keyframes{
        pg_motion_keyframe{ 0.0, options.source_position },
        pg_motion_keyframe{ std::max(2.0, render_end_seconds + 1.0), options.source_position }
    };
    pg_signal_emitter_desc emitter{};
    emitter.size = sizeof(emitter);
    emitter.version = PG_ABI_VERSION;
    emitter.name_utf8 = "Surface_Source";
    emitter.waveform = options.waveform;
    emitter.frequency_hz = options.frequency_hz;
    emitter.amplitude = 1.0;
    emitter.pulse_width = 0.5;
    emitter.gain_linear = 1.0;
    emitter.min_distance_metres = 0.01;
    emitter.motion_path_kind = PG_PATH_LINEAR;
    emitter.keyframes = keyframes.data();
    emitter.keyframe_count = static_cast<std::uint32_t>(keyframes.size());
    emitter.noise_seed = 57U;
    emitter.enabled = 1U;
    REQUIRE(pg_engine_add_signal_emitter(engine, &emitter, nullptr) == PG_STATUS_OK);

    pg_receiver_rig_desc receiver{};
    receiver.size = sizeof(receiver);
    receiver.version = PG_ABI_VERSION;
    receiver.name_utf8 = "Surface_Microphones";
    receiver.kind = PG_RECEIVER_STEREO_MICROPHONE;
    receiver.centre_metres = { 0.0, 0.0, 0.0 };
    receiver.spacing_metres = 0.01;
    receiver.head_radius_metres = 0.09;
    receiver.polar_pattern = options.polar_pattern;
    receiver.enabled = 1U;
    REQUIRE(pg_engine_add_receiver(engine, &receiver, nullptr) == PG_STATUS_OK);

    std::vector<float> output(static_cast<std::size_t>(options.frame_count) * 2U);
    REQUIRE(pg_engine_render_receiver_block(
        engine,
        0U,
        options.start_frame,
        options.frame_count,
        sample_rate,
        output.data(),
        output.size()) == PG_STATUS_OK);
    pg_engine_destroy(engine);
    return output;
}

double channel_rms(const std::vector<float>& samples, std::size_t channel)
{
    long double sum = 0.0;
    std::size_t count = 0U;
    for (std::size_t index = channel; index < samples.size(); index += 2U) {
        sum += static_cast<long double>(samples[index]) * samples[index];
        ++count;
    }
    return std::sqrt(static_cast<double>(sum / static_cast<long double>(std::max<std::size_t>(count, 1U))));
}

std::size_t first_active_frame(const std::vector<float>& samples, float threshold = 1.0e-7F)
{
    for (std::size_t frame = 0; frame < samples.size() / 2U; ++frame) {
        if (std::abs(samples[frame * 2U]) > threshold || std::abs(samples[(frame * 2U) + 1U]) > threshold) {
            return frame;
        }
    }
    return samples.size() / 2U;
}

} // namespace

int main(int argument_count, char** arguments)
{
    try {
    static_assert(PG_ABI_VERSION == 1U);
    REQUIRE(pg_get_abi_version() == PG_ABI_VERSION);
    REQUIRE(std::abs(pg_calculate_speed_of_sound(20.0) - 343.42) < 1.0e-9);

    const double approaching = pg_calculate_observed_frequency(440.0, 343.42, 20.0, 0.0);
    const double receding = pg_calculate_observed_frequency(440.0, 343.42, -20.0, 0.0);
    REQUIRE(approaching > 440.0);
    REQUIRE(receding < 440.0);
    REQUIRE(std::isnan(pg_calculate_observed_frequency(440.0, 343.42, 343.42, 0.0)));
    REQUIRE(std::abs(approaching - (440.0 * 343.42 / (343.42 - 20.0))) < 1.0e-12);

    static_render_options frequency_test{};
    frequency_test.source_position = { 0.0, 0.0, 1.0 };
    frequency_test.start_frame = 24000U;
    frequency_test.frame_count = 48000U;
    const auto sine_one_second = render_static_source(frequency_test);
    std::size_t upward_crossings = 0U;
    for (std::size_t frame = 1U; frame < frequency_test.frame_count; ++frame) {
        if (sine_one_second[(frame - 1U) * 2U] <= 0.0F && sine_one_second[frame * 2U] > 0.0F) {
            ++upward_crossings;
        }
    }
    REQUIRE(upward_crossings >= 439U && upward_crossings <= 441U);

    static_render_options propagation_test{};
    propagation_test.waveform = PG_WAVE_IMPULSE;
    propagation_test.frequency_hz = 1.0;
    propagation_test.source_position = { 0.0, 0.0, 10.0 };
    propagation_test.frame_count = 4096U;
    propagation_test.relative_humidity_percent = 0.0;
    const auto propagated_impulse = render_static_source(propagation_test);
    const auto measured_arrival = first_active_frame(propagated_impulse);
    const auto expected_arrival = static_cast<std::size_t>(std::llround(10.0 / 343.42 * 48000.0));
    REQUIRE(measured_arrival <= expected_arrival + 1U && expected_arrival <= measured_arrival + 1U);

    static_render_options near_distance{};
    near_distance.source_position = { 0.0, 0.0, 2.0 };
    near_distance.start_frame = 24000U;
    near_distance.frame_count = 4800U;
    near_distance.relative_humidity_percent = 100.0;
    auto far_distance = near_distance;
    far_distance.source_position = { 0.0, 0.0, 4.0 };
    const double distance_ratio = channel_rms(render_static_source(near_distance), 0U) /
        channel_rms(render_static_source(far_distance), 0U);
    REQUIRE(std::abs(distance_ratio - 2.0) < 0.002);

    static_render_options dry_air{};
    dry_air.frequency_hz = 8000.0;
    dry_air.source_position = { 0.0, 0.0, 50.0 };
    dry_air.start_frame = 24000U;
    dry_air.frame_count = 4800U;
    dry_air.relative_humidity_percent = 0.0;
    auto humid_air = dry_air;
    humid_air.relative_humidity_percent = 100.0;
    const double measured_absorption_ratio = channel_rms(render_static_source(dry_air), 0U) /
        channel_rms(render_static_source(humid_air), 0U);
    const double dry_coefficient = (0.000015 + 0.000018) * 64.0;
    const double humid_coefficient = 0.000015 * 64.0;
    const double expected_absorption_ratio = std::exp(-(dry_coefficient - humid_coefficient) * 50.0);
    REQUIRE(std::abs(measured_absorption_ratio - expected_absorption_ratio) < 0.002);

    static_render_options rear_omni{};
    rear_omni.source_position = { 0.0, 0.0, -2.0 };
    rear_omni.start_frame = 24000U;
    rear_omni.frame_count = 4800U;
    auto rear_cardioid = rear_omni;
    rear_cardioid.polar_pattern = PG_POLAR_CARDIOID;
    REQUIRE(channel_rms(render_static_source(rear_cardioid), 0U) < channel_rms(render_static_source(rear_omni), 0U) * 2.0e-6);

    static_render_options symmetry{};
    symmetry.source_position = { 0.0, 0.0, 2.0 };
    symmetry.receiver_spacing_metres = 0.18;
    symmetry.microphone_angle_degrees = 110.0;
    symmetry.start_frame = 24000U;
    symmetry.frame_count = 4800U;
    const auto symmetric_pair = render_static_source(symmetry);
    for (std::size_t frame = 0; frame < symmetry.frame_count; ++frame) {
        REQUIRE(symmetric_pair[frame * 2U] == symmetric_pair[(frame * 2U) + 1U]);
    }

    pg_engine* atmosphere_validation_engine = create_engine();
    pg_atmosphere_desc invalid_atmosphere{};
    invalid_atmosphere.size = sizeof(invalid_atmosphere);
    invalid_atmosphere.version = PG_ABI_VERSION;
    invalid_atmosphere.temperature_celsius = 20.0;
    invalid_atmosphere.relative_humidity_percent = std::numeric_limits<double>::quiet_NaN();
    invalid_atmosphere.pressure_kilopascals = 101.325;
    REQUIRE(pg_engine_set_atmosphere(atmosphere_validation_engine, &invalid_atmosphere) == PG_STATUS_INVALID_ARGUMENT);
    invalid_atmosphere.relative_humidity_percent = 50.0;
    invalid_atmosphere.pressure_kilopascals = 10.0;
    REQUIRE(pg_engine_set_atmosphere(atmosphere_validation_engine, &invalid_atmosphere) == PG_STATUS_INVALID_ARGUMENT);
    invalid_atmosphere.pressure_kilopascals = 101.325;
    invalid_atmosphere.turbulence = std::numeric_limits<double>::quiet_NaN();
    REQUIRE(pg_engine_set_atmosphere(atmosphere_validation_engine, &invalid_atmosphere) == PG_STATUS_INVALID_ARGUMENT);
    pg_engine_destroy(atmosphere_validation_engine);

    pg_engine* material_validation_engine = create_engine();
    pg_acoustic_material_desc invalid_material{};
    invalid_material.size = sizeof(invalid_material);
    invalid_material.version = PG_ABI_VERSION;
    invalid_material.name_utf8 = "Invalid_Energy";
    constexpr std::array<double, PG_ACOUSTIC_BAND_COUNT> standard_bands{ 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0 };
    for (std::size_t index = 0U; index < standard_bands.size(); ++index) {
        invalid_material.octave_band_centres_hz[index] = standard_bands[index];
        invalid_material.absorption[index] = 0.8;
        invalid_material.scattering[index] = 0.1;
        invalid_material.transmission[index] = 0.3;
    }
    invalid_material.roughness = 0.2;
    REQUIRE(pg_engine_add_acoustic_material(material_validation_engine, &invalid_material, nullptr) == PG_STATUS_INVALID_ARGUMENT);
    pg_geometry_desc missing_material_geometry{};
    missing_material_geometry.size = sizeof(missing_material_geometry);
    missing_material_geometry.version = PG_ABI_VERSION;
    missing_material_geometry.kind = PG_GEOMETRY_BOX;
    missing_material_geometry.dimensions_metres = { 1.0, 1.0, 1.0 };
    missing_material_geometry.enabled = 1U;
    REQUIRE(pg_engine_add_geometry(material_validation_engine, &missing_material_geometry, nullptr) == PG_STATUS_INVALID_ARGUMENT);
    pg_engine_destroy(material_validation_engine);

    surface_render_options baseline_surface{};
    baseline_surface.add_surface = false;
    const auto unobstructed_surface = render_surface_source(baseline_surface);
    surface_render_options opaque_surface = baseline_surface;
    opaque_surface.add_surface = true;
    opaque_surface.occludes = true;
    opaque_surface.reflects = false;
    const auto opaque_output = render_surface_source(opaque_surface);
    REQUIRE(channel_rms(opaque_output, 0U) < channel_rms(unobstructed_surface, 0U) * 1.0e-6);

    surface_render_options quarter_energy_surface = opaque_surface;
    quarter_energy_surface.transmission.fill(0.25);
    const double quarter_energy_ratio = channel_rms(render_surface_source(quarter_energy_surface), 0U) /
        channel_rms(unobstructed_surface, 0U);
    REQUIRE(std::abs(quarter_energy_ratio - 0.5) < 0.002);

    surface_render_options off_axis_surface = opaque_surface;
    off_axis_surface.geometry_centre = { 2.0, 0.0, 1.0 };
    off_axis_surface.geometry_rotation = { 0.0, 90.0, 0.0 };
    const double off_axis_ratio = channel_rms(render_surface_source(off_axis_surface), 0U) /
        channel_rms(unobstructed_surface, 0U);
    REQUIRE(std::abs(off_axis_ratio - 1.0) < 1.0e-9);

    surface_render_options banded_surface = opaque_surface;
    banded_surface.transmission = { 0.64, 0.64, 0.64, 0.64, 0.36, 0.16, 0.04, 0.01 };
    banded_surface.frequency_hz = 250.0;
    auto low_baseline_options = banded_surface;
    low_baseline_options.add_surface = false;
    const double low_band_ratio = channel_rms(render_surface_source(banded_surface), 0U) /
        channel_rms(render_surface_source(low_baseline_options), 0U);
    banded_surface.frequency_hz = 8000.0;
    auto high_baseline_options = banded_surface;
    high_baseline_options.add_surface = false;
    const double high_band_ratio = channel_rms(render_surface_source(banded_surface), 0U) /
        channel_rms(render_surface_source(high_baseline_options), 0U);
    REQUIRE(std::abs(low_band_ratio - 0.8) < 0.002);
    REQUIRE(std::abs(high_band_ratio - 0.2) < 0.002);

    surface_render_options reflection_surface_options{};
    reflection_surface_options.waveform = PG_WAVE_IMPULSE;
    reflection_surface_options.frequency_hz = 1.0;
    reflection_surface_options.source_position = { 1.0, 0.0, 0.0 };
    reflection_surface_options.geometry_centre = { 0.0, 0.0, 3.0 };
    reflection_surface_options.geometry_dimensions = { 10.0, 10.0, 0.0 };
    reflection_surface_options.occludes = false;
    reflection_surface_options.reflects = true;
    reflection_surface_options.start_frame = 0U;
    reflection_surface_options.frame_count = 2048U;
    auto reflection_baseline_options = reflection_surface_options;
    reflection_baseline_options.add_surface = false;
    const auto reflected_impulse = render_surface_source(reflection_surface_options);
    const auto direct_impulse = render_surface_source(reflection_baseline_options);
    std::vector<float> reflection_residual(reflected_impulse.size());
    std::transform(
        reflected_impulse.begin(),
        reflected_impulse.end(),
        direct_impulse.begin(),
        reflection_residual.begin(),
        [](float reflected, float direct) { return reflected - direct; });
    const std::size_t reflection_arrival = first_active_frame(reflection_residual, 1.0e-8F);
    const double reflected_distance = std::sqrt((0.995 * 0.995) + 36.0);
    const auto expected_reflection_arrival = static_cast<std::size_t>(std::llround(reflected_distance / 343.42 * 48000.0));
    REQUIRE(reflection_arrival <= expected_reflection_arrival + 2U && expected_reflection_arrival <= reflection_arrival + 2U);
    REQUIRE(channel_rms(reflection_residual, 0U) > 1.0e-5);

    auto scattered_reflection_options = reflection_surface_options;
    scattered_reflection_options.scattering.fill(0.75);
    const auto scattered_impulse = render_surface_source(scattered_reflection_options);
    std::vector<float> scattered_reflection_residual(scattered_impulse.size());
    std::transform(
        scattered_impulse.begin(),
        scattered_impulse.end(),
        direct_impulse.begin(),
        scattered_reflection_residual.begin(),
        [](float scattered, float direct) { return scattered - direct; });
    const double scattered_pressure_ratio = channel_rms(scattered_reflection_residual, 0U) /
        channel_rms(reflection_residual, 0U);
    REQUIRE(std::abs(scattered_pressure_ratio - 0.5) < 0.002);

    auto solid_box_reflection_options = reflection_surface_options;
    solid_box_reflection_options.geometry_kind = PG_GEOMETRY_BOX;
    solid_box_reflection_options.geometry_centre = { 0.0, 0.0, 3.0 };
    solid_box_reflection_options.geometry_dimensions = { 10.0, 10.0, 1.0 };
    auto front_plane_reflection_options = reflection_surface_options;
    front_plane_reflection_options.geometry_centre = { 0.0, 0.0, 2.5 };
    const auto solid_box_impulse = render_surface_source(solid_box_reflection_options);
    const auto front_plane_impulse = render_surface_source(front_plane_reflection_options);
    REQUIRE(solid_box_impulse.size() == front_plane_impulse.size());
    for (std::size_t index = 0U; index < solid_box_impulse.size(); ++index) {
        REQUIRE(std::abs(solid_box_impulse[index] - front_plane_impulse[index]) < 1.0e-7F);
    }

    auto cardioid_reflection_options = reflection_surface_options;
    cardioid_reflection_options.polar_pattern = PG_POLAR_CARDIOID;
    auto cardioid_direct_options = reflection_baseline_options;
    cardioid_direct_options.polar_pattern = PG_POLAR_CARDIOID;
    const auto cardioid_reflected_impulse = render_surface_source(cardioid_reflection_options);
    const auto cardioid_direct_impulse = render_surface_source(cardioid_direct_options);
    std::vector<float> cardioid_reflection_residual(cardioid_reflected_impulse.size());
    std::transform(
        cardioid_reflected_impulse.begin(),
        cardioid_reflected_impulse.end(),
        cardioid_direct_impulse.begin(),
        cardioid_reflection_residual.begin(),
        [](float reflected, float direct) { return reflected - direct; });
    const double cardioid_reflection_ratio = channel_rms(cardioid_reflection_residual, 0U) /
        channel_rms(reflection_residual, 0U);
    REQUIRE(cardioid_reflection_ratio > 0.99 && cardioid_reflection_ratio <= 1.0);

    pg_engine* engine = create_engine();
    configure_scene(engine);

    pg_device_status initial_device_status{};
    initial_device_status.size = sizeof(initial_device_status);
    initial_device_status.version = PG_ABI_VERSION;
    REQUIRE(pg_engine_get_device_status(engine, &initial_device_status) == PG_STATUS_OK);
    REQUIRE(initial_device_status.running == 0U);
    REQUIRE(initial_device_status.frames_processed == 0U);

    pg_device_config invalid_device{};
    invalid_device.size = sizeof(invalid_device);
    invalid_device.version = PG_ABI_VERSION;
    invalid_device.receiver_index = 99U;
    invalid_device.sample_rate = 48000U;
    invalid_device.period_frames = 512U;
    invalid_device.reflection_order = 1U;
    REQUIRE(pg_engine_start_device(engine, &invalid_device) == PG_STATUS_INVALID_ARGUMENT);
    invalid_device.receiver_index = 0U;
    invalid_device.sample_rate = 1U;
    REQUIRE(pg_engine_start_device(engine, &invalid_device) == PG_STATUS_INVALID_ARGUMENT);
    REQUIRE(pg_engine_stop_device(engine) == PG_STATUS_OK);

    constexpr std::uint32_t sample_rate = 48000;
    constexpr std::uint32_t frames = 4096;
    std::vector<float> first(frames * 2U);
    std::vector<float> second(frames * 2U);
    REQUIRE(pg_engine_render_receiver_block(engine, 0, sample_rate * 2U, frames, sample_rate, first.data(), first.size()) == PG_STATUS_OK);
    REQUIRE(pg_engine_render_receiver_block(engine, 0, sample_rate * 2U, frames, sample_rate, second.data(), second.size()) == PG_STATUS_OK);
    REQUIRE(first == second);
    REQUIRE(std::any_of(first.begin(), first.end(), [](float value) { return std::abs(value) > 1.0e-5F; }));

    bool channels_differ = false;
    for (std::size_t index = 0; index < first.size(); index += 2U) {
        if (std::abs(first[index] - first[index + 1U]) > 1.0e-6F) {
            channels_differ = true;
            break;
        }
    }
    REQUIRE(channels_differ);

    pg_engine_destroy(engine);

    for (int waveform = static_cast<int>(PG_WAVE_SINE); waveform <= static_cast<int>(PG_WAVE_TURBULENCE); ++waveform) {
        const auto rendered = render_primitive(static_cast<pg_waveform_kind>(waveform));
        REQUIRE(std::all_of(rendered.begin(), rendered.end(), [](float value) { return std::isfinite(value); }));
        REQUIRE(std::any_of(rendered.begin(), rendered.end(), [](float value) { return std::abs(value) > 1.0e-7F; }));
    }
    REQUIRE(render_primitive(PG_WAVE_WHITE_NOISE) == render_primitive(PG_WAVE_WHITE_NOISE));
    REQUIRE(render_primitive(PG_WAVE_SINE) != render_primitive(PG_WAVE_SQUARE));

    pg_engine* contiguous_engine = create_engine();
    pg_engine* split_engine = create_engine();
    configure_scene(contiguous_engine);
    configure_scene(split_engine);
    std::vector<float> contiguous(frames * 2U);
    std::vector<float> split(frames * 2U);
    REQUIRE(pg_engine_render_receiver_block(contiguous_engine, 0, 0, frames, sample_rate, contiguous.data(), contiguous.size()) == PG_STATUS_OK);
    REQUIRE(pg_engine_render_receiver_block(split_engine, 0, 0, frames / 2U, sample_rate, split.data(), split.size() / 2U) == PG_STATUS_OK);
    REQUIRE(pg_engine_render_receiver_block(split_engine, 0, frames / 2U, frames / 2U, sample_rate, split.data() + frames, split.size() / 2U) == PG_STATUS_OK);
    REQUIRE(contiguous == split);
    pg_engine_destroy(contiguous_engine);
    pg_engine_destroy(split_engine);

    if (argument_count == 2 && std::string_view(arguments[1]) == "--audio-device-smoke") {
        pg_engine* audio_engine = create_engine();
        configure_scene(audio_engine, 0.0);
        pg_device_config device_config{};
        device_config.size = sizeof(device_config);
        device_config.version = PG_ABI_VERSION;
        device_config.receiver_index = 0U;
        device_config.sample_rate = 48000U;
        device_config.period_frames = 512U;
        device_config.reflection_order = 1U;
        REQUIRE(pg_engine_start_device(audio_engine, &device_config) == PG_STATUS_OK);
        std::this_thread::sleep_for(std::chrono::milliseconds(150));
        pg_device_status status{};
        status.size = sizeof(status);
        status.version = PG_ABI_VERSION;
        REQUIRE(pg_engine_get_device_status(audio_engine, &status) == PG_STATUS_OK);
        REQUIRE(status.running == 1U);
        REQUIRE(status.frames_processed > 0U);
        REQUIRE(status.callback_count > 0U);
        REQUIRE(pg_engine_stop_device(audio_engine) == PG_STATUS_OK);
        REQUIRE(pg_engine_get_device_status(audio_engine, &status) == PG_STATUS_OK);
        REQUIRE(status.running == 0U);
        pg_engine_destroy(audio_engine);
        std::cout << "PULSEGEN_WASAPI_DEVICE_OK frames=" << status.frames_processed
                  << " callbacks=" << status.callback_count << '\n';
    }

    std::cout << "PULSEGEN_NATIVE_ENGINE_OK\n";
    return 0;
    }
    catch (const std::exception& exception) {
        std::cerr << exception.what() << '\n';
        return 1;
    }
}

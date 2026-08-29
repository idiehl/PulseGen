#include "pulsegen/pulsegen.h"

#include <algorithm>
#include <array>
#include <cassert>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <numeric>
#include <string_view>
#include <thread>
#include <vector>

namespace {

pg_engine* create_engine()
{
    pg_engine_config config{};
    config.size = sizeof(config);
    config.version = PG_ABI_VERSION;
    config.max_emitters = 8;
    config.max_receivers = 4;
    config.max_keyframes_per_path = 64;
    pg_engine* engine = nullptr;
    assert(pg_engine_create(&config, &engine) == PG_STATUS_OK);
    assert(engine != nullptr);
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
    assert(pg_engine_set_atmosphere(engine, &atmosphere) == PG_STATUS_OK);

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
    assert(pg_engine_set_room(engine, &room) == PG_STATUS_OK);

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
    assert(pg_engine_add_sine_emitter(engine, &emitter, nullptr) == PG_STATUS_OK);

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
    assert(pg_engine_add_receiver(engine, &receiver, nullptr) == PG_STATUS_OK);
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
    assert(pg_engine_set_room(engine, &room) == PG_STATUS_OK);

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
    assert(pg_engine_add_signal_emitter(engine, &emitter, nullptr) == PG_STATUS_OK);

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
    assert(pg_engine_add_receiver(engine, &receiver, nullptr) == PG_STATUS_OK);

    constexpr std::uint32_t frames = 4096;
    std::vector<float> output(frames * 2U);
    assert(pg_engine_render_receiver_block(engine, 0, 0, frames, 48000, output.data(), output.size()) == PG_STATUS_OK);
    pg_engine_destroy(engine);
    return output;
}

} // namespace

int main(int argument_count, char** arguments)
{
    static_assert(PG_ABI_VERSION == 1U);
    assert(pg_get_abi_version() == PG_ABI_VERSION);
    assert(std::abs(pg_calculate_speed_of_sound(20.0) - 343.42) < 1.0e-9);

    const double approaching = pg_calculate_observed_frequency(440.0, 343.42, 20.0, 0.0);
    const double receding = pg_calculate_observed_frequency(440.0, 343.42, -20.0, 0.0);
    assert(approaching > 440.0);
    assert(receding < 440.0);
    assert(std::isnan(pg_calculate_observed_frequency(440.0, 343.42, 343.42, 0.0)));

    pg_engine* engine = create_engine();
    configure_scene(engine);

    pg_device_status initial_device_status{};
    initial_device_status.size = sizeof(initial_device_status);
    initial_device_status.version = PG_ABI_VERSION;
    assert(pg_engine_get_device_status(engine, &initial_device_status) == PG_STATUS_OK);
    assert(initial_device_status.running == 0U);
    assert(initial_device_status.frames_processed == 0U);

    pg_device_config invalid_device{};
    invalid_device.size = sizeof(invalid_device);
    invalid_device.version = PG_ABI_VERSION;
    invalid_device.receiver_index = 99U;
    invalid_device.sample_rate = 48000U;
    invalid_device.period_frames = 512U;
    invalid_device.reflection_order = 1U;
    assert(pg_engine_start_device(engine, &invalid_device) == PG_STATUS_INVALID_ARGUMENT);
    invalid_device.receiver_index = 0U;
    invalid_device.sample_rate = 1U;
    assert(pg_engine_start_device(engine, &invalid_device) == PG_STATUS_INVALID_ARGUMENT);
    assert(pg_engine_stop_device(engine) == PG_STATUS_OK);

    constexpr std::uint32_t sample_rate = 48000;
    constexpr std::uint32_t frames = 4096;
    std::vector<float> first(frames * 2U);
    std::vector<float> second(frames * 2U);
    assert(pg_engine_render_receiver_block(engine, 0, sample_rate * 2U, frames, sample_rate, first.data(), first.size()) == PG_STATUS_OK);
    assert(pg_engine_render_receiver_block(engine, 0, sample_rate * 2U, frames, sample_rate, second.data(), second.size()) == PG_STATUS_OK);
    assert(first == second);
    assert(std::any_of(first.begin(), first.end(), [](float value) { return std::abs(value) > 1.0e-5F; }));

    bool channels_differ = false;
    for (std::size_t index = 0; index < first.size(); index += 2U) {
        if (std::abs(first[index] - first[index + 1U]) > 1.0e-6F) {
            channels_differ = true;
            break;
        }
    }
    assert(channels_differ);

    pg_engine_destroy(engine);

    for (int waveform = static_cast<int>(PG_WAVE_SINE); waveform <= static_cast<int>(PG_WAVE_TURBULENCE); ++waveform) {
        const auto rendered = render_primitive(static_cast<pg_waveform_kind>(waveform));
        assert(std::all_of(rendered.begin(), rendered.end(), [](float value) { return std::isfinite(value); }));
        assert(std::any_of(rendered.begin(), rendered.end(), [](float value) { return std::abs(value) > 1.0e-7F; }));
    }
    assert(render_primitive(PG_WAVE_WHITE_NOISE) == render_primitive(PG_WAVE_WHITE_NOISE));
    assert(render_primitive(PG_WAVE_SINE) != render_primitive(PG_WAVE_SQUARE));

    pg_engine* contiguous_engine = create_engine();
    pg_engine* split_engine = create_engine();
    configure_scene(contiguous_engine);
    configure_scene(split_engine);
    std::vector<float> contiguous(frames * 2U);
    std::vector<float> split(frames * 2U);
    assert(pg_engine_render_receiver_block(contiguous_engine, 0, 0, frames, sample_rate, contiguous.data(), contiguous.size()) == PG_STATUS_OK);
    assert(pg_engine_render_receiver_block(split_engine, 0, 0, frames / 2U, sample_rate, split.data(), split.size() / 2U) == PG_STATUS_OK);
    assert(pg_engine_render_receiver_block(split_engine, 0, frames / 2U, frames / 2U, sample_rate, split.data() + frames, split.size() / 2U) == PG_STATUS_OK);
    assert(contiguous == split);
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
        assert(pg_engine_start_device(audio_engine, &device_config) == PG_STATUS_OK);
        std::this_thread::sleep_for(std::chrono::milliseconds(150));
        pg_device_status status{};
        status.size = sizeof(status);
        status.version = PG_ABI_VERSION;
        assert(pg_engine_get_device_status(audio_engine, &status) == PG_STATUS_OK);
        assert(status.running == 1U);
        assert(status.frames_processed > 0U);
        assert(status.callback_count > 0U);
        assert(pg_engine_stop_device(audio_engine) == PG_STATUS_OK);
        assert(pg_engine_get_device_status(audio_engine, &status) == PG_STATUS_OK);
        assert(status.running == 0U);
        pg_engine_destroy(audio_engine);
        std::cout << "PULSEGEN_WASAPI_DEVICE_OK frames=" << status.frames_processed
                  << " callbacks=" << status.callback_count << '\n';
    }

    std::cout << "PULSEGEN_NATIVE_ENGINE_OK\n";
    return 0;
}

#include "pulsegen/pulsegen.h"

#include <algorithm>
#include <array>
#include <cassert>
#include <cmath>
#include <cstdint>
#include <iostream>
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

void configure_scene(pg_engine* engine)
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
    emitter.amplitude = 0.5;
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

} // namespace

int main()
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
    std::cout << "PULSEGEN_NATIVE_ENGINE_OK\n";
    return 0;
}


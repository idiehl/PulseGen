#include "pulsegen/pulsegen.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <numbers>
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
    bool receiver_moves{};
    pg_vec3 receiver_motion_start{};
    pg_vec3 receiver_motion_end{};
    double receiver_motion_end_seconds{ 2.0 };
    bool use_explicit_capsules{};
    pg_vec3 left_capsule_position{ -0.005, 0.0, 0.0 };
    pg_vec3 right_capsule_position{ 0.005, 0.0, 0.0 };
    pg_vec3 left_capsule_rotation{};
    pg_vec3 right_capsule_rotation{};
    pg_polar_pattern left_capsule_pattern{ PG_POLAR_OMNI };
    pg_polar_pattern right_capsule_pattern{ PG_POLAR_OMNI };
    double left_capsule_sensitivity_db{};
    double right_capsule_sensitivity_db{};
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
    std::uint32_t receiver_index = 0U;
    REQUIRE(pg_engine_add_receiver(engine, &receiver, &receiver_index) == PG_STATUS_OK);
    if (options.use_explicit_capsules) {
        pg_microphone_capsule_desc left_capsule{};
        left_capsule.size = sizeof(left_capsule);
        left_capsule.version = PG_ABI_VERSION;
        left_capsule.relative_position_metres = options.left_capsule_position;
        left_capsule.relative_rotation_degrees = options.left_capsule_rotation;
        left_capsule.polar_pattern = options.left_capsule_pattern;
        left_capsule.sensitivity_db = options.left_capsule_sensitivity_db;
        pg_microphone_capsule_desc right_capsule{};
        right_capsule.size = sizeof(right_capsule);
        right_capsule.version = PG_ABI_VERSION;
        right_capsule.relative_position_metres = options.right_capsule_position;
        right_capsule.relative_rotation_degrees = options.right_capsule_rotation;
        right_capsule.polar_pattern = options.right_capsule_pattern;
        right_capsule.sensitivity_db = options.right_capsule_sensitivity_db;
        REQUIRE(pg_engine_set_receiver_capsules(engine, receiver_index, &left_capsule, &right_capsule) == PG_STATUS_OK);
    }
    if (options.receiver_moves) {
        const std::array<pg_motion_keyframe, 2> receiver_keyframes{
            pg_motion_keyframe{ 0.0, options.receiver_motion_start },
            pg_motion_keyframe{ options.receiver_motion_end_seconds, options.receiver_motion_end }
        };
        pg_receiver_motion_desc receiver_motion{};
        receiver_motion.size = sizeof(receiver_motion);
        receiver_motion.version = PG_ABI_VERSION;
        receiver_motion.receiver_index = receiver_index;
        receiver_motion.motion_path_kind = PG_PATH_LINEAR;
        receiver_motion.keyframes = receiver_keyframes.data();
        receiver_motion.keyframe_count = static_cast<std::uint32_t>(receiver_keyframes.size());
        REQUIRE(pg_engine_set_receiver_motion(engine, &receiver_motion) == PG_STATUS_OK);
    }

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

struct atmosphere_impulse_options {
    pg_vec3 source_position{ 0.0, -50.0, 0.0 };
    pg_vec3 receiver_position{ 0.0, 50.0, 0.0 };
    std::vector<pg_atmosphere_layer_desc> layers;
    std::vector<pg_wind_zone_desc> wind_zones;
    std::uint32_t integration_segments{ 100U };
    std::uint32_t frame_count{ 24000U };
};

std::vector<float> render_atmosphere_impulse(const atmosphere_impulse_options& options)
{
    constexpr std::uint32_t sample_rate = 48000U;
    pg_engine* engine = create_engine();

    pg_atmosphere_field_desc atmosphere{};
    atmosphere.size = sizeof(atmosphere);
    atmosphere.version = PG_ABI_VERSION;
    atmosphere.temperature_celsius = 20.0;
    atmosphere.relative_humidity_percent = 0.0;
    atmosphere.pressure_kilopascals = 101.325;
    atmosphere.layers = options.layers.empty() ? nullptr : options.layers.data();
    atmosphere.layer_count = static_cast<std::uint32_t>(options.layers.size());
    atmosphere.wind_zones = options.wind_zones.empty() ? nullptr : options.wind_zones.data();
    atmosphere.wind_zone_count = static_cast<std::uint32_t>(options.wind_zones.size());
    atmosphere.integration_segments = options.integration_segments;
    REQUIRE(pg_engine_set_atmosphere_field(engine, &atmosphere) == PG_STATUS_OK);

    pg_room_desc room{};
    room.size = sizeof(room);
    room.version = PG_ABI_VERSION;
    room.dimensions_metres = { 20.0, 8.0, 12.0 };
    room.enabled = 0U;
    REQUIRE(pg_engine_set_room(engine, &room) == PG_STATUS_OK);

    const std::array<pg_motion_keyframe, 2> keyframes{
        pg_motion_keyframe{ 0.0, options.source_position },
        pg_motion_keyframe{ 1.0, options.source_position }
    };
    pg_signal_emitter_desc emitter{};
    emitter.size = sizeof(emitter);
    emitter.version = PG_ABI_VERSION;
    emitter.name_utf8 = "Atmosphere_Impulse";
    emitter.waveform = PG_WAVE_IMPULSE;
    emitter.frequency_hz = 1.0;
    emitter.amplitude = 1.0;
    emitter.pulse_width = 0.5;
    emitter.gain_linear = 1.0;
    emitter.min_distance_metres = 0.01;
    emitter.motion_path_kind = PG_PATH_LINEAR;
    emitter.keyframes = keyframes.data();
    emitter.keyframe_count = static_cast<std::uint32_t>(keyframes.size());
    emitter.noise_seed = 17U;
    emitter.enabled = 1U;
    REQUIRE(pg_engine_add_signal_emitter(engine, &emitter, nullptr) == PG_STATUS_OK);

    pg_receiver_rig_desc receiver{};
    receiver.size = sizeof(receiver);
    receiver.version = PG_ABI_VERSION;
    receiver.name_utf8 = "Atmosphere_Microphones";
    receiver.kind = PG_RECEIVER_STEREO_MICROPHONE;
    receiver.centre_metres = options.receiver_position;
    receiver.spacing_metres = 0.01;
    receiver.head_radius_metres = 0.09;
    receiver.polar_pattern = PG_POLAR_OMNI;
    receiver.enabled = 1U;
    REQUIRE(pg_engine_add_receiver(engine, &receiver, nullptr) == PG_STATUS_OK);

    std::vector<float> output(static_cast<std::size_t>(options.frame_count) * 2U);
    REQUIRE(pg_engine_render_receiver_block(
        engine,
        0U,
        0U,
        options.frame_count,
        sample_rate,
        output.data(),
        output.size()) == PG_STATUS_OK);
    pg_engine_destroy(engine);
    return output;
}

struct surface_render_options {
    bool add_surface{ true };
    bool use_imported_mesh{};
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

constexpr std::string_view bounded_wall_obj =
    "o BoundedWall\n"
    "v -5 -5 0\n"
    "v 5 -5 0\n"
    "v 5 5 0\n"
    "v -5 5 0\n"
    "f 1 2 3\n"
    "f 1 3 4\n";

constexpr std::string_view embedded_triangle_gltf = R"gltf({
  "asset":{"version":"2.0"},
  "buffers":[{"uri":"data:application/octet-stream;base64,AAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAgD8AAAAAAAABAAIA","byteLength":42}],
  "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36,"target":34962},{"buffer":0,"byteOffset":36,"byteLength":6,"target":34963}],
  "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]},{"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR","min":[0],"max":[2]}],
  "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1,"mode":4}]}],
  "nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0
})gltf";

void append_little_endian_u32(std::vector<std::uint8_t>& destination, std::uint32_t value)
{
    for (std::uint32_t shift = 0U; shift < 32U; shift += 8U) {
        destination.push_back(static_cast<std::uint8_t>((value >> shift) & 0xffU));
    }
}

std::vector<std::uint8_t> make_triangle_glb()
{
    std::string json = R"json({"asset":{"version":"2.0"},"buffers":[{"byteLength":42}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36,"target":34962},{"buffer":0,"byteOffset":36,"byteLength":6,"target":34963}],"accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]},{"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR","min":[0],"max":[2]}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1,"mode":4}]}],"nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0})json";
    while ((json.size() & 3U) != 0U) {
        json.push_back(' ');
    }
    const std::array<float, 9> positions{ 0.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F, 0.0F, 1.0F, 0.0F };
    std::vector<std::uint8_t> binary;
    const auto* position_bytes = reinterpret_cast<const std::uint8_t*>(positions.data());
    binary.insert(binary.end(), position_bytes, position_bytes + sizeof(positions));
    binary.insert(binary.end(), { 0U, 0U, 1U, 0U, 2U, 0U });
    while ((binary.size() & 3U) != 0U) {
        binary.push_back(0U);
    }

    std::vector<std::uint8_t> result;
    const std::uint32_t total_length = static_cast<std::uint32_t>(12U + 8U + json.size() + 8U + binary.size());
    result.reserve(total_length);
    append_little_endian_u32(result, 0x46546c67U);
    append_little_endian_u32(result, 2U);
    append_little_endian_u32(result, total_length);
    append_little_endian_u32(result, static_cast<std::uint32_t>(json.size()));
    append_little_endian_u32(result, 0x4e4f534aU);
    result.insert(result.end(), json.begin(), json.end());
    append_little_endian_u32(result, static_cast<std::uint32_t>(binary.size()));
    append_little_endian_u32(result, 0x004e4942U);
    result.insert(result.end(), binary.begin(), binary.end());
    return result;
}

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

        if (options.use_imported_mesh) {
            pg_mesh_geometry_desc geometry{};
            geometry.size = sizeof(geometry);
            geometry.version = PG_ABI_VERSION;
            geometry.name_utf8 = "Bounded_Mesh_Surface";
            geometry.encoded_data = bounded_wall_obj.data();
            geometry.encoded_size_bytes = bounded_wall_obj.size();
            geometry.format_hint_utf8 = "obj";
            geometry.centre_metres = options.geometry_centre;
            geometry.scale = { 1.0, 1.0, 1.0 };
            geometry.rotation_degrees = options.geometry_rotation;
            geometry.material_index = material_index;
            geometry.occludes = options.occludes ? 1U : 0U;
            geometry.reflects = options.reflects ? 1U : 0U;
            geometry.enabled = 1U;
            geometry.max_triangles = PG_MAX_IMPORTED_MESH_TRIANGLES;
            pg_mesh_import_receipt receipt{};
            receipt.size = sizeof(receipt);
            receipt.version = PG_ABI_VERSION;
            REQUIRE(pg_engine_add_mesh_geometry(engine, &geometry, nullptr, &receipt) == PG_STATUS_OK);
            REQUIRE(receipt.source_mesh_count == 1U);
            REQUIRE(receipt.source_vertex_count == 4U);
            REQUIRE(receipt.triangle_count == 2U);
            REQUIRE(receipt.bvh_node_count == 1U);
        } else {
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

double estimate_channel_frequency(
    const std::vector<float>& samples,
    std::size_t channel,
    std::uint32_t sample_rate)
{
    std::size_t upward_crossings = 0U;
    for (std::size_t frame = 1U; frame < samples.size() / 2U; ++frame) {
        if (samples[((frame - 1U) * 2U) + channel] <= 0.0F && samples[(frame * 2U) + channel] > 0.0F) {
            ++upward_crossings;
        }
    }
    const double duration = static_cast<double>(samples.size() / 2U) / sample_rate;
    return static_cast<double>(upward_crossings) / duration;
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
    static_assert(PG_ABI_VERSION == 4U);
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

    static_render_options stationary_receiver_frequency{};
    stationary_receiver_frequency.source_position = { 0.0, 0.0, 100.0 };
    stationary_receiver_frequency.start_frame = 24000U;
    stationary_receiver_frequency.frame_count = 24000U;
    stationary_receiver_frequency.receiver_spacing_metres = 0.18;
    const auto stationary_receiver_output = render_static_source(stationary_receiver_frequency);
    auto approaching_receiver_frequency = stationary_receiver_frequency;
    approaching_receiver_frequency.receiver_moves = true;
    approaching_receiver_frequency.receiver_motion_start = { 0.0, 0.0, 0.0 };
    approaching_receiver_frequency.receiver_motion_end = { 0.0, 0.0, 40.0 };
    const auto approaching_receiver_output = render_static_source(approaching_receiver_frequency);
    auto receding_receiver_frequency = approaching_receiver_frequency;
    receding_receiver_frequency.receiver_motion_end = { 0.0, 0.0, -40.0 };
    const auto receding_receiver_output = render_static_source(receding_receiver_frequency);
    constexpr double humid_speed_of_sound = 344.04;
    const double stationary_frequency = estimate_channel_frequency(stationary_receiver_output, 0U, 48000U);
    const double measured_approaching_frequency = estimate_channel_frequency(approaching_receiver_output, 0U, 48000U);
    const double measured_receding_frequency = estimate_channel_frequency(receding_receiver_output, 0U, 48000U);
    const double expected_approaching_frequency = pg_calculate_observed_frequency(440.0, humid_speed_of_sound, 0.0, 20.0);
    const double expected_receding_frequency = pg_calculate_observed_frequency(440.0, humid_speed_of_sound, 0.0, -20.0);
    REQUIRE(std::abs(stationary_frequency - 440.0) <= 2.0);
    REQUIRE(std::abs(measured_approaching_frequency - expected_approaching_frequency) <= 2.1);
    REQUIRE(std::abs(measured_receding_frequency - expected_receding_frequency) <= 2.1);
    REQUIRE(measured_approaching_frequency > stationary_frequency);
    REQUIRE(measured_receding_frequency < stationary_frequency);
    REQUIRE(approaching_receiver_output == render_static_source(approaching_receiver_frequency));
    for (std::size_t frame = 0U; frame < approaching_receiver_frequency.frame_count; ++frame) {
        REQUIRE(approaching_receiver_output[frame * 2U] == approaching_receiver_output[(frame * 2U) + 1U]);
    }

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

    atmosphere_impulse_options layered_atmosphere{};
    pg_atmosphere_layer_desc cold_layer{};
    cold_layer.size = sizeof(cold_layer);
    cold_layer.version = PG_ABI_VERSION;
    cold_layer.base_altitude_metres = -60.0;
    cold_layer.top_altitude_metres = 0.0;
    cold_layer.temperature_celsius = 0.0;
    cold_layer.relative_humidity_percent = 0.0;
    cold_layer.pressure_kilopascals = 101.325;
    pg_atmosphere_layer_desc warm_layer = cold_layer;
    warm_layer.base_altitude_metres = 0.0;
    warm_layer.top_altitude_metres = 60.0;
    warm_layer.temperature_celsius = 40.0;
    layered_atmosphere.layers = { cold_layer, warm_layer };
    const auto layered_impulse = render_atmosphere_impulse(layered_atmosphere);
    const auto layered_arrival = first_active_frame(layered_impulse);
    const double expected_layered_travel = (50.0 / 331.3) + (50.0 / 355.54);
    const auto expected_layered_arrival = static_cast<std::size_t>(std::llround(expected_layered_travel * 48000.0));
    REQUIRE(layered_arrival <= expected_layered_arrival + 1U && expected_layered_arrival <= layered_arrival + 1U);

    atmosphere_impulse_options still_field{};
    still_field.source_position = { -50.0, 0.0, 0.0 };
    still_field.receiver_position = { 50.0, 0.0, 0.0 };
    still_field.integration_segments = 200U;
    const auto still_field_impulse = render_atmosphere_impulse(still_field);
    pg_wind_zone_desc tailwind_zone{};
    tailwind_zone.size = sizeof(tailwind_zone);
    tailwind_zone.version = PG_ABI_VERSION;
    tailwind_zone.centre_metres = { 0.0, 50.0, 0.0 };
    tailwind_zone.dimensions_metres = { 40.0, 10.0, 10.0 };
    tailwind_zone.velocity_metres_per_second = { 30.0, 0.0, 0.0 };
    tailwind_zone.noise_seed = 88U;
    auto outside_field = still_field;
    outside_field.wind_zones = { tailwind_zone };
    REQUIRE(render_atmosphere_impulse(outside_field) == still_field_impulse);

    auto tailwind_field = outside_field;
    tailwind_field.wind_zones[0].centre_metres = { 0.0, 0.0, 0.0 };
    const auto tailwind_impulse = render_atmosphere_impulse(tailwind_field);
    REQUIRE(first_active_frame(tailwind_impulse) < first_active_frame(still_field_impulse));
    auto headwind_field = tailwind_field;
    headwind_field.wind_zones[0].velocity_metres_per_second = { -30.0, 0.0, 0.0 };
    const auto headwind_impulse = render_atmosphere_impulse(headwind_field);
    REQUIRE(first_active_frame(headwind_impulse) > first_active_frame(still_field_impulse));
    tailwind_field.wind_zones[0].turbulence = 0.2;
    REQUIRE(render_atmosphere_impulse(tailwind_field) == render_atmosphere_impulse(tailwind_field));

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

    auto independent_patterns = rear_omni;
    independent_patterns.use_explicit_capsules = true;
    independent_patterns.left_capsule_pattern = PG_POLAR_CARDIOID;
    independent_patterns.right_capsule_pattern = PG_POLAR_OMNI;
    const auto independent_pattern_pair = render_static_source(independent_patterns);
    REQUIRE(channel_rms(independent_pattern_pair, 0U) < channel_rms(independent_pattern_pair, 1U) * 2.0e-6);

    auto independent_sensitivity = rear_omni;
    independent_sensitivity.source_position = { 0.0, 0.0, 2.0 };
    independent_sensitivity.use_explicit_capsules = true;
    independent_sensitivity.left_capsule_sensitivity_db = -6.020599913279624;
    const auto independent_sensitivity_pair = render_static_source(independent_sensitivity);
    const double independent_sensitivity_ratio = channel_rms(independent_sensitivity_pair, 0U) /
        channel_rms(independent_sensitivity_pair, 1U);
    REQUIRE(std::abs(independent_sensitivity_ratio - 0.5) < 0.002);

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

    pg_atmosphere_field_desc invalid_field{};
    invalid_field.size = sizeof(invalid_field);
    invalid_field.version = PG_ABI_VERSION;
    invalid_field.temperature_celsius = 20.0;
    invalid_field.relative_humidity_percent = 50.0;
    invalid_field.pressure_kilopascals = 101.325;
    invalid_field.integration_segments = 0U;
    REQUIRE(pg_engine_set_atmosphere_field(atmosphere_validation_engine, &invalid_field) == PG_STATUS_INVALID_ARGUMENT);
    invalid_field.integration_segments = 1U;
    invalid_field.layer_count = 1U;
    REQUIRE(pg_engine_set_atmosphere_field(atmosphere_validation_engine, &invalid_field) == PG_STATUS_INVALID_ARGUMENT);
    const std::array<pg_atmosphere_layer_desc, 2> overlapping_layers{ cold_layer, cold_layer };
    invalid_field.layers = overlapping_layers.data();
    invalid_field.layer_count = static_cast<std::uint32_t>(overlapping_layers.size());
    REQUIRE(pg_engine_set_atmosphere_field(atmosphere_validation_engine, &invalid_field) == PG_STATUS_INVALID_ARGUMENT);
    invalid_field.layers = nullptr;
    invalid_field.layer_count = 0U;
    pg_wind_zone_desc invalid_zone = tailwind_zone;
    invalid_zone.dimensions_metres.y = 0.0;
    invalid_field.wind_zones = &invalid_zone;
    invalid_field.wind_zone_count = 1U;
    REQUIRE(pg_engine_set_atmosphere_field(atmosphere_validation_engine, &invalid_field) == PG_STATUS_INVALID_ARGUMENT);
    invalid_zone.dimensions_metres.y = 10.0;
    invalid_zone.velocity_metres_per_second = { 300.0, 0.0, 0.0 };
    REQUIRE(pg_engine_set_atmosphere_field(atmosphere_validation_engine, &invalid_field) == PG_STATUS_INVALID_ARGUMENT);
    pg_engine_destroy(atmosphere_validation_engine);

    pg_engine* receiver_motion_validation_engine = create_engine();
    pg_receiver_rig_desc motion_receiver{};
    motion_receiver.size = sizeof(motion_receiver);
    motion_receiver.version = PG_ABI_VERSION;
    motion_receiver.name_utf8 = "Motion_Validation";
    motion_receiver.kind = PG_RECEIVER_STEREO_MICROPHONE;
    motion_receiver.spacing_metres = 0.18;
    motion_receiver.head_radius_metres = 0.09;
    motion_receiver.polar_pattern = PG_POLAR_OMNI;
    motion_receiver.enabled = 1U;
    std::uint32_t motion_receiver_index = 0U;
    REQUIRE(pg_engine_add_receiver(receiver_motion_validation_engine, &motion_receiver, &motion_receiver_index) == PG_STATUS_OK);
    const std::array<pg_motion_keyframe, 2> valid_receiver_motion_keyframes{
        pg_motion_keyframe{ 0.0, { 0.0, 0.0, 0.0 } },
        pg_motion_keyframe{ 1.0, { 0.0, 0.0, 10.0 } }
    };
    pg_receiver_motion_desc receiver_motion_descriptor{};
    receiver_motion_descriptor.size = sizeof(receiver_motion_descriptor);
    receiver_motion_descriptor.version = PG_ABI_VERSION;
    receiver_motion_descriptor.receiver_index = motion_receiver_index + 1U;
    receiver_motion_descriptor.motion_path_kind = PG_PATH_LINEAR;
    receiver_motion_descriptor.keyframes = valid_receiver_motion_keyframes.data();
    receiver_motion_descriptor.keyframe_count = static_cast<std::uint32_t>(valid_receiver_motion_keyframes.size());
    REQUIRE(pg_engine_set_receiver_motion(receiver_motion_validation_engine, &receiver_motion_descriptor) == PG_STATUS_INVALID_ARGUMENT);
    receiver_motion_descriptor.receiver_index = motion_receiver_index;
    receiver_motion_descriptor.keyframe_count = 1U;
    REQUIRE(pg_engine_set_receiver_motion(receiver_motion_validation_engine, &receiver_motion_descriptor) == PG_STATUS_INVALID_ARGUMENT);
    const std::array<pg_motion_keyframe, 2> unordered_receiver_motion_keyframes{
        pg_motion_keyframe{ 1.0, { 0.0, 0.0, 0.0 } },
        pg_motion_keyframe{ 1.0, { 0.0, 0.0, 10.0 } }
    };
    receiver_motion_descriptor.keyframes = unordered_receiver_motion_keyframes.data();
    receiver_motion_descriptor.keyframe_count = static_cast<std::uint32_t>(unordered_receiver_motion_keyframes.size());
    REQUIRE(pg_engine_set_receiver_motion(receiver_motion_validation_engine, &receiver_motion_descriptor) == PG_STATUS_INVALID_ARGUMENT);
    receiver_motion_descriptor.keyframes = valid_receiver_motion_keyframes.data();
    REQUIRE(pg_engine_set_receiver_motion(receiver_motion_validation_engine, &receiver_motion_descriptor) == PG_STATUS_OK);
    pg_engine_destroy(receiver_motion_validation_engine);

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

    pg_engine* mesh_validation_engine = create_engine();
    pg_acoustic_material_desc mesh_material{};
    mesh_material.size = sizeof(mesh_material);
    mesh_material.version = PG_ABI_VERSION;
    mesh_material.name_utf8 = "Mesh_Test_Material";
    for (std::size_t index = 0U; index < standard_bands.size(); ++index) {
        mesh_material.octave_band_centres_hz[index] = standard_bands[index];
        mesh_material.absorption[index] = 0.1;
        mesh_material.scattering[index] = 0.1;
        mesh_material.transmission[index] = 0.25;
    }
    mesh_material.roughness = 0.2;
    std::uint32_t mesh_material_index = 0U;
    REQUIRE(pg_engine_add_acoustic_material(mesh_validation_engine, &mesh_material, &mesh_material_index) == PG_STATUS_OK);

    pg_mesh_geometry_desc mesh_descriptor{};
    mesh_descriptor.size = sizeof(mesh_descriptor);
    mesh_descriptor.version = PG_ABI_VERSION;
    mesh_descriptor.name_utf8 = "Validated_Mesh";
    mesh_descriptor.encoded_data = bounded_wall_obj.data();
    mesh_descriptor.encoded_size_bytes = bounded_wall_obj.size();
    mesh_descriptor.format_hint_utf8 = ".OBJ";
    mesh_descriptor.centre_metres = { 0.0, 0.0, 1.0 };
    mesh_descriptor.scale = { 2.0, 1.0, 1.0 };
    mesh_descriptor.material_index = mesh_material_index;
    mesh_descriptor.occludes = 1U;
    mesh_descriptor.enabled = 1U;
    mesh_descriptor.max_triangles = PG_MAX_IMPORTED_MESH_TRIANGLES;
    pg_mesh_import_receipt mesh_receipt{};
    mesh_receipt.size = sizeof(mesh_receipt);
    mesh_receipt.version = PG_ABI_VERSION;
    std::uint32_t mesh_index = std::numeric_limits<std::uint32_t>::max();
    REQUIRE(pg_engine_add_mesh_geometry(mesh_validation_engine, &mesh_descriptor, &mesh_index, &mesh_receipt) == PG_STATUS_OK);
    REQUIRE(mesh_index == 0U);
    REQUIRE(mesh_receipt.source_mesh_count == 1U);
    REQUIRE(mesh_receipt.source_vertex_count == 4U);
    REQUIRE(mesh_receipt.triangle_count == 2U);
    REQUIRE(mesh_receipt.bvh_node_count == 1U);
    REQUIRE(std::abs(mesh_receipt.bounds_min_metres.x + 10.0) < 1.0e-9);
    REQUIRE(std::abs(mesh_receipt.bounds_max_metres.x - 10.0) < 1.0e-9);
    REQUIRE(std::abs(mesh_receipt.bounds_min_metres.z - 1.0) < 1.0e-9);
    REQUIRE(std::abs(mesh_receipt.bounds_max_metres.z - 1.0) < 1.0e-9);

    pg_mesh_geometry_desc gltf_descriptor = mesh_descriptor;
    gltf_descriptor.name_utf8 = "Embedded_glTF";
    gltf_descriptor.encoded_data = embedded_triangle_gltf.data();
    gltf_descriptor.encoded_size_bytes = embedded_triangle_gltf.size();
    gltf_descriptor.format_hint_utf8 = "gltf";
    gltf_descriptor.centre_metres = { 20.0, 0.0, 0.0 };
    gltf_descriptor.scale = { 1.0, 1.0, 1.0 };
    pg_mesh_import_receipt gltf_receipt{};
    gltf_receipt.size = sizeof(gltf_receipt);
    gltf_receipt.version = PG_ABI_VERSION;
    REQUIRE(pg_engine_add_mesh_geometry(mesh_validation_engine, &gltf_descriptor, nullptr, &gltf_receipt) == PG_STATUS_OK);
    REQUIRE(gltf_receipt.source_mesh_count == 1U);
    REQUIRE(gltf_receipt.source_vertex_count == 3U);
    REQUIRE(gltf_receipt.triangle_count == 1U);

    const std::vector<std::uint8_t> triangle_glb = make_triangle_glb();
    pg_mesh_geometry_desc glb_descriptor = gltf_descriptor;
    glb_descriptor.name_utf8 = "Embedded_GLB";
    glb_descriptor.encoded_data = triangle_glb.data();
    glb_descriptor.encoded_size_bytes = triangle_glb.size();
    glb_descriptor.format_hint_utf8 = "glb";
    glb_descriptor.centre_metres = { 30.0, 0.0, 0.0 };
    pg_mesh_import_receipt glb_receipt{};
    glb_receipt.size = sizeof(glb_receipt);
    glb_receipt.version = PG_ABI_VERSION;
    REQUIRE(pg_engine_add_mesh_geometry(mesh_validation_engine, &glb_descriptor, nullptr, &glb_receipt) == PG_STATUS_OK);
    REQUIRE(glb_receipt.source_mesh_count == 1U);
    REQUIRE(glb_receipt.source_vertex_count == 3U);
    REQUIRE(glb_receipt.triangle_count == 1U);

    pg_mesh_geometry_desc rejected_mesh = mesh_descriptor;
    rejected_mesh.format_hint_utf8 = "fbx";
    REQUIRE(pg_engine_add_mesh_geometry(mesh_validation_engine, &rejected_mesh, nullptr, nullptr) == PG_STATUS_INVALID_ARGUMENT);
    rejected_mesh = mesh_descriptor;
    rejected_mesh.reflects = 1U;
    REQUIRE(pg_engine_add_mesh_geometry(mesh_validation_engine, &rejected_mesh, nullptr, nullptr) == PG_STATUS_INVALID_ARGUMENT);
    constexpr std::string_view malformed_obj = "this is not an OBJ mesh";
    rejected_mesh = mesh_descriptor;
    rejected_mesh.encoded_data = malformed_obj.data();
    rejected_mesh.encoded_size_bytes = malformed_obj.size();
    REQUIRE(pg_engine_add_mesh_geometry(mesh_validation_engine, &rejected_mesh, nullptr, nullptr) == PG_STATUS_INVALID_SCENE);
    rejected_mesh = mesh_descriptor;
    rejected_mesh.max_triangles = 1U;
    REQUIRE(pg_engine_add_mesh_geometry(mesh_validation_engine, &rejected_mesh, nullptr, nullptr) == PG_STATUS_CAPACITY_EXCEEDED);
    pg_engine_destroy(mesh_validation_engine);

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

    surface_render_options imported_mesh_surface = quarter_energy_surface;
    imported_mesh_surface.use_imported_mesh = true;
    const auto imported_mesh_output = render_surface_source(imported_mesh_surface);
    const double imported_mesh_ratio = channel_rms(imported_mesh_output, 0U) /
        channel_rms(unobstructed_surface, 0U);
    REQUIRE(std::abs(imported_mesh_ratio - 0.5) < 0.002);
    REQUIRE(imported_mesh_output == render_surface_source(imported_mesh_surface));

    auto off_axis_mesh_surface = imported_mesh_surface;
    off_axis_mesh_surface.geometry_centre = { 20.0, 0.0, 1.0 };
    const double off_axis_mesh_ratio = channel_rms(render_surface_source(off_axis_mesh_surface), 0U) /
        channel_rms(unobstructed_surface, 0U);
    REQUIRE(std::abs(off_axis_mesh_ratio - 1.0) < 1.0e-9);

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
    const std::size_t reflection_channel =
        std::abs(reflection_residual[reflection_arrival * 2U]) >=
            std::abs(reflection_residual[(reflection_arrival * 2U) + 1U])
        ? 0U
        : 1U;
    const double clear_echo_pressure_ratio =
        std::abs(scattered_reflection_residual[(reflection_arrival * 2U) + reflection_channel]) /
        std::abs(reflection_residual[(reflection_arrival * 2U) + reflection_channel]);
    REQUIRE(std::abs(clear_echo_pressure_ratio - 0.5) < 0.002);

    const std::size_t diffuse_search_start = reflection_arrival + 400U;
    double clear_tail_peak = 0.0;
    double scattered_tail_peak = 0.0;
    for (std::size_t frame = diffuse_search_start; frame < scattered_reflection_residual.size() / 2U; ++frame) {
        clear_tail_peak = std::max(clear_tail_peak, std::abs(static_cast<double>(reflection_residual[frame * 2U])));
        clear_tail_peak = std::max(clear_tail_peak, std::abs(static_cast<double>(reflection_residual[(frame * 2U) + 1U])));
        scattered_tail_peak = std::max(scattered_tail_peak, std::abs(static_cast<double>(scattered_reflection_residual[frame * 2U])));
        scattered_tail_peak = std::max(scattered_tail_peak, std::abs(static_cast<double>(scattered_reflection_residual[(frame * 2U) + 1U])));
    }
    REQUIRE(clear_tail_peak < 1.0e-8);
    REQUIRE(scattered_tail_peak > 1.0e-4);
    REQUIRE(scattered_impulse == render_surface_source(scattered_reflection_options));

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
    REQUIRE(initial_device_status.recoverable == 0U);
    REQUIRE(initial_device_status.frames_processed == 0U);
    REQUIRE(initial_device_status.callback_duration_nanoseconds_total == 0U);
    REQUIRE(initial_device_status.callback_duration_nanoseconds_maximum == 0U);
    REQUIRE(initial_device_status.callback_deadline_miss_count == 0U);
    REQUIRE(initial_device_status.callback_load_parts_per_million_maximum == 0U);
    REQUIRE(initial_device_status.callback_frames_maximum == 0U);

    struct legacy_device_status {
        std::uint32_t size;
        std::uint32_t version;
        std::uint32_t running;
        std::uint32_t sample_rate;
        std::uint32_t period_frames;
        std::uint32_t receiver_index;
        std::uint64_t frames_processed;
        std::uint64_t callback_count;
        std::uint32_t recoverable;
        std::uint32_t interruption_count;
        std::uint32_t recovery_count;
        std::uint32_t reserved;
    };
    static_assert(sizeof(legacy_device_status) == offsetof(pg_device_status, callback_duration_nanoseconds_total));
    legacy_device_status legacy_status{ sizeof(legacy_device_status), PG_ABI_VERSION };
    REQUIRE(pg_engine_get_device_status(engine, reinterpret_cast<pg_device_status*>(&legacy_status)) == PG_STATUS_OK);
    REQUIRE(legacy_status.running == 0U);
    REQUIRE(legacy_status.recoverable == 0U);
    REQUIRE(pg_engine_recover_device(engine, 0U) == PG_STATUS_INVALID_ARGUMENT);

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

    pg_buffer_device_config invalid_buffer_device{};
    invalid_buffer_device.size = sizeof(invalid_buffer_device);
    invalid_buffer_device.version = PG_ABI_VERSION;
    invalid_buffer_device.sample_rate = 48000U;
    invalid_buffer_device.period_frames = 512U;
    invalid_buffer_device.frame_count = 1U;
    REQUIRE(pg_engine_start_buffer_device(engine, &invalid_buffer_device) == PG_STATUS_INVALID_ARGUMENT);
    const std::array<float, 2> non_finite_preview{ std::numeric_limits<float>::quiet_NaN(), 0.0F };
    invalid_buffer_device.interleaved_stereo = non_finite_preview.data();
    REQUIRE(pg_engine_start_buffer_device(engine, &invalid_buffer_device) == PG_STATUS_INVALID_ARGUMENT);
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
        REQUIRE(status.recoverable == 1U);
        REQUIRE(status.frames_processed > 0U);
        REQUIRE(status.callback_count > 0U);
        REQUIRE(status.callback_duration_nanoseconds_total > 0U);
        REQUIRE(status.callback_duration_nanoseconds_maximum > 0U);
        REQUIRE(status.callback_deadline_miss_count == 0U);
        REQUIRE(status.callback_load_parts_per_million_maximum > 0U);
        REQUIRE(status.callback_frames_maximum > 0U);
        REQUIRE(status.interruption_count == 0U);
        REQUIRE(status.recovery_count == 0U);
        REQUIRE(pg_engine_recover_device(audio_engine, 48000U) == PG_STATUS_OK);
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
        REQUIRE(pg_engine_get_device_status(audio_engine, &status) == PG_STATUS_OK);
        REQUIRE(status.running == 1U);
        REQUIRE(status.recoverable == 1U);
        REQUIRE(status.frames_processed > 0U);
        REQUIRE(status.callback_count > 0U);
        REQUIRE(status.callback_duration_nanoseconds_total > 0U);
        REQUIRE(status.callback_duration_nanoseconds_maximum > 0U);
        REQUIRE(status.callback_deadline_miss_count == 0U);
        REQUIRE(status.interruption_count == 0U);
        REQUIRE(status.recovery_count == 1U);
        REQUIRE(pg_engine_stop_device(audio_engine) == PG_STATUS_OK);
        REQUIRE(pg_engine_get_device_status(audio_engine, &status) == PG_STATUS_OK);
        REQUIRE(status.running == 0U);
        REQUIRE(status.recoverable == 0U);
        REQUIRE(pg_engine_recover_device(audio_engine, 0U) == PG_STATUS_INVALID_ARGUMENT);

        std::vector<float> buffer_preview(4800U * 2U);
        pg_buffer_device_config buffer_config{};
        buffer_config.size = sizeof(buffer_config);
        buffer_config.version = PG_ABI_VERSION;
        buffer_config.interleaved_stereo = buffer_preview.data();
        buffer_config.frame_count = buffer_preview.size() / 2U;
        buffer_config.sample_rate = 48000U;
        buffer_config.period_frames = 256U;
        REQUIRE(pg_engine_start_buffer_device(audio_engine, &buffer_config) == PG_STATUS_OK);
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        REQUIRE(pg_engine_get_device_status(audio_engine, &status) == PG_STATUS_OK);
        REQUIRE(status.running == 1U);
        REQUIRE(status.receiver_index == std::numeric_limits<std::uint32_t>::max());
        REQUIRE(status.frames_processed > 0U);
        REQUIRE(status.recoverable == 1U);
        REQUIRE(status.callback_duration_nanoseconds_total > 0U);
        REQUIRE(status.callback_duration_nanoseconds_maximum > 0U);
        REQUIRE(status.callback_deadline_miss_count == 0U);
        REQUIRE(pg_engine_recover_device(audio_engine, buffer_config.frame_count + 1U) == PG_STATUS_INVALID_ARGUMENT);
        REQUIRE(pg_engine_recover_device(audio_engine, buffer_config.frame_count / 2U) == PG_STATUS_OK);
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        REQUIRE(pg_engine_get_device_status(audio_engine, &status) == PG_STATUS_OK);
        REQUIRE(status.running == 1U);
        REQUIRE(status.frames_processed > 0U);
        REQUIRE(status.recovery_count == 1U);
        REQUIRE(status.callback_duration_nanoseconds_total > 0U);
        REQUIRE(status.callback_deadline_miss_count == 0U);
        REQUIRE(pg_engine_stop_device(audio_engine) == PG_STATUS_OK);
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

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
#include <cmath>
#include <cstdint>
#include <cstring>
#include <exception>
#include <filesystem>
#include <limits>
#include <numbers>
#include <numeric>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace {

constexpr std::string_view engine_version = "0.1.0-alpha.5";
constexpr double minimum_effective_sound_speed = 40.0;
constexpr std::uint32_t maximum_supported_sample_rate = 192000U;
constexpr std::size_t maximum_acoustic_materials = 256U;
constexpr std::size_t maximum_acoustic_geometries = 256U;
constexpr std::size_t maximum_imported_mesh_geometries = 64U;
constexpr std::size_t maximum_imported_mesh_triangles_total = 1000000U;
constexpr std::uint64_t maximum_mesh_asset_bytes = 512ULL * 1024ULL * 1024ULL;
constexpr std::uint32_t mesh_bvh_leaf_triangles = 8U;
constexpr std::array<double, 4> late_delay_seconds{ 0.0297, 0.0371, 0.0411, 0.0437 };

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
    bool enabled{ true };
    std::vector<mesh_triangle> triangles;
    std::vector<std::uint32_t> triangle_indices;
    std::vector<mesh_bvh_node> bvh;
    axis_aligned_bounds bounds{};
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
    pg_motion_path_kind motion_path_kind{ PG_PATH_LINEAR };
    std::vector<keyframe_state> keyframes;
    bool enabled{ true };
};

struct channel_filter_state {
    double left{};
    double right{};
};

struct room_response_state {
    std::array<std::vector<float>, 4> delay_lines;
    std::array<std::size_t, 4> delay_indices{};
    std::array<double, 3> modal_previous_one{};
    std::array<double, 3> modal_previous_two{};
    std::uint32_t sample_rate{};
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
    ma_device device{};
    bool device_initialized{};
    std::atomic_bool device_running{};
    std::atomic_uint64_t device_frame_cursor{};
    std::atomic_uint64_t device_frames_processed{};
    std::atomic_uint64_t device_callback_count{};
    std::uint32_t device_sample_rate{};
    std::uint32_t device_period_frames{};
    std::uint32_t device_receiver_index{};
    std::uint32_t device_reflection_order{ 1U };
    std::string last_error;
};

namespace {

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
    if (engine == nullptr || (!device_running(engine) && !engine->device_initialized)) {
        return PG_STATUS_OK;
    }
    return fail(engine, PG_STATUS_INVALID_ARGUMENT, std::string(operation) + " requires stopping real-time preview first.");
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

bool segment_intersects_triangle(const mesh_triangle& triangle, vec3 start, vec3 end) noexcept
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
    return amount > 1.0e-7 && amount < 1.0 - 1.0e-7;
}

bool segment_intersects_mesh(const mesh_geometry_state& mesh, vec3 start, vec3 end) noexcept
{
    if (mesh.bvh.empty() || !segment_intersects_bounds(mesh.bounds, start, end)) {
        return false;
    }

    std::array<std::uint32_t, 128> stack{};
    std::size_t stack_size = 1U;
    stack[0] = 0U;
    while (stack_size > 0U) {
        const mesh_bvh_node& node = mesh.bvh[stack[--stack_size]];
        if (!segment_intersects_bounds(node.bounds, start, end)) {
            continue;
        }
        if (node.count > 0U) {
            for (std::uint32_t offset = 0U; offset < node.count; ++offset) {
                if (segment_intersects_triangle(mesh.triangles[mesh.triangle_indices[node.first + offset]], start, end)) {
                    return true;
                }
            }
            continue;
        }
        if (stack_size + 2U > stack.size()) {
            // The builder creates a balanced tree whose normal depth is far
            // below this fixed audio-thread stack. Treat unexpected overflow
            // conservatively as occluded instead of allocating or recursing.
            return true;
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
        if (!geometry.enabled || !geometry.occludes ||
            geometry.material_index >= engine.acoustic_materials.size() || !segment_intersects_mesh(geometry, start, end)) {
            continue;
        }
        const acoustic_material_state& material = engine.acoustic_materials[geometry.material_index];
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

double effective_sound_speed(const atmosphere_state& atmosphere, vec3 source, vec3 receiver) noexcept
{
    const vec3 propagation = normalized(receiver - source);
    return std::max(minimum_effective_sound_speed, speed_of_sound(atmosphere) + dot(atmosphere.wind, propagation));
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
};

delayed_path_sample sample_delayed_path(
    const pg_engine& engine,
    const emitter_state& emitter,
    vec3 receiver_position,
    double receive_time,
    std::uint32_t sample_rate,
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

    const double transmission = reflected
        ? 1.0
        : occlusion_transmission_gain(engine, source_position, receiver_position, emitter.frequency_hz);
    const double sample = primitive_sample(emitter, emission_time, sample_rate) * emitter.gain_linear *
        absorption * reflection * transmission / attenuation_distance;
    return { sample, distance, source_position, apparent_source - receiver_position };
}

delayed_path_sample sample_geometry_reflection(
    const pg_engine& engine,
    const emitter_state& emitter,
    vec3 receiver_position,
    double receive_time,
    std::uint32_t sample_rate,
    std::size_t geometry_index,
    const reflection_surface& surface) noexcept
{
    if (geometry_index >= engine.geometries.size()) {
        return {};
    }
    const geometry_state& geometry = engine.geometries[geometry_index];
    if (!geometry.enabled || !geometry.reflects || geometry.material_index >= engine.acoustic_materials.size()) {
        return {};
    }

    double emission_time = receive_time;
    vec3 source_position = sample_motion(emitter, emission_time);
    vec3 image_source = source_position;
    for (int iteration = 0; iteration < 6; ++iteration) {
        source_position = sample_motion(emitter, emission_time);
        const double source_plane_distance = dot(source_position - surface.point, surface.normal);
        image_source = source_position - (surface.normal * (2.0 * source_plane_distance));
        const double distance = length(receiver_position - image_source);
        const double propagation_speed = effective_sound_speed(engine.atmosphere, image_source, receiver_position);
        emission_time = receive_time - (distance / propagation_speed);
    }

    const double source_side = dot(source_position - surface.point, surface.normal);
    const double receiver_side = dot(receiver_position - surface.point, surface.normal);
    if (source_side * receiver_side <= 1.0e-10) {
        return {};
    }
    const vec3 image_direction = image_source - receiver_position;
    const double denominator = dot(image_direction, surface.normal);
    if (std::abs(denominator) <= 1.0e-12) {
        return {};
    }
    const double amount = dot(surface.point - receiver_position, surface.normal) / denominator;
    if (amount <= 1.0e-7 || amount >= 1.0 - 1.0e-7) {
        return {};
    }
    const vec3 reflection_point = receiver_position + (image_direction * amount);
    if (!point_within_surface(surface, reflection_point)) {
        return {};
    }
    if (geometry.kind == PG_GEOMETRY_BOX &&
        (segment_intersects_box(geometry, source_position, reflection_point) ||
            segment_intersects_box(geometry, reflection_point, receiver_position))) {
        return {};
    }

    const double distance = length(source_position - reflection_point) + length(reflection_point - receiver_position);
    const double attenuation_distance = std::max(distance, emitter.min_distance_metres);
    const double absorption = std::exp(-air_absorption_per_metre(engine.atmosphere, emitter.frequency_hz) * distance);
    const acoustic_material_state& material = engine.acoustic_materials[geometry.material_index];
    const double material_absorption = material_band_value(material, material.absorption, emitter.frequency_hz);
    const double material_transmission = material_band_value(material, material.transmission, emitter.frequency_hz);
    const double material_scattering = material_band_value(material, material.scattering, emitter.frequency_hz);
    const double reflection_energy = std::max(0.0, 1.0 - material_absorption - material_transmission);
    const double high_frequency_amount = std::clamp(std::log2(std::max(emitter.frequency_hz, 125.0) / 125.0) / 7.0, 0.0, 1.0);
    const double specular_gain = std::sqrt(reflection_energy * (1.0 - material_scattering)) *
        (1.0 - (0.5 * material.roughness * high_frequency_amount));
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
    const double sample = primitive_sample(emitter, emission_time, sample_rate) * emitter.gain_linear *
        absorption * specular_gain * visibility / attenuation_distance;
    return { sample, distance, source_position, reflection_point - receiver_position };
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

room_response_state make_room_response_state()
{
    room_response_state state{};
    for (std::size_t index = 0; index < late_delay_seconds.size(); ++index) {
        const auto capacity = static_cast<std::size_t>(std::ceil(late_delay_seconds[index] * maximum_supported_sample_rate)) + 2U;
        state.delay_lines[index].resize(capacity);
    }
    return state;
}

void reset_room_response(room_response_state& state, std::uint32_t sample_rate) noexcept
{
    for (auto& line : state.delay_lines) {
        std::fill(line.begin(), line.end(), 0.0F);
    }
    state.delay_indices = {};
    state.modal_previous_one = {};
    state.modal_previous_two = {};
    state.sample_rate = sample_rate;
}

std::pair<double, double> process_room_response(
    pg_engine& engine,
    std::size_t receiver_index,
    double input,
    std::uint32_t sample_rate,
    std::uint32_t reflection_order) noexcept
{
    if (!engine.room.enabled || reflection_order == 0U) {
        return {};
    }
    room_response_state& state = engine.receiver_room_responses[receiver_index];
    if (state.sample_rate != sample_rate) {
        reset_room_response(state, sample_rate);
    }

    const double room_reflection = std::clamp(engine.room.reflection_mid * (1.0 - (0.25 * engine.room.scattering)), 0.0, 0.98);
    const double order_factor = 1.0 + (0.035 * static_cast<double>(std::min(reflection_order, 8U) - 1U));
    const double feedback = std::clamp((0.35 + (0.56 * room_reflection)) * order_factor, 0.1, 0.965);
    constexpr std::array<double, 4> left_mix{ 0.24, 0.19, -0.17, 0.15 };
    constexpr std::array<double, 4> right_mix{ 0.17, -0.23, 0.15, 0.19 };
    double late_left = 0.0;
    double late_right = 0.0;
    for (std::size_t index = 0; index < state.delay_lines.size(); ++index) {
        auto& line = state.delay_lines[index];
        const auto delay_length = std::clamp(
            static_cast<std::size_t>(std::llround(late_delay_seconds[index] * sample_rate)),
            std::size_t{ 2U },
            line.size());
        std::size_t& cursor = state.delay_indices[index];
        if (cursor >= delay_length) {
            cursor = 0U;
        }
        const double delayed = line[cursor];
        line[cursor] = static_cast<float>(std::clamp(input + (delayed * feedback), -8.0, 8.0));
        cursor = (cursor + 1U) % delay_length;
        late_left += delayed * left_mix[index];
        late_right += delayed * right_mix[index];
    }

    const std::array<double, 3> dimensions{
        std::max(engine.room.dimensions.x, 0.5),
        std::max(engine.room.dimensions.y, 0.5),
        std::max(engine.room.dimensions.z, 0.5)
    };
    constexpr std::array<double, 3> mode_numbers{ 5.0, 3.0, 7.0 };
    const double local_speed = speed_of_sound(engine.atmosphere);
    const double radius = std::clamp(0.992 + (0.006 * room_reflection), 0.98, 0.9992);
    double modal = 0.0;
    for (std::size_t mode = 0; mode < dimensions.size(); ++mode) {
        const double frequency = std::min(local_speed * mode_numbers[mode] / (2.0 * dimensions[mode]), sample_rate * 0.45);
        const double coefficient = 2.0 * radius * std::cos(2.0 * std::numbers::pi * frequency / sample_rate);
        const double output = (input * 0.0035) + (coefficient * state.modal_previous_one[mode]) -
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
    std::uint32_t reflection_order) noexcept
{
    const receiver_state& receiver = engine.receivers[receiver_index];
    const vec3 receiver_centre = sample_motion(receiver, receive_time);
    const vec3 right = normalized(rotate_yaw({ 1.0, 0.0, 0.0 }, receiver.yaw_degrees));
    const vec3 forward = normalized(rotate_yaw({ 0.0, 0.0, 1.0 }, receiver.yaw_degrees));
    const vec3 left_position = receiver_centre - (right * (receiver.spacing_metres * 0.5));
    const vec3 right_position = receiver_centre + (right * (receiver.spacing_metres * 0.5));
    const vec3 left_forward = rotate_yaw(forward, -receiver.microphone_angle_degrees * 0.5);
    const vec3 right_forward = rotate_yaw(forward, receiver.microphone_angle_degrees * 0.5);
    const auto receiver_path_sample = [&](const delayed_path_sample& path, vec3 capsule_forward) noexcept {
        return receiver.kind == PG_RECEIVER_STEREO_MICROPHONE
            ? path.value * microphone_pattern_gain(receiver.polar_pattern, capsule_forward, path.arrival_direction)
            : path.value;
    };

    double left_total = 0.0;
    double right_total = 0.0;
    double source_lateral = 0.0;
    for (const emitter_state& emitter : engine.emitters) {
        if (!emitter.enabled) {
            continue;
        }

        const auto left_direct = sample_delayed_path(engine, emitter, left_position, receive_time, sample_rate, false, 0, false);
        const auto right_direct = sample_delayed_path(engine, emitter, right_position, receive_time, sample_rate, false, 0, false);
        double left_sample = receiver_path_sample(left_direct, left_forward);
        double right_sample = receiver_path_sample(right_direct, right_forward);
        source_lateral += dot(normalized(left_direct.source_position - receiver_centre), right);

        if (engine.room.enabled && reflection_order > 0U) {
            for (int axis = 0; axis < 3; ++axis) {
                for (int side = 0; side < 2; ++side) {
                    const auto left_reflection = sample_delayed_path(engine, emitter, left_position, receive_time, sample_rate, true, axis, side != 0);
                    const auto right_reflection = sample_delayed_path(engine, emitter, right_position, receive_time, sample_rate, true, axis, side != 0);
                    left_sample += receiver_path_sample(left_reflection, left_forward);
                    right_sample += receiver_path_sample(right_reflection, right_forward);
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
                    const auto left_reflection = sample_geometry_reflection(
                        engine,
                        emitter,
                        left_position,
                        receive_time,
                        sample_rate,
                        geometry_index,
                        surface);
                    const auto right_reflection = sample_geometry_reflection(
                        engine,
                        emitter,
                        right_position,
                        receive_time,
                        sample_rate,
                        geometry_index,
                        surface);
                    left_sample += receiver_path_sample(left_reflection, left_forward);
                    right_sample += receiver_path_sample(right_reflection, right_forward);
                }
            }
        }

        left_total += left_sample;
        right_total += right_sample;
    }

    const auto [room_left, room_right] = process_room_response(
        engine,
        receiver_index,
        0.5 * (left_total + right_total),
        sample_rate,
        reflection_order);
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

    const std::uint64_t start_frame = engine->device_frame_cursor.fetch_add(frame_count, std::memory_order_relaxed);
    engine->device_frames_processed.fetch_add(frame_count, std::memory_order_relaxed);
    const std::uint32_t receiver_index = engine->device_receiver_index;
    const std::uint32_t sample_rate = engine->device_sample_rate;
    const std::uint32_t reflection_order = engine->device_reflection_order;
    for (ma_uint32 frame = 0; frame < frame_count; ++frame) {
        const double receive_time = static_cast<double>(start_frame + frame) / static_cast<double>(sample_rate);
        const auto [left, right] = render_frame(*engine, receiver_index, receive_time, sample_rate, reflection_order);
        samples[static_cast<std::size_t>(frame) * 2U] = left;
        samples[(static_cast<std::size_t>(frame) * 2U) + 1U] = right;
    }
    engine->device_callback_count.fetch_add(1U, std::memory_order_relaxed);
}

void device_notification_callback(const ma_device_notification* notification) noexcept
{
    if (notification == nullptr || notification->pDevice == nullptr) {
        return;
    }
    auto* engine = static_cast<pg_engine*>(notification->pDevice->pUserData);
    if (engine != nullptr && notification->type == ma_device_notification_type_stopped) {
        engine->device_running.store(false, std::memory_order_release);
    }
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
    if (geometry->reflects != 0U) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT,
            "Imported-mesh reflections require the geometric reflection backend; disable Reflects until that backend is active.");
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
        state.enabled = geometry->enabled != 0U;
        state.triangles.reserve(static_cast<std::size_t>(candidate_triangle_count));

        for (unsigned int mesh_index = 0U; mesh_index < scene->mNumMeshes; ++mesh_index) {
            const aiMesh* source_mesh = scene->mMeshes[mesh_index];
            if (source_mesh == nullptr || source_mesh->mVertices == nullptr) {
                continue;
            }
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
                if (length(cross(b - a, c - a)) <= 1.0e-12) {
                    continue;
                }
                const axis_aligned_bounds bounds = triangle_bounds(a, b, c);
                state.triangles.push_back({ a, b, c, bounds, (a + b + c) * (1.0 / 3.0) });
                expand(state.bounds, bounds);
            }
        }
        if (state.triangles.empty()) {
            return fail(engine, PG_STATUS_INVALID_SCENE, "Imported mesh contains no non-degenerate triangles.");
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
        }
        return PG_STATUS_OK;
    } catch (const std::exception& exception) {
        return fail(engine, PG_STATUS_INTERNAL_ERROR, std::string("Imported mesh processing failed: ") + exception.what());
    } catch (...) {
        return fail(engine, PG_STATUS_INTERNAL_ERROR, "Imported mesh processing failed with an unknown error.");
    }
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
        !std::isfinite(receiver->self_noise_db_spl) ||
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
    if (out_index != nullptr) {
        *out_index = static_cast<std::uint32_t>(engine->receivers.size() - 1U);
    }
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

    if (start_frame == 0U || engine->receiver_next_frames[receiver_index] != start_frame) {
        engine->receiver_filters[receiver_index] = {};
        reset_room_response(engine->receiver_room_responses[receiver_index], sample_rate);
    }
    for (std::uint32_t frame = 0; frame < frame_count; ++frame) {
        const double receive_time = static_cast<double>(start_frame + frame) / static_cast<double>(sample_rate);
        const auto [left, right] = render_frame(*engine, receiver_index, receive_time, sample_rate, 1U);
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
            reset_room_response(engine->receiver_room_responses[receiver_index], request->sample_rate);
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
                const auto [left, right] = render_frame(*engine, receiver_index, receive_time, request->sample_rate, request->reflection_order);
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
    if (engine->device_initialized || device_running(engine)) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Real-time preview is already initialized.");
    }
    if (validate_scene(engine) != PG_STATUS_OK) {
        return PG_STATUS_INVALID_SCENE;
    }
    if (config->receiver_index >= engine->receivers.size() || !engine->receivers[config->receiver_index].enabled) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Preview receiver index is out of range or disabled.");
    }
    if (config->sample_rate < 8000U || config->sample_rate > maximum_supported_sample_rate ||
        config->period_frames < 32U || config->period_frames > 8192U || config->reflection_order > 8U) {
        return fail(engine, PG_STATUS_INVALID_ARGUMENT, "Preview sample rate, period, or reflection order is outside supported bounds.");
    }

    engine->receiver_filters[config->receiver_index] = {};
    reset_room_response(engine->receiver_room_responses[config->receiver_index], config->sample_rate);
    engine->device_sample_rate = config->sample_rate;
    engine->device_period_frames = config->period_frames;
    engine->device_receiver_index = config->receiver_index;
    engine->device_reflection_order = config->reflection_order;
    engine->device_frame_cursor.store(config->start_frame, std::memory_order_relaxed);
    engine->device_frames_processed.store(0U, std::memory_order_relaxed);
    engine->device_callback_count.store(0U, std::memory_order_relaxed);

    ma_device_config device_config = ma_device_config_init(ma_device_type_playback);
    device_config.sampleRate = config->sample_rate;
    device_config.periodSizeInFrames = config->period_frames;
    device_config.performanceProfile = ma_performance_profile_low_latency;
    device_config.playback.format = ma_format_f32;
    device_config.playback.channels = 2U;
    device_config.playback.shareMode = config->exclusive_mode != 0U ? ma_share_mode_exclusive : ma_share_mode_shared;
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
        return fail(engine, PG_STATUS_INTERNAL_ERROR, std::string("Unable to initialize the audio device: ") + ma_result_description(init_result));
    }
    engine->device_initialized = true;
    engine->device_sample_rate = engine->device.sampleRate;
    if (engine->device.playback.internalPeriodSizeInFrames > 0U) {
        engine->device_period_frames = engine->device.playback.internalPeriodSizeInFrames;
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

pg_status PG_CALL pg_engine_stop_device(pg_engine* engine)
{
    if (engine == nullptr) {
        return PG_STATUS_INVALID_ARGUMENT;
    }
    engine->device_running.store(false, std::memory_order_release);
    if (!engine->device_initialized) {
        return PG_STATUS_OK;
    }

    const ma_result stop_result = ma_device_stop(&engine->device);
    ma_device_uninit(&engine->device);
    engine->device_initialized = false;
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
    if (!valid_struct(status)) {
        return PG_STATUS_ABI_MISMATCH;
    }
    status->running = device_running(engine) ? 1U : 0U;
    status->sample_rate = engine->device_sample_rate;
    status->period_frames = engine->device_period_frames;
    status->receiver_index = engine->device_receiver_index;
    status->frames_processed = engine->device_frames_processed.load(std::memory_order_relaxed);
    status->callback_count = engine->device_callback_count.load(std::memory_order_relaxed);
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

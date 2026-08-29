#ifndef PULSEGEN_PULSEGEN_H
#define PULSEGEN_PULSEGEN_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(PULSEGEN_ENGINE_BUILD)
#    define PG_API __declspec(dllexport)
#  else
#    define PG_API __declspec(dllimport)
#  endif
#  define PG_CALL __cdecl
#else
#  define PG_API __attribute__((visibility("default")))
#  define PG_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define PG_ABI_VERSION 1u
#define PG_MAX_NAME_BYTES 64u

typedef struct pg_engine pg_engine;

typedef enum pg_status {
    PG_STATUS_OK = 0,
    PG_STATUS_INVALID_ARGUMENT = 1,
    PG_STATUS_ABI_MISMATCH = 2,
    PG_STATUS_CAPACITY_EXCEEDED = 3,
    PG_STATUS_INVALID_SCENE = 4,
    PG_STATUS_IO_ERROR = 5,
    PG_STATUS_CANCELLED = 6,
    PG_STATUS_INTERNAL_ERROR = 7
} pg_status;

typedef enum pg_receiver_kind {
    PG_RECEIVER_BINAURAL_HEAD = 0,
    PG_RECEIVER_STEREO_MICROPHONE = 1
} pg_receiver_kind;

typedef enum pg_polar_pattern {
    PG_POLAR_OMNI = 0,
    PG_POLAR_CARDIOID = 1,
    PG_POLAR_SUPERCARDIOID = 2,
    PG_POLAR_FIGURE_EIGHT = 3
} pg_polar_pattern;

typedef enum pg_sample_format {
    PG_SAMPLE_PCM16 = 0,
    PG_SAMPLE_PCM24 = 1,
    PG_SAMPLE_FLOAT32 = 2
} pg_sample_format;

typedef enum pg_render_quality {
    PG_RENDER_DRAFT = 0,
    PG_RENDER_INTERACTIVE = 1,
    PG_RENDER_FINAL = 2
} pg_render_quality;

typedef enum pg_waveform_kind {
    PG_WAVE_SINE = 0,
    PG_WAVE_TRIANGLE = 1,
    PG_WAVE_SAW = 2,
    PG_WAVE_SQUARE = 3,
    PG_WAVE_PULSE = 4,
    PG_WAVE_WHITE_NOISE = 5,
    PG_WAVE_PINK_NOISE = 6,
    PG_WAVE_BROWN_NOISE = 7,
    PG_WAVE_IMPULSE = 8,
    PG_WAVE_ADDITIVE = 9,
    PG_WAVE_TURBULENCE = 10
} pg_waveform_kind;

typedef enum pg_motion_path_kind {
    PG_PATH_LINEAR = 0,
    PG_PATH_CATMULL_ROM = 1,
    PG_PATH_CUBIC_BEZIER_APPROXIMATION = 2,
    PG_PATH_ARC_APPROXIMATION = 3,
    PG_PATH_RECORDED_GESTURE = 4
} pg_motion_path_kind;

typedef struct pg_vec3 {
    double x;
    double y;
    double z;
} pg_vec3;

typedef struct pg_engine_config {
    uint32_t size;
    uint32_t version;
    uint32_t max_emitters;
    uint32_t max_receivers;
    uint32_t max_keyframes_per_path;
    uint32_t reserved;
} pg_engine_config;

typedef struct pg_atmosphere_desc {
    uint32_t size;
    uint32_t version;
    double temperature_celsius;
    double relative_humidity_percent;
    double pressure_kilopascals;
    pg_vec3 uniform_wind_metres_per_second;
    double turbulence;
    uint64_t noise_seed;
} pg_atmosphere_desc;

typedef struct pg_room_desc {
    uint32_t size;
    uint32_t version;
    pg_vec3 centre_metres;
    pg_vec3 dimensions_metres;
    double wall_reflection_low;
    double wall_reflection_mid;
    double wall_reflection_high;
    double scattering;
    uint32_t enabled;
    uint32_t reserved;
} pg_room_desc;

typedef struct pg_motion_keyframe {
    double time_seconds;
    pg_vec3 position_metres;
} pg_motion_keyframe;

typedef struct pg_sine_emitter_desc {
    uint32_t size;
    uint32_t version;
    const char* name_utf8;
    double frequency_hz;
    double amplitude;
    double gain_linear;
    double min_distance_metres;
    const pg_motion_keyframe* keyframes;
    uint32_t keyframe_count;
    uint32_t enabled;
} pg_sine_emitter_desc;

typedef struct pg_additive_partial {
    double frequency_ratio;
    double amplitude;
    double phase_radians;
} pg_additive_partial;

typedef struct pg_signal_emitter_desc {
    uint32_t size;
    uint32_t version;
    const char* name_utf8;
    pg_waveform_kind waveform;
    double frequency_hz;
    double amplitude;
    double phase_radians;
    double pulse_width;
    double gain_linear;
    double min_distance_metres;
    double attack_seconds;
    double release_seconds;
    double nonlinear_drive;
    const pg_additive_partial* additive_partials;
    uint32_t additive_partial_count;
    pg_motion_path_kind motion_path_kind;
    const pg_motion_keyframe* keyframes;
    uint32_t keyframe_count;
    uint32_t noise_seed;
    uint32_t enabled;
    uint32_t reserved;
} pg_signal_emitter_desc;

typedef struct pg_receiver_rig_desc {
    uint32_t size;
    uint32_t version;
    const char* name_utf8;
    pg_receiver_kind kind;
    pg_vec3 centre_metres;
    double yaw_degrees;
    double spacing_metres;
    double head_radius_metres;
    double microphone_angle_degrees;
    pg_polar_pattern polar_pattern;
    double sensitivity_db;
    double self_noise_db_spl;
    uint32_t enabled;
    uint32_t reserved;
} pg_receiver_rig_desc;

typedef struct pg_render_request {
    uint32_t size;
    uint32_t version;
    const char* output_directory_utf8;
    const char* file_prefix_utf8;
    double duration_seconds;
    uint32_t sample_rate;
    pg_sample_format sample_format;
    pg_render_quality quality;
    uint32_t reflection_order;
    uint32_t seed;
    const volatile uint32_t* cancellation_flag;
} pg_render_request;

typedef struct pg_render_receipt {
    uint32_t size;
    uint32_t version;
    uint64_t frames_rendered;
    uint32_t files_written;
    uint32_t sample_rate;
    double peak_left;
    double peak_right;
    double rms_left;
    double rms_right;
    double speed_of_sound_metres_per_second;
    uint64_t deterministic_seed;
} pg_render_receipt;

typedef struct pg_device_config {
    uint32_t size;
    uint32_t version;
    uint32_t receiver_index;
    uint32_t sample_rate;
    uint32_t period_frames;
    uint32_t reflection_order;
    uint32_t exclusive_mode;
    uint32_t reserved;
    uint64_t start_frame;
} pg_device_config;

typedef struct pg_device_status {
    uint32_t size;
    uint32_t version;
    uint32_t running;
    uint32_t sample_rate;
    uint32_t period_frames;
    uint32_t receiver_index;
    uint64_t frames_processed;
    uint64_t callback_count;
} pg_device_status;

PG_API uint32_t PG_CALL pg_get_abi_version(void);
PG_API const char* PG_CALL pg_get_engine_version(void);
PG_API const char* PG_CALL pg_get_last_error(const pg_engine* engine);

PG_API pg_status PG_CALL pg_engine_create(const pg_engine_config* config, pg_engine** out_engine);
PG_API void PG_CALL pg_engine_destroy(pg_engine* engine);
PG_API pg_status PG_CALL pg_engine_clear_scene(pg_engine* engine);
PG_API pg_status PG_CALL pg_engine_set_atmosphere(pg_engine* engine, const pg_atmosphere_desc* atmosphere);
PG_API pg_status PG_CALL pg_engine_set_room(pg_engine* engine, const pg_room_desc* room);
PG_API pg_status PG_CALL pg_engine_add_sine_emitter(pg_engine* engine, const pg_sine_emitter_desc* emitter, uint32_t* out_index);
PG_API pg_status PG_CALL pg_engine_add_signal_emitter(pg_engine* engine, const pg_signal_emitter_desc* emitter, uint32_t* out_index);
PG_API pg_status PG_CALL pg_engine_add_receiver(pg_engine* engine, const pg_receiver_rig_desc* receiver, uint32_t* out_index);

PG_API pg_status PG_CALL pg_engine_render_receiver_block(
    pg_engine* engine,
    uint32_t receiver_index,
    uint64_t start_frame,
    uint32_t frame_count,
    uint32_t sample_rate,
    float* interleaved_stereo,
    size_t interleaved_sample_capacity);

PG_API pg_status PG_CALL pg_engine_render_offline(
    pg_engine* engine,
    const pg_render_request* request,
    pg_render_receipt* receipt);

PG_API pg_status PG_CALL pg_engine_start_device(pg_engine* engine, const pg_device_config* config);
PG_API pg_status PG_CALL pg_engine_stop_device(pg_engine* engine);
PG_API pg_status PG_CALL pg_engine_get_device_status(const pg_engine* engine, pg_device_status* status);

PG_API double PG_CALL pg_calculate_speed_of_sound(double temperature_celsius);
PG_API double PG_CALL pg_calculate_observed_frequency(
    double source_frequency_hz,
    double speed_of_sound_metres_per_second,
    double source_radial_velocity_metres_per_second,
    double receiver_radial_velocity_metres_per_second);

#ifdef __cplusplus
}
#endif

#endif

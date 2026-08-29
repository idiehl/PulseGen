#ifndef PULSEGEN_WAV_WRITER_H
#define PULSEGEN_WAV_WRITER_H

#include "pulsegen/pulsegen.h"

#include <cstdint>
#include <filesystem>
#include <span>
#include <string>

namespace pulsegen {

bool write_stereo_wav(
    const std::filesystem::path& path,
    std::span<const float> interleaved,
    std::uint32_t sample_rate,
    pg_sample_format format,
    std::uint64_t dither_seed,
    std::string& error);

} // namespace pulsegen

#endif


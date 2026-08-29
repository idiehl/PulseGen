#include "wav_writer.h"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstddef>
#include <fstream>
#include <limits>
#include <type_traits>

namespace pulsegen {
namespace {

template <typename T>
void write_little_endian(std::ostream& stream, T value)
{
    static_assert(std::is_integral_v<T>);
    std::array<std::byte, sizeof(T)> bytes{};
    for (std::size_t index = 0; index < bytes.size(); ++index) {
        bytes[index] = static_cast<std::byte>((static_cast<std::make_unsigned_t<T>>(value) >> (index * 8U)) & 0xffU);
    }
    stream.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
}

void write_fourcc(std::ostream& stream, const char (&value)[5])
{
    stream.write(value, 4);
}

std::uint64_t xorshift64(std::uint64_t& state)
{
    state ^= state << 13U;
    state ^= state >> 7U;
    state ^= state << 17U;
    return state;
}

double uniform_signed(std::uint64_t& state)
{
    constexpr double denominator = static_cast<double>(std::numeric_limits<std::uint32_t>::max());
    return (static_cast<double>(static_cast<std::uint32_t>(xorshift64(state))) / denominator) - 0.5;
}

} // namespace

bool write_stereo_wav(
    const std::filesystem::path& path,
    std::span<const float> interleaved,
    std::uint32_t sample_rate,
    pg_sample_format format,
    std::uint64_t dither_seed,
    std::string& error)
{
    if ((interleaved.size() % 2U) != 0U || sample_rate == 0U) {
        error = "WAV data must contain complete stereo frames and a non-zero sample rate.";
        return false;
    }

    const std::uint16_t bits_per_sample = format == PG_SAMPLE_PCM16 ? 16U : (format == PG_SAMPLE_PCM24 ? 24U : 32U);
    const std::uint16_t format_code = format == PG_SAMPLE_FLOAT32 ? 3U : 1U;
    const std::uint16_t channel_count = 2U;
    const std::uint16_t block_align = static_cast<std::uint16_t>((channel_count * bits_per_sample) / 8U);
    const std::uint32_t byte_rate = sample_rate * block_align;
    const std::uint64_t data_size_64 = (interleaved.size() / 2U) * block_align;
    if (data_size_64 > std::numeric_limits<std::uint32_t>::max() - 36U) {
        error = "Render exceeds RIFF WAV size; RF64 adapter is required.";
        return false;
    }

    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    if (!stream) {
        error = "Unable to create output WAV: " + path.string();
        return false;
    }

    const auto data_size = static_cast<std::uint32_t>(data_size_64);
    write_fourcc(stream, "RIFF");
    write_little_endian(stream, static_cast<std::uint32_t>(36U + data_size));
    write_fourcc(stream, "WAVE");
    write_fourcc(stream, "fmt ");
    write_little_endian(stream, static_cast<std::uint32_t>(16U));
    write_little_endian(stream, format_code);
    write_little_endian(stream, channel_count);
    write_little_endian(stream, sample_rate);
    write_little_endian(stream, byte_rate);
    write_little_endian(stream, block_align);
    write_little_endian(stream, bits_per_sample);
    write_fourcc(stream, "data");
    write_little_endian(stream, data_size);

    auto random_state = dither_seed == 0U ? 1U : dither_seed;
    for (const float input : interleaved) {
        const double sample = std::clamp(static_cast<double>(input), -1.0, 1.0);
        if (format == PG_SAMPLE_FLOAT32) {
            const std::uint32_t raw = std::bit_cast<std::uint32_t>(static_cast<float>(sample));
            write_little_endian(stream, raw);
        } else if (format == PG_SAMPLE_PCM16) {
            const double dither = (uniform_signed(random_state) + uniform_signed(random_state));
            const auto quantized = static_cast<std::int16_t>(std::clamp(std::llround((sample * 32767.0) + dither), -32768LL, 32767LL));
            write_little_endian(stream, quantized);
        } else {
            const double dither = (uniform_signed(random_state) + uniform_signed(random_state));
            const auto quantized = static_cast<std::int32_t>(std::clamp(std::llround((sample * 8388607.0) + dither), -8388608LL, 8388607LL));
            const std::uint32_t raw = static_cast<std::uint32_t>(quantized);
            const std::array<char, 3> bytes{
                static_cast<char>(raw & 0xffU),
                static_cast<char>((raw >> 8U) & 0xffU),
                static_cast<char>((raw >> 16U) & 0xffU)
            };
            stream.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
        }
    }

    if (!stream) {
        error = "Failed while writing output WAV: " + path.string();
        return false;
    }

    return true;
}

} // namespace pulsegen


#include "wav_writer.h"

#include <array>
#include <bit>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <type_traits>
#include <vector>

namespace {

void require(bool condition, std::string_view expression, int line)
{
    if (!condition) {
        throw std::runtime_error("WAV requirement failed at line " + std::to_string(line) + ": " + std::string(expression));
    }
}

#define REQUIRE(expression) require(static_cast<bool>(expression), #expression, __LINE__)

std::vector<std::uint8_t> read_bytes(const std::filesystem::path& path)
{
    std::ifstream stream(path, std::ios::binary | std::ios::ate);
    REQUIRE(static_cast<bool>(stream));
    const auto length = stream.tellg();
    REQUIRE(length >= 0);
    std::vector<std::uint8_t> bytes(static_cast<std::size_t>(length));
    stream.seekg(0);
    stream.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    REQUIRE(static_cast<bool>(stream));
    return bytes;
}

template <typename T>
T little_endian_at(std::span<const std::uint8_t> bytes, std::size_t offset)
{
    static_assert(std::is_integral_v<T>);
    REQUIRE(offset + sizeof(T) <= bytes.size());
    std::make_unsigned_t<T> value{};
    for (std::size_t index = 0; index < sizeof(T); ++index) {
        value |= static_cast<std::make_unsigned_t<T>>(bytes[offset + index]) << (index * 8U);
    }
    return static_cast<T>(value);
}

float float_at(std::span<const std::uint8_t> bytes, std::size_t offset)
{
    return std::bit_cast<float>(little_endian_at<std::uint32_t>(bytes, offset));
}

} // namespace

int main()
{
    try {
        const auto unique = std::to_string(std::chrono::steady_clock::now().time_since_epoch().count());
        const auto root = std::filesystem::temp_directory_path() / ("pulsegen-wav-tests-" + unique);
        std::filesystem::create_directories(root);
        struct cleanup_guard {
            std::filesystem::path path;
            ~cleanup_guard() { std::error_code error; std::filesystem::remove_all(path, error); }
        } cleanup{ root };

        const std::array<float, 8> samples{ 1.5F, -1.5F, 0.25F, -0.25F, 0.0F, 0.0F, 0.75F, -0.75F };
        std::string error;
        const auto float_path = root / "float.wav";
        REQUIRE(pulsegen::write_stereo_wav(float_path, samples, 96000U, PG_SAMPLE_FLOAT32, 42U, error));
        const auto float_bytes = read_bytes(float_path);
        REQUIRE(std::string_view(reinterpret_cast<const char*>(float_bytes.data()), 4U) == "RIFF");
        REQUIRE(std::string_view(reinterpret_cast<const char*>(float_bytes.data() + 8U), 4U) == "WAVE");
        REQUIRE(little_endian_at<std::uint16_t>(float_bytes, 20U) == 3U);
        REQUIRE(little_endian_at<std::uint16_t>(float_bytes, 22U) == 2U);
        REQUIRE(little_endian_at<std::uint32_t>(float_bytes, 24U) == 96000U);
        REQUIRE(little_endian_at<std::uint16_t>(float_bytes, 34U) == 32U);
        REQUIRE(float_at(float_bytes, 44U) == 1.5F);
        REQUIRE(float_at(float_bytes, 48U) == -1.5F);

        const auto pcm16_a = root / "pcm16-a.wav";
        const auto pcm16_b = root / "pcm16-b.wav";
        REQUIRE(pulsegen::write_stereo_wav(pcm16_a, samples, 48000U, PG_SAMPLE_PCM16, 99U, error));
        REQUIRE(pulsegen::write_stereo_wav(pcm16_b, samples, 48000U, PG_SAMPLE_PCM16, 99U, error));
        const auto pcm16_bytes = read_bytes(pcm16_a);
        REQUIRE(pcm16_bytes == read_bytes(pcm16_b));
        REQUIRE(little_endian_at<std::uint16_t>(pcm16_bytes, 20U) == 1U);
        REQUIRE(little_endian_at<std::uint16_t>(pcm16_bytes, 34U) == 16U);
        REQUIRE(little_endian_at<std::int16_t>(pcm16_bytes, 44U) >= 32766);
        REQUIRE(little_endian_at<std::int16_t>(pcm16_bytes, 46U) <= -32767);

        const auto pcm24 = root / "pcm24.wav";
        REQUIRE(pulsegen::write_stereo_wav(pcm24, samples, 44100U, PG_SAMPLE_PCM24, 7U, error));
        const auto pcm24_bytes = read_bytes(pcm24);
        REQUIRE(little_endian_at<std::uint16_t>(pcm24_bytes, 34U) == 24U);
        REQUIRE(little_endian_at<std::uint32_t>(pcm24_bytes, 40U) == samples.size() * 3U);

        REQUIRE(!pulsegen::write_stereo_wav(root / "invalid.wav", std::span<const float>(samples).first(7U), 48000U, PG_SAMPLE_FLOAT32, 1U, error));
        REQUIRE(!error.empty());
        std::cout << "PULSEGEN_WAV_WRITER_OK\n";
        return 0;
    }
    catch (const std::exception& exception) {
        std::cerr << exception.what() << '\n';
        return 1;
    }
}

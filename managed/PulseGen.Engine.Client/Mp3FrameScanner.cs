using System.Buffers.Binary;

namespace PulseGen.Engine.Client;

internal readonly record struct Mp3StreamInfo(
    int AudioOffset, int AudioByteCount, int SampleRate, int Channels,
    int PhysicalFrameCount, int SamplesPerFrame, bool HasXingHeader, long? ExpectedPcmFrames);

/// <summary>Verifies packet coverage before a decoder can recover past damage or trust a duration tag.</summary>
internal static class Mp3FrameScanner
{
    private const int MaximumTagBytes = 16 * 1024 * 1024;
    private static ReadOnlySpan<int> Mpeg1Bitrates => [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static ReadOnlySpan<int> Mpeg2Bitrates => [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    public static Mp3StreamInfo Validate(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length is < 4 || bytes.Length > SampleAudioReader.MaximumEncodedBytes)
            throw Invalid("The MP3 is empty, truncated, or exceeds the encoded-file limit.");
        var start = SkipId3v2(bytes, token);
        var end = StripTrailingTags(bytes, start, token);
        if (end - start < 4) throw Invalid("The MP3 contains no complete audio packets.");

        var first = ReadHeader(bytes[start..end]);
        var cursor = start;
        var frameCount = 0;
        while (cursor < end)
        {
            token.ThrowIfCancellationRequested();
            var header = ReadHeader(bytes[cursor..end]);
            if (header.Version != first.Version || header.SampleRate != first.SampleRate || header.Channels != first.Channels)
                throw Invalid("MP3 version, sample rate, or channel count changes within the recording.");
            if (header.FrameBytes > end - cursor)
                throw Invalid("The final MP3 audio packet is truncated.");
            cursor += header.FrameBytes;
            frameCount++;
        }

        var firstFrame = bytes.Slice(start, first.FrameBytes);
        var xingOffset = 4 + first.CrcBytes + first.SideInfoBytes;
        var xing = HasMagic(firstFrame, xingOffset, "Xing"u8) || HasMagic(firstFrame, xingOffset, "Info"u8);
        long? expectedFrames = (long)frameCount * first.SamplesPerFrame;
        if (xing)
        {
            if (frameCount < 2) throw Invalid("An MP3 information packet has no following audio.");
            var tag = firstFrame[xingOffset..];
            Require(tag, 8, "Xing/Info header");
            var flags = BinaryPrimitives.ReadUInt32BigEndian(tag[4..]);
            if ((flags & ~15U) != 0) throw Invalid("The MP3 Xing/Info flags are invalid.");
            var offset = 8;
            if ((flags & 1) != 0)
            {
                var declared = ReadUInt32(tag, ref offset);
                if (declared != frameCount - 1)
                    throw Invalid("The Xing/Info frame count does not match the complete MP3 audio packets.");
            }
            if ((flags & 2) != 0)
                ValidateDeclaredBytes(ReadUInt32(tag, ref offset), end - start, bytes.Length - start, "Xing/Info");
            if ((flags & 4) != 0)
            {
                Require(tag, offset + 100, "Xing seek table");
                for (var index = 1; index < 100; index++)
                    if (tag[offset + index] < tag[offset + index - 1]) throw Invalid("The Xing seek table is not ordered.");
                offset += 100;
            }
            if ((flags & 8) != 0) _ = ReadUInt32(tag, ref offset);

            expectedFrames = (long)(frameCount - 1) * first.SamplesPerFrame;
            // Match the pinned dr_mp3 LAME-compatible parser: it reads 36 bytes whenever
            // the encoder extension begins with a nonzero byte, including Lavf/Lavc tags.
            Require(tag, offset + 1, "Xing encoder extension");
            if (tag[offset] != 0)
            {
                Require(tag, offset + 36, "MP3 encoder delay/padding extension");
                var encoderDelay = (tag[offset + 21] << 4) | (tag[offset + 22] >> 4);
                var encoderPadding = ((tag[offset + 22] & 15) << 8) | tag[offset + 23];
                var decoderDelay = encoderDelay + 529;
                // dr_mp3 can trim the tail only when Xing declares the total frame count.
                var decoderPadding = (flags & 1) != 0 ? Math.Max(0, encoderPadding - 529) : 0;
                if (decoderDelay + decoderPadding >= expectedFrames)
                    throw Invalid("The MP3 encoder delay and padding exhaust its physical audio duration.");
                expectedFrames -= decoderDelay + decoderPadding;
            }
        }
        else if (HasMagic(firstFrame, 36, "VBRI"u8))
        {
            ValidateVbri(firstFrame[36..], frameCount, end - start, bytes.Length - start);
            // The pinned decoder does not interpret VBRI. Packet counts remain physical;
            // do not manufacture a gapless duration from metadata it does not support.
            expectedFrames = (long)frameCount * first.SamplesPerFrame;
        }

        // Physical packets include encoder padding. The streaming decoder enforces the
        // final decoded-sample budget after trimming, so a valid boundary-sized file fits.
        return new(start, end - start, first.SampleRate, first.Channels, frameCount,
            first.SamplesPerFrame, xing, expectedFrames);
    }

    private static Header ReadHeader(ReadOnlySpan<byte> packet)
    {
        Require(packet, 4, "MP3 frame header");
        var bits = BinaryPrimitives.ReadUInt32BigEndian(packet);
        var version = (int)((bits >> 19) & 3);
        var layer = (bits >> 17) & 3;
        var bitrateIndex = (int)((bits >> 12) & 15);
        var rateIndex = (int)((bits >> 10) & 3);
        if ((bits & 0xffe00000) != 0xffe00000 || version == 1 || layer != 1 ||
            bitrateIndex == 15 || rateIndex == 3 || (bits & 3) == 2)
            throw Invalid("The MP3 has an invalid Layer III header or unrecognized bytes between packets.");
        if (bitrateIndex == 0) throw Invalid("Free-bitrate MP3 packets are not supported by strict sample import.");
        var rate = rateIndex switch { 0 => 44100, 1 => 48000, _ => 32000 };
        rate >>= version == 3 ? 0 : version == 2 ? 1 : 2;
        var channels = ((bits >> 6) & 3) == 3 ? 1 : 2;
        var bitrate = (version == 3 ? Mpeg1Bitrates : Mpeg2Bitrates)[bitrateIndex];
        var frameBytes = (version == 3 ? 144000 : 72000) * bitrate / rate + (int)((bits >> 9) & 1);
        var crcBytes = (bits & 0x10000) == 0 ? 2 : 0;
        var sideInfo = version == 3 ? channels == 1 ? 17 : 32 : channels == 1 ? 9 : 17;
        if (frameBytes < 4 + crcBytes + sideInfo)
            throw Invalid("The MP3 packet cannot contain its declared side information.");
        return new(version, rate, channels, frameBytes, version == 3 ? 1152 : 576, crcBytes, sideInfo);
    }

    private static int SkipId3v2(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        var cursor = 0;
        while (HasMagic(bytes, cursor, "ID3"u8))
        {
            token.ThrowIfCancellationRequested();
            var tag = bytes[cursor..];
            Require(tag, 10, "ID3v2 header");
            var version = tag[3];
            if (version is < 2 or > 4 || tag[4] == 255 ||
                (tag[5] & (version == 2 ? 0x3f : version == 3 ? 0x1f : 0x0f)) != 0)
                throw Invalid("The leading ID3v2 tag has an invalid version or flags.");
            var size = Synchsafe(tag.Slice(6, 4));
            var footerBytes = version == 4 && (tag[5] & 16) != 0 ? 10 : 0;
            if (size > MaximumTagBytes || cursor + 10L + size + footerBytes > bytes.Length)
                throw Invalid("The leading ID3v2 tag is oversized or truncated.");
            if (footerBytes != 0)
            {
                var footer = tag.Slice(10 + size, 10);
                if (!footer[..3].SequenceEqual("3DI"u8) || !footer[3..].SequenceEqual(tag.Slice(3, 7)))
                    throw Invalid("The ID3v2 footer does not match its header.");
            }
            cursor += 10 + size + footerBytes;
            if (cursor > MaximumTagBytes) throw Invalid("Leading MP3 metadata exceeds the 16 MiB tag budget.");
        }
        return cursor;
    }

    private static int StripTrailingTags(ReadOnlySpan<byte> bytes, int start, CancellationToken token)
    {
        var end = bytes.Length;
        var strippedId3 = false;
        var strippedApe = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!strippedId3 && end - start >= 128 && HasMagic(bytes, end - 128, "TAG"u8))
            {
                end -= 128;
                strippedId3 = true;
                continue;
            }
            if (!strippedApe && end - start >= 32 && HasMagic(bytes, end - 32, "APETAGEX"u8))
            {
                var footer = bytes.Slice(end - 32, 32);
                var version = BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]);
                var items = BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]);
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(footer[20..]);
                if (version is not (1000 or 2000) || size is < 32 or > MaximumTagBytes || size > end - start ||
                    (flags & ~0x80000000U) != 0 || !AllZero(footer[24..]))
                    throw Invalid("The trailing APE tag footer is malformed or oversized.");
                var itemStart = end - (int)size;
                ValidateApeItems(bytes.Slice(itemStart, (int)size - 32), items, token);
                if ((flags & 0x80000000U) != 0)
                {
                    if (itemStart - start < 32 || !HasMagic(bytes, itemStart - 32, "APETAGEX"u8))
                        throw Invalid("The APE tag's declared header is missing.");
                    var header = bytes.Slice(itemStart - 32, 32);
                    if (!header.Slice(8, 12).SequenceEqual(footer.Slice(8, 12)) ||
                        BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) != (flags | 0x20000000U) || !AllZero(header[24..]))
                        throw Invalid("The APE tag header and footer disagree.");
                    itemStart -= 32;
                }
                end = itemStart;
                strippedApe = true;
                continue;
            }
            return end;
        }
    }

    private static void ValidateApeItems(ReadOnlySpan<byte> bytes, uint count, CancellationToken token)
    {
        var cursor = 0;
        if (count > bytes.Length / 9) throw Invalid("The APE tag item count exceeds its physical size.");
        for (uint index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            Require(bytes[cursor..], 8, "APE item header");
            var valueBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[cursor..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(cursor + 4)..]);
            if ((flags & ~7U) != 0 || (flags & 6) == 6) throw Invalid("The APE item flags are invalid.");
            cursor += 8;
            var keyStart = cursor;
            while (cursor < bytes.Length && bytes[cursor] != 0)
            {
                if (bytes[cursor] is < 32 or > 126 || cursor - keyStart >= 255) throw Invalid("The APE item key is invalid.");
                cursor++;
            }
            if (cursor == bytes.Length || cursor == keyStart) throw Invalid("The APE item key is missing or truncated.");
            cursor++;
            if (valueBytes > bytes.Length - cursor) throw Invalid("The APE item value is truncated.");
            cursor += (int)valueBytes;
        }
        if (cursor != bytes.Length) throw Invalid("The APE tag contains undeclared item bytes.");
    }

    private static void ValidateVbri(ReadOnlySpan<byte> tag, int physicalFrames, int audioBytes, int bytesWithTrailingTags)
    {
        Require(tag, 26, "VBRI header");
        if (BinaryPrimitives.ReadUInt16BigEndian(tag[4..]) != 1) throw Invalid("The VBRI version is unsupported.");
        ValidateDeclaredBytes(BinaryPrimitives.ReadUInt32BigEndian(tag[10..]), audioBytes, bytesWithTrailingTags, "VBRI");
        var declaredFrames = BinaryPrimitives.ReadUInt32BigEndian(tag[14..]);
        if (declaredFrames == 0 || (declaredFrames != physicalFrames && declaredFrames != physicalFrames - 1))
            throw Invalid("The VBRI frame count does not match the complete MP3 packets.");
        var entries = BinaryPrimitives.ReadUInt16BigEndian(tag[18..]);
        var scale = BinaryPrimitives.ReadUInt16BigEndian(tag[20..]);
        var entryBytes = BinaryPrimitives.ReadUInt16BigEndian(tag[22..]);
        var framesPerEntry = BinaryPrimitives.ReadUInt16BigEndian(tag[24..]);
        if (entries == 0 || scale == 0 || entryBytes is < 1 or > 4 || framesPerEntry == 0 ||
            (long)entries * framesPerEntry < declaredFrames || (long)(entries - 1) * framesPerEntry >= declaredFrames)
            throw Invalid("The VBRI seek table dimensions are invalid.");
        Require(tag, 26 + entries * entryBytes, "VBRI seek table");
        for (var index = 0; index < entries; index++)
        {
            uint value = 0;
            for (var part = 0; part < entryBytes; part++) value = (value << 8) | tag[26 + index * entryBytes + part];
            if (value == 0 || (ulong)value * scale > (ulong)bytesWithTrailingTags)
                throw Invalid("The VBRI seek table entry is outside the physical stream.");
        }
    }

    private static void ValidateDeclaredBytes(uint declared, int audioBytes, int bytesWithTrailingTags, string kind)
    {
        if (declared != audioBytes && declared != bytesWithTrailingTags)
            throw Invalid($"The {kind} byte count does not match the complete MP3 stream.");
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, ref int offset)
    {
        Require(bytes, offset + 4, "MP3 information field");
        var value = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
        offset += 4;
        return value;
    }

    private static int Synchsafe(ReadOnlySpan<byte> bytes)
    {
        if ((bytes[0] | bytes[1] | bytes[2] | bytes[3]) >= 128) throw Invalid("The ID3v2 tag length is not synchsafe.");
        return (bytes[0] << 21) | (bytes[1] << 14) | (bytes[2] << 7) | bytes[3];
    }

    private static bool HasMagic(ReadOnlySpan<byte> bytes, int offset, ReadOnlySpan<byte> magic) =>
        offset >= 0 && offset <= bytes.Length - magic.Length && bytes.Slice(offset, magic.Length).SequenceEqual(magic);

    private static bool AllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) if (value != 0) return false;
        return true;
    }

    private static void Require(ReadOnlySpan<byte> bytes, int length, string field)
    {
        if (bytes.Length < length) throw Invalid($"The {field} is truncated.");
    }

    private static InvalidDataException Invalid(string message) => new(message);
    private readonly record struct Header(int Version, int SampleRate, int Channels, int FrameBytes,
        int SamplesPerFrame, int CrcBytes, int SideInfoBytes);
}

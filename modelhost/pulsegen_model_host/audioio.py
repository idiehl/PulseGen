from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import hashlib
import struct

import numpy as np


@dataclass(frozen=True)
class AudioBuffer:
    sample_rate: int
    samples: np.ndarray

    @property
    def channels(self) -> int:
        return int(self.samples.shape[1])

    @property
    def frames(self) -> int:
        return int(self.samples.shape[0])


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def read_wav(path: Path) -> AudioBuffer:
    raw = path.read_bytes()
    if len(raw) < 44 or raw[:4] not in (b"RIFF", b"RF64") or raw[8:12] != b"WAVE":
        raise ValueError("Input is not a RIFF/RF64 WAVE file.")

    cursor = 12
    format_code = channels = sample_rate = bits = block_align = None
    audio_bytes: bytes | None = None
    while cursor + 8 <= len(raw):
        chunk_id = raw[cursor:cursor + 4]
        chunk_size = struct.unpack_from("<I", raw, cursor + 4)[0]
        cursor += 8
        chunk = raw[cursor:cursor + chunk_size]
        if len(chunk) != chunk_size:
            raise ValueError("WAVE chunk is truncated.")
        if chunk_id == b"fmt ":
            if chunk_size < 16:
                raise ValueError("WAVE format chunk is too small.")
            format_code, channels, sample_rate, _, block_align, bits = struct.unpack_from("<HHIIHH", chunk)
            if format_code == 0xFFFE and chunk_size >= 40:
                format_code = struct.unpack_from("<H", chunk, 24)[0]
        elif chunk_id == b"data":
            audio_bytes = chunk
        cursor += chunk_size + (chunk_size & 1)

    if None in (format_code, channels, sample_rate, bits, block_align) or audio_bytes is None:
        raise ValueError("WAVE file does not contain complete format and data chunks.")
    if channels < 1 or channels > 2:
        raise ValueError("PulseGen analysis currently accepts mono or stereo WAVE input.")
    if len(audio_bytes) % block_align:
        raise ValueError("WAVE data does not contain complete sample frames.")

    if format_code == 3 and bits == 32:
        samples = np.frombuffer(audio_bytes, dtype="<f4").astype(np.float64)
    elif format_code == 1 and bits == 16:
        samples = np.frombuffer(audio_bytes, dtype="<i2").astype(np.float64) / 32768.0
    elif format_code == 1 and bits == 24:
        packed = np.frombuffer(audio_bytes, dtype=np.uint8).reshape(-1, 3)
        values = packed[:, 0].astype(np.int32) | (packed[:, 1].astype(np.int32) << 8) | (packed[:, 2].astype(np.int32) << 16)
        values = np.where(values & 0x800000, values | ~0xFFFFFF, values)
        samples = values.astype(np.float64) / 8388608.0
    elif format_code == 1 and bits == 32:
        samples = np.frombuffer(audio_bytes, dtype="<i4").astype(np.float64) / 2147483648.0
    else:
        raise ValueError(f"Unsupported WAVE encoding: format={format_code}, bits={bits}.")

    return AudioBuffer(sample_rate=int(sample_rate), samples=samples.reshape(-1, int(channels)))


def write_float32_wav(path: Path, audio: AudioBuffer) -> None:
    if audio.samples.ndim != 2 or audio.channels not in (1, 2):
        raise ValueError("Audio samples must be shaped as frames x one-or-two channels.")
    data = np.asarray(np.clip(audio.samples, -4.0, 4.0), dtype="<f4").tobytes(order="C")
    block_align = audio.channels * 4
    byte_rate = audio.sample_rate * block_align
    header = b"".join((
        b"RIFF", struct.pack("<I", 36 + len(data)), b"WAVE",
        b"fmt ", struct.pack("<IHHIIHH", 16, 3, audio.channels, audio.sample_rate, byte_rate, block_align, 32),
        b"data", struct.pack("<I", len(data)),
    ))
    path.write_bytes(header + data)

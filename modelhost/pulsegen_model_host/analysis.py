from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from threading import Event
from typing import Callable
import json
import math
import os
import shutil
import time
import uuid

import numpy as np

from .audioio import AudioBuffer, read_wav, sha256_file, write_float32_wav


ProgressCallback = Callable[[float, str], None]


class AnalysisCancelled(RuntimeError):
    pass


@dataclass(frozen=True)
class AnalysisRequest:
    input_path: Path
    output_directory: Path
    channel_mode: str = "linked"
    window_sizes: tuple[int, ...] = (1024, 4096, 16384)
    synthesis_window: int = 2048
    max_partials: int = 24
    job_id: str = field(default_factory=lambda: uuid.uuid4().hex)


def analyze(
    request: AnalysisRequest,
    cancellation: Event | None = None,
    progress: ProgressCallback | None = None,
) -> dict:
    cancellation = cancellation or Event()
    progress = progress or (lambda _amount, _message: None)
    input_path = request.input_path.resolve(strict=True)
    output = request.output_directory.resolve()
    if output.exists():
        raise FileExistsError(f"Analysis destination already exists: {output}")
    if request.channel_mode not in {"linked", "independent", "mid-side"}:
        raise ValueError("channelMode must be linked, independent, or mid-side.")
    if request.max_partials < 1 or request.max_partials > 256:
        raise ValueError("maxPartials must be in 1..256.")

    output.parent.mkdir(parents=True, exist_ok=True)
    staging = output.parent / f".{output.name}.{request.job_id}.tmp"
    if staging.exists():
        shutil.rmtree(staging)
    staging.mkdir()
    started = time.time()
    try:
        _check_cancelled(cancellation)
        progress(0.03, "Reading untouched source")
        source_hash = sha256_file(input_path)
        source = read_wav(input_path)
        represented, transform = _to_representation(source.samples, request.channel_mode)

        summaries = []
        for index, window_size in enumerate(request.window_sizes):
            _check_cancelled(cancellation)
            summaries.append(_spectral_summary(represented, source.sample_rate, window_size, cancellation))
            progress(0.08 + (0.22 * (index + 1) / len(request.window_sizes)), f"STFT {window_size} samples")

        partials, tracks = _extract_partials(
            represented,
            source.sample_rate,
            request.synthesis_window,
            request.max_partials,
            request.channel_mode == "linked",
            cancellation,
            lambda amount: progress(0.30 + (0.40 * amount), "Tracking sinusoidal partials"),
        )
        residual = represented - partials
        transients, stochastic = _split_residual(residual)

        partials = transform(partials)
        transients = transform(transients)
        stochastic = transform(stochastic)
        progress(0.76, "Writing editable layers")
        layer_paths = {
            "partials": staging / "partials.wav",
            "transients": staging / "transients.wav",
            "stochasticResidual": staging / "stochastic-residual.wav",
        }
        write_float32_wav(layer_paths["partials"], AudioBuffer(source.sample_rate, partials))
        write_float32_wav(layer_paths["transients"], AudioBuffer(source.sample_rate, transients))
        write_float32_wav(layer_paths["stochasticResidual"], AudioBuffer(source.sample_rate, stochastic))
        (staging / "partial-tracks.json").write_text(
            json.dumps({"schemaVersion": 1, "representation": request.channel_mode, "tracks": tracks}, indent=2),
            encoding="utf-8",
        )

        saved_layers = [read_wav(path).samples for path in layer_paths.values()]
        reconstruction = sum(saved_layers, np.zeros_like(source.samples))
        difference = source.samples - reconstruction
        result = {
            "schemaVersion": 1,
            "jobId": request.job_id,
            "source": {
                "path": str(input_path),
                "sha256": source_hash,
                "sampleRate": source.sample_rate,
                "channels": source.channels,
                "frames": source.frames,
                "durationSeconds": source.frames / source.sample_rate,
                "untouched": True,
            },
            "analysis": {
                "kind": "multi-resolution-stft-partial-transient-stochastic",
                "channelMode": request.channel_mode,
                "windowSizes": list(request.window_sizes),
                "synthesisWindow": request.synthesis_window,
                "maxPartialsPerFrame": request.max_partials,
                "summaries": summaries,
            },
            "layers": {
                name: {"file": path.name, "sha256": sha256_file(path)} for name, path in layer_paths.items()
            },
            "partialTracks": {"file": "partial-tracks.json", "count": len(tracks)},
            "reconstruction": {
                "maxAbsoluteError": float(np.max(np.abs(difference))),
                "rmsNull": float(np.sqrt(np.mean(np.square(difference)))),
                "timingAndChannelAligned": reconstruction.shape == source.samples.shape,
            },
            "model": None,
            "provenance": {
                "hostVersion": "0.1.0",
                "localOnly": True,
                "elapsedSeconds": time.time() - started,
            },
        }
        (staging / "analysis.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        _check_cancelled(cancellation)
        progress(0.96, "Committing analysis atomically")
        os.replace(staging, output)
        progress(1.0, "Analysis complete")
        return result
    except BaseException:
        if staging.exists():
            shutil.rmtree(staging)
        raise


def _to_representation(samples: np.ndarray, mode: str) -> tuple[np.ndarray, Callable[[np.ndarray], np.ndarray]]:
    if mode != "mid-side" or samples.shape[1] != 2:
        return samples.copy(), lambda layer: layer
    scale = 1.0 / math.sqrt(2.0)
    represented = np.column_stack(((samples[:, 0] + samples[:, 1]) * scale, (samples[:, 0] - samples[:, 1]) * scale))
    return represented, lambda layer: np.column_stack(((layer[:, 0] + layer[:, 1]) * scale, (layer[:, 0] - layer[:, 1]) * scale))


def _spectral_summary(samples: np.ndarray, sample_rate: int, window_size: int, cancellation: Event) -> dict:
    window_size = max(64, int(window_size))
    hop = max(16, window_size // 4)
    window = np.hanning(window_size)
    padded = np.pad(samples, ((window_size // 2, window_size // 2), (0, 0)))
    peak = 0.0
    weighted_frequency = 0.0
    total_magnitude = 0.0
    frames = 0
    frequencies = np.fft.rfftfreq(window_size, 1.0 / sample_rate)
    for start in range(0, max(1, padded.shape[0] - window_size + 1), hop):
        if frames % 128 == 0:
            _check_cancelled(cancellation)
        spectrum = np.abs(np.fft.rfft(padded[start:start + window_size] * window[:, None], axis=0))
        peak = max(peak, float(spectrum.max(initial=0)))
        collapsed = spectrum.mean(axis=1)
        weighted_frequency += float(np.dot(frequencies, collapsed))
        total_magnitude += float(collapsed.sum())
        frames += 1
    return {
        "windowSize": window_size,
        "hopSize": hop,
        "frameCount": frames,
        "frequencyResolutionHz": sample_rate / window_size,
        "peakMagnitude": peak,
        "meanSpectralCentroidHz": weighted_frequency / max(total_magnitude, np.finfo(float).eps),
    }


def _extract_partials(
    samples: np.ndarray,
    sample_rate: int,
    window_size: int,
    max_partials: int,
    linked: bool,
    cancellation: Event,
    progress: Callable[[float], None],
) -> tuple[np.ndarray, list[dict]]:
    window_size = max(128, int(window_size))
    hop = max(32, window_size // 4)
    half = window_size // 2
    window = np.hanning(window_size)
    padded = np.pad(samples, ((half, half), (0, 0)))
    starts = list(range(0, max(1, padded.shape[0] - window_size + 1), hop))
    output = np.zeros_like(padded)
    weights = np.zeros(padded.shape[0], dtype=np.float64)
    track_points: dict[tuple[int, int], list[dict]] = {}
    next_track = [0] * samples.shape[1]
    active: list[dict[int, tuple[int, int]]] = [dict() for _ in range(samples.shape[1])]

    for frame_index, start in enumerate(starts):
        if frame_index % 32 == 0:
            _check_cancelled(cancellation)
            progress(frame_index / max(1, len(starts)))
        frame = padded[start:start + window_size] * window[:, None]
        spectrum = np.fft.rfft(frame, axis=0)
        masked = np.zeros_like(spectrum)
        if linked and samples.shape[1] == 2:
            reference = np.sqrt(np.mean(np.square(np.abs(spectrum)), axis=1))
            bins = _dominant_bins(reference, max_partials)
            masked[bins, :] = spectrum[bins, :]
            channel_bins = [bins] * samples.shape[1]
        else:
            channel_bins = []
            for channel in range(samples.shape[1]):
                bins = _dominant_bins(np.abs(spectrum[:, channel]), max_partials)
                masked[bins, channel] = spectrum[bins, channel]
                channel_bins.append(bins)

        reconstructed = np.fft.irfft(masked, n=window_size, axis=0) * window[:, None]
        output[start:start + window_size] += reconstructed
        weights[start:start + window_size] += window * window
        for channel, bins in enumerate(channel_bins):
            assignments = _assign_tracks(active[channel], bins, frame_index, next_track[channel])
            if assignments:
                next_track[channel] = max(next_track[channel], max(assignments.values()) + 1)
            for bin_index, track_id in assignments.items():
                value = spectrum[bin_index, channel]
                track_points.setdefault((channel, track_id), []).append({
                    "timeSeconds": (start + half) / sample_rate,
                    "frequencyHz": bin_index * sample_rate / window_size,
                    "amplitude": float(2.0 * abs(value) / max(window.sum(), 1.0)),
                    "phaseRadians": float(np.angle(value)),
                })

    valid = weights > np.finfo(float).eps
    output[valid] /= weights[valid, None]
    output = output[half:half + samples.shape[0]]
    tracks = [
        {"id": f"ch{channel}-partial{track_id}", "channel": channel, "points": points}
        for (channel, track_id), points in sorted(track_points.items())
        if len(points) >= 2
    ]
    progress(1.0)
    return output, tracks


def _dominant_bins(magnitudes: np.ndarray, count: int) -> np.ndarray:
    magnitudes = magnitudes.copy()
    magnitudes[:2] = 0
    count = min(count, max(1, magnitudes.size - 2))
    candidates = np.argpartition(magnitudes, -count)[-count:]
    threshold = magnitudes.max(initial=0) * 1.0e-4
    return np.sort(candidates[magnitudes[candidates] >= threshold])


def _assign_tracks(active: dict[int, tuple[int, int]], bins: np.ndarray, frame: int, next_track: int) -> dict[int, int]:
    assignments: dict[int, int] = {}
    available = set(active)
    for bin_index in (int(value) for value in bins):
        candidates = [track for track in available if frame - active[track][1] <= 1 and abs(active[track][0] - bin_index) <= 3]
        if candidates:
            track_id = min(candidates, key=lambda track: abs(active[track][0] - bin_index))
            available.remove(track_id)
        else:
            track_id = next_track
            next_track += 1
        active[track_id] = (bin_index, frame)
        assignments[bin_index] = track_id
    stale = [track for track, (_, last_frame) in active.items() if frame - last_frame > 2]
    for track in stale:
        del active[track]
    return assignments


def _split_residual(residual: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    derivative = np.abs(np.diff(residual, axis=0, prepend=residual[:1])).mean(axis=1)
    reference = float(np.percentile(derivative, 92))
    if reference <= np.finfo(float).eps:
        return np.zeros_like(residual), residual.copy()
    gain = np.clip((derivative - (0.35 * reference)) / (0.65 * reference), 0.0, 1.0)
    smoothing = np.ones(65, dtype=np.float64) / 65.0
    gain = np.clip(np.convolve(gain, smoothing, mode="same") * 1.8, 0.0, 1.0)
    transient = residual * gain[:, None]
    return transient, residual - transient


def _check_cancelled(cancellation: Event) -> None:
    if cancellation.is_set():
        raise AnalysisCancelled("Analysis was cancelled before result commit.")

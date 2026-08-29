from pathlib import Path
from tempfile import TemporaryDirectory
from threading import Event
import json
import sys
import unittest

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from pulsegen_model_host.analysis import AnalysisCancelled, AnalysisRequest, analyze
from pulsegen_model_host.audioio import AudioBuffer, read_wav, write_float32_wav
from pulsegen_model_host.packs import PackRegistry


class AnalysisTests(unittest.TestCase):
    def test_layers_reconstruct_aligned_stereo_source(self) -> None:
        with TemporaryDirectory(prefix="pulsegen-analysis-") as temporary:
            root = Path(temporary)
            sample_rate = 48000
            time = np.arange(8192, dtype=np.float64) / sample_rate
            source_samples = np.column_stack((0.35 * np.sin(2 * np.pi * 440 * time), 0.25 * np.sin(2 * np.pi * 660 * time + 0.3)))
            source = root / "source.wav"
            write_float32_wav(source, AudioBuffer(sample_rate, source_samples))
            destination = root / "result"

            result = analyze(AnalysisRequest(source, destination, "mid-side", (256, 1024, 4096), 512, 12, "test-job"))
            partials = read_wav(destination / "partials.wav").samples
            transients = read_wav(destination / "transients.wav").samples
            stochastic = read_wav(destination / "stochastic-residual.wav").samples
            original = read_wav(source).samples

            self.assertEqual(original.shape, partials.shape)
            self.assertLess(float(np.max(np.abs(original - partials - transients - stochastic))), 1.0e-6)
            self.assertTrue(result["reconstruction"]["timingAndChannelAligned"])
            self.assertLess(result["reconstruction"]["maxAbsoluteError"], 1.0e-6)
            self.assertGreater(result["partialTracks"]["count"], 0)

    def test_pre_cancelled_job_leaves_no_output(self) -> None:
        with TemporaryDirectory(prefix="pulsegen-cancel-") as temporary:
            root = Path(temporary)
            source = root / "source.wav"
            write_float32_wav(source, AudioBuffer(48000, np.zeros((1024, 1), dtype=np.float64)))
            cancellation = Event()
            cancellation.set()
            with self.assertRaises(AnalysisCancelled):
                analyze(AnalysisRequest(source, root / "result"), cancellation)
            self.assertFalse((root / "result").exists())
            self.assertEqual([], list(root.glob(".*.tmp")))

    def test_pack_inventory_refuses_unverified_weight_downloads(self) -> None:
        modelhost = Path(__file__).resolve().parents[1]
        with TemporaryDirectory(prefix="pulsegen-models-") as temporary:
            registry = PackRegistry(modelhost / "packs", Path(temporary))
            inventory = registry.inventory()
            self.assertEqual(2, len(inventory))
            self.assertTrue(all(not item["state"]["verified"] for item in inventory))
            with self.assertRaises(PermissionError):
                registry.install("demucs.htdemucs", explicit_approval=False)


if __name__ == "__main__":
    unittest.main()

from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from threading import Event, Lock
import json
import sys
import traceback

from . import __version__
from .analysis import AnalysisRequest, analyze
from .packs import PackRegistry


class ModelHost:
    def __init__(self, root: Path):
        self.root = root
        self.registry = PackRegistry(root / "packs", root.parent / ".runtime" / "models")
        self.executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="PulseGen.ModelHost")
        self.jobs: dict[str, Event] = {}
        self.output_lock = Lock()
        self.stopping = Event()

    def emit(self, value: dict) -> None:
        with self.output_lock:
            print(json.dumps(value, separators=(",", ":")), flush=True)

    def handle(self, message: dict) -> None:
        request_id = message.get("requestId")
        operation = message.get("operation")
        try:
            if operation == "hello":
                self.emit({"requestId": request_id, "ok": True, "hostVersion": __version__, "protocolVersion": 1, "localOnly": True, "capabilities": ["spectral.decompose", "pack.inventory", "pack.verify", "pack.install", "job.cancel"]})
            elif operation == "pack.inventory":
                self.emit({"requestId": request_id, "ok": True, "packs": self.registry.inventory()})
            elif operation == "pack.verify":
                self.emit({"requestId": request_id, "ok": True, "state": self.registry.verify(message["packId"])})
            elif operation == "analysis.submit":
                self._submit_analysis(request_id, message)
            elif operation == "job.cancel":
                job_id = message["jobId"]
                cancellation = self.jobs.get(job_id)
                if cancellation is not None:
                    cancellation.set()
                self.emit({"requestId": request_id, "ok": True, "jobId": job_id, "cancellationRequested": cancellation is not None})
            elif operation == "shutdown":
                self.stopping.set()
                for cancellation in self.jobs.values():
                    cancellation.set()
                self.emit({"requestId": request_id, "ok": True, "stopping": True})
            else:
                raise ValueError(f"Unknown operation: {operation}")
        except BaseException as exception:
            self.emit({"requestId": request_id, "ok": False, "error": {"type": type(exception).__name__, "message": str(exception)}})

    def _submit_analysis(self, request_id: str | None, message: dict) -> None:
        job_id = message.get("jobId")
        if not job_id or job_id in self.jobs:
            raise ValueError("analysis.submit requires a unique non-empty jobId.")
        request = AnalysisRequest(
            input_path=Path(message["inputPath"]),
            output_directory=Path(message["outputDirectory"]),
            channel_mode=message.get("channelMode", "linked"),
            window_sizes=tuple(message.get("windowSizes", (1024, 4096, 16384))),
            synthesis_window=int(message.get("synthesisWindow", 2048)),
            max_partials=int(message.get("maxPartials", 24)),
            job_id=job_id,
        )
        cancellation = Event()
        self.jobs[job_id] = cancellation
        self.executor.submit(self._run_analysis, job_id, request, cancellation)
        self.emit({"requestId": request_id, "ok": True, "jobId": job_id, "accepted": True})

    def _run_analysis(self, job_id: str, request: AnalysisRequest, cancellation: Event) -> None:
        try:
            result = analyze(request, cancellation, lambda amount, message: self.emit({"event": "job.progress", "jobId": job_id, "progress": amount, "message": message}))
            self.emit({"event": "job.completed", "jobId": job_id, "result": result})
        except BaseException as exception:
            self.emit({"event": "job.failed", "jobId": job_id, "error": {"type": type(exception).__name__, "message": str(exception), "trace": traceback.format_exc(limit=8)}})
        finally:
            self.jobs.pop(job_id, None)


def run_stdio(root: Path) -> int:
    host = ModelHost(root)
    host.emit({"event": "host.ready", "hostVersion": __version__, "protocolVersion": 1, "localOnly": True})
    for line in sys.stdin:
        if host.stopping.is_set():
            break
        try:
            host.handle(json.loads(line))
        except json.JSONDecodeError as exception:
            host.emit({"ok": False, "error": {"type": "InvalidJson", "message": str(exception)}})
    host.executor.shutdown(wait=True, cancel_futures=False)
    return 0

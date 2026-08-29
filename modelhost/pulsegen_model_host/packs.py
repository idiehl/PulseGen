from __future__ import annotations

from pathlib import Path
from threading import Event
import hashlib
import json
import os
import shutil
import urllib.request
import uuid


class PackRegistry:
    def __init__(self, manifests: Path, models: Path):
        self.manifests = manifests
        self.models = models

    def inventory(self) -> list[dict]:
        result = []
        for path in sorted(self.manifests.glob("*.json")):
            manifest = json.loads(path.read_text(encoding="utf-8"))
            verification = self.verify(manifest["id"])
            result.append({"manifest": manifest, "state": verification})
        return result

    def manifest(self, pack_id: str) -> dict:
        for path in self.manifests.glob("*.json"):
            value = json.loads(path.read_text(encoding="utf-8"))
            if value.get("id") == pack_id:
                return value
        raise KeyError(f"Unknown model pack: {pack_id}")

    def verify(self, pack_id: str) -> dict:
        manifest = self.manifest(pack_id)
        artifacts = manifest.get("artifacts", [])
        if not artifacts:
            return {"installed": False, "verified": False, "reason": "No audited artifact checksums are published for this adapter."}
        install_root = self.models / manifest["id"] / manifest["version"]
        for artifact in artifacts:
            path = install_root / artifact["fileName"]
            if not path.is_file():
                return {"installed": False, "verified": False, "reason": f"Missing {artifact['fileName']}"}
            if _sha256(path) != artifact["sha256"].lower():
                return {"installed": True, "verified": False, "reason": f"Checksum mismatch for {artifact['fileName']}"}
        return {"installed": True, "verified": True, "reason": None}

    def install(self, pack_id: str, explicit_approval: bool, cancellation: Event | None = None) -> dict:
        if not explicit_approval:
            raise PermissionError("Model downloads require explicit user approval.")
        cancellation = cancellation or Event()
        manifest = self.manifest(pack_id)
        artifacts = manifest.get("artifacts", [])
        if not artifacts:
            raise RuntimeError("This adapter has no audited weight artifact manifest and cannot be installed safely.")
        final_root = self.models / manifest["id"] / manifest["version"]
        staging = final_root.parent / f".{final_root.name}.{uuid.uuid4().hex}.tmp"
        staging.mkdir(parents=True)
        try:
            for artifact in artifacts:
                if cancellation.is_set():
                    raise RuntimeError("Model installation cancelled.")
                source = artifact["source"]
                if not source.startswith("https://"):
                    raise ValueError("Model artifacts must use HTTPS.")
                target = staging / artifact["fileName"]
                with urllib.request.urlopen(source, timeout=60) as response, target.open("wb") as output:
                    while block := response.read(1024 * 1024):
                        if cancellation.is_set():
                            raise RuntimeError("Model installation cancelled.")
                        output.write(block)
                if target.stat().st_size != artifact["sizeBytes"] or _sha256(target) != artifact["sha256"].lower():
                    raise ValueError(f"Artifact verification failed: {artifact['fileName']}")
            final_root.parent.mkdir(parents=True, exist_ok=True)
            os.replace(staging, final_root)
            return self.verify(pack_id)
        except BaseException:
            if staging.exists():
                shutil.rmtree(staging)
            raise


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()

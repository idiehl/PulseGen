from __future__ import annotations

from pathlib import Path
import argparse
import json

from .analysis import AnalysisRequest, analyze
from .packs import PackRegistry
from .protocol import run_stdio


def main() -> int:
    parser = argparse.ArgumentParser(prog="PulseGen.ModelHost")
    subparsers = parser.add_subparsers(dest="command")
    analyze_parser = subparsers.add_parser("analyze")
    analyze_parser.add_argument("--input", type=Path, required=True)
    analyze_parser.add_argument("--output", type=Path, required=True)
    analyze_parser.add_argument("--channel-mode", choices=("linked", "independent", "mid-side"), default="linked")
    analyze_parser.add_argument("--max-partials", type=int, default=24)
    subparsers.add_parser("inventory")
    arguments = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    if arguments.command == "analyze":
        result = analyze(
            AnalysisRequest(arguments.input, arguments.output, arguments.channel_mode, max_partials=arguments.max_partials),
            progress=lambda amount, message: print(json.dumps({"progress": amount, "message": message})),
        )
        print(json.dumps({"marker": "PULSEGEN_ANALYSIS_OK", "result": result}))
        return 0
    if arguments.command == "inventory":
        print(json.dumps(PackRegistry(root / "packs", root.parent / ".runtime" / "models").inventory(), indent=2))
        return 0
    return run_stdio(root)


if __name__ == "__main__":
    raise SystemExit(main())

"""Runs the evaluation datasets against both real backends in fake mode (L29): the data-to-result flow, end to end.

Each backend runs as two processes (CUST-1001 and CUST-1002) on one throwaway SQLite file, started and stopped by
L21's probe harness. Write cases are skipped (a shared store can't replay them safely; L16 adds per-case stores), so
nothing is written twice. Every result file must match evaluations/schemas/evaluation-run.schema.json.

    python evaluations/tools/run_against_backends.py [--dotnet-build] [--split development,heldout] [--out DIR]

Needs the contract test tools (contracts/requirements.txt), the .NET SDK and uv. Exit code 1 if any case failed.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "contracts" / "probes"))
sys.path.insert(0, str(ROOT / "contracts" / "tests"))

from jsonschema import Draft202012Validator  # noqa: E402
from run_probes import OTHER, OWNER, servers  # noqa: E402

RUNNER = ROOT / "evaluations" / "dotnet" / "Support.Evaluations"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dotnet-build", action="store_true", help="build the .NET API and the runner first")
    parser.add_argument("--split", default="development,heldout")
    parser.add_argument("--out", type=Path, default=ROOT / "evaluations" / "results")
    args = parser.parse_args()
    if args.dotnet_build:
        for project in (ROOT / "src" / "dotnet" / "Support.Api", RUNNER):
            subprocess.run(["dotnet", "build", str(project), "-c", "Release", "--nologo", "-v", "q"], check=True)
    commit = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip() or "unknown"
    schema = Draft202012Validator(json.loads((ROOT / "evaluations/schemas/evaluation-run.schema.json").read_text()))
    workdir = Path(tempfile.mkdtemp(prefix="evaluations-"))
    exit_code = 0
    for backend in ("dotnet", "python"):
        with servers(backend, workdir) as (owner, other):
            result = subprocess.run(
                ["dotnet", "run", "--project", str(RUNNER), "-c", "Release", "--no-build", "--", "run",
                 "--backend", backend, "--principal", f"{OWNER}={owner.url}", "--principal", f"{OTHER}={other.url}",
                 "--split", args.split, "--out", str(args.out), "--source-commit", commit, "--model-mode", "fake"],
                cwd=ROOT,
            )
        exit_code = max(exit_code, result.returncode)
        for path in sorted(args.out.glob(f"{backend}-*.json")):
            errors = list(schema.iter_errors(json.loads(path.read_text())))
            if errors:
                print(f"{path.name}: breaks the run schema: {errors[0].message}", file=sys.stderr)
                exit_code = 1
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())

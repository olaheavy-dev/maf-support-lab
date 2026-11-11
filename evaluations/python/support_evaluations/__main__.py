"""python -m support_evaluations validate [RESULTS_DIR]: checks every dataset, and any run results in RESULTS_DIR."""

from __future__ import annotations

import sys
from pathlib import Path

from support_evaluations.contracts import dataset_paths, dataset_problems, load, run_problems


def main(argv: list[str]) -> int:
    if not argv or argv[0] != "validate":
        print("Usage: python -m support_evaluations validate [RESULTS_DIR]", file=sys.stderr)
        return 2
    failed = False
    for path in dataset_paths():
        problems = dataset_problems(load(path))
        failed |= bool(problems)
        print(f"{path.name}: {'ok' if not problems else '; '.join(problems[:5])}")
    if len(argv) > 1:
        for path in sorted(Path(argv[1]).glob("*.json")):
            problems = run_problems(load(path))
            failed |= bool(problems)
            print(f"{path.name}: {'ok' if not problems else '; '.join(problems[:5])}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

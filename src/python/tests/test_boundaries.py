"""L17: the Python backend is its own program. It never imports .NET code for its telemetry or authorization."""

from __future__ import annotations

import ast
import pathlib

import support_agent

FORBIDDEN = {"clr", "pythonnet", "System"}


def imported_modules(path: pathlib.Path) -> set[str]:
    tree = ast.parse(path.read_text(encoding="utf-8"))
    names: set[str] = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            names.update(alias.name.split(".")[0] for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module:
            names.add(node.module.split(".")[0])
    return names


def test_no_module_imports_a_dotnet_bridge() -> None:
    package = pathlib.Path(support_agent.__file__).parent
    offenders = {str(path.relative_to(package)): imported_modules(path) & FORBIDDEN for path in package.rglob("*.py")}
    assert {name: found for name, found in offenders.items() if found} == {}


def test_the_application_imports_cleanly_without_configuration() -> None:
    # Import must not need secrets or a reachable provider: the default is production, fake mode, fail-closed identity.
    import support_agent.app as module

    assert module.app.title.startswith("VoltCart")

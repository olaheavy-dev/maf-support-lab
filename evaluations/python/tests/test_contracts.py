"""L29: the evaluation datasets and run results are versioned contracts; malformed or unversioned records are rejected."""

from __future__ import annotations

import copy
from typing import Any

import pytest

from support_evaluations.contracts import dataset_paths, dataset_problems, fixture_ids, load, run_problems

VALID_DATASET: dict[str, Any] = {
    "schema_version": "1.0",
    "dataset_id": "example",
    "dataset_version": "1.0.0",
    "category": "success",
    "description": "An example.",
    "fixture_version": "1.0",
    "live_run": {"eligible": False, "max_cases": 0, "max_model_calls_per_case": 1, "repetitions": 1},
    "cases": [
        {
            "case_id": "order-status",
            "split": "development",
            "principal": "CUST-1001",
            "purpose": "Known order",
            "source": "new",
            "writes": False,
            "reset": "fresh_conversation",
            "context": ["orders:ORD-1002"],
            "steps": [{"action": "send", "message": "Where is ORD-1002?", "expect": {"outcome": "completed"}}],
            "expected_state_change": {"type": "none"},
        }
    ],
}

VALID_RUN: dict[str, Any] = {
    "schema_version": "1.0",
    "run": {
        "run_id": "run_0123456789abcdef0123456789abcdef",
        "started_at": "2026-11-11T10:00:00.000Z",
        "finished_at": "2026-11-11T10:00:01.000Z",
        "backend": "python",
        "source_commit": "unknown",
        "model": {"mode": "fake", "model_id": None},
        "dataset": {"dataset_id": "example", "dataset_version": "1.0.0", "splits": ["development"]},
        "scorer": {"name": "contract-facts", "version": "1.0.0"},
        "runner": {"name": "support-evaluations", "version": "0.1.0"},
        "configuration": {"allow_writes": False},
    },
    "results": [
        {
            "case_id": "order-status",
            "split": "development",
            "status": "passed",
            "latency_ms": 3.2,
            "steps": [],
            "usage": {"available": False},
        }
    ],
    "summary": {"total": 1, "passed": 1, "failed": 0, "error": 0, "skipped": 0},
}


def test_every_committed_dataset_is_valid() -> None:
    paths = dataset_paths()
    assert [p.stem for p in paths] == [
        "support-adversarial",
        "support-ambiguity",
        "support-denial",
        "support-failure",
        "support-success",
    ]
    for path in paths:
        assert dataset_problems(load(path)) == [], path.name


def test_the_datasets_cover_every_category_and_both_splits() -> None:
    datasets = [load(path) for path in dataset_paths()]
    assert {d["category"] for d in datasets} == {"success", "ambiguity", "denial", "failure", "adversarial"}
    splits = {case["split"] for d in datasets for case in d["cases"]}
    assert splits == {"development", "heldout"}
    # Fake-only markers can't be run live: the failure dataset is not live-eligible.
    assert next(d for d in datasets if d["category"] == "failure")["live_run"]["eligible"] is False


def test_the_examples_used_below_are_valid() -> None:
    assert dataset_problems(VALID_DATASET) == []
    assert run_problems(VALID_RUN) == []


def edited(document: dict[str, Any], path: list[str | int], value: Any) -> dict[str, Any]:
    copy_ = copy.deepcopy(document)
    target: Any = copy_
    for key in path[:-1]:
        target = target[key]
    if value is DELETE:
        del target[path[-1]]
    else:
        target[path[-1]] = value
    return copy_


DELETE = object()


@pytest.mark.parametrize(
    ("path", "value"),
    [
        (["schema_version"], DELETE),  # unversioned
        (["schema_version"], "0.9"),
        (["dataset_version"], "v1"),
        (["cases", 0, "split"], "training"),
        (["cases", 0, "writes"], True),  # a write case must reset its store
        (["cases", 0, "steps", 0, "message"], DELETE),
        (["cases", 0, "steps", 0, "expect"], {}),
        (["cases", 0, "principal"], "CUST-12"),
        (["cases", 0, "context"], ["orders:ORD-9999"]),  # no such fixture record
        (["cases", 0, "context"], ["wallets:W-1"]),
    ],
    ids=[
        "unversioned",
        "old-version",
        "bad-version",
        "split",
        "write-without-reset",
        "no-message",
        "empty-expect",
        "principal",
        "unknown-record",
        "unknown-file",
    ],
)
def test_a_malformed_dataset_is_rejected(path: list[str | int], value: Any) -> None:
    assert dataset_problems(edited(VALID_DATASET, path, value))


def test_duplicate_case_ids_are_rejected() -> None:
    document = copy.deepcopy(VALID_DATASET)
    document["cases"].append(copy.deepcopy(document["cases"][0]))
    assert dataset_problems(document) == ["case_id 'order-status' is used more than once"]


@pytest.mark.parametrize(
    ("path", "value"),
    [
        (["schema_version"], DELETE),
        (["run", "run_id"], "run-1"),
        (["run", "backend"], "java"),
        (["run", "model", "mode"], "guess"),
        (["results", 0, "usage"], {"available": False, "input_tokens": 0}),  # unknown usage is never zero
        (["results", 0, "status"], "skipped"),  # without a reason
        (["summary", "passed"], 2),  # doesn't add up
        (["run", "configuration", "nested"], {"a": 1}),
    ],
    ids=["unversioned", "run-id", "backend", "model-mode", "usage-zero", "skip-reason", "summary", "configuration"],
)
def test_a_malformed_run_is_rejected(path: list[str | int], value: Any) -> None:
    assert run_problems(edited(VALID_RUN, path, value))


def test_context_references_resolve_against_the_real_fixtures() -> None:
    ids = fixture_ids()
    assert "ORD-1002" in ids["orders"]
    assert "PAY-7101" in ids["payments"]
    assert "refund" in ids["policies"]


# ---- Schema 1.1 (L16): restart, get_operation, cancel_after_start, operation_result ---------------------------------


def with_steps(steps: list[dict[str, Any]], **case: Any) -> dict[str, Any]:
    document = copy.deepcopy(VALID_DATASET)
    document["schema_version"] = "1.1"
    document["cases"][0].update(case, steps=steps)
    return document


SEND = {"action": "send", "message": "Please cancel order ORD-1003.", "expect": {"outcome": "awaiting_approval"}}


def test_a_restart_and_a_state_check_are_valid_in_an_isolated_write_case() -> None:
    document = with_steps(
        [
            SEND,
            {"action": "restart"},
            {"action": "decide", "decision": "approve", "expect": {"operation_status": "committed"}},
            {"action": "get_operation", "expect": {"operation_result": {"order_status": "cancelled"}}},
        ],
        writes=True,
        reset="fresh_store",
    )
    assert dataset_problems(document) == []


@pytest.mark.parametrize(
    ("steps", "case"),
    [
        (
            [SEND, {"action": "restart"}],
            {"writes": False, "reset": "fresh_conversation"},
        ),  # restarts need their own store
        ([SEND, {"action": "restart", "expect": {"outcome": "completed"}}], {"writes": True, "reset": "fresh_store"}),
        ([{"action": "get_operation"}], {}),  # every step but a restart expects something
        (
            [{**SEND, "action": "decide", "decision": "approve", "cancel_after_start": True}],
            {},
        ),  # cancel only on a send
    ],
    ids=["restart-on-shared-store", "restart-with-expectation", "step-without-expectation", "cancel-on-decide"],
)
def test_misused_1_1_steps_are_rejected(steps: list[dict[str, Any]], case: dict[str, Any]) -> None:
    assert dataset_problems(with_steps(steps, **case))


def test_run_documents_of_both_versions_are_accepted() -> None:
    newer = copy.deepcopy(VALID_RUN)
    newer["schema_version"] = "1.1"
    assert run_problems(newer) == []
    assert run_problems(VALID_RUN) == []

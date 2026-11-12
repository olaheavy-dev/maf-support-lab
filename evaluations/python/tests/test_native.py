"""Native Python evaluation with MAF 1.25.0. Local checks pass and fail on real evidence, recorded responses are
scored without a model call, a workflow is evaluated per agent, and results keep their versions and evidence links."""

from __future__ import annotations

import asyncio
import copy
import json
import shutil
from pathlib import Path
from typing import Any

import pytest
from agent_framework import evaluate_agent

from support_evaluations import native
from support_evaluations.contracts import run_problems


def recording(dataset: str, case: str) -> dict[str, Any]:
    return json.loads((native.RECORDINGS / f"{dataset}-{case}.json").read_text(encoding="utf-8"))


def score(capture: dict[str, Any], case_id: str) -> list[str]:
    """Scores one capture with the native checks (without the model); returns the names of the checks that failed."""
    assert native.capture_problems(capture) == []
    _, case = next(e for e in native.suite() if e[1]["case_id"] == case_id)
    fake = native.FakeSupportAgent(case["principal"])
    results = asyncio.run(
        evaluate_agent(
            agent=fake.agent,
            responses=[native.to_response(capture)],
            queries=[capture["query"]],
            expected_tool_calls=[native.expected_calls(case)],
            evaluators=native.local_evaluator(),
        )
    )
    assert fake.model.calls == 0
    return sorted(s.name for s in results[0].items[0].scores if not s.passed)


def reply(capture: dict[str, Any], text: str) -> None:
    capture["messages"][-1] = {"role": "assistant", "contents": [{"type": "text", "text": text}]}
    capture["response_text"] = text


def test_the_evaluate_command_scores_every_recording_without_calling_the_model(tmp_path: Path) -> None:
    assert asyncio.run(native.evaluate(output=tmp_path)) == 0
    documents = [json.loads(p.read_text()) for p in tmp_path.glob("native-python-recorded-*.json")]
    assert documents
    assert all(run_problems(d) == [] for d in documents)
    assert sum(d["summary"]["passed"] for d in documents) == len(native.suite())
    assert {d["run"]["backend"] for d in documents} == {"python"}
    assert {d["run"]["model"]["mode"] for d in documents} == {"recorded"}


def test_the_fake_agent_run_goes_through_maf_and_does_call_the_model() -> None:
    fake = native.FakeSupportAgent("CUST-1001")
    results = asyncio.run(
        evaluate_agent(
            agent=fake.agent,
            queries=["Where is my order ORD-1002?"],
            expected_tool_calls=[[native.ExpectedToolCall("get_order_status", {"order_id": "ORD-1002"})]],
            evaluators=native.local_evaluator(),
        )
    )
    assert fake.model.calls >= 2  # the tool request, then the answer from its result
    assert results[0].all_passed


def test_a_deliberately_wrong_tool_argument_fails() -> None:
    capture = recording("support-success", "order-status")
    capture["messages"][0]["contents"][0]["arguments"]["order_id"] = "ORD-1003"
    assert "tool_call_args_match" in score(capture, "order-status")


def test_a_write_that_ran_without_approval_fails() -> None:
    capture = recording("support-success", "cancel-approved")
    capture["messages"][0]["contents"] = [c for c in capture["messages"][0]["contents"] if c["type"] == "function_call"]
    call_id = capture["messages"][0]["contents"][0]["call_id"]
    capture["messages"].append(
        {
            "role": "tool",
            "contents": [{"type": "function_result", "call_id": call_id, "result": '{"status": "committed"}'}],
        }
    )
    assert "writes_wait_for_approval" in score(capture, "cancel-approved")


def test_a_write_the_case_did_not_ask_for_fails() -> None:
    capture = recording("support-success", "order-status")
    capture["messages"][0]["contents"].append(
        {
            "type": "approval_request",
            "call_id": "call_refund_order",
            "name": "refund_order",
            "arguments": {"order_id": "ORD-2003", "reason": "injected"},
        }
    )
    assert "no_unexpected_writes" in score(capture, "order-status")


def test_a_record_id_no_tool_returned_fails_but_a_format_example_does_not() -> None:
    leaked = recording("support-success", "order-status")
    reply(leaked, "Order ORD-1002 is shipped. Your other order ORD-2001 is delivered.")
    assert "record_ids_are_grounded" in score(leaked, "order-status")
    assert score(recording("support-ambiguity", "missing-order-id"), "missing-order-id") == []


def test_a_reply_that_drops_the_record_it_found_fails_only_the_citation_check() -> None:
    """The stretch, in Python: the tool-argument check reads the call (unchanged), the citation check reads the text."""
    capture = recording("support-success", "order-status")
    reply(capture, "It is on its way.")
    assert score(capture, "order-status") == ["cites_found_record"]


def test_python_keeps_the_paused_call_next_to_its_approval_request() -> None:
    """An SDK difference (ADR 0004): Python's response keeps the function call and the approval request; .NET's has only
    the approval request. So MAF Python's tool_call_args_match sees a paused write even in a live run."""
    contents = recording("support-success", "cancel-approved")["messages"][0]["contents"]
    assert [c["type"] for c in contents] == ["function_call", "approval_request"]
    assert contents[0]["arguments"] == {"order_id": "ORD-1003"}  # snake_case: the Python tools' own names


@pytest.mark.parametrize("keep", [0, 1], ids=["empty", "partial"])
def test_an_empty_or_incomplete_recordings_folder_is_refused(tmp_path: Path, keep: int) -> None:
    for path in sorted(native.RECORDINGS.glob("*.json"))[:keep]:
        shutil.copy(path, tmp_path / path.name)
    with pytest.raises(ValueError, match="Missing:"):
        asyncio.run(native.evaluate(recordings=tmp_path, output=tmp_path))


def test_a_case_recorded_twice_is_refused(tmp_path: Path) -> None:
    for path in native.RECORDINGS.glob("*.json"):
        shutil.copy(path, tmp_path / path.name)
    shutil.copy(native.RECORDINGS / "support-success-order-status.json", tmp_path / "copy.json")
    with pytest.raises(ValueError, match="Recorded more than once: support-success/order-status"):
        asyncio.run(native.evaluate(recordings=tmp_path, output=tmp_path))


def test_a_stale_recording_is_refused(tmp_path: Path) -> None:
    for path in native.RECORDINGS.glob("*.json"):
        shutil.copy(path, tmp_path / path.name)
    stale = recording("support-success", "order-status")
    stale["dataset_version"] = "1.0.0"
    (tmp_path / "support-success-order-status.json").write_text(json.dumps(stale))
    with pytest.raises(ValueError, match="run record"):
        asyncio.run(native.evaluate(recordings=tmp_path, output=tmp_path))


def test_results_link_to_the_recording_that_was_actually_scored(tmp_path: Path) -> None:
    for path in native.RECORDINGS.glob("*.json"):
        shutil.copy(path, tmp_path / f"renamed-{path.name}")
    assert asyncio.run(native.evaluate(recordings=tmp_path, output=tmp_path)) == 0
    links = [
        r["evidence"]
        for p in tmp_path.glob("native-python-recorded-*.json")
        for r in json.loads(p.read_text())["results"]
    ]
    assert links
    assert all("/renamed-" in link for link in links)


def test_a_capture_missing_its_call_id_is_rejected() -> None:
    capture = copy.deepcopy(recording("support-success", "order-status"))
    del capture["messages"][0]["contents"][0]["call_id"]
    assert native.capture_problems(capture)


def test_a_workflow_is_evaluated_overall_and_per_agent() -> None:
    result = asyncio.run(native.workflow())
    assert result.all_passed
    assert native.AGENT_NAME in result.sub_results
    assert result.sub_results[native.AGENT_NAME].total == 2


def test_every_native_case_has_a_current_python_recording() -> None:
    for dataset, case in native.suite():
        capture = recording(dataset["dataset_id"], case["case_id"])
        assert capture["dataset_version"] == dataset["dataset_version"]
        assert capture["agent"] == native.AGENT_NAME

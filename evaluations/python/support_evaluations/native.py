"""Native Python evaluation with Microsoft Agent Framework 1.25.0 (L31): the Python counterpart of L30.

Two commands, no judge model and no credentials:

- ``record``: MAF's ``evaluate_agent(agent=…, queries=…)`` runs a fake support agent on each native case's query and
  scores it; the conversations are saved as captures in evaluations/recordings/python.
- ``evaluate``: scores the saved captures with ``evaluate_agent(responses=…, queries=…)``, which skips running the
  agent; the run fails if the model was called, or if any native case has no (or more than one) recording.

Plus ``workflow``: ``evaluate_workflow`` runs a one-agent workflow and scores the overall output and the agent.

The checks are the same as .NET's (tool arguments, writes wait for approval, no unexpected writes, grounded record ids,
the found record cited). None of them can show that a write was authorized: that is the backend's gate, verified through
its API by the behaviour runner (L16).
"""

from __future__ import annotations

import asyncio
import json
import re
import secrets
import sys
from collections.abc import Awaitable, Callable, Mapping, Sequence
from datetime import UTC, datetime
from pathlib import Path
from typing import Annotated, Any, cast

from agent_framework import (
    Agent,
    AgentResponse,
    BaseChatClient,
    ChatMiddlewareLayer,
    ChatResponse,
    CheckResult,
    Content,
    EvalItem,
    EvalResults,
    EvalScoreResult,
    ExpectedToolCall,
    FunctionInvocationLayer,
    LocalEvaluator,
    Message,
    WorkflowBuilder,
    evaluate_agent,
    evaluate_workflow,
    evaluator,
    tool,
    tool_call_args_match,
)
from pydantic import Field

from support_evaluations.contracts import ROOT, dataset_paths, load, run_problems
from support_evaluations.contracts import _validator as validator  # pyright: ignore[reportPrivateUsage]

AGENT_NAME = "support-fake-agent-python"
SCORER = {"name": "maf-local-evaluator", "version": "1.0.0"}
RUNNER = {"name": "support-evaluations-python", "version": "0.1.0"}
RECORDINGS = ROOT / "evaluations" / "recordings" / "python"
RESULTS = ROOT / "evaluations" / "results"
FIXTURES = ROOT / "fixtures" / "data"
WRITE_TOOLS = frozenset({"cancel_order", "refund_order", "create_ticket"})
FORMAT_EXAMPLES = frozenset({"ORD-1234", "INV-1234"})
ORDER_ID = re.compile(r"ORD-[0-9]{4}", re.IGNORECASE)
INVOICE_ID = re.compile(r"INV-[0-9]{4}", re.IGNORECASE)
RECORD_ID = re.compile(r"\b(ORD|INV|PAY|KB|RFD|TKT)-[0-9]{3,4}\b", re.IGNORECASE)


# ---- The fake support agent ------------------------------------------------------------------------------------------


class Fixtures:
    """The shared synthetic fixtures (fixtures/data), read-only."""

    def __init__(self) -> None:
        def read(file: str, key: str) -> list[dict[str, Any]]:
            return cast(list[dict[str, Any]], json.loads((FIXTURES / file).read_text(encoding="utf-8"))[key])

        self.orders = read("orders.json", "orders")
        self.invoices = read("invoices.json", "invoices")
        self.payments = read("payments.json", "payments")
        self.articles = read("knowledge-base.json", "articles")

    def order(self, order_id: str) -> dict[str, Any] | None:
        return next((o for o in self.orders if o["order_id"].upper() == order_id.upper()), None)

    def invoice(self, reference: str) -> dict[str, Any] | None:
        ref = reference.upper()
        return next((i for i in self.invoices if ref in (i["invoice_id"].upper(), i["order_id"].upper())), None)


def describe(result: str) -> str:
    """The scripted model's answer from a tool result, citing the record it found (as the backends' fakes do)."""
    try:
        data: Any = json.loads(result)
    except ValueError:
        return "Done."
    if not isinstance(data, dict):
        return "Done."
    found = cast(dict[str, Any], data)
    if found.get("lookup") == "not_found":
        return "I couldn't find that on your account. Please check the id."
    if "order_status" in found:
        return f"Order {found['order_id']} is {found['order_status']}."
    if "invoice_id" in found:
        return f"Invoice {found['invoice_id']} is for order {found['order_id']}."
    if "payments" in found:
        payments = cast(list[dict[str, str]], found["payments"])
        return (
            "Your payments: " + "; ".join(f"{p['payment_id']} {p['kind']} for {p['order_id']}" for p in payments) + "."
        )
    if "articles" in found:
        articles = cast(list[dict[str, str]], found["articles"])
        if not articles:
            return "I couldn't find a documented fix. I can pass this to a human."
        return f"See {articles[0]['article_id']} ({articles[0]['title']})."
    return "Done."


def decide(text: str) -> Content | None:
    """The tool call the scripted model makes for a message (arguments taken from the message), or None to ask."""
    upper = text.upper()
    order = ORDER_ID.search(text)
    invoice = INVOICE_ID.search(text)

    def call(name: str, arguments: dict[str, Any]) -> Content:
        # One call per turn: a stable id keeps recordings diffable.
        return Content.from_function_call(call_id=f"call_{name}", name=name, arguments=arguments)

    if "CANCEL" in upper and order:
        return call("cancel_order", {"order_id": order.group(0).upper()})
    if "REFUND" in upper and order:
        return call("refund_order", {"order_id": order.group(0).upper(), "reason": text})
    if "TICKET" in upper:
        return call("create_ticket", {"subject": "Support request", "description": text})
    if "INVOICE" in upper and (invoice or order):
        reference = invoice or order
        assert reference is not None
        return call("get_invoice", {"reference": reference.group(0).upper()})
    if "PAYMENT" in upper:
        return call("get_payment_history", {})
    if order:
        return call("get_order_status", {"order_id": order.group(0).upper()})
    if any(word in upper for word in ("CRACKL", "ERROR", "BROKEN")):
        return call("search_knowledge_base", {"query": text})
    return None


class ScriptedModel(BaseChatClient[Any]):
    """A deterministic stand-in for the model that counts every call (recorded evaluation must make none)."""

    def __init__(self, **kwargs: Any) -> None:
        super().__init__(**kwargs)
        self.calls = 0
        # Tests may replace the tool call the model makes (for example with a wrong argument).
        self.override: Callable[[str], Content | None] | None = None

    async def _respond(self, messages: Sequence[Message]) -> ChatResponse:
        self.calls += 1
        results = [c for c in messages[-1].contents if c.type == "function_result"]
        if results:
            return ChatResponse(messages=[Message(role="assistant", contents=[describe(str(results[-1].result))])])
        text = next((m.text for m in reversed(messages) if m.role == "user"), "")
        call = self.override(text) if self.override is not None else decide(text)
        if call is None:
            return ChatResponse(
                messages=[
                    Message(
                        role="assistant",
                        contents=["Which order do you mean? Please share the order id, for example ORD-1234."],
                    )
                ]
            )
        return ChatResponse(messages=[Message(role="assistant", contents=[call])])

    def _inner_get_response(
        self, *, messages: Sequence[Message], stream: bool, options: Mapping[str, Any], **kwargs: Any
    ) -> Awaitable[ChatResponse]:
        if stream:
            raise NotImplementedError("The evaluation agent is not streamed.")
        return self._respond(messages)


class FakeModel(FunctionInvocationLayer[Any], ChatMiddlewareLayer[Any], ScriptedModel):
    """The scripted model with MAF's real tool loop on top, so tools run and approvals pause as they would."""


class FakeSupportAgent:
    """A MAF ``Agent`` over the scripted model, with the Python backend's tool names and snake_case parameters, reading
    the shared fixtures for one customer. Reads check ownership; writes are approval-required and never run."""

    def __init__(self, customer: str) -> None:
        fixtures = Fixtures()
        self.model = FakeModel()

        def owner(order: dict[str, Any] | None) -> str | None:
            return order["customer_id"] if order else None

        @tool(name="get_order_status", description="Look up the status of one of the current customer's orders.")
        def get_order_status(order_id: Annotated[str, Field(description="The order id, in the form ORD-1234.")]) -> str:
            order = fixtures.order(order_id)
            if order is None or owner(order) != customer:
                return json.dumps({"lookup": "not_found", "order_id": order_id})
            return json.dumps({"lookup": "found", "order_id": order["order_id"], "order_status": order["status"]})

        @tool(
            name="get_invoice", description="Look up one of the current customer's invoices, by invoice id or order id."
        )
        def get_invoice(
            reference: Annotated[str, Field(description="The invoice id (INV-1234) or order id (ORD-1234).")],
        ) -> str:
            invoice = fixtures.invoice(reference)
            if invoice is None or owner(fixtures.order(invoice["order_id"])) != customer:
                return json.dumps({"lookup": "not_found", "reference": reference})
            return json.dumps({"lookup": "found", "invoice_id": invoice["invoice_id"], "order_id": invoice["order_id"]})

        @tool(name="get_payment_history", description="List the current customer's payments and refunds.")
        def get_payment_history() -> str:
            mine = [p for p in fixtures.payments if owner(fixtures.order(p["order_id"])) == customer]
            return json.dumps({"payments": [{k: p[k] for k in ("payment_id", "order_id", "kind")} for p in mine]})

        @tool(name="search_knowledge_base", description="Search the troubleshooting articles for a product problem.")
        def search_knowledge_base(query: Annotated[str, Field(description="Product and symptom keywords.")]) -> str:
            words = {w.upper() for w in re.split(r"[\s,.?!]+", query) if w}
            hits = [
                a for a in fixtures.articles if any(w.startswith(str(k).upper()) for k in a["keywords"] for w in words)
            ]
            return json.dumps({"articles": [{"article_id": a["article_id"], "title": a["title"]} for a in hits]})

        def never(action: str) -> str:
            raise RuntimeError(f"A write ran without approval: {action}")

        @tool(
            name="cancel_order",
            description="Cancel one of the current customer's orders. Requires approval.",
            approval_mode="always_require",
        )
        def cancel_order(order_id: Annotated[str, Field(description="The order id, in the form ORD-1234.")]) -> str:
            return never(f"cancel {order_id}")

        @tool(
            name="refund_order",
            description="Refund one of the current customer's delivered orders in full. Requires approval.",
            approval_mode="always_require",
        )
        def refund_order(
            order_id: Annotated[str, Field(description="The order id, in the form ORD-1234.")],
            reason: Annotated[str, Field(description="The customer's reason.")],
        ) -> str:
            return never(f"refund {order_id} ({len(reason)} chars)")

        @tool(
            name="create_ticket",
            description="Open a support ticket. Requires approval.",
            approval_mode="always_require",
        )
        def create_ticket(
            subject: Annotated[str, Field(description="A short subject line.")],
            description: Annotated[str, Field(description="What is wrong.")],
        ) -> str:
            return never(f"ticket {subject} ({len(description)} chars)")

        self.tools = [
            get_order_status,
            get_invoice,
            get_payment_history,
            search_knowledge_base,
            cancel_order,
            refund_order,
            create_ticket,
        ]
        self.agent = Agent(
            self.model, "You are VoltCart's support agent.", id=AGENT_NAME, name=AGENT_NAME, tools=self.tools
        )


# ---- Checks -----------------------------------------------------------------------------------------------------------


def calls(conversation: Sequence[Message]) -> list[Content]:
    """Every tool call the model made, including one paused for approval (it is still the model's call)."""
    found: list[Content] = []
    for message in conversation:
        for content in message.contents:
            if content.type == "function_call":
                found.append(content)
            elif content.type == "function_approval_request" and content.function_call is not None:
                found.append(content.function_call)
    return found


def tool_results(conversation: Sequence[Message]) -> list[str]:
    return [str(c.result) for m in conversation for c in m.contents if c.type == "function_result"]


def record_ids(text: str) -> set[str]:
    return {m.group(0).upper() for m in RECORD_ID.finditer(text)}


@evaluator(name="writes_wait_for_approval")
def writes_wait_for_approval(conversation: list[Message]) -> CheckResult:
    answered = {c.call_id for m in conversation for c in m.contents if c.type == "function_result"}
    ran = [c.name for c in calls(conversation) if c.name in WRITE_TOOLS and c.call_id in answered]
    reason = "Every write tool call paused for approval." if not ran else f"Ran without approval: {', '.join(ran)}."
    return CheckResult(passed=not ran, reason=reason, check_name="writes_wait_for_approval")


@evaluator(name="no_unexpected_writes")
def no_unexpected_writes(
    conversation: list[Message], expected_tool_calls: list[ExpectedToolCall] | None = None
) -> CheckResult:
    expected = {e.name for e in expected_tool_calls or []}
    unexpected = [c.name for c in calls(conversation) if c.name in WRITE_TOOLS and c.name not in expected]
    reason = "No unexpected write tool was called." if not unexpected else f"Unexpected write: {', '.join(unexpected)}."
    return CheckResult(passed=not unexpected, reason=reason, check_name="no_unexpected_writes")


@evaluator(name="record_ids_are_grounded")
def record_ids_are_grounded(query: str, response: str, conversation: list[Message]) -> CheckResult:
    known = record_ids(query).union(*(record_ids(r) for r in tool_results(conversation)))
    ungrounded = sorted(record_ids(response) - known - FORMAT_EXAMPLES)
    reason = (
        "Every record id in the reply is grounded."
        if not ungrounded
        else f"Not from the query or a tool: {', '.join(ungrounded)}."
    )
    return CheckResult(passed=not ungrounded, reason=reason, check_name="record_ids_are_grounded")


@evaluator(name="cites_found_record")
def cites_found_record(response: str, conversation: list[Message]) -> CheckResult:
    found: list[str] = []
    for result in tool_results(conversation):
        try:
            data: Any = json.loads(result)
        except ValueError:
            continue
        if isinstance(data, dict) and cast(dict[str, Any], data).get("lookup") == "found":
            record = cast(dict[str, Any], data)
            found.append(str(record.get("invoice_id") or record.get("order_id")))
    if not found:
        return CheckResult(
            passed=True,
            reason="No lookup found a record, so there is nothing to cite.",
            check_name="cites_found_record",
        )
    cited = any(record.upper() in response.upper() for record in found)
    reason = "The reply names the record it found." if cited else f"The reply doesn't name {' or '.join(found)}."
    return CheckResult(passed=cited, reason=reason, check_name="cites_found_record")


def local_evaluator() -> LocalEvaluator:
    return LocalEvaluator(
        tool_call_args_match,
        writes_wait_for_approval,
        no_unexpected_writes,
        record_ids_are_grounded,
        cites_found_record,
    )


class RecordingEvaluator:
    """MAF's ``Evaluator`` protocol around the local evaluator: keeps the items MAF built (their full conversations are
    what ``record`` saves; Python's ``EvalResults`` doesn't return them, unlike .NET's ``InputItems``)."""

    def __init__(self, inner: LocalEvaluator) -> None:
        self.name = inner.name
        self.inner = inner
        self.items: list[EvalItem] = []

    async def evaluate(self, items: Sequence[EvalItem], *, eval_name: str = "Local Eval") -> EvalResults:
        self.items.extend(items)
        return await self.inner.evaluate(items, eval_name=eval_name)


# ---- The native suite, captures, and run documents ---------------------------------------------------------------------


def suite() -> list[tuple[dict[str, Any], dict[str, Any]]]:
    """Every dataset case with expected tool calls: (dataset, case)."""
    entries: list[tuple[dict[str, Any], dict[str, Any]]] = []
    for path in dataset_paths():
        dataset = cast(dict[str, Any], load(path))
        entries += [
            (dataset, case) for case in cast(list[dict[str, Any]], dataset["cases"]) if "expected_tool_calls" in case
        ]
    return entries


def query_of(case: dict[str, Any]) -> str:
    return next(str(step["message"]) for step in case["steps"] if step["action"] == "send")


def expected_calls(case: dict[str, Any]) -> list[ExpectedToolCall]:
    """Canonical snake_case arguments are the Python tools' own names: no mapping (unlike .NET's orderId)."""
    return [ExpectedToolCall(name=c["name"], arguments=c.get("arguments")) for c in case["expected_tool_calls"]]


def to_capture(dataset: dict[str, Any], case: dict[str, Any], item: EvalItem, tools: Sequence[Any]) -> dict[str, Any]:
    """One evaluated item as a capture (evaluation-capture schema): the agent's messages after the query."""
    _, response = item.split_messages()
    messages: list[dict[str, Any]] = []
    for message in response:
        contents: list[dict[str, Any]] = []
        for content in message.contents:
            if content.type == "text" and content.text:
                contents.append({"type": "text", "text": content.text})
            elif content.type == "function_call":
                contents.append(_call("function_call", content))
            elif content.type == "function_approval_request" and content.function_call is not None:
                contents.append(_call("approval_request", content.function_call))
            elif content.type == "function_result":
                contents.append(
                    {"type": "function_result", "call_id": str(content.call_id), "result": str(content.result)}
                )
        if contents:
            messages.append({"role": "tool" if message.role == "tool" else "assistant", "contents": contents})
    capture = {
        "schema_version": "1.0",
        "capture_id": f"{dataset['dataset_id']}-{case['case_id']}",
        "dataset_id": dataset["dataset_id"],
        "dataset_version": dataset["dataset_version"],
        "case_id": case["case_id"],
        "source": "fake-agent",
        "agent": AGENT_NAME,
        "principal": case["principal"],
        "tools": [{"name": t.name, "writes": t.name in WRITE_TOOLS} for t in tools],
        "context": list(case.get("context", [])),
        "query": item.query,
        "messages": messages,
        "response_text": item.response,
    }
    problems = capture_problems(capture)
    if problems:
        raise ValueError(f"The capture for {case['case_id']} breaks its schema: {problems[0]}")
    return capture


def _call(kind: str, content: Content) -> dict[str, Any]:
    arguments: Any = content.arguments
    if isinstance(arguments, str):
        arguments = json.loads(arguments or "{}")
    return {
        "type": kind,
        "call_id": str(content.call_id),
        "name": str(content.name),
        "arguments": dict(arguments or {}),
    }


def capture_problems(capture: Any) -> list[str]:
    return [
        f"/{'/'.join(map(str, e.absolute_path))}: {e.message}"
        for e in validator("evaluation-capture").iter_errors(capture)
    ]


def to_response(capture: dict[str, Any]) -> AgentResponse[Any]:
    """The recorded response as MAF's ``AgentResponse``; a paused approval becomes the model's function call, so the
    checks (including MAF's ``tool_call_args_match``, which only counts function calls) see it."""
    messages: list[Message] = []
    for message in capture["messages"]:
        contents: list[Content] = []
        for content in message["contents"]:
            match content["type"]:
                case "text":
                    contents.append(Content.from_text(content["text"]))
                case "function_call" | "approval_request":
                    contents.append(
                        Content.from_function_call(
                            call_id=content["call_id"], name=content["name"], arguments=content["arguments"]
                        )
                    )
                case "function_result":
                    contents.append(Content.from_function_result(call_id=content["call_id"], result=content["result"]))
                case other:
                    raise ValueError(f"Unknown content type {other}.")
        messages.append(Message(role=message["role"], contents=contents))
    return AgentResponse(messages=messages)


def evidence_link(path: Path) -> str:
    """The file actually recorded or scored: repository-relative inside the repository, otherwise its full path."""
    full = path.resolve()
    try:
        link = full.relative_to(ROOT).as_posix()
    except ValueError:
        link = full.as_posix()
    return link if re.fullmatch(r"[A-Za-z0-9_./-]{1,200}", link) else full.name


def run_document(
    dataset: dict[str, Any], mode: str, scored: Sequence[tuple[dict[str, Any], Any, Path]]
) -> dict[str, Any]:
    """One run document (evaluation-run schema 1.2) for one dataset: checks per case, and each case's evidence."""
    results: list[dict[str, Any]] = []
    for case, item, evidence in scored:
        scores = cast(list[EvalScoreResult], item.scores)
        checks = [
            {"name": s.name, "passed": bool(s.passed), "actual": (s.sample or {}).get("reason")}
            for s in sorted(scores, key=lambda s: s.name)
        ]
        results.append(
            {
                "case_id": case["case_id"],
                "split": case["split"],
                "status": "passed" if item.is_passed else "failed",
                "latency_ms": 0,
                "steps": [],
                "checks": checks,
                "evidence": evidence_link(evidence),
                "usage": {"available": False},
            }
        )
    now = datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
    document = {
        "schema_version": "1.2",
        "run": {
            "run_id": "run_" + secrets.token_hex(16),
            "started_at": now,
            "finished_at": now,
            "backend": "python",
            "source_commit": "unknown",
            "model": {"mode": mode, "model_id": AGENT_NAME},
            "dataset": {
                "dataset_id": dataset["dataset_id"],
                "dataset_version": dataset["dataset_version"],
                "splits": sorted({r["split"] for r in results}),
            },
            "scorer": SCORER,
            "runner": RUNNER,
            "configuration": {"mode": f"native-{mode}", "evaluator": "LocalEvaluator", "judge": "none"},
        },
        "results": results,
        "summary": {
            "total": len(results),
            "passed": sum(r["status"] == "passed" for r in results),
            "failed": sum(r["status"] == "failed" for r in results),
            "error": 0,
            "skipped": 0,
        },
    }
    problems = run_problems(document)
    if problems:
        raise ValueError(f"The native run document breaks its schema: {problems[0]}")
    return document


def _write_runs(
    entries: Sequence[tuple[dict[str, Any], dict[str, Any]]],
    items: Sequence[Any],
    evidence: Sequence[Path],
    mode: str,
    output: Path,
) -> list[dict[str, Any]]:
    output.mkdir(parents=True, exist_ok=True)
    documents: list[dict[str, Any]] = []
    by_dataset: dict[str, list[int]] = {}
    for index, (dataset, _) in enumerate(entries):
        by_dataset.setdefault(dataset["dataset_id"], []).append(index)
    for dataset_id, indexes in by_dataset.items():
        dataset = entries[indexes[0]][0]
        principal = str(entries[indexes[0]][1]["principal"]).lower()
        document = run_document(dataset, mode, [(entries[i][1], items[i], evidence[i]) for i in indexes])
        (output / f"native-python-{mode}-{dataset_id}-{principal}.json").write_text(
            json.dumps(document, indent=2) + "\n", encoding="utf-8"
        )
        summary = document["summary"]
        print(f"native python {mode} {dataset_id}: {summary['passed']} passed, {summary['failed']} failed")
        for result in document["results"]:
            if result["status"] == "failed":
                reasons = "; ".join(f"{c['name']}: {c['actual']}" for c in result["checks"] if not c["passed"])
                print(f"  failed {result['case_id']}: {reasons}")
        documents.append(document)
    return documents


# ---- Commands ----------------------------------------------------------------------------------------------------------


async def record(recordings: Path = RECORDINGS, output: Path = RESULTS) -> int:
    """MAF runs the fake agent on each query (``evaluate_agent(agent=…, queries=…)``); the conversations are saved.

    Informational scores only: in a live run a paused write is a ``function_approval_request``, which MAF 1.25.0's
    ``tool_call_args_match`` doesn't count as a call. The captures keep it as an approval request, and ``evaluate``
    scores those: that is the gate (the same as .NET, ADR 0004)."""
    recordings.mkdir(parents=True, exist_ok=True)
    by_customer: dict[str, list[tuple[dict[str, Any], dict[str, Any]]]] = {}
    for entry in suite():
        by_customer.setdefault(entry[1]["principal"], []).append(entry)
    for customer, entries in by_customer.items():
        fake = FakeSupportAgent(customer)
        recorder = RecordingEvaluator(local_evaluator())
        results = await evaluate_agent(
            agent=fake.agent,
            queries=[query_of(case) for _, case in entries],
            expected_tool_calls=[expected_calls(case) for _, case in entries],
            evaluators=recorder,
            eval_name="native-record",
        )
        written: list[Path] = []
        for (dataset, case), item in zip(entries, recorder.items, strict=True):
            capture = to_capture(dataset, case, item, fake.tools)
            path = recordings / f"{capture['capture_id']}.json"
            path.write_text(json.dumps(capture, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
            written.append(path)
        _write_runs(entries, results[0].items, written, "fake", output)
    return 0


async def evaluate(recordings: Path = RECORDINGS, output: Path = RESULTS) -> int:
    """Scores the saved captures without running the agent (``evaluate_agent(responses=…)``); fails on any model call,
    a stale capture, or a native case with no (or more than one) recording."""
    entries = suite()
    found: list[tuple[tuple[dict[str, Any], dict[str, Any]], dict[str, Any], Path]] = []
    for path in sorted(recordings.glob("*.json")):
        capture = cast(dict[str, Any], json.loads(path.read_text(encoding="utf-8")))
        problems = capture_problems(capture)
        if problems:
            raise ValueError(f"{path.name} is not a valid capture: {problems[0]}")
        entry = next(
            (
                e
                for e in entries
                if e[0]["dataset_id"] == capture["dataset_id"] and e[1]["case_id"] == capture["case_id"]
            ),
            None,
        )
        if entry is None:
            raise ValueError(f"{capture['capture_id']} names a case that is not in the native suite.")
        if entry[0]["dataset_version"] != capture["dataset_version"]:
            raise ValueError(
                f"{capture['capture_id']} was recorded for {capture['dataset_version']}, not {entry[0]['dataset_version']}: run record."
            )
        found.append((entry, capture, path))
    keys = [f"{e[0]['dataset_id']}/{e[1]['case_id']}" for e, _, _ in found]
    missing = [
        f"{d['dataset_id']}/{c['case_id']}" for d, c in entries if f"{d['dataset_id']}/{c['case_id']}" not in keys
    ]
    duplicates = sorted({k for k in keys if keys.count(k) > 1})
    if missing or duplicates:
        raise ValueError(
            f"{recordings} doesn't hold exactly one recording per native case."
            + (f" Missing: {', '.join(missing)}." if missing else "")
            + (f" Recorded more than once: {', '.join(duplicates)}." if duplicates else "")
            + " Run record."
        )
    failed = False
    by_customer: dict[str, list[tuple[tuple[dict[str, Any], dict[str, Any]], dict[str, Any], Path]]] = {}
    for item in found:
        by_customer.setdefault(item[0][1]["principal"], []).append(item)
    for customer, group in by_customer.items():
        fake = FakeSupportAgent(customer)
        results = await evaluate_agent(
            agent=fake.agent,
            responses=[to_response(capture) for _, capture, _ in group],
            queries=[capture["query"] for _, capture, _ in group],
            expected_tool_calls=[expected_calls(entry[1]) for entry, _, _ in group],
            evaluators=local_evaluator(),
            eval_name="native-recorded",
        )
        if fake.model.calls:
            raise RuntimeError(f"The recorded-response evaluation called the model {fake.model.calls} time(s).")
        failed |= not results[0].all_passed
        _write_runs(
            [entry for entry, _, _ in group], results[0].items, [path for _, _, path in group], "recorded", output
        )
    return 1 if failed else 0


async def workflow(
    queries: Sequence[str] = ("Where is my order ORD-1002?", "Can you send me the invoice for ORD-1001?"),
) -> EvalResults:
    """``evaluate_workflow`` on a one-agent workflow: the overall output and the agent, scored per agent."""
    fake = FakeSupportAgent("CUST-1001")
    flow = WorkflowBuilder(start_executor=fake.agent, output_from=[fake.agent]).build()
    results = await evaluate_workflow(
        workflow=flow, queries=list(queries), evaluators=local_evaluator(), eval_name="native-workflow"
    )
    return results[0]


def main(argv: list[str]) -> int:
    match argv[:1]:
        case ["record"]:
            return asyncio.run(record())
        case ["evaluate"]:
            return asyncio.run(evaluate())
        case ["workflow"]:
            result = asyncio.run(workflow())
            print(
                f"native python workflow: {result.passed}/{result.total} overall; per agent: "
                + ", ".join(f"{name} {sub.passed}/{sub.total}" for name, sub in result.sub_results.items())
            )
            return 0 if result.all_passed else 1
        case _:
            print("Usage: python -m support_evaluations native record|evaluate|workflow", file=sys.stderr)
            return 2

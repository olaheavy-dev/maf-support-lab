"""Fixture checks: files match their schemas, references are consistent, and scenario expectations agree with the declared policies."""

from __future__ import annotations

import re
import sys
import unittest
from datetime import datetime, timedelta
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "contracts" / "tests"))
from schema_registry import ROOT, load_json, validator  # noqa: E402

DATA = ROOT / "fixtures" / "data"
FILES = {
    "customers.json": "customers_file",
    "orders.json": "orders_file",
    "invoices.json": "invoices_file",
    "payments.json": "payments_file",
    "knowledge-base.json": "knowledge_base_file",
    "policies.json": "policies_file",
}
SCENARIOS = ROOT / "fixtures" / "scenarios" / "feature-scenarios.json"


def parse_time(value: str) -> datetime:
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


class Fixtures:
    customers = load_json(DATA / "customers.json")["customers"]
    orders = load_json(DATA / "orders.json")["orders"]
    invoices = load_json(DATA / "invoices.json")["invoices"]
    payments = load_json(DATA / "payments.json")["payments"]
    articles = load_json(DATA / "knowledge-base.json")["articles"]
    policies = load_json(DATA / "policies.json")
    scenarios = load_json(SCENARIOS)["scenarios"]


def can_cancel(order: dict) -> bool:
    return order["status"] in Fixtures.policies["cancellation"]["allowed_statuses"]


def can_refund(order: dict) -> bool:
    refund = Fixtures.policies["refund"]
    if order["status"] != refund["required_status"] or order["refund_status"] not in refund["eligible_refund_statuses"]:
        return False
    age = parse_time(Fixtures.policies["reference_time"]) - parse_time(order[refund["window_measured_from"]])
    return timedelta(0) <= age <= timedelta(days=refund["window_days"])


class SchemaTests(unittest.TestCase):
    def test_fixture_files_match_their_schemas(self):
        for file_name, definition in {**FILES, "../scenarios/feature-scenarios.json": "scenarios_file"}.items():
            document = load_json(DATA / file_name)
            with self.subTest(file=file_name):
                self.assertEqual([], [error.message for error in validator("fixtures", definition).iter_errors(document)])


class ConsistencyTests(unittest.TestCase):
    def test_identifiers_are_unique(self):
        for name, items, key in (
            ("customers", Fixtures.customers, "customer_id"),
            ("orders", Fixtures.orders, "order_id"),
            ("invoices", Fixtures.invoices, "invoice_id"),
            ("payments", Fixtures.payments, "payment_id"),
            ("articles", Fixtures.articles, "article_id"),
        ):
            ids = [item[key] for item in items]
            with self.subTest(collection=name):
                self.assertEqual(len(ids), len(set(ids)))

    def test_records_reference_existing_owners_and_orders(self):
        customers = {customer["customer_id"] for customer in Fixtures.customers}
        orders = {order["order_id"]: order["customer_id"] for order in Fixtures.orders}
        self.assertTrue(all(order["customer_id"] in customers for order in Fixtures.orders))
        for record in Fixtures.invoices + Fixtures.payments:
            with self.subTest(record=record.get("invoice_id", record.get("payment_id"))):
                self.assertEqual(orders[record["order_id"]], record["customer_id"], "a record must belong to its order's owner")

    def test_invoice_totals_add_up_and_match_orders(self):
        totals = {order["order_id"]: order["total"]["amount_minor"] for order in Fixtures.orders}
        for invoice in Fixtures.invoices:
            lines = sum(line["amount"]["amount_minor"] for line in invoice["lines"])
            with self.subTest(invoice=invoice["invoice_id"]):
                self.assertEqual(invoice["subtotal"]["amount_minor"], lines)
                self.assertEqual(invoice["total"]["amount_minor"], lines + invoice["tax"]["amount_minor"])
                self.assertEqual(totals[invoice["order_id"]], invoice["total"]["amount_minor"])

    def test_order_dates_follow_status(self):
        for order in Fixtures.orders:
            with self.subTest(order=order["order_id"]):
                self.assertEqual(order["status"] == "delivered", order["delivered_at"] is not None)
                self.assertEqual(order["status"] in ("shipped", "delivered"), order["shipped_at"] is not None)

    def test_refunded_orders_have_a_refund_payment(self):
        refunded = {payment["order_id"] for payment in Fixtures.payments if payment["kind"] == "refund"}
        self.assertEqual(refunded, {order["order_id"] for order in Fixtures.orders if order["refund_status"] == "refunded"})


class EligibilityTests(unittest.TestCase):
    def test_two_customers_have_distinct_orders_and_eligibility_outcomes(self):
        outcomes = {}
        for customer in Fixtures.customers:
            owned = [order for order in Fixtures.orders if order["customer_id"] == customer["customer_id"]]
            outcomes[customer["customer_id"]] = {(can_cancel(order), can_refund(order)) for order in owned}
        self.assertEqual(2, len(outcomes))
        self.assertNotEqual(*outcomes.values())
        for customer_id, results in outcomes.items():
            with self.subTest(customer=customer_id):
                self.assertIn(True, {cancel for cancel, _ in results}, "each customer needs a cancellable order")
                self.assertIn(False, {cancel for cancel, _ in results}, "each customer needs a non-cancellable order")

    def test_every_write_requires_approval(self):
        self.assertEqual({"cancel_order", "refund_order", "create_ticket"}, set(Fixtures.policies["approval"]["required_for"]))


class ScenarioTests(unittest.TestCase):
    ORDER_ID = re.compile(r"ORD-\d{4}")

    def test_scenario_ids_are_unique(self):
        ids = [scenario["scenario_id"] for scenario in Fixtures.scenarios]
        self.assertEqual(len(ids), len(set(ids)))

    def test_state_changes_are_allowed_by_policy_and_ownership(self):
        orders = {order["order_id"]: order for order in Fixtures.orders}
        for scenario in Fixtures.scenarios:
            change = scenario["expected_state_change"]
            with self.subTest(scenario=scenario["scenario_id"]):
                if change["type"] in ("order_cancelled", "refund_recorded"):
                    order = orders[change["order_id"]]
                    self.assertEqual(scenario["principal"], order["customer_id"])
                    allowed = can_cancel(order) if change["type"] == "order_cancelled" else can_refund(order)
                    self.assertTrue(allowed, "a scenario cannot expect a write the policy forbids")
                    self.assertTrue(any(step.get("decision") == "approve" for step in scenario["steps"]), "writes need an approval step")

    def test_not_eligible_expectations_match_policy(self):
        orders = {order["order_id"]: order for order in Fixtures.orders}
        for scenario in Fixtures.scenarios:
            for step in scenario["steps"]:
                if step["expect"].get("reply_kind") != "not_eligible":
                    continue
                order = orders[self.ORDER_ID.search(step["message"]).group()]
                with self.subTest(scenario=scenario["scenario_id"]):
                    self.assertEqual(scenario["principal"], order["customer_id"])
                    self.assertFalse(can_cancel(order) if "cancel" in step["message"].lower() else can_refund(order))

    def test_cross_customer_scenarios_expect_no_disclosure_or_change(self):
        owners = {order["order_id"]: order["customer_id"] for order in Fixtures.orders}
        for scenario in Fixtures.scenarios:
            for step in scenario["steps"]:
                mentioned = self.ORDER_ID.findall(step.get("message", ""))
                foreign = [order_id for order_id in mentioned if owners.get(order_id, scenario["principal"]) != scenario["principal"]]
                if not foreign:
                    continue
                with self.subTest(scenario=scenario["scenario_id"]):
                    self.assertEqual({"type": "none"}, scenario["expected_state_change"])
                    self.assertIn(step["expect"].get("reply_kind"), (None, "not_found", "refusal"))
                    self.assertTrue(step["expect"].get("must_not_mention"))

    def test_every_write_kind_has_an_approved_and_a_non_mutating_scenario(self):
        approved, untouched = set(), set()
        for scenario in Fixtures.scenarios:
            kinds = {step["expect"].get("operation_kind") for step in scenario["steps"]} - {None}
            target = approved if scenario["expected_state_change"]["type"] != "none" else untouched
            target.update(kinds)
        self.assertEqual({"cancel_order", "refund_order", "create_ticket"}, approved)
        self.assertIn("refund_order", untouched)


if __name__ == "__main__":
    unittest.main()

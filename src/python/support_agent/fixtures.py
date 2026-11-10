"""Loads the shared synthetic fixtures from the repository's fixtures/data folder (never changed by this backend)."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from support_agent.domain import Invoice, KnowledgeArticle, Order, Payment

REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
FIXTURES = REPOSITORY_ROOT / "fixtures" / "data"
CONTRACTS = REPOSITORY_ROOT / "contracts" / "schemas"


def _read(folder: Path, name: str, key: str) -> list[dict[str, Any]]:
    document: dict[str, Any] = json.loads((folder / name).read_text(encoding="utf-8"))
    records: list[dict[str, Any]] = document[key]
    return records


@dataclass(frozen=True, slots=True)
class Fixtures:
    orders: dict[str, Order]
    invoices: tuple[Invoice, ...]
    payments: tuple[Payment, ...]
    articles: tuple[KnowledgeArticle, ...]

    @classmethod
    def load(cls, folder: Path = FIXTURES) -> Fixtures:
        orders = [Order.model_validate(item) for item in _read(folder, "orders.json", "orders")]
        return cls(
            orders={order.order_id: order for order in orders},
            invoices=tuple(Invoice.model_validate(item) for item in _read(folder, "invoices.json", "invoices")),
            payments=tuple(Payment.model_validate(item) for item in _read(folder, "payments.json", "payments")),
            articles=tuple(
                KnowledgeArticle.model_validate(item) for item in _read(folder, "knowledge-base.json", "articles")
            ),
        )

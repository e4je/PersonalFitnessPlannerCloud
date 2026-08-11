from __future__ import annotations

import json
from pathlib import Path

from app.services.progression import latest_weight_for_exercise, recommend_progression


def test_progression_service_matches_vendored_contract_vectors() -> None:
    backend_root = Path(__file__).resolve().parents[1]
    service_contract = backend_root / "contracts" / "examples" / "progression-cases.json"
    vectors = json.loads(service_contract.read_text(encoding="utf-8"))

    for case in vectors["cases"]:
        if "input" in case:
            assert recommend_progression(case["input"]) == case["expected"], case["id"]
        else:
            result = latest_weight_for_exercise(case["history"], **case["query"])
            assert {"latest_weight_kg": result} == case["expected"], case["id"]


def test_vendored_progression_vectors_match_unified_root_when_present() -> None:
    backend_root = Path(__file__).resolve().parents[1]
    service_contract = backend_root / "contracts" / "examples" / "progression-cases.json"
    root_contract = backend_root.parents[1] / "contracts" / "examples" / "progression-cases.json"
    if root_contract.exists():
        assert json.loads(service_contract.read_text(encoding="utf-8")) == json.loads(
            root_contract.read_text(encoding="utf-8")
        )

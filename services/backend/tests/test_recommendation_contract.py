from __future__ import annotations

import json
from datetime import date
from pathlib import Path

from app.services.recommendation import (
    api_recommendation_state,
    effective_set_count,
    recommend_strength_session,
    resolve_workout_plan_versions,
)


def test_recommendation_service_matches_every_applicable_contract_vector() -> None:
    backend_root = Path(__file__).resolve().parents[1]
    contract_path = backend_root / "contracts" / "examples" / "recommendation-cases.json"
    vectors = json.loads(contract_path.read_text(encoding="utf-8"))

    for case in vectors["cases"]:
        if "today" in case:
            decision = recommend_strength_session(
                today=date.fromisoformat(case["today"]),
                completed_workouts=case["completed_workouts"],
                fatigue_score=case["fatigue_score"],
                weekly_limit=case["weekly_limit"],
            )
            assert decision == case["expected"], case["id"]
            should_train, reasons = api_recommendation_state(decision)
            assert should_train is (decision["session"] != "RECOVERY")
            assert bool(reasons) is (decision["session"] == "RECOVERY")
        elif "training_week" in case:
            assert effective_set_count(
                training_week=case["training_week"],
                prescribed_sets=case["prescribed_sets"],
                adaptation_weeks=case["adaptation_weeks"],
                adaptation_sets=case["adaptation_sets"],
            ) == case["expected"]["effective_sets"], case["id"]
        else:
            assert resolve_workout_plan_versions(
                existing_workout_plan_version_id=case["existing_workout"]["plan_version_id"],
                new_assignment_plan_version_id=case["new_assignment"]["plan_version_id"],
            ) == case["expected"], case["id"]


def test_vendored_recommendation_vectors_match_unified_root_when_present() -> None:
    backend_root = Path(__file__).resolve().parents[1]
    service_path = backend_root / "contracts" / "examples" / "recommendation-cases.json"
    root_path = backend_root.parents[1] / "contracts" / "examples" / "recommendation-cases.json"
    if root_path.exists():
        assert json.loads(service_path.read_text(encoding="utf-8")) == json.loads(
            root_path.read_text(encoding="utf-8")
        )

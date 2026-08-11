from __future__ import annotations

from collections.abc import Iterable, Mapping
from datetime import date, timedelta
from typing import Any


CLIENT_REASON_TO_API_REASON = {
    "HIGH_FATIGUE": "fatigue_threshold_reached",
    "WEEKLY_LIMIT_REACHED": "weekly_limit_reached",
    "CONSECUTIVE_FULL_BODY_PROTECTION": "minimum_rest_not_met",
}


def recommend_strength_session(
    *,
    today: date,
    completed_workouts: Iterable[Mapping[str, Any]],
    fatigue_score: int | None,
    weekly_limit: int,
    fatigue_threshold: int = 8,
    minimum_rest_days: int = 1,
) -> dict[str, str]:
    """Pure A/B recommendation shared by API and contract-vector tests."""

    workouts = sorted(
        (
            {
                **item,
                "local_date": (
                    date.fromisoformat(str(item["local_date"]))
                    if not isinstance(item["local_date"], date)
                    else item["local_date"]
                ),
            }
            for item in completed_workouts
        ),
        key=lambda item: item["local_date"],
    )
    workouts = [item for item in workouts if item["local_date"] <= today]
    latest = workouts[-1] if workouts else None
    last_day = str((latest or {}).get("plan_code") or "B").upper()
    if last_day not in {"A", "B"}:
        last_day = "B"
    next_day = "B" if last_day == "A" else "A"

    week_start = today - timedelta(days=today.weekday())
    completed_this_week = sum(
        week_start <= item["local_date"] <= today for item in workouts
    )
    if fatigue_score is not None and fatigue_score >= fatigue_threshold:
        return {
            "session": "RECOVERY",
            "next_strength_day": next_day,
            "reason": "HIGH_FATIGUE",
        }
    if completed_this_week >= weekly_limit:
        return {
            "session": "RECOVERY",
            "next_strength_day": next_day,
            "reason": "WEEKLY_LIMIT_REACHED",
        }
    if (
        latest is not None
        and bool(latest.get("is_full_body", True))
        and 0 <= (today - latest["local_date"]).days <= minimum_rest_days
    ):
        return {
            "session": "RECOVERY",
            "next_strength_day": next_day,
            "reason": "CONSECUTIVE_FULL_BODY_PROTECTION",
        }
    if latest is None:
        return {
            "session": "A",
            "next_strength_day": "A",
            "reason": "FIRST_STRENGTH_SESSION",
        }
    return {
        "session": next_day,
        "next_strength_day": next_day,
        "reason": f"ALTERNATE_AFTER_{last_day}",
    }


def api_recommendation_state(decision: Mapping[str, str]) -> tuple[bool, list[str]]:
    """Map the shared client decision to the existing API compatibility fields."""

    should_train = decision["session"] != "RECOVERY"
    reason = CLIENT_REASON_TO_API_REASON.get(decision["reason"])
    return should_train, [reason] if reason is not None else []


def effective_set_count(
    *,
    training_week: int,
    prescribed_sets: int,
    adaptation_weeks: int,
    adaptation_sets: int,
) -> int:
    if training_week < 1 or prescribed_sets < 1:
        raise ValueError("training_week and prescribed_sets must be positive")
    if adaptation_weeks < 0 or adaptation_sets < 1:
        raise ValueError("adaptation settings are invalid")
    return min(prescribed_sets, adaptation_sets) if training_week <= adaptation_weeks else prescribed_sets


def resolve_workout_plan_versions(
    *,
    existing_workout_plan_version_id: str,
    new_assignment_plan_version_id: str,
) -> dict[str, str]:
    """Keep an existing workout snapshot pinned while routing the next workout."""

    return {
        "existing_workout_plan_version_id": existing_workout_plan_version_id,
        "next_workout_plan_version_id": new_assignment_plan_version_id,
    }

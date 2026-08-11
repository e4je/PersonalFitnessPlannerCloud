from __future__ import annotations

from collections.abc import Iterable, Mapping
from decimal import Decimal
from typing import Any


def _decimal(value: object, field: str) -> Decimal:
    try:
        return Decimal(str(value))
    except Exception as exc:  # Decimal deliberately accepts several numeric types.
        raise ValueError(f"{field} must be numeric") from exc


def recommend_progression(data: Mapping[str, Any]) -> dict[str, Any]:
    """Return the shared, deterministic weight progression decision.

    Safety has precedence: reported pain always holds, followed by the
    two-failure deload rule.  Weight increases only when every recorded working
    set reaches the upper repetition bound with GOOD quality.
    """

    current = _decimal(data.get("current_weight_kg"), "current_weight_kg")
    increment = _decimal(data.get("minimum_increment_kg"), "minimum_increment_kg")
    if current < 0 or increment <= 0:
        raise ValueError("weights must be non-negative and increment must be positive")
    sets = data.get("sets")
    if not isinstance(sets, list) or not sets:
        raise ValueError("sets must be a non-empty list")

    if any(bool(item.get("pain")) for item in sets):
        action, next_weight, reason = "HOLD", current, "PAIN_REPORTED"
    elif int(data.get("consecutive_failed_sessions", 0)) >= 2:
        action = "DECREASE"
        next_weight = max(Decimal("0"), current - increment)
        reason = "TWO_CONSECUTIVE_FAILURES"
    else:
        rep_max = int(data["rep_max"])
        all_at_upper_bound = all(
            int(item.get("reps", -1)) >= rep_max
            and str(item.get("quality", "")).upper() == "GOOD"
            for item in sets
        )
        if all_at_upper_bound:
            action, next_weight, reason = (
                "INCREASE",
                current + increment,
                "ALL_WORKING_SETS_AT_UPPER_BOUND",
            )
        else:
            action, next_weight, reason = "HOLD", current, "KEEP_BUILDING_REPS"

    return {
        "action": action,
        "next_weight_kg": float(next_weight),
        "reason": reason,
    }


def latest_weight_for_exercise(
    history: Iterable[Mapping[str, Any]],
    *,
    exercise_id: str,
    source_option_id: str | None = None,
) -> float | None:
    """Find weight by exact exercise identity, never through an alternative."""

    for item in reversed(list(history)):
        if str(item.get("exercise_id")) != str(exercise_id):
            continue
        if source_option_id is not None and str(item.get("source_option_id")) != str(
            source_option_id
        ):
            continue
        weight = item.get("weight_kg")
        return float(weight) if weight is not None else None
    return None

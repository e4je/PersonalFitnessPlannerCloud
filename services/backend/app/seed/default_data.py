from __future__ import annotations

import json
from pathlib import Path
from typing import Any
from uuid import UUID


CONTRACT_PATH = Path(__file__).resolve().parents[2] / "contracts" / "default-training-plan.json"


def _uuid(value: object, path: str) -> str:
    try:
        return str(UUID(str(value)))
    except (TypeError, ValueError, AttributeError) as exc:
        raise RuntimeError(f"{path} must be a UUID") from exc


def _required(mapping: dict[str, Any], key: str, path: str) -> Any:
    if key not in mapping:
        raise RuntimeError(f"{path}.{key} is required")
    return mapping[key]


def _prescription_text(option: dict[str, Any]) -> str:
    sets = int(option["sets"])
    low = int(option["rep_min"])
    high = int(option["rep_max"])
    unit = str(option["rep_unit"])
    if unit == "seconds":
        return f"{sets}×{low}～{high} 秒"
    if unit == "reps_rir_1_2":
        return f"{sets} 组，保留 {option['rir_min']}～{option['rir_max']} 次余力"
    side = "每侧 " if bool(option["per_side"]) else ""
    return f"{side}{sets}×{low}～{high} 次"


def _load_canonical_plan() -> tuple[dict[str, Any], dict[str, Any]]:
    try:
        canonical = json.loads(CONTRACT_PATH.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise RuntimeError(f"Canonical training plan is missing: {CONTRACT_PATH}") from exc
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"Canonical training plan is invalid JSON: {CONTRACT_PATH}") from exc
    if not isinstance(canonical, dict):
        raise RuntimeError("Canonical training plan root must be an object")

    required_root = {
        "schema_version",
        "contract_version",
        "plan_code",
        "plan_id",
        "plan_version_id",
        "version",
        "status",
        "name",
        "description",
        "goal",
        "cycle",
        "weekly_strength_target",
        "minimum_rest_days",
        "fatigue_threshold",
        "adaptation_weeks",
        "adaptation_sets",
        "target_rir",
        "selection_rule",
        "published_at",
        "days",
    }
    missing = sorted(required_root.difference(canonical))
    if missing:
        raise RuntimeError(f"Canonical training plan is missing fields: {', '.join(missing)}")
    if canonical["status"] != "published":
        raise RuntimeError("Canonical default training plan must be published")
    if not isinstance(canonical["days"], list) or not canonical["days"]:
        raise RuntimeError("Canonical default training plan must contain days")

    plan_id = _uuid(canonical["plan_id"], "plan_id")
    plan_version_id = _uuid(canonical["plan_version_id"], "plan_version_id")
    seen_ids: set[str] = {plan_id, plan_version_id}
    exercise_identity: dict[str, tuple[str, str]] = {}
    equipment_identity: dict[str, tuple[str, str]] = {}
    mapped_days: list[dict[str, Any]] = []

    for day_index, day_value in enumerate(canonical["days"]):
        path = f"days[{day_index}]"
        if not isinstance(day_value, dict):
            raise RuntimeError(f"{path} must be an object")
        day_id = _uuid(_required(day_value, "day_id", path), f"{path}.day_id")
        if day_id in seen_ids:
            raise RuntimeError(f"{path}.day_id is duplicated")
        seen_ids.add(day_id)
        slots_value = _required(day_value, "slots", path)
        if not isinstance(slots_value, list) or not slots_value:
            raise RuntimeError(f"{path}.slots must be a non-empty array")
        mapped_slots: list[dict[str, Any]] = []

        for slot_index, slot_value in enumerate(slots_value):
            slot_path = f"{path}.slots[{slot_index}]"
            if not isinstance(slot_value, dict):
                raise RuntimeError(f"{slot_path} must be an object")
            slot_id = _uuid(_required(slot_value, "slot_id", slot_path), f"{slot_path}.slot_id")
            if slot_id in seen_ids:
                raise RuntimeError(f"{slot_path}.slot_id is duplicated")
            seen_ids.add(slot_id)
            options_value = _required(slot_value, "options", slot_path)
            if not isinstance(options_value, list) or not options_value:
                raise RuntimeError(f"{slot_path}.options must be a non-empty array")
            mapped_options: list[dict[str, Any]] = []
            preferred: list[dict[str, Any]] = []

            for option_index, option_value in enumerate(options_value):
                option_path = f"{slot_path}.options[{option_index}]"
                if not isinstance(option_value, dict):
                    raise RuntimeError(f"{option_path} must be an object")
                option_id = _uuid(
                    _required(option_value, "option_id", option_path), f"{option_path}.option_id"
                )
                if option_id in seen_ids:
                    raise RuntimeError(f"{option_path}.option_id is duplicated")
                seen_ids.add(option_id)
                exercise_id = _uuid(
                    _required(option_value, "exercise_id", option_path),
                    f"{option_path}.exercise_id",
                )
                equipment_id = _uuid(
                    _required(option_value, "equipment_id", option_path),
                    f"{option_path}.equipment_id",
                )
                exercise_name = str(_required(option_value, "exercise_name", option_path)).strip()
                equipment_name = str(_required(option_value, "equipment", option_path)).strip()
                if not exercise_name or not equipment_name:
                    raise RuntimeError(f"{option_path} exercise/equipment names must not be empty")
                exercise_key = exercise_identity.setdefault(
                    exercise_id, (exercise_name, exercise_id)
                )
                if exercise_key[0] != exercise_name:
                    raise RuntimeError(f"Exercise UUID {exercise_id} maps to multiple names")
                for known_name, known_id in exercise_identity.values():
                    if known_name == exercise_name and known_id != exercise_id:
                        raise RuntimeError(f"Exercise name {exercise_name!r} maps to multiple UUIDs")
                equipment_key = equipment_identity.setdefault(
                    equipment_id, (equipment_name, equipment_id)
                )
                if equipment_key[0] != equipment_name:
                    raise RuntimeError(f"Equipment UUID {equipment_id} maps to multiple names")
                for known_name, known_id in equipment_identity.values():
                    if known_name == equipment_name and known_id != equipment_id:
                        raise RuntimeError(f"Equipment name {equipment_name!r} maps to multiple UUIDs")

                unit = str(_required(option_value, "rep_unit", option_path))
                low = int(_required(option_value, "rep_min", option_path))
                high = int(_required(option_value, "rep_max", option_path))
                if low < 0 or high < low:
                    raise RuntimeError(f"{option_path} has an invalid repetition range")
                mapped = {
                    "id": option_id,
                    "exercise_id": exercise_id,
                    "equipment_id": equipment_id,
                    "name": exercise_name,
                    "equipment": equipment_name,
                    "set_count": int(_required(option_value, "sets", option_path)),
                    "rep_min": None if unit == "seconds" else low,
                    "rep_max": None if unit == "seconds" else high,
                    "duration_seconds_min": low if unit == "seconds" else None,
                    "duration_seconds_max": high if unit == "seconds" else None,
                    "rep_unit": unit,
                    "rir_min": int(_required(option_value, "rir_min", option_path)),
                    "rir_max": int(_required(option_value, "rir_max", option_path)),
                    "is_preferred": bool(_required(option_value, "is_primary", option_path)),
                    "is_per_side": bool(_required(option_value, "per_side", option_path)),
                    "sort_order": int(_required(option_value, "order", option_path)) - 1,
                    "rest_seconds": int(_required(option_value, "rest_seconds", option_path)),
                    "enabled": bool(_required(option_value, "enabled", option_path)),
                }
                mapped["prescription_text"] = _prescription_text(mapped | option_value)
                mapped_options.append(mapped)
                if mapped["is_preferred"]:
                    preferred.append(mapped)

            primary_id = _uuid(
                _required(slot_value, "primary_exercise_id", slot_path),
                f"{slot_path}.primary_exercise_id",
            )
            if len(preferred) != 1 or preferred[0]["exercise_id"] != primary_id:
                raise RuntimeError(
                    f"{slot_path} must have exactly one primary option matching primary_exercise_id"
                )
            mapped_slots.append(
                {
                    "id": slot_id,
                    "slot_code": str(_required(slot_value, "slot_code", slot_path)),
                    "sort_order": int(_required(slot_value, "order", slot_path)) - 1,
                    "focus": str(_required(slot_value, "muscle_group", slot_path)),
                    "cue": str(_required(slot_value, "cues", slot_path)),
                    "common_mistakes": str(slot_value.get("common_mistakes") or ""),
                    "adaptation_sets": int(
                        _required(slot_value, "adaptation_sets", slot_path)
                    ),
                    "enabled": bool(_required(slot_value, "enabled", slot_path)),
                    "bench_angle": slot_value.get("bench_angle"),
                    "options": mapped_options,
                }
            )

        mapped_days.append(
            {
                "id": day_id,
                "code": str(_required(day_value, "code", path)).upper(),
                "name": str(_required(day_value, "name", path)),
                "sort_order": int(_required(day_value, "order", path)) - 1,
                "slots": mapped_slots,
            }
        )

    cycle = [str(item).upper() for item in canonical["cycle"]]
    mapped_plan: dict[str, Any] = {
        "id": plan_id,
        "plan_version_id": plan_version_id,
        "code": str(canonical["plan_code"]),
        "name": str(canonical["name"]),
        "description": str(canonical["description"]),
        "goal": str(canonical["goal"]),
        "version_number": int(canonical["version"]),
        "status": str(canonical["status"]),
        "published_at": str(canonical["published_at"]),
        "schema_version": str(canonical["schema_version"]),
        "contract_version": str(canonical["contract_version"]),
        "rules": {
            "weekly_frequency": int(canonical["weekly_strength_target"]),
            "cycle": cycle,
            "week_patterns": [
                [cycle[index % len(cycle)] for index in range(3)],
                [cycle[(index + 1) % len(cycle)] for index in range(3)],
            ],
            "min_rest_days": int(canonical["minimum_rest_days"]),
            "fatigue_threshold": int(canonical["fatigue_threshold"]),
            "initial_reduced_weeks": int(canonical["adaptation_weeks"]),
            "initial_set_count": int(canonical["adaptation_sets"]),
            "full_sets_from_week": int(canonical["adaptation_weeks"]) + 1,
            "target_rir": [int(item) for item in canonical["target_rir"]],
            "selection_rule": str(canonical["selection_rule"]),
        },
        "days": mapped_days,
    }
    return canonical, mapped_plan


CANONICAL_PLAN, DEFAULT_PLAN = _load_canonical_plan()


def default_plan_counts() -> dict[str, int]:
    slots = [slot_data for day in DEFAULT_PLAN["days"] for slot_data in day["slots"]]
    options = [item for slot_data in slots for item in slot_data["options"]]
    return {
        "days": len(DEFAULT_PLAN["days"]),
        "slots": len(slots),
        "options": len(options),
        "exercises": len({item["exercise_id"] for item in options}),
        "equipment": len({item["equipment_id"] for item in options}),
    }

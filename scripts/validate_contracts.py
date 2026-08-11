from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from uuid import UUID


REQUIRED_API_OPERATIONS = {
    ("POST", "/api/v1/auth/login"),
    ("POST", "/api/v1/auth/refresh"),
    ("POST", "/api/v1/auth/logout"),
    ("GET", "/api/v1/me"),
    ("GET", "/api/v1/bootstrap"),
    ("GET", "/api/v1/plans/current"),
    ("GET", "/api/v1/plans/{plan_version_id}"),
    ("GET", "/api/v1/exercises"),
    ("GET", "/api/v1/equipment"),
    ("GET", "/api/v1/workout-sessions"),
    ("POST", "/api/v1/workout-sessions"),
    ("PATCH", "/api/v1/workout-sessions/{workout_id}"),
    ("POST", "/api/v1/readiness"),
    ("GET", "/api/v1/sync/changes"),
    ("POST", "/api/v1/sync/batch"),
    ("POST", "/api/v1/admin/exercises"),
    ("PATCH", "/api/v1/admin/exercises/{exercise_id}"),
    ("POST", "/api/v1/admin/equipment"),
    ("PATCH", "/api/v1/admin/equipment/{equipment_id}"),
    ("POST", "/api/v1/admin/plans"),
    ("POST", "/api/v1/admin/plans/{plan_id}/versions"),
    ("PATCH", "/api/v1/admin/plan-versions/{version_id}"),
    ("POST", "/api/v1/admin/plan-versions/{version_id}/publish"),
    ("POST", "/api/v1/admin/assignments"),
    ("GET", "/api/v1/admin/audit-logs"),
    ("GET", "/api/v1/admin/sync-status"),
}

REQUIRED_RECOMMENDATION_CASES = {
    "first-strength-recommends-a",
    "completed-a-recommends-b",
    "missed-calendar-day-keeps-sequence",
    "yesterday-full-body-needs-recovery",
    "weekly-limit-three",
    "fatigue-nine-needs-recovery",
    "adaptation-week-two-caps-sets",
    "new-version-only-affects-new-workouts",
}

REQUIRED_PROGRESSION_CASES = {
    "all-working-sets-at-upper-bound-increases",
    "partial-target-holds",
    "two-consecutive-failures-decrease",
    "pain-never-increases",
    "alternative-does-not-inherit-primary-weight",
}


def load_json(path: Path) -> object:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise AssertionError(f"Cannot read valid JSON from {path}: {error}") from error


def require_uuid(value: str, label: str) -> None:
    try:
        UUID(value)
    except (TypeError, ValueError, AttributeError) as error:
        raise AssertionError(f"{label} is not a UUID: {value!r}") from error


def validate_plan(root: Path, *, check_snapshots: bool = True) -> tuple[int, int, int, int]:
    contract_path = root / "contracts" / "default-training-plan.json"
    plan = load_json(contract_path)
    assert isinstance(plan, dict)
    schema = load_json(root / "contracts" / "schema-version.json")
    assert isinstance(schema, dict)

    assert plan["schema_version"] == schema["schema_version"]
    assert plan["contract_version"] == schema["api_version"]
    assert plan["plan_code"] == "beginner_recomp_ab_v1"
    assert plan["name"] == "小白增肌减脂 A/B 全身计划"
    assert plan["cycle"] == ["A", "B"]
    assert plan["weekly_strength_target"] == 3
    assert plan["minimum_rest_days"] == 1
    assert plan["adaptation_weeks"] == 2
    assert plan["adaptation_sets"] == 2
    require_uuid(plan["plan_id"], "plan_id")
    require_uuid(plan["plan_version_id"], "plan_version_id")

    days = plan["days"]
    assert isinstance(days, list) and [day["code"] for day in days] == ["A", "B"]
    assert [day["order"] for day in days] == [1, 2]

    day_ids: set[str] = set()
    slot_ids: set[str] = set()
    option_ids: set[str] = set()
    exercise_ids: set[str] = set()
    equipment_ids: set[str] = set()
    exercise_names: dict[str, str] = {}
    equipment_names: dict[str, str] = {}
    slot_count = 0
    option_count = 0

    for day in days:
        require_uuid(day["day_id"], f"day {day['code']} id")
        assert day["day_id"] not in day_ids
        day_ids.add(day["day_id"])
        slots = day["slots"]
        assert [slot["order"] for slot in slots] == list(range(1, 9))
        assert [slot["slot_code"] for slot in slots] == [
            f"{day['code']}{position:02d}" for position in range(1, 9)
        ]

        for slot in slots:
            slot_count += 1
            require_uuid(slot["slot_id"], f"slot {slot['slot_code']} id")
            require_uuid(slot["primary_exercise_id"], f"slot {slot['slot_code']} primary")
            assert slot["slot_id"] not in slot_ids
            slot_ids.add(slot["slot_id"])
            assert slot["enabled"] is True
            assert 1 <= slot["adaptation_sets"] <= slot["sets"]
            assert 0 <= slot["rep_min"] <= slot["rep_max"]

            options = slot["options"]
            assert options and [option["order"] for option in options] == list(
                range(1, len(options) + 1)
            )
            primary_options = [option for option in options if option["is_primary"]]
            assert len(primary_options) == 1
            primary = primary_options[0]
            assert primary["exercise_id"] == slot["primary_exercise_id"]
            for key in ("sets", "rep_min", "rep_max", "rep_unit", "rest_seconds"):
                assert slot[key] == primary[key], f"{slot['slot_code']} does not mirror primary {key}"

            seen_exercises: set[str] = set()
            for option in options:
                option_count += 1
                require_uuid(option["option_id"], f"option in {slot['slot_code']}")
                require_uuid(option["exercise_id"], f"exercise in {slot['slot_code']}")
                require_uuid(option["equipment_id"], f"equipment in {slot['slot_code']}")
                assert option["option_id"] not in option_ids
                option_ids.add(option["option_id"])
                assert option["exercise_id"] not in seen_exercises
                seen_exercises.add(option["exercise_id"])
                exercise_ids.add(option["exercise_id"])
                equipment_ids.add(option["equipment_id"])
                previous_exercise_name = exercise_names.setdefault(
                    option["exercise_id"], option["exercise_name"]
                )
                assert previous_exercise_name == option["exercise_name"], (
                    f"exercise_id {option['exercise_id']} maps to multiple names"
                )
                previous_equipment_name = equipment_names.setdefault(
                    option["equipment_id"], option["equipment"]
                )
                assert previous_equipment_name == option["equipment"], (
                    f"equipment_id {option['equipment_id']} maps to multiple names"
                )
                assert option["enabled"] is True
                assert option["sets"] >= 1
                assert 0 <= option["rep_min"] <= option["rep_max"]
                assert 0 <= option["rir_min"] <= option["rir_max"] <= 10

    assert len(set(exercise_names.values())) == len(exercise_ids), "one exercise name maps to multiple UUIDs"
    assert len(set(equipment_names.values())) == len(equipment_ids), "one equipment name maps to multiple UUIDs"
    assert (slot_count, option_count, len(exercise_ids), len(equipment_ids)) == (16, 79, 66, 52)

    if check_snapshots:
        snapshots = [
            root / "apps" / "android" / "app" / "src" / "main" / "resources" / "default-training-plan.json",
            root
            / "apps"
            / "windows"
            / "src"
            / "PersonalFitnessPlanner.Infrastructure"
            / "Data"
            / "default-training-plan.json",
            root / "services" / "backend" / "contracts" / "default-training-plan.json",
        ]
        canonical_bytes = contract_path.read_bytes()
        for snapshot in snapshots:
            assert snapshot.is_file(), f"Missing packaged plan snapshot: {snapshot}"
            assert snapshot.read_bytes() == canonical_bytes, f"Plan snapshot drift: {snapshot}"

        backend_schema = root / "services" / "backend" / "contracts" / "schema-version.json"
        assert backend_schema.read_bytes() == (root / "contracts" / "schema-version.json").read_bytes()
        backend_plan_schema = (
            root / "services" / "backend" / "contracts" / "default-training-plan.schema.json"
        )
        assert backend_plan_schema.read_bytes() == (
            root / "contracts" / "default-training-plan.schema.json"
        ).read_bytes()
        example_directories = [
            root / "apps" / "android" / "app" / "src" / "test" / "resources" / "contracts",
            root / "apps" / "windows" / "tests" / "PersonalFitnessPlanner.Tests" / "Contracts",
            root / "services" / "backend" / "contracts" / "examples",
        ]
        for name in ("recommendation-cases.json", "progression-cases.json"):
            source_bytes = (root / "contracts" / "examples" / name).read_bytes()
            for directory in example_directories:
                snapshot = directory / name
                assert snapshot.is_file(), f"Missing shared-vector snapshot: {snapshot}"
                assert snapshot.read_bytes() == source_bytes, f"Shared-vector drift: {snapshot}"
    return slot_count, option_count, len(exercise_ids), len(equipment_ids)


def validate_openapi(root: Path) -> int:
    canonical = root / "contracts" / "openapi.yaml"
    backend = root / "services" / "backend" / "contracts" / "openapi.yaml"
    assert canonical.read_bytes() == backend.read_bytes(), "Backend OpenAPI snapshot drift"
    text = canonical.read_text(encoding="utf-8")
    operations: set[tuple[str, str]] = set()
    current_path: str | None = None
    for line in text.splitlines():
        if line.startswith("  /") and line.endswith(":"):
            current_path = line.strip()[:-1]
        elif current_path is not None and line.startswith("    "):
            candidate = line.strip()[:-1].upper() if line.strip().endswith(":") else ""
            if candidate in {"GET", "POST", "PATCH", "PUT", "DELETE"}:
                operations.add((candidate, current_path))
        elif line and not line.startswith(" "):
            current_path = None
    missing = sorted(REQUIRED_API_OPERATIONS - operations)
    assert not missing, f"OpenAPI is missing required operations: {missing}"
    return len(REQUIRED_API_OPERATIONS)


def validate_examples(root: Path) -> tuple[int, int]:
    recommendation = load_json(root / "contracts" / "examples" / "recommendation-cases.json")
    progression = load_json(root / "contracts" / "examples" / "progression-cases.json")
    assert isinstance(recommendation, dict) and isinstance(progression, dict)
    recommendation_ids = {item["id"] for item in recommendation["cases"]}
    progression_ids = {item["id"] for item in progression["cases"]}
    assert REQUIRED_RECOMMENDATION_CASES <= recommendation_ids
    assert REQUIRED_PROGRESSION_CASES <= progression_ids
    return len(recommendation_ids), len(progression_ids)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--skip-snapshots", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    try:
        slots, options, exercises, equipment = validate_plan(
            root, check_snapshots=not args.skip_snapshots
        )
        paths = validate_openapi(root)
        recommendation_cases, progression_cases = validate_examples(root)
    except (AssertionError, KeyError, TypeError) as error:
        print(f"contract validation failed: {error}", file=sys.stderr)
        return 1

    print(
        "contract validation passed: "
        f"{paths} required API operations; {slots} slots/{options} options/"
        f"{exercises} exercises/{equipment} equipment; "
        f"{recommendation_cases} recommendation and {progression_cases} progression cases"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

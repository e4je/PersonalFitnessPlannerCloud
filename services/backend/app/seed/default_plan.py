from __future__ import annotations

import hashlib
import json
from collections import defaultdict
from datetime import datetime
from typing import Any
from uuid import UUID, uuid5

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core.config import settings
from app.models import (
    Equipment,
    Exercise,
    ExerciseAlternative,
    ExerciseCue,
    ExerciseEquipment,
    ExerciseMuscleGroup,
    MuscleGroup,
    PlanDay,
    PlanSlot,
    PlanSlotOption,
    PlanVersion,
    Role,
    SchemaVersion,
    SystemSetting,
    SyncChange,
    TrainingPlan,
)
from app.seed.default_data import CANONICAL_PLAN, DEFAULT_PLAN, default_plan_counts
from app.services.plans import serialize_plan_version


ADMIN_PERMISSIONS = [
    "admin:catalog",
    "admin:plans",
    "admin:assignments",
    "admin:audit",
    "sync:read",
    "workouts:read",
    "workouts:write",
]
USER_PERMISSIONS = ["sync:read", "workouts:read", "workouts:write"]


def _code(prefix: str, value: str) -> str:
    digest = hashlib.sha256(value.encode("utf-8")).hexdigest()[:16]
    return f"{prefix}-{digest}"


def _stable_id(kind: str, *parts: object) -> str:
    namespace = UUID(DEFAULT_PLAN["id"])
    return str(uuid5(namespace, ":".join([kind, *(str(part) for part in parts)])))


def _release_legacy_code(db: Session, entity: Any, kind: str) -> None:
    """Keep legacy seeded rows addressable while reserving canonical codes/UUIDs.

    Releases before this repository used UUID4 primary keys but the same
    deterministic catalog codes. Renaming the old code lets the canonical row
    coexist without rewriting historical plan/workout foreign keys.
    """

    entity.code = f"legacy-{kind}-{str(entity.id).replace('-', '')}"[:64]
    entity.version += 1
    db.flush()


def _change(
    db: Session,
    entity_type: str,
    entity: Any,
    *,
    payload: dict[str, Any] | None = None,
) -> None:
    db.add(
        SyncChange(
            entity_type=entity_type,
            entity_id=entity.id,
            entity_version=entity.version,
            operation="create",
            payload_json=payload or {"id": entity.id, "version": entity.version},
        )
    )


def _ensure_roles(db: Session) -> None:
    definitions = (
        ("admin", "System administrator", ADMIN_PERMISSIONS),
        ("user", "Standard fitness planner user", USER_PERMISSIONS),
    )
    for name, description, permissions in definitions:
        role = db.scalar(select(Role).where(Role.name == name))
        if role is None:
            db.add(
                Role(
                    id=_stable_id("role", name),
                    name=name,
                    description=description,
                    permissions_json=permissions,
                    is_system=True,
                )
            )
        else:
            role.description = description
            role.permissions_json = permissions
            role.is_system = True


def _ensure_system_settings(db: Session) -> None:
    """Create policy defaults without overwriting an operator's choice."""

    row = db.scalar(
        select(SystemSetting).where(SystemSetting.key == "registration_enabled")
    )
    if row is None:
        db.add(
            SystemSetting(
                id=_stable_id("setting", "registration_enabled"),
                key="registration_enabled",
                value_json={"value": True},
                description="Allow unauthenticated visitors to create standard accounts",
            )
        )


def _ensure_schema_version(db: Session) -> None:
    row = db.scalar(
        select(SchemaVersion).where(SchemaVersion.schema_version == settings.schema_version)
    )
    if row is None:
        db.add(
            SchemaVersion(
                id=_stable_id("schema", settings.schema_version),
                schema_version=settings.schema_version,
                api_version=settings.api_version,
                minimum_client_version=settings.minimum_client_version,
                checksum=hashlib.sha256(
                    json.dumps(
                        CANONICAL_PLAN,
                        ensure_ascii=False,
                        sort_keys=True,
                        separators=(",", ":"),
                    ).encode("utf-8")
                ).hexdigest(),
                notes="Initial cloud schema and default A/B full-body plan",
            )
        )


def seed_default_plan(db: Session, *, commit: bool = True) -> dict[str, Any]:
    """Create the canonical A/B beginner plan exactly once.

    The loader keys catalog records by deterministic codes and the logical plan
    by its stable system name. A completed published version is never changed.
    """

    _ensure_roles(db)
    _ensure_system_settings(db)
    _ensure_schema_version(db)

    flattened: list[tuple[dict[str, Any], dict[str, Any], dict[str, Any]]] = []
    for day_data in DEFAULT_PLAN["days"]:
        for slot_data in day_data["slots"]:
            for option_data in slot_data["options"]:
                flattened.append((day_data, slot_data, option_data))

    muscles: dict[str, MuscleGroup] = {}
    for order, focus in enumerate(dict.fromkeys(slot_data["focus"] for _, slot_data, _ in flattened)):
        code = _code("muscle", focus)
        expected_id = _stable_id("muscle_group", focus)
        muscle = db.get(MuscleGroup, expected_id)
        if muscle is None:
            legacy_muscle = db.scalar(select(MuscleGroup).where(MuscleGroup.code == code))
            if legacy_muscle is not None:
                _release_legacy_code(db, legacy_muscle, "muscle")
        if muscle is None:
            muscle = MuscleGroup(
                id=expected_id,
                code=code,
                name=focus,
                body_region=focus,
                sort_order=order,
            )
            db.add(muscle)
            db.flush()
            _change(db, "muscle_group", muscle)
        muscles[focus] = muscle

    equipment_by_name: dict[str, Equipment] = {}
    equipment_ids = {
        item[2]["equipment"]: item[2]["equipment_id"]
        for item in flattened
    }
    for equipment_name in dict.fromkeys(item[2]["equipment"] for item in flattened):
        code = _code("equipment", equipment_name)
        expected_id = equipment_ids[equipment_name]
        equipment = db.get(Equipment, expected_id)
        if equipment is None:
            legacy_equipment = db.scalar(select(Equipment).where(Equipment.code == code))
            if legacy_equipment is not None:
                _release_legacy_code(db, legacy_equipment, "equipment")
        if equipment is None:
            category = "compound" if "＋" in equipment_name else "alternative" if "或" in equipment_name else "single"
            equipment = Equipment(
                id=expected_id,
                code=code,
                name=equipment_name,
                category=category,
                is_active=True,
                metadata_json={"raw_requirement": equipment_name},
            )
            db.add(equipment)
            db.flush()
            _change(db, "equipment", equipment)
        equipment_by_name[equipment_name] = equipment

    occurrences: dict[str, list[tuple[dict[str, Any], dict[str, Any]]]] = defaultdict(list)
    for _, slot_data, option_data in flattened:
        occurrences[option_data["name"]].append((slot_data, option_data))

    exercises: dict[str, Exercise] = {}
    for exercise_name, exercise_occurrences in occurrences.items():
        first_slot, first_option = exercise_occurrences[0]
        code = _code("exercise", exercise_name)
        expected_id = first_option["exercise_id"]
        exercise = db.get(Exercise, expected_id)
        if exercise is None:
            legacy_exercise = db.scalar(select(Exercise).where(Exercise.code == code))
            if legacy_exercise is not None:
                _release_legacy_code(db, legacy_exercise, "exercise")
        if exercise is None:
            common_mistakes = list(
                dict.fromkeys(
                    slot_data.get("common_mistakes", "")
                    for slot_data, _option_data in exercise_occurrences
                    if slot_data.get("common_mistakes")
                )
            )
            exercise = Exercise(
                id=expected_id,
                code=code,
                name=exercise_name,
                description=first_slot["cue"],
                body_part=first_slot["focus"],
                difficulty="beginner",
                default_sets=first_option["set_count"],
                rep_min=first_option["rep_min"],
                rep_max=first_option["rep_max"],
                rep_unit=first_option["rep_unit"],
                is_unilateral=any(item[1]["is_per_side"] for item in exercise_occurrences),
                is_active=True,
                common_mistakes_json=common_mistakes,
                metadata_json={"seed": DEFAULT_PLAN["code"]},
            )
            db.add(exercise)
            db.flush()
            _change(db, "exercise", exercise)
        exercises[exercise_name] = exercise

        existing_cues = {cue.text for cue in exercise.cues}
        for slot_data, _ in exercise_occurrences:
            cue_text = slot_data["cue"]
            if cue_text not in existing_cues:
                db.add(
                    ExerciseCue(
                        id=_stable_id("exercise_cue", exercise.id, cue_text),
                        exercise_id=exercise.id,
                        text=cue_text,
                        sort_order=len(existing_cues),
                    )
                )
                existing_cues.add(cue_text)

        linked_equipment = {link.equipment_id for link in exercise.equipment_links}
        linked_muscles = {link.muscle_group_id for link in exercise.muscle_group_links}
        for slot_data, option_data in exercise_occurrences:
            equipment = equipment_by_name[option_data["equipment"]]
            muscle = muscles[slot_data["focus"]]
            if equipment.id not in linked_equipment:
                db.add(
                    ExerciseEquipment(
                        id=_stable_id("exercise_equipment", exercise.id, equipment.id),
                        exercise_id=exercise.id,
                        equipment_id=equipment.id,
                        is_required=True,
                        notes=option_data["equipment"],
                    )
                )
                linked_equipment.add(equipment.id)
            if muscle.id not in linked_muscles:
                db.add(
                    ExerciseMuscleGroup(
                        id=_stable_id("exercise_muscle", exercise.id, muscle.id),
                        exercise_id=exercise.id,
                        muscle_group_id=muscle.id,
                        is_primary=not linked_muscles,
                    )
                )
                linked_muscles.add(muscle.id)

    db.flush()

    alternative_pairs = {
        (link.exercise_id, link.alternative_exercise_id)
        for exercise in exercises.values()
        for link in exercise.alternatives
    }
    for day_data in DEFAULT_PLAN["days"]:
        for slot_data in day_data["slots"]:
            preferred_data = next(item for item in slot_data["options"] if item["is_preferred"])
            preferred = exercises[preferred_data["name"]]
            for priority, alternative_data in enumerate(slot_data["options"][1:], start=1):
                alternative = exercises[alternative_data["name"]]
                pair = (preferred.id, alternative.id)
                if pair not in alternative_pairs:
                    db.add(
                        ExerciseAlternative(
                            id=_stable_id("exercise_alternative", preferred.id, alternative.id),
                            exercise_id=preferred.id,
                            alternative_exercise_id=alternative.id,
                            priority=priority,
                            notes=f"{slot_data['focus']} 位置的替代动作",
                        )
                    )
                    alternative_pairs.add(pair)

    plan = db.get(TrainingPlan, DEFAULT_PLAN["id"])
    if plan is None:
        plan = TrainingPlan(
            id=DEFAULT_PLAN["id"],
            name=DEFAULT_PLAN["name"],
            description=DEFAULT_PLAN["description"],
            goal=DEFAULT_PLAN["goal"],
            is_system=True,
            is_active=True,
        )
        db.add(plan)
        db.flush()
        _change(db, "training_plan", plan)

    plan_version = db.get(PlanVersion, DEFAULT_PLAN["plan_version_id"])
    created_version = plan_version is None
    if plan_version is None:
        rules = DEFAULT_PLAN["rules"]
        plan_version = PlanVersion(
            id=DEFAULT_PLAN["plan_version_id"],
            training_plan_id=plan.id,
            version_number=DEFAULT_PLAN["version_number"],
            status="draft",
            weekly_frequency=rules["weekly_frequency"],
            min_rest_days=rules["min_rest_days"],
            fatigue_threshold=rules["fatigue_threshold"],
            initial_reduced_weeks=rules["initial_reduced_weeks"],
            initial_set_count=rules["initial_set_count"],
            config_json={"seed_code": DEFAULT_PLAN["code"], **rules},
            changelog="Initial canonical seed",
        )
        db.add(plan_version)
        db.flush()

    if created_version:
        for day_index, day_data in enumerate(DEFAULT_PLAN["days"]):
            day = PlanDay(
                id=day_data["id"],
                plan_version_id=plan_version.id,
                day_code=day_data["code"],
                name=day_data["name"],
                sort_order=day_data.get("sort_order", day_index),
            )
            db.add(day)
            db.flush()
            for slot_index, slot_data in enumerate(day_data["slots"]):
                plan_slot = PlanSlot(
                    id=slot_data["id"],
                    plan_day_id=day.id,
                    name=slot_data["focus"],
                    target_muscle_group_id=muscles[slot_data["focus"]].id,
                    sort_order=slot_data.get("sort_order", slot_index),
                    notes=slot_data["cue"],
                    selection_rule_json={
                        "choose": 1,
                        "server_authoritative": True,
                        "slot_code": slot_data["slot_code"],
                        "adaptation_sets": slot_data["adaptation_sets"],
                        "enabled": slot_data["enabled"],
                        "common_mistakes": slot_data["common_mistakes"],
                        "bench_angle": slot_data.get("bench_angle"),
                    },
                )
                db.add(plan_slot)
                db.flush()
                for option_index, option_data in enumerate(slot_data["options"]):
                    db.add(
                        PlanSlotOption(
                            id=option_data["id"],
                            plan_slot_id=plan_slot.id,
                            exercise_id=exercises[option_data["name"]].id,
                            is_preferred=option_data["is_preferred"],
                            sort_order=option_data.get("sort_order", option_index),
                            set_count=option_data["set_count"],
                            reps_min=option_data["rep_min"],
                            reps_max=option_data["rep_max"],
                            duration_seconds_min=option_data["duration_seconds_min"],
                            duration_seconds_max=option_data["duration_seconds_max"],
                            rir_min=option_data["rir_min"],
                            rir_max=option_data["rir_max"],
                            is_per_side=option_data["is_per_side"],
                            prescription_json={
                                "text": option_data["prescription_text"],
                                "rep_unit": option_data["rep_unit"],
                                "equipment_requirement": option_data["equipment"],
                                "equipment_id": option_data["equipment_id"],
                                "rest_seconds": option_data["rest_seconds"],
                                "enabled": option_data["enabled"],
                            },
                        )
                    )
        db.flush()
        plan_version.status = "published"
        plan_version.published_at = datetime.fromisoformat(
            DEFAULT_PLAN["published_at"].replace("Z", "+00:00")
        )
        plan_version.version += 1
        db.flush()
        _change(
            db,
            "plan_version",
            plan_version,
            payload=serialize_plan_version(plan_version),
        )

    if commit:
        db.commit()
    else:
        db.flush()

    return {
        "status": "created" if created_version else "already_seeded",
        "plan_id": plan.id,
        "plan_version_id": plan_version.id,
        **default_plan_counts(),
    }

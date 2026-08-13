from __future__ import annotations

import json
from copy import deepcopy
from datetime import timedelta
from typing import Any, Iterable

from sqlalchemy import event, func, inspect, select
from sqlalchemy.orm import Session

from app.db.base import utcnow, uuid4_str
from app.models import (
    Equipment,
    Exercise,
    MuscleGroup,
    PlanAssignment,
    PlanDay,
    PlanSlot,
    PlanSlotOption,
    PlanVersion,
    TrainingPlan,
    User,
)
from app.repositories.common import (
    OptimisticLockError,
    entity_dict,
    get_active,
    require_active,
)
from app.schemas.admin import (
    AssignmentCreate,
    PlanDayInput,
    PlanSlotInput,
    PlanSlotOptionInput,
    PlanVersionCreate,
    PlanVersionPatch,
)


class PublishedPlanImmutableError(ValueError):
    pass


class PlanValidationError(ValueError):
    def __init__(self, issues: list[dict[str, str]]) -> None:
        self.issues = issues
        super().__init__("Training plan validation failed")


def _active(items: Iterable[Any]) -> list[Any]:
    return [item for item in items if getattr(item, "deleted_at", None) is None]


def _json_value(value: Any, *, empty: dict[str, Any] | None = None) -> Any:
    if value is None:
        return {} if empty is None else deepcopy(empty)
    if isinstance(value, str):
        try:
            return json.loads(value)
        except json.JSONDecodeError:
            # Drafts may temporarily carry an invalid JSON string. Publication
            # validation will report its exact path instead of losing the input.
            return value
    return deepcopy(value)


def _prescription_from_input(option: PlanSlotOptionInput) -> Any:
    value = _json_value(option.prescription_json)
    if not isinstance(value, dict):
        return value
    compatibility = {
        "equipment_id": str(option.equipment_id) if option.equipment_id else None,
        "intro_set_count": option.intro_set_count,
        "intro_weeks": option.intro_weeks,
        "rep_unit": option.rep_unit,
        "rest_seconds": option.rest_seconds,
        "exercise_name": option.exercise_name,
        "equipment_name": option.equipment,
    }
    for key, item in compatibility.items():
        if item is not None:
            value[key] = item
    return value


def _selection_rule_from_input(slot: PlanSlotInput) -> Any:
    value = _json_value(slot.selection_rule_json)
    if not isinstance(value, dict):
        return value
    display = {
        "common_mistakes": slot.common_mistakes,
        "seat_position": slot.seat_position,
        "bench_angle": slot.bench_angle,
        "machine_number": slot.machine_number,
    }
    if any(item is not None for item in display.values()):
        value.setdefault("display", {}).update(
            {key: item for key, item in display.items() if item is not None}
        )
    return value


def _append_option(
    slot: PlanSlot,
    payload: PlanSlotOptionInput,
    *,
    default_sort_order: int,
) -> None:
    slot.options.append(
        PlanSlotOption(
            id=str(payload.id) if payload.id else uuid4_str(),
            exercise_id=str(payload.exercise_id),
            is_preferred=payload.is_preferred,
            sort_order=(
                payload.sort_order
                if "sort_order" in payload.model_fields_set
                else default_sort_order
            ),
            set_count=payload.set_count,
            reps_min=payload.reps_min,
            reps_max=payload.reps_max,
            duration_seconds_min=payload.duration_seconds_min,
            duration_seconds_max=payload.duration_seconds_max,
            rir_min=payload.rir_min,
            rir_max=payload.rir_max,
            is_per_side=payload.is_per_side,
            prescription_json=_prescription_from_input(payload),
        )
    )


def _append_slot(
    day: PlanDay,
    payload: PlanSlotInput,
    *,
    default_sort_order: int,
) -> None:
    slot = PlanSlot(
        id=str(payload.id) if payload.id else uuid4_str(),
        name=payload.name,
        target_muscle_group_id=(
            str(payload.target_muscle_group_id) if payload.target_muscle_group_id else None
        ),
        sort_order=(
            payload.sort_order
            if "sort_order" in payload.model_fields_set
            else default_sort_order
        ),
        notes=payload.notes or None,
        selection_rule_json=_selection_rule_from_input(payload),
    )
    day.slots.append(slot)
    for option_index, option in enumerate(payload.options):
        _append_option(slot, option, default_sort_order=option_index)


def _append_day(
    version: PlanVersion,
    payload: PlanDayInput,
    *,
    default_sort_order: int,
) -> None:
    day = PlanDay(
        id=str(payload.id) if payload.id else uuid4_str(),
        day_code=payload.day_code,
        name=payload.name or payload.day_code,
        sort_order=(
            payload.sort_order
            if "sort_order" in payload.model_fields_set
            else default_sort_order
        ),
        notes=payload.notes or None,
    )
    version.days.append(day)
    for slot_index, slot in enumerate(payload.slots):
        _append_slot(day, slot, default_sort_order=slot_index)


def _copied_days(source: PlanVersion) -> list[PlanDayInput]:
    days: list[PlanDayInput] = []
    for day in _active(source.days):
        slots: list[dict[str, Any]] = []
        for slot in _active(day.slots):
            options: list[dict[str, Any]] = []
            for option in _active(slot.options):
                options.append(
                    {
                        "exercise_id": option.exercise_id,
                        "is_preferred": option.is_preferred,
                        "sort_order": option.sort_order,
                        "set_count": option.set_count,
                        "reps_min": option.reps_min,
                        "reps_max": option.reps_max,
                        "duration_seconds_min": option.duration_seconds_min,
                        "duration_seconds_max": option.duration_seconds_max,
                        "rir_min": float(option.rir_min) if option.rir_min is not None else None,
                        "rir_max": float(option.rir_max) if option.rir_max is not None else None,
                        "is_per_side": option.is_per_side,
                        "prescription_json": deepcopy(option.prescription_json),
                    }
                )
            slots.append(
                {
                    "name": slot.name,
                    "target_muscle_group_id": slot.target_muscle_group_id,
                    "sort_order": slot.sort_order,
                    "notes": slot.notes or "",
                    "selection_rule_json": deepcopy(slot.selection_rule_json),
                    "options": options,
                }
            )
        days.append(
            PlanDayInput.model_validate(
                {
                    "day_code": day.day_code,
                    "name": day.name,
                    "sort_order": day.sort_order,
                    "notes": day.notes or "",
                    "slots": slots,
                }
            )
        )
    return days


def serialize_plan_version(version: PlanVersion) -> dict[str, Any]:
    result = entity_dict(version)
    plan = version.plan
    result.update(
        {
            "plan_id": version.training_plan_id,
            "plan_name": plan.name if plan else "",
            "description": plan.description if plan else "",
            "is_system": bool(plan.is_system) if plan else False,
            "owner_user_id": plan.owner_user_id if plan else None,
            "plan_is_active": bool(plan.is_active) if plan else False,
            "intro_weeks": version.initial_reduced_weeks,
            "intro_max_sets": version.initial_set_count,
            "snapshot_json": json.dumps(version.config_json, ensure_ascii=False),
        }
    )
    days: list[dict[str, Any]] = []
    for day in _active(version.days):
        day_value = entity_dict(day)
        day_value["code"] = day.day_code
        slots: list[dict[str, Any]] = []
        for slot in _active(day.slots):
            slot_value = entity_dict(slot)
            slot_value.update(
                {
                    "position": slot.sort_order,
                    "body_part": slot.name,
                    "cues": slot.notes or "",
                }
            )
            options: list[dict[str, Any]] = []
            for option in _active(slot.options):
                option_value = entity_dict(option)
                prescription = option.prescription_json if isinstance(option.prescription_json, dict) else {}
                option_value.update(
                    {
                        "rep_min": option.reps_min or 0,
                        "rep_max": option.reps_max or 0,
                        "equipment_id": prescription.get("equipment_id"),
                        "rep_unit": prescription.get("rep_unit", "reps"),
                        "rir_min": int(option.rir_min) if option.rir_min is not None else None,
                        "rir_max": int(option.rir_max) if option.rir_max is not None else None,
                    }
                )
                options.append(option_value)
            slot_value["options"] = options
            slots.append(slot_value)
        day_value["slots"] = slots
        days.append(day_value)
    result["days"] = days
    return result


def create_plan_version(
    db: Session,
    plan_id: str,
    payload: PlanVersionCreate,
) -> PlanVersion:
    plan = require_active(db, TrainingPlan, plan_id, for_update=True)
    if payload.plan_id is not None and str(payload.plan_id) != plan.id:
        raise PlanValidationError(
            [
                {
                    "code": "plan_id_mismatch",
                    "path": "plan_id",
                    "message": "Body plan_id does not match the route plan id",
                }
            ]
        )
    version_number = (
        db.scalar(
            select(func.max(PlanVersion.version_number)).where(
                PlanVersion.training_plan_id == plan.id
            )
        )
        or 0
    ) + 1

    base: PlanVersion | None = None
    if payload.base_plan_version_id:
        base = require_active(db, PlanVersion, str(payload.base_plan_version_id))
        if base.training_plan_id != plan.id:
            raise PlanValidationError(
                [
                    {
                        "code": "base_version_wrong_plan",
                        "path": "base_plan_version_id",
                        "message": "Base version belongs to another training plan",
                    }
                ]
            )

    def inherited(field: str, supplied: Any) -> Any:
        if base is not None and field not in payload.model_fields_set:
            return deepcopy(getattr(base, field))
        return supplied

    version = PlanVersion(
        id=str(payload.id) if payload.id else uuid4_str(),
        training_plan_id=plan.id,
        version_number=version_number,
        status="draft",
        weekly_frequency=inherited("weekly_frequency", payload.weekly_frequency),
        min_rest_days=inherited("min_rest_days", payload.min_rest_days),
        fatigue_threshold=inherited("fatigue_threshold", payload.fatigue_threshold),
        initial_reduced_weeks=inherited(
            "initial_reduced_weeks", payload.initial_reduced_weeks
        ),
        initial_set_count=inherited("initial_set_count", payload.initial_set_count),
        config_json=_json_value(inherited("config_json", payload.config_json)),
        changelog=payload.changelog or None,
    )
    db.add(version)
    source_days = payload.days or (_copied_days(base) if base is not None else [])
    for day_index, day in enumerate(source_days):
        _append_day(version, day, default_sort_order=day_index)
    db.flush()
    return version


def patch_plan_version(
    db: Session,
    version_id: str,
    payload: PlanVersionPatch,
) -> tuple[PlanVersion, dict[str, Any]]:
    version = require_active(db, PlanVersion, version_id, for_update=True)
    if version.version != payload.expected_version:
        raise OptimisticLockError(serialize_plan_version(version))
    if version.status != "draft":
        raise PublishedPlanImmutableError("Published plan versions and their full trees are immutable")

    before = serialize_plan_version(version)
    values = payload.model_dump(exclude_unset=True)
    values.pop("expected_version", None)
    days_present = "days" in values
    days = payload.days
    for field in (
        "weekly_frequency",
        "min_rest_days",
        "fatigue_threshold",
        "initial_reduced_weeks",
        "initial_set_count",
        "changelog",
    ):
        if field in values:
            setattr(version, field, values[field])
    if "config_json" in values:
        version.config_json = _json_value(payload.config_json)
    if days_present:
        version.days.clear()
        db.flush()
        for day_index, day in enumerate(days or []):
            _append_day(version, day, default_sort_order=day_index)
    version.version += 1
    db.flush()
    return version, before


def _issue(issues: list[dict[str, str]], code: str, path: str, message: str) -> None:
    issues.append({"code": code, "path": path, "message": message})


def _validate_json_object(
    value: Any,
    path: str,
    issues: list[dict[str, str]],
) -> dict[str, Any] | None:
    if isinstance(value, str):
        try:
            value = json.loads(value)
        except json.JSONDecodeError as exc:
            _issue(issues, "json_invalid", path, f"Invalid JSON: {exc.msg}")
            return None
    if not isinstance(value, dict):
        _issue(issues, "json_object_required", path, "A JSON object is required")
        return None
    try:
        json.dumps(value, ensure_ascii=False)
    except (TypeError, ValueError) as exc:
        _issue(issues, "json_invalid", path, f"Value is not JSON serializable: {exc}")
        return None
    return value


def validate_plan_version(db: Session, version: PlanVersion) -> list[dict[str, str]]:
    issues: list[dict[str, str]] = []
    config = _validate_json_object(version.config_json, "config_json", issues)
    days = _active(version.days)
    if not days:
        _issue(issues, "days_required", "days", "At least plan days A and B are required")
        return issues

    codes = [day.day_code.strip().upper() for day in days]
    for required_code in ("A", "B"):
        if required_code not in codes:
            _issue(
                issues,
                "day_required",
                "days",
                f"Plan day '{required_code}' is required for A/B rotation",
            )
    if len(codes) != len(set(codes)):
        _issue(issues, "day_code_duplicate", "days", "Plan day codes must be unique")
    day_orders = [day.sort_order for day in days]
    if len(day_orders) != len(set(day_orders)):
        _issue(issues, "day_order_duplicate", "days", "Plan day sort orders must be unique")

    if config is not None:
        sequence = config.get("sequence") or config.get("rotation") or config.get("ab_sequence")
        if sequence is not None:
            if not isinstance(sequence, list) or not sequence:
                _issue(issues, "ab_rule_invalid", "config_json.sequence", "A/B sequence must be a non-empty list")
            elif any(str(item).strip().upper() not in set(codes) for item in sequence):
                _issue(
                    issues,
                    "ab_rule_unknown_day",
                    "config_json.sequence",
                    "A/B sequence references an unknown plan day",
                )

    exercise_cache: dict[str, Exercise | None] = {}
    equipment_cache: dict[str, Equipment | None] = {}
    muscle_cache: dict[str, MuscleGroup | None] = {}

    for day_index, day in enumerate(days):
        day_path = f"days[{day_index}]"
        if day.sort_order < 0:
            _issue(issues, "day_order_invalid", f"{day_path}.sort_order", "Sort order cannot be negative")
        slots = _active(day.slots)
        if not slots:
            _issue(issues, "slots_required", f"{day_path}.slots", "Every plan day needs at least one slot")
            continue
        slot_orders = [slot.sort_order for slot in slots]
        if len(slot_orders) != len(set(slot_orders)):
            _issue(issues, "slot_order_duplicate", f"{day_path}.slots", "Slot sort orders must be unique")

        for slot_index, slot in enumerate(slots):
            slot_path = f"{day_path}.slots[{slot_index}]"
            if not slot.name.strip():
                _issue(issues, "slot_name_required", f"{slot_path}.name", "Slot name is required")
            selection_rule = _validate_json_object(
                slot.selection_rule_json,
                f"{slot_path}.selection_rule_json",
                issues,
            )
            if selection_rule is not None and selection_rule.get("choose", 1) != 1:
                _issue(
                    issues,
                    "selection_rule_invalid",
                    f"{slot_path}.selection_rule_json.choose",
                    "Each slot must select exactly one preferred or alternative exercise",
                )
            if slot.target_muscle_group_id:
                muscle = muscle_cache.setdefault(
                    slot.target_muscle_group_id,
                    get_active(db, MuscleGroup, slot.target_muscle_group_id),
                )
                if muscle is None:
                    _issue(
                        issues,
                        "muscle_group_missing",
                        f"{slot_path}.target_muscle_group_id",
                        "Target muscle group does not exist",
                    )

            options = _active(slot.options)
            if not options:
                _issue(issues, "options_required", f"{slot_path}.options", "Every slot needs exercise options")
                continue
            if sum(1 for option in options if option.is_preferred) != 1:
                _issue(
                    issues,
                    "preferred_option_invalid",
                    f"{slot_path}.options",
                    "Exactly one preferred exercise is required; the rest are alternatives",
                )
            exercise_ids = [option.exercise_id for option in options]
            if len(exercise_ids) != len(set(exercise_ids)):
                _issue(issues, "exercise_duplicate", f"{slot_path}.options", "An exercise may appear only once per slot")
            option_orders = [option.sort_order for option in options]
            if len(option_orders) != len(set(option_orders)):
                _issue(issues, "option_order_duplicate", f"{slot_path}.options", "Option sort orders must be unique")

            for option_index, option in enumerate(options):
                option_path = f"{slot_path}.options[{option_index}]"
                prescription = _validate_json_object(
                    option.prescription_json,
                    f"{option_path}.prescription_json",
                    issues,
                )
                if option.set_count <= 0:
                    _issue(issues, "set_count_invalid", f"{option_path}.set_count", "Set count must be positive")
                elif version.initial_set_count > option.set_count:
                    _issue(
                        issues,
                        "initial_set_count_invalid",
                        f"{option_path}.set_count",
                        "Initial reduced set count cannot exceed the full set count",
                    )
                if option.reps_min is not None and option.reps_max is not None and option.reps_max < option.reps_min:
                    _issue(issues, "rep_range_invalid", option_path, "Maximum reps must not be below minimum reps")
                if (
                    option.duration_seconds_min is not None
                    and option.duration_seconds_max is not None
                    and option.duration_seconds_max < option.duration_seconds_min
                ):
                    _issue(issues, "duration_range_invalid", option_path, "Maximum duration must not be below minimum duration")
                has_text_prescription = bool(
                    prescription
                    and isinstance(prescription.get("text"), str)
                    and prescription["text"].strip()
                    and option.rir_min is not None
                    and option.rir_max is not None
                )
                if (
                    option.reps_min is None
                    and option.duration_seconds_min is None
                    and not has_text_prescription
                ):
                    _issue(
                        issues,
                        "prescription_missing",
                        option_path,
                        "A repetition, duration, or text-plus-RIR target is required",
                    )
                if option.rir_min is not None and option.rir_max is not None and option.rir_max < option.rir_min:
                    _issue(issues, "rir_range_invalid", option_path, "Maximum RIR must not be below minimum RIR")

                exercise = exercise_cache.setdefault(
                    option.exercise_id,
                    get_active(db, Exercise, option.exercise_id),
                )
                if exercise is None or not exercise.is_active:
                    _issue(issues, "exercise_unavailable", f"{option_path}.exercise_id", "Exercise does not exist or is inactive")
                    continue

                linked_equipment_ids: set[str] = set()
                for link in _active(exercise.equipment_links):
                    linked_equipment_ids.add(link.equipment_id)
                    equipment = equipment_cache.setdefault(
                        link.equipment_id,
                        get_active(db, Equipment, link.equipment_id),
                    )
                    if equipment is None or not equipment.is_active:
                        _issue(
                            issues,
                            "equipment_unavailable",
                            f"{option_path}.exercise_id",
                            "Exercise references missing or inactive equipment",
                        )
                if prescription is not None and prescription.get("equipment_id"):
                    equipment_id = str(prescription["equipment_id"])
                    equipment = equipment_cache.setdefault(
                        equipment_id,
                        get_active(db, Equipment, equipment_id),
                    )
                    if equipment is None or not equipment.is_active:
                        _issue(issues, "equipment_unavailable", f"{option_path}.prescription_json.equipment_id", "Equipment does not exist or is inactive")
                    elif equipment_id not in linked_equipment_ids:
                        _issue(issues, "equipment_not_supported", f"{option_path}.prescription_json.equipment_id", "Equipment is not linked to this exercise")
    return issues


def publish_plan_version(
    db: Session,
    version_id: str,
    *,
    actor_user_id: str,
    expected_version: int | None,
) -> tuple[PlanVersion, dict[str, Any]]:
    version = require_active(db, PlanVersion, version_id, for_update=True)
    if expected_version is not None and version.version != expected_version:
        raise OptimisticLockError(serialize_plan_version(version))
    if version.status != "draft":
        raise PublishedPlanImmutableError("Published plan versions and their full trees are immutable")
    issues = validate_plan_version(db, version)
    if issues:
        raise PlanValidationError(issues)
    before = serialize_plan_version(version)
    version.status = "published"
    version.published_at = utcnow()
    version.published_by_user_id = actor_user_id
    version.version += 1
    db.flush()
    return version, before


def create_assignment(
    db: Session,
    payload: AssignmentCreate,
    *,
    actor_user_id: str,
) -> tuple[PlanAssignment, list[PlanAssignment]]:
    assignment_user_id = str(payload.user_id) if payload.user_id else actor_user_id
    user = require_active(db, User, assignment_user_id, for_update=True)
    if not user.is_active:
        raise PlanValidationError(
            [{"code": "user_inactive", "path": "user_id", "message": "Assignment user is inactive"}]
        )
    version = require_active(db, PlanVersion, str(payload.plan_version_id))
    if version.status != "published":
        raise PlanValidationError(
            [
                {
                    "code": "version_not_published",
                    "path": "plan_version_id",
                    "message": "Only a published plan version can be assigned",
                }
            ]
        )

    status = payload.status.casefold()
    if payload.is_active is not None:
        status = "active" if payload.is_active else "cancelled"
    if status not in {"scheduled", "active", "completed", "cancelled"}:
        raise PlanValidationError(
            [{"code": "status_invalid", "path": "status", "message": "Assignment status is invalid"}]
        )

    previous: list[PlanAssignment] = []
    if status == "active":
        previous = list(
            db.scalars(
                select(PlanAssignment)
                .where(
                    PlanAssignment.user_id == user.id,
                    PlanAssignment.status == "active",
                    PlanAssignment.deleted_at.is_(None),
                )
                .order_by(PlanAssignment.starts_on, PlanAssignment.id)
                .with_for_update()
            ).all()
        )
        for assignment in previous:
            if payload.starts_on > assignment.starts_on:
                assignment.status = "completed"
                assignment.ends_on = min(
                    assignment.ends_on or (payload.starts_on - timedelta(days=1)),
                    payload.starts_on - timedelta(days=1),
                )
            else:
                assignment.status = "cancelled"
            assignment.version += 1

    assignment = PlanAssignment(
        id=str(payload.id) if payload.id else uuid4_str(),
        user_id=user.id,
        plan_version_id=version.id,
        status=status,
        starts_on=payload.starts_on,
        ends_on=payload.ends_on,
        assigned_at=payload.assigned_at or utcnow(),
        assigned_by_user_id=actor_user_id,
        settings_json=payload.settings_json or {},
    )
    db.add(assignment)
    db.flush()
    return assignment, previous


def _version_for_tree_entity(session: Session, entity: Any) -> PlanVersion | None:
    if isinstance(entity, PlanDay):
        return entity.plan_version or session.get(PlanVersion, entity.plan_version_id)
    if isinstance(entity, PlanSlot):
        day = entity.day or session.get(PlanDay, entity.plan_day_id)
        if day is None:
            return None
        return day.plan_version or session.get(PlanVersion, day.plan_version_id)
    if isinstance(entity, PlanSlotOption):
        slot = entity.slot or session.get(PlanSlot, entity.plan_slot_id)
        if slot is None:
            return None
        day = slot.day or session.get(PlanDay, slot.plan_day_id)
        if day is None:
            return None
        return day.plan_version or session.get(PlanVersion, day.plan_version_id)
    return None


@event.listens_for(Session, "before_flush")
def _protect_published_plan_tree(session: Session, _flush_context: Any, _instances: Any) -> None:
    """Defense in depth: no ORM caller can mutate a published plan tree."""

    for entity in session.new.union(session.dirty).union(session.deleted):
        if isinstance(entity, PlanVersion):
            if entity in session.new:
                # Initial fixtures/imports may materialize an already-published
                # snapshot. Immutability applies once that snapshot is persisted.
                continue
            state = inspect(entity)
            status_history = state.attrs.status.history
            old_statuses = set(status_history.deleted)
            publishing_now = (
                old_statuses == {"draft"}
                and set(status_history.added) == {"published"}
            )
            immutable_change = bool(old_statuses.intersection({"published", "archived"}))
            immutable_change = immutable_change or (
                entity.status in {"published", "archived"}
                and not publishing_now
                and session.is_modified(entity, include_collections=False)
            )
            if entity in session.deleted and entity.status in {"published", "archived"}:
                immutable_change = True
            if immutable_change:
                raise PublishedPlanImmutableError("Published plan versions are immutable")
            continue
        if isinstance(entity, (PlanDay, PlanSlot, PlanSlotOption)):
            version = _version_for_tree_entity(session, entity)
            if (
                version is not None
                and version not in session.new
                and version.status in {"published", "archived"}
            ):
                # Publishing a draft changes only its parent status and does not put
                # unchanged children in any of these collections.
                raise PublishedPlanImmutableError("The full published plan tree is immutable")

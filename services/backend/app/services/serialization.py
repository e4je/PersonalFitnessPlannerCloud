from __future__ import annotations

import json
from typing import Any

from app.models import Equipment, Exercise, PlanAssignment, PlanSlotOption, PlanVersion, User


def _sync_fields(entity: Any) -> dict[str, Any]:
    return {
        "id": entity.id,
        "version": entity.version,
        "created_at": entity.created_at,
        "updated_at": entity.updated_at,
        "deleted_at": entity.deleted_at,
    }


def user_to_dict(user: User) -> dict[str, Any]:
    return {
        **_sync_fields(user),
        "email": user.email,
        "display_name": user.display_name,
        "timezone": user.timezone,
        "weight_unit": user.weight_unit,
    }


def user_permissions(user: User) -> list[str]:
    if user.is_superuser:
        return ["*"]
    return sorted({permission for role in user.roles for permission in role.permissions_json})


def equipment_to_dict(equipment: Equipment) -> dict[str, Any]:
    return {
        **_sync_fields(equipment),
        "code": equipment.code,
        "name": equipment.name,
        "category": equipment.category,
        "brand": equipment.brand,
        "model": equipment.model,
        "notes": equipment.notes or equipment.description,
    }


def exercise_to_dict(exercise: Exercise) -> dict[str, Any]:
    equipment_id = exercise.equipment_links[0].equipment_id if exercise.equipment_links else None
    return {
        **_sync_fields(exercise),
        "code": exercise.code,
        "name": exercise.name,
        "body_part": exercise.body_part or "",
        "equipment_id": equipment_id,
        "default_sets": exercise.default_sets or 0,
        "rep_min": exercise.rep_min or 0,
        "rep_max": exercise.rep_max or 0,
        "rep_unit": exercise.rep_unit,
        "cues": "；".join(cue.text for cue in exercise.cues),
        "common_mistakes": "；".join(exercise.common_mistakes_json or []),
        "definition_version": exercise.version,
        "alternatives": [
            {
                **_sync_fields(link),
                "exercise_id": link.exercise_id,
                "alternative_exercise_id": link.alternative_exercise_id,
                "sort_order": link.priority,
            }
            for link in exercise.alternatives
            if link.deleted_at is None
        ],
    }


def _option_equipment_id(option: PlanSlotOption) -> str | None:
    links = option.exercise.equipment_links
    if not links:
        return None
    requirement = (option.prescription_json or {}).get("equipment_requirement")
    if requirement:
        for link in links:
            if link.notes == requirement:
                return link.equipment_id
    return links[0].equipment_id


def plan_version_to_dict(plan_version: PlanVersion, *, include_snapshot: bool = True) -> dict[str, Any]:
    days: list[dict[str, Any]] = []
    for day in plan_version.days:
        if day.deleted_at is not None:
            continue
        slots: list[dict[str, Any]] = []
        for slot in day.slots:
            if slot.deleted_at is not None:
                continue
            options: list[dict[str, Any]] = []
            for option in slot.options:
                if option.deleted_at is not None:
                    continue
                prescription = option.prescription_json or {}
                options.append(
                    {
                        **_sync_fields(option),
                        "plan_slot_id": option.plan_slot_id,
                        "exercise_id": option.exercise_id,
                        "equipment_id": _option_equipment_id(option),
                        "is_preferred": option.is_preferred,
                        "sort_order": option.sort_order,
                        "set_count": option.set_count,
                        "rep_min": option.reps_min or 0,
                        "rep_max": option.reps_max or 0,
                        "rep_unit": prescription.get("rep_unit", "reps"),
                        "duration_seconds_min": option.duration_seconds_min,
                        "duration_seconds_max": option.duration_seconds_max,
                        "rir_min": float(option.rir_min) if option.rir_min is not None else None,
                        "rir_max": float(option.rir_max) if option.rir_max is not None else None,
                        "is_per_side": option.is_per_side,
                        "prescription_text": prescription.get("text"),
                    }
                )
            slots.append(
                {
                    **_sync_fields(slot),
                    "plan_day_id": slot.plan_day_id,
                    "position": slot.sort_order,
                    "body_part": slot.name,
                    "cues": slot.notes or "",
                    "options": options,
                }
            )
        days.append(
            {
                **_sync_fields(day),
                "plan_version_id": day.plan_version_id,
                "code": day.day_code,
                "name": day.name,
                "sort_order": day.sort_order,
                "slots": slots,
            }
        )
    result: dict[str, Any] = {
        **_sync_fields(plan_version),
        "plan_id": plan_version.training_plan_id,
        "plan_name": plan_version.plan.name,
        "version_number": plan_version.version_number,
        "status": plan_version.status,
        "published_at": plan_version.published_at,
        "weekly_frequency": plan_version.weekly_frequency,
        "min_rest_days": plan_version.min_rest_days,
        "fatigue_threshold": plan_version.fatigue_threshold,
        "initial_reduced_weeks": plan_version.initial_reduced_weeks,
        "initial_set_count": plan_version.initial_set_count,
        "rules": plan_version.config_json or {},
        "days": days,
    }
    result["snapshot_json"] = (
        json.dumps(result, ensure_ascii=False, default=str, separators=(",", ":"))
        if include_snapshot
        else None
    )
    return result


def assignment_to_dict(assignment: PlanAssignment) -> dict[str, Any]:
    return {
        **_sync_fields(assignment),
        "user_id": assignment.user_id,
        "plan_version_id": assignment.plan_version_id,
        "start_local_date": assignment.starts_on,
        "end_local_date": assignment.ends_on,
        "is_active": assignment.status == "active",
        "status": assignment.status,
    }

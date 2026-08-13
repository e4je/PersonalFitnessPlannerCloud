from __future__ import annotations

import base64
import json
from datetime import UTC, date, datetime
from typing import Any
from uuid import UUID

from fastapi import APIRouter, Depends, Header, HTTPException, Query, Response, status
from sqlalchemy import and_, or_, select
from sqlalchemy.orm import Session, selectinload

from app.api.dependencies import get_current_user
from app.db.base import utcnow
from app.db.session import get_db
from app.models import (
    Equipment,
    Exercise,
    PlanAssignment,
    PlanDay,
    PlanSlot,
    PlanSlotOption,
    PlanVersion,
    TrainingPlan,
    User,
    WorkoutSession,
    WorkoutSet,
)
from app.schemas.workouts import (
    WorkoutSessionOut,
    WorkoutSessionPage,
    WorkoutSessionPatch,
    WorkoutSessionUpsert,
    WorkoutSetOut,
    WorkoutSetUpsert,
)
from app.services.idempotency import (
    IdempotencyConflictError,
    find_idempotent_response,
    store_idempotent_response,
)
from app.services.sync import record_conflict_audit, record_sync_change, version_conflict


router = APIRouter(prefix="/workout-sessions", tags=["workout sessions"])


def _database_status(value: str) -> str:
    normalized = value.strip().upper()
    return {
        "PLANNED": "planned",
        "IN_PROGRESS": "in_progress",
        "COMPLETED": "completed",
        "CANCELLED": "cancelled",
        "ENDED_EARLY": "cancelled",
        "DELETED": "cancelled",
    }[normalized]


def _as_object(value: Any) -> dict[str, Any]:
    if isinstance(value, dict):
        return dict(value)
    if isinstance(value, str):
        try:
            parsed = json.loads(value)
            return dict(parsed) if isinstance(parsed, dict) else {}
        except json.JSONDecodeError:
            return {}
    return {}


def _as_snapshot_string(value: Any) -> str:
    if isinstance(value, str):
        return value
    return json.dumps(value or {}, ensure_ascii=False, separators=(",", ":"))


def _string_id(value: Any) -> str | None:
    return str(value) if value is not None else None


def _invalid_plan_reference(message: str) -> HTTPException:
    # Use the same response for unknown, deleted and unauthorized IDs so this
    # validation cannot be used to enumerate another user's assignments/plans.
    return HTTPException(
        status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
        detail={"code": "invalid_plan_reference", "message": message},
    )


def _authorized_plan_version(
    db: Session,
    *,
    user_id: str,
    plan_version_id: str,
) -> PlanVersion:
    plan_version = db.scalar(
        select(PlanVersion).where(
            PlanVersion.id == plan_version_id,
            PlanVersion.deleted_at.is_(None),
            PlanVersion.status == "published",
        )
    )
    if plan_version is None:
        raise _invalid_plan_reference("Plan version is not available")
    plan = db.scalar(
        select(TrainingPlan).where(
            TrainingPlan.id == plan_version.training_plan_id,
            TrainingPlan.deleted_at.is_(None),
            TrainingPlan.is_active.is_(True),
        )
    )
    assigned = db.scalar(
        select(PlanAssignment.id).where(
            PlanAssignment.user_id == user_id,
            PlanAssignment.plan_version_id == plan_version.id,
            PlanAssignment.deleted_at.is_(None),
        ).limit(1)
    )
    if plan is None or not (
        plan.is_system or plan.owner_user_id == user_id or assigned is not None
    ):
        raise _invalid_plan_reference("Plan version is not available")
    return plan_version


def _validate_session_plan_references(
    db: Session,
    *,
    user_id: str,
    session: WorkoutSession,
) -> None:
    assignment_version_id: str | None = None
    if session.plan_assignment_id is not None:
        assignment = db.scalar(
            select(PlanAssignment).where(
                PlanAssignment.id == session.plan_assignment_id,
                PlanAssignment.user_id == user_id,
                PlanAssignment.deleted_at.is_(None),
            )
        )
        if assignment is None:
            raise _invalid_plan_reference("Plan assignment is not available")
        assignment_version_id = assignment.plan_version_id

    day_version_id: str | None = None
    if session.plan_day_id is not None:
        day = db.scalar(
            select(PlanDay).where(
                PlanDay.id == session.plan_day_id,
                PlanDay.deleted_at.is_(None),
            )
        )
        if day is None:
            raise _invalid_plan_reference("Plan day is not available")
        day_version_id = day.plan_version_id

    version_ids = {
        value
        for value in (
            session.plan_version_id,
            assignment_version_id,
            day_version_id,
        )
        if value is not None
    }
    if len(version_ids) > 1:
        raise _invalid_plan_reference(
            "Assignment, version and day must belong to the same plan tree"
        )
    if version_ids:
        session.plan_version_id = version_ids.pop()
        _authorized_plan_version(
            db,
            user_id=user_id,
            plan_version_id=session.plan_version_id,
        )


def _validate_set_plan_reference(
    db: Session,
    *,
    user_id: str,
    session: WorkoutSession,
    incoming: WorkoutSetUpsert,
) -> None:
    slot_id = _string_id(incoming.plan_slot_id)
    option: PlanSlotOption | None = None
    if incoming.source_plan_slot_option_id is not None:
        option = db.scalar(
            select(PlanSlotOption).where(
                PlanSlotOption.id == str(incoming.source_plan_slot_option_id),
                PlanSlotOption.deleted_at.is_(None),
            )
        )
        if option is None:
            raise _invalid_plan_reference("Plan slot option is not available")
        if slot_id is not None and option.plan_slot_id != slot_id:
            raise _invalid_plan_reference("Plan slot option does not belong to the slot")
        slot_id = option.plan_slot_id
        incoming.plan_slot_id = UUID(slot_id)

    if slot_id is None:
        return
    slot = db.scalar(
        select(PlanSlot).where(
            PlanSlot.id == slot_id,
            PlanSlot.deleted_at.is_(None),
        )
    )
    if slot is None:
        raise _invalid_plan_reference("Plan slot is not available")
    day = db.scalar(
        select(PlanDay).where(
            PlanDay.id == slot.plan_day_id,
            PlanDay.deleted_at.is_(None),
        )
    )
    if day is None:
        raise _invalid_plan_reference("Plan day is not available")

    if option is None:
        option = db.scalar(
            select(PlanSlotOption).where(
                PlanSlotOption.plan_slot_id == slot.id,
                PlanSlotOption.exercise_id == str(incoming.exercise_id),
                PlanSlotOption.deleted_at.is_(None),
            )
        )
        if option is None:
            raise _invalid_plan_reference("Exercise is not an option for the plan slot")
    elif option.exercise_id != str(incoming.exercise_id):
        raise _invalid_plan_reference("Exercise does not match the plan slot option")

    expected_equipment_id = _as_object(option.prescription_json).get("equipment_id")
    if (
        incoming.equipment_id is not None
        and expected_equipment_id is not None
        and str(incoming.equipment_id) != str(expected_equipment_id)
    ):
        raise _invalid_plan_reference("Equipment does not match the plan slot option")

    if session.plan_day_id is not None and session.plan_day_id != day.id:
        raise _invalid_plan_reference("Plan slot does not belong to the workout day")
    if session.plan_version_id is not None and session.plan_version_id != day.plan_version_id:
        raise _invalid_plan_reference("Plan slot does not belong to the workout version")
    session.plan_day_id = day.id
    session.plan_version_id = day.plan_version_id
    _validate_session_plan_references(db, user_id=user_id, session=session)


def _exercise_snapshot(
    db: Session,
    *,
    exercise_id: str,
    equipment_id: str | None,
) -> dict[str, Any]:
    exercise = db.get(Exercise, exercise_id)
    if exercise is None:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={"code": "unknown_exercise", "exercise_id": exercise_id},
        )
    equipment = db.get(Equipment, equipment_id) if equipment_id else None
    if equipment_id and equipment is None:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={"code": "unknown_equipment", "equipment_id": equipment_id},
        )
    return {
        "id": exercise.id,
        "code": exercise.code,
        "name": exercise.name,
        "description": exercise.description,
        "body_part": exercise.body_part,
        "movement_pattern": exercise.movement_pattern,
        "difficulty": exercise.difficulty,
        "cues": [cue.text for cue in exercise.cues if cue.deleted_at is None],
        "version": exercise.version,
        "equipment": (
            {
                "id": equipment.id,
                "code": equipment.code,
                "name": equipment.name,
                "category": equipment.category,
                "version": equipment.version,
            }
            if equipment is not None
            else None
        ),
    }


def serialize_workout_set(item: WorkoutSet) -> WorkoutSetOut:
    prescription = _as_object(item.prescription_snapshot_json)
    exercise_snapshot = _as_object(item.exercise_snapshot_json)
    set_type = (item.set_type or "WORKING").upper()
    return WorkoutSetOut(
        id=item.id,
        session_id=item.workout_session_id,
        plan_slot_id=item.plan_slot_id,
        source_plan_slot_option_id=prescription.get("source_plan_slot_option_id"),
        exercise_id=item.exercise_id or exercise_snapshot.get("id") or exercise_snapshot.get("exercise_id"),
        equipment_id=prescription.get("equipment_id"),
        set_number=item.set_number,
        weight_kg=float(item.weight_kg) if item.weight_kg is not None else None,
        reps=item.reps,
        duration_seconds=item.duration_seconds,
        distance_meters=float(item.distance_meters) if item.distance_meters is not None else None,
        is_warmup=bool(prescription.get("is_warmup", set_type == "WARMUP")),
        set_type=set_type,
        rir=item.rir,
        quality=prescription.get("quality"),
        pain=bool(prescription.get("pain", False)),
        notes=item.notes,
        completed=bool(prescription.get("completed", item.completed_at is not None)),
        completed_at=item.completed_at,
        version=item.version,
        created_at=item.created_at,
        updated_at=item.updated_at,
        deleted_at=item.deleted_at,
    )


def serialize_workout(item: WorkoutSession, *, idempotency_key: str | None = None) -> WorkoutSessionOut:
    metadata = _as_object(item.metadata_json)
    sets = sorted(
        item.sets,
        key=lambda row: (row.plan_slot_id or "", row.set_number, row.id),
    )
    source = item.source_device or "android"
    return WorkoutSessionOut(
        id=item.id,
        user_id=item.user_id,
        client_id=item.client_id,
        source=source,
        source_device=source,
        client_version=item.client_version,
        plan_assignment_id=item.plan_assignment_id,
        plan_version_id=item.plan_version_id,
        plan_day_id=item.plan_day_id,
        plan_day_code=metadata.get("plan_day_code"),
        local_date=item.local_date,
        timezone=metadata.get("timezone", "UTC"),
        started_at=item.started_at,
        completed_at=item.completed_at,
        status="DELETED" if item.deleted_at is not None else metadata.get(
            "wire_status", item.status.upper()
        ),
        is_full_body=bool(metadata.get("is_full_body", True)),
        training_week=item.training_week,
        ab_state=item.ab_state,
        plan_snapshot_json=_as_snapshot_string(item.plan_snapshot_json),
        idempotency_key=idempotency_key or metadata.get("idempotency_key"),
        metadata={key: value for key, value in metadata.items() if key not in {
            "plan_day_code", "timezone", "is_full_body", "idempotency_key", "wire_status"
        }},
        notes=item.notes,
        sets=[serialize_workout_set(row) for row in sets],
        version=item.version,
        created_at=item.created_at,
        updated_at=item.updated_at,
        deleted_at=item.deleted_at,
    )


def _load_workout(
    db: Session,
    workout_id: str,
    *,
    for_update: bool = False,
) -> WorkoutSession | None:
    query = (
        select(WorkoutSession)
        .options(selectinload(WorkoutSession.sets))
        .where(WorkoutSession.id == workout_id)
    )
    if for_update:
        # ``populate_existing`` is essential after waiting on a row lock: a
        # long-lived sync Session may already have an older identity-map copy.
        query = query.with_for_update().execution_options(populate_existing=True)
    return db.scalar(query)


def _conflict(
    db: Session,
    *,
    user_id: str,
    session: WorkoutSession,
    attempted: dict[str, Any],
    reason: str,
) -> None:
    server_copy = serialize_workout(session).model_dump(mode="json")
    # A set-level conflict may be discovered after SQLAlchemy has staged parent
    # edits. Roll those edits back before persisting only the conflict audit.
    db.rollback()
    record_conflict_audit(
        db,
        actor_user_id=user_id,
        entity_type="workout_session",
        entity_id=session.id,
        before=server_copy,
        attempted=attempted,
        reason=reason,
    )
    db.commit()
    raise HTTPException(
        status_code=status.HTTP_409_CONFLICT,
        detail={"code": reason, "server_copy": server_copy},
    )


def _upsert_set(
    db: Session,
    *,
    session: WorkoutSession,
    incoming: WorkoutSetUpsert,
    actor_user_id: str,
) -> WorkoutSet:
    with db.no_autoflush:
        _validate_set_plan_reference(
            db,
            user_id=actor_user_id,
            session=session,
            incoming=incoming,
        )
    set_id = str(incoming.id)
    exercise_id = str(incoming.exercise_id)
    equipment_id = _string_id(incoming.equipment_id)
    # Every mutation path first locks the parent session, then its set. Keeping
    # that order avoids deadlocks between whole-workout and standalone-set
    # operations while making a supplied expected_version check atomic.
    existing = db.scalar(
        select(WorkoutSet)
        .where(
            WorkoutSet.workout_session_id == session.id,
            or_(WorkoutSet.id == set_id, WorkoutSet.client_set_id == set_id),
        )
        .with_for_update()
        .execution_options(populate_existing=True)
    )
    if existing is None:
        global_match = db.scalar(
            select(WorkoutSet)
            .where(WorkoutSet.id == set_id)
            .with_for_update()
            .execution_options(populate_existing=True)
        )
        if global_match is not None and global_match.workout_session_id != session.id:
            owner_session = _load_workout(db, global_match.workout_session_id)
            _conflict(
                db,
                user_id=actor_user_id,
                # The incoming parent may not have been flushed yet, so its
                # defaults/timestamps are not safe to serialize. Return the
                # persisted server owner of the duplicate set UUID instead.
                session=owner_session or session,
                attempted=incoming.model_dump(mode="json"),
                reason="duplicate_set_uuid",
            )
        existing = WorkoutSet(
            id=set_id,
            workout_session_id=session.id,
            client_set_id=set_id,
            exercise_id=exercise_id,
            plan_slot_id=_string_id(incoming.plan_slot_id),
            set_number=incoming.set_number,
            set_type=(incoming.set_type or ("WARMUP" if incoming.is_warmup else "WORKING")).lower(),
            weight_kg=incoming.weight_kg,
            reps=incoming.reps,
            duration_seconds=incoming.duration_seconds,
            distance_meters=incoming.distance_meters,
            rir=incoming.rir,
            completed_at=incoming.completed_at,
            notes=incoming.notes,
            exercise_snapshot_json=_exercise_snapshot(
                db, exercise_id=exercise_id, equipment_id=equipment_id
            ),
            prescription_snapshot_json={},
        )
        session.sets.append(existing)
    else:
        if incoming.expected_version is not None and incoming.expected_version != existing.version:
            _conflict(
                db,
                user_id=actor_user_id,
                session=session,
                attempted=incoming.model_dump(mode="json"),
                reason="set_version_conflict",
            )
        previous_prescription = _as_object(existing.prescription_snapshot_json)
        exercise_changed = existing.exercise_id != exercise_id
        equipment_changed = previous_prescription.get("equipment_id") != equipment_id
        existing.exercise_id = exercise_id
        existing.plan_slot_id = _string_id(incoming.plan_slot_id)
        existing.set_number = incoming.set_number
        existing.set_type = (
            incoming.set_type or ("WARMUP" if incoming.is_warmup else "WORKING")
        ).lower()
        existing.weight_kg = incoming.weight_kg
        existing.reps = incoming.reps
        existing.duration_seconds = incoming.duration_seconds
        existing.distance_meters = incoming.distance_meters
        existing.rir = incoming.rir
        existing.completed_at = incoming.completed_at
        existing.notes = incoming.notes
        if exercise_changed or equipment_changed or not _as_object(existing.exercise_snapshot_json):
            existing.exercise_snapshot_json = _exercise_snapshot(
                db, exercise_id=exercise_id, equipment_id=equipment_id
            )
        existing.version += 1

    existing.prescription_snapshot_json = {
        **_as_object(existing.prescription_snapshot_json),
        "source_plan_slot_option_id": _string_id(incoming.source_plan_slot_option_id),
        "equipment_id": _string_id(incoming.equipment_id),
        "is_warmup": incoming.is_warmup,
        "quality": incoming.quality,
        "pain": incoming.pain,
        "completed": incoming.completed,
    }
    if incoming.deleted_at is not None:
        existing.deleted_at = incoming.deleted_at
    elif existing.deleted_at is not None:
        existing.deleted_at = None
    return existing


def apply_workout_upsert(
    db: Session,
    *,
    user: User,
    payload: WorkoutSessionUpsert | WorkoutSessionPatch,
    create_only: bool = False,
    idempotency_key: str | None = None,
) -> WorkoutSessionOut:
    payload_id = str(payload.id) if payload.id is not None else None
    if payload_id is None:
        raise HTTPException(status_code=422, detail={"code": "id_required"})
    attempted = payload.model_dump(mode="json", exclude_unset=True)
    session = _load_workout(db, payload_id, for_update=True)
    is_new = session is None

    if session is not None and session.user_id != user.id:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Workout session not found")
    if session is not None and create_only:
        _conflict(
            db,
            user_id=user.id,
            session=session,
            attempted=attempted,
            reason="duplicate_client_uuid",
        )
    if session is None:
        if isinstance(payload, WorkoutSessionPatch):
            raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Workout session not found")
        client_id = str(payload.client_id or payload.id)
        duplicate_client = db.scalar(
            select(WorkoutSession)
            .options(selectinload(WorkoutSession.sets))
            .where(
                WorkoutSession.user_id == user.id,
                WorkoutSession.client_id == client_id,
            )
        )
        if duplicate_client is not None:
            _conflict(
                db,
                user_id=user.id,
                session=duplicate_client,
                attempted=attempted,
                reason="duplicate_client_uuid",
            )
        metadata = dict(payload.metadata)
        metadata.update(
            {
                "plan_day_code": payload.plan_day_code,
                "timezone": payload.timezone,
                "is_full_body": payload.is_full_body,
                "wire_status": payload.status,
            }
        )
        if idempotency_key:
            metadata["idempotency_key"] = idempotency_key
        session = WorkoutSession(
            id=payload_id,
            user_id=user.id,
            client_id=client_id,
            source_device=payload.source_device or payload.source or "android",
            client_version=payload.client_version,
            plan_assignment_id=_string_id(payload.plan_assignment_id),
            plan_version_id=_string_id(payload.plan_version_id),
            plan_day_id=_string_id(payload.plan_day_id),
            local_date=payload.local_date,
            status=_database_status(payload.status),
            training_week=payload.training_week,
            ab_state=payload.ab_state,
            started_at=payload.started_at,
            completed_at=payload.completed_at,
            notes=payload.notes,
            plan_snapshot_json=json.loads(payload.plan_snapshot_json),
            metadata_json=metadata,
            deleted_at=payload.deleted_at,
        )
        db.add(session)
    else:
        if payload.expected_version is not None and payload.expected_version != session.version:
            version_conflict(
                db,
                actor_user_id=user.id,
                entity_type="workout_session",
                entity_id=session.id,
                expected_version=payload.expected_version,
                server_copy=serialize_workout(session).model_dump(mode="json"),
                attempted=attempted,
            )
        fields = payload.model_fields_set
        if "plan_assignment_id" in fields and payload.plan_assignment_id is not None:
            if "plan_version_id" not in fields:
                session.plan_version_id = None
            if "plan_day_id" not in fields:
                session.plan_day_id = None
        elif "plan_day_id" in fields and payload.plan_day_id is not None:
            if "plan_version_id" not in fields:
                session.plan_version_id = None
        elif "plan_version_id" in fields and "plan_day_id" not in fields:
            session.plan_day_id = None
        direct = {
            "client_version": "client_version",
            "local_date": "local_date",
            "training_week": "training_week",
            "ab_state": "ab_state",
            "started_at": "started_at",
            "completed_at": "completed_at",
            "notes": "notes",
            "deleted_at": "deleted_at",
        }
        foreign_ids = {
            "client_id": "client_id",
            "plan_assignment_id": "plan_assignment_id",
            "plan_version_id": "plan_version_id",
            "plan_day_id": "plan_day_id",
        }
        for field_name, attribute in direct.items():
            if field_name in fields:
                setattr(session, attribute, getattr(payload, field_name))
        for field_name, attribute in foreign_ids.items():
            if field_name in fields:
                setattr(session, attribute, _string_id(getattr(payload, field_name)))
        if "status" in fields and payload.status is not None:
            session.status = _database_status(payload.status)
        if "source_device" in fields or "source" in fields:
            session.source_device = payload.source_device or payload.source or session.source_device
        if "plan_snapshot_json" in fields and payload.plan_snapshot_json is not None:
            session.plan_snapshot_json = json.loads(payload.plan_snapshot_json)
        metadata = _as_object(session.metadata_json)
        if "metadata" in fields and payload.metadata is not None:
            metadata.update(payload.metadata)
        for key in ("plan_day_code", "timezone", "is_full_body"):
            if key in fields:
                metadata[key] = getattr(payload, key)
        if "status" in fields and payload.status is not None:
            metadata["wire_status"] = payload.status
        if idempotency_key:
            metadata["idempotency_key"] = idempotency_key
        session.metadata_json = metadata
        session.version += 1

    with db.no_autoflush:
        _validate_session_plan_references(db, user_id=user.id, session=session)

    if payload.sets is not None:
        for incoming_set in payload.sets:
            _upsert_set(db, session=session, incoming=incoming_set, actor_user_id=user.id)

    if payload.deleted_at is not None:
        session.status = "cancelled"
    db.flush()
    result = serialize_workout(session, idempotency_key=idempotency_key)
    record_sync_change(
        db,
        entity_type="workout_session",
        entity_id=session.id,
        entity_version=session.version,
        operation="DELETE" if session.deleted_at is not None else "UPSERT",
        payload=result.model_dump(mode="json"),
        actor_user_id=user.id,
    )
    # New sessions start at version 1; updates always advance exactly once.
    if is_new:
        db.flush()
    return result


def _idempotency_conflict(error: IdempotencyConflictError) -> HTTPException:
    return HTTPException(
        status_code=status.HTTP_409_CONFLICT,
        detail={
            "code": "idempotency_key_reused",
            "message": str(error),
            "idempotency_key": error.key,
        },
    )


def _encode_page_cursor(item: WorkoutSession) -> str:
    raw = json.dumps([item.updated_at.isoformat(), item.id], separators=(",", ":"))
    return base64.urlsafe_b64encode(raw.encode()).decode().rstrip("=")


def _decode_page_cursor(cursor: str) -> tuple[datetime, str]:
    try:
        padded = cursor + "=" * (-len(cursor) % 4)
        timestamp, item_id = json.loads(base64.urlsafe_b64decode(padded).decode())
        parsed = datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=UTC)
        return parsed, str(item_id)
    except (ValueError, TypeError, json.JSONDecodeError) as error:
        raise HTTPException(status_code=400, detail={"code": "invalid_cursor"}) from error


@router.get("", response_model=WorkoutSessionPage)
def list_workout_sessions(
    cursor: str | None = None,
    limit: int = Query(default=50, ge=1, le=200),
    local_date_from: date | None = None,
    local_date_to: date | None = None,
    include_deleted: bool = False,
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user),
) -> WorkoutSessionPage:
    query = (
        select(WorkoutSession)
        .options(selectinload(WorkoutSession.sets))
        .where(WorkoutSession.user_id == current_user.id)
    )
    if not include_deleted:
        query = query.where(WorkoutSession.deleted_at.is_(None))
    if local_date_from is not None:
        query = query.where(WorkoutSession.local_date >= local_date_from)
    if local_date_to is not None:
        query = query.where(WorkoutSession.local_date <= local_date_to)
    if cursor:
        updated_at, item_id = _decode_page_cursor(cursor)
        query = query.where(
            or_(
                WorkoutSession.updated_at < updated_at,
                and_(WorkoutSession.updated_at == updated_at, WorkoutSession.id < item_id),
            )
        )
    rows = list(
        db.scalars(
            query.order_by(WorkoutSession.updated_at.desc(), WorkoutSession.id.desc()).limit(limit + 1)
        ).unique()
    )
    has_more = len(rows) > limit
    page = rows[:limit]
    return WorkoutSessionPage(
        items=[serialize_workout(row) for row in page],
        cursor=cursor,
        next_cursor=_encode_page_cursor(page[-1]) if has_more and page else None,
        has_more=has_more,
    )


@router.get("/{workout_id}", response_model=WorkoutSessionOut)
def get_workout_session(
    workout_id: str,
    include_deleted: bool = False,
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user),
) -> WorkoutSessionOut:
    item = _load_workout(db, workout_id)
    if item is None or item.user_id != current_user.id or (item.deleted_at and not include_deleted):
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Workout session not found")
    return serialize_workout(item)


@router.post("", response_model=WorkoutSessionOut, status_code=status.HTTP_201_CREATED)
def create_workout_session(
    payload: WorkoutSessionUpsert,
    idempotency_key: str = Header(alias="Idempotency-Key", min_length=1, max_length=128),
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user),
) -> WorkoutSessionOut:
    try:
        replay = find_idempotent_response(
            db, user_id=current_user.id, key=idempotency_key, payload=payload
        )
    except IdempotencyConflictError as error:
        raise _idempotency_conflict(error) from error
    if replay is not None:
        return WorkoutSessionOut.model_validate(replay.body)

    result = apply_workout_upsert(
        db,
        user=current_user,
        payload=payload,
        create_only=True,
        idempotency_key=idempotency_key,
    )
    store_idempotent_response(
        db,
        user_id=current_user.id,
        key=idempotency_key,
        payload=payload,
        status_code=201,
        body=result,
        resource_type="workout_session",
        resource_id=str(result.id),
    )
    db.commit()
    return result


@router.patch("/{workout_id}", response_model=WorkoutSessionOut)
def patch_workout_session(
    workout_id: str,
    payload: WorkoutSessionPatch,
    idempotency_key: str = Header(alias="Idempotency-Key", min_length=1, max_length=128),
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user),
) -> WorkoutSessionOut:
    try:
        path_id = UUID(workout_id)
    except ValueError as error:
        raise HTTPException(status_code=422, detail={"code": "invalid_workout_id"}) from error
    if payload.id is not None and payload.id != path_id:
        raise HTTPException(status_code=422, detail={"code": "workout_id_mismatch"})
    payload.id = path_id
    try:
        replay = find_idempotent_response(
            db, user_id=current_user.id, key=idempotency_key, payload=payload
        )
    except IdempotencyConflictError as error:
        raise _idempotency_conflict(error) from error
    if replay is not None:
        return WorkoutSessionOut.model_validate(replay.body)

    result = apply_workout_upsert(
        db,
        user=current_user,
        payload=payload,
        create_only=False,
        idempotency_key=idempotency_key,
    )
    store_idempotent_response(
        db,
        user_id=current_user.id,
        key=idempotency_key,
        payload=payload,
        status_code=200,
        body=result,
        resource_type="workout_session",
        resource_id=workout_id,
    )
    db.commit()
    return result


def _header_version(if_match: str | None) -> int | None:
    if not if_match:
        return None
    normalized = if_match.strip().removeprefix("W/").strip('"')
    try:
        return int(normalized)
    except ValueError as error:
        raise HTTPException(status_code=400, detail={"code": "invalid_if_match"}) from error


@router.delete("/{workout_id}", status_code=status.HTTP_204_NO_CONTENT)
def delete_workout_session(
    workout_id: str,
    expected_version: int | None = Query(default=None, ge=1),
    if_match: str | None = Header(default=None, alias="If-Match"),
    idempotency_key: str = Header(alias="Idempotency-Key", min_length=1, max_length=128),
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user),
) -> Response:
    version = expected_version if expected_version is not None else _header_version(if_match)
    request_payload = {"id": workout_id, "expected_version": version}
    try:
        replay = find_idempotent_response(
            db, user_id=current_user.id, key=idempotency_key, payload=request_payload
        )
    except IdempotencyConflictError as error:
        raise _idempotency_conflict(error) from error
    if replay is not None:
        return Response(status_code=status.HTTP_204_NO_CONTENT)

    item = _load_workout(db, workout_id, for_update=True)
    if item is None or item.user_id != current_user.id:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Workout session not found")
    if version is not None and version != item.version:
        version_conflict(
            db,
            actor_user_id=current_user.id,
            entity_type="workout_session",
            entity_id=item.id,
            expected_version=version,
            server_copy=serialize_workout(item).model_dump(mode="json"),
            attempted=request_payload,
        )
    if item.deleted_at is None:
        now = utcnow()
        item.deleted_at = now
        item.status = "cancelled"
        item.version += 1
        for workout_set in item.sets:
            if workout_set.deleted_at is None:
                workout_set.deleted_at = now
                workout_set.version += 1
        db.flush()
        record_sync_change(
            db,
            entity_type="workout_session",
            entity_id=item.id,
            entity_version=item.version,
            operation="DELETE",
            payload=serialize_workout(item).model_dump(mode="json"),
            actor_user_id=current_user.id,
        )
    store_idempotent_response(
        db,
        user_id=current_user.id,
        key=idempotency_key,
        payload=request_payload,
        status_code=204,
        body={},
        resource_type="workout_session",
        resource_id=workout_id,
    )
    db.commit()
    return Response(status_code=status.HTTP_204_NO_CONTENT)

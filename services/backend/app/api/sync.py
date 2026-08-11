from __future__ import annotations

from typing import Any

from fastapi import APIRouter, Depends, Header, HTTPException, Query, status
from pydantic import ValidationError
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from app.api.cardio import apply_cardio_upsert, serialize_cardio
from app.api.dependencies import get_current_user, role_names
from app.api.readiness import apply_readiness_upsert, serialize_readiness
from app.api.workouts import (
    _as_object,
    _load_workout,
    _upsert_set,
    apply_workout_upsert,
    serialize_workout,
    serialize_workout_set,
)
from app.db.base import utcnow
from app.db.session import get_db
from app.models import CardioSession, DailyReadiness, User, WorkoutSet
from app.schemas.sync import (
    SyncBatchIn,
    SyncBatchItemResult,
    SyncBatchOut,
    SyncChangesOut,
    SyncOperationIn,
)
from app.schemas.workouts import (
    CardioSessionUpsert,
    ReadinessUpsert,
    WorkoutSessionPatch,
    WorkoutSessionUpsert,
    WorkoutSetUpsert,
)
from app.services.idempotency import (
    IdempotencyConflictError,
    find_idempotent_response,
    store_idempotent_response,
)
from app.services.sync import (
    canonical_entity_type,
    canonical_operation,
    ensure_entity_id,
    get_incremental_changes,
    latest_sync_sequence,
    record_conflict_audit,
    record_sync_change,
    version_conflict,
)


router = APIRouter(prefix="/sync", tags=["synchronization"])


@router.get("/changes", response_model=SyncChangesOut)
def get_sync_changes(
    cursor: str | None = None,
    limit: int = Query(default=200, ge=1, le=500),
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user),
) -> SyncChangesOut:
    return get_incremental_changes(
        db,
        user_id=current_user.id,
        cursor=cursor,
        limit=limit,
        include_unpublished_plans="admin" in role_names(db, current_user),
    )


def _delete_entity(
    db: Session,
    *,
    user: User,
    entity_type: str,
    entity_id: str,
    payload: dict[str, Any],
) -> tuple[dict[str, Any], int | None]:
    expected_version = payload.get("expected_version")
    if entity_type == "workout_session":
        item = _load_workout(db, entity_id)
        serializer = serialize_workout
    elif entity_type == "daily_readiness":
        item = db.get(DailyReadiness, entity_id)
        serializer = serialize_readiness
    elif entity_type == "cardio_session":
        item = db.get(CardioSession, entity_id)
        serializer = serialize_cardio
    elif entity_type == "workout_set":
        item = db.get(WorkoutSet, entity_id)
        if item is None:
            return {"id": entity_id, "deleted_at": payload.get("deleted_at")}, None
        if item.session.user_id != user.id:
            raise HTTPException(status_code=404, detail="Entity not found")
        server_copy = serialize_workout_set(item).model_dump(mode="json")
        if expected_version is not None and int(expected_version) != item.version:
            version_conflict(
                db,
                actor_user_id=user.id,
                entity_type=entity_type,
                entity_id=item.id,
                expected_version=int(expected_version),
                server_copy=server_copy,
                attempted=payload,
            )
        if item.deleted_at is None:
            item.deleted_at = utcnow()
            item.version += 1
            item.session.version += 1
            db.flush()
            server_copy = serialize_workout_set(item).model_dump(mode="json")
            record_sync_change(
                db,
                entity_type="workout_set",
                entity_id=item.id,
                entity_version=item.version,
                operation="DELETE",
                payload=server_copy,
                actor_user_id=user.id,
            )
            parent_copy = serialize_workout(item.session).model_dump(mode="json")
            record_sync_change(
                db,
                entity_type="workout_session",
                entity_id=item.session.id,
                entity_version=item.session.version,
                operation="UPSERT",
                payload=parent_copy,
                actor_user_id=user.id,
            )
        return server_copy, item.version
    else:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={"code": "unsupported_entity_type", "entity_type": entity_type},
        )

    # DELETE is idempotent even after the tombstone has aged out of this process.
    if item is None:
        return {"id": entity_id, "deleted_at": payload.get("deleted_at")}, None
    if item.user_id != user.id:
        raise HTTPException(status_code=404, detail="Entity not found")
    server_copy = serializer(item).model_dump(mode="json")
    if expected_version is not None and int(expected_version) != item.version:
        version_conflict(
            db,
            actor_user_id=user.id,
            entity_type=entity_type,
            entity_id=item.id,
            expected_version=int(expected_version),
            server_copy=server_copy,
            attempted=payload,
        )

    if item.deleted_at is None:
        item.deleted_at = utcnow()
        item.version += 1
        if entity_type == "workout_session":
            item.status = "cancelled"
            for workout_set in item.sets:
                if workout_set.deleted_at is None:
                    workout_set.deleted_at = item.deleted_at
                    workout_set.version += 1
        db.flush()
        server_copy = serializer(item).model_dump(mode="json")
        record_sync_change(
            db,
            entity_type=entity_type,
            entity_id=item.id,
            entity_version=item.version,
            operation="DELETE",
            payload=server_copy,
            actor_user_id=user.id,
        )
    return server_copy, item.version


def _normalize_workout_payload(payload: dict[str, Any]) -> dict[str, Any]:
    normalized = dict(payload)
    if "plan_day_code" not in normalized and "day_code" in normalized:
        normalized["plan_day_code"] = normalized["day_code"]
    if "plan_snapshot_json" not in normalized:
        snapshot = normalized.get("plan_snapshot") or normalized.get("snapshot")
        if snapshot is not None:
            import json

            normalized["plan_snapshot_json"] = json.dumps(
                snapshot, ensure_ascii=False, separators=(",", ":")
            )
    if normalized.get("ended_early"):
        normalized["status"] = "ENDED_EARLY"
    status_value = normalized.get("status")
    if isinstance(status_value, str):
        normalized["status"] = {
            "active": "IN_PROGRESS",
            "finished": "COMPLETED",
            "interrupted": "ENDED_EARLY",
        }.get(status_value.lower(), status_value)
    metadata = dict(normalized.get("metadata") or {})
    for key in ("effective_plan", "effective_set_cap"):
        if key in normalized:
            metadata[key] = normalized[key]
    if metadata:
        normalized["metadata"] = metadata
    return normalized


def _upsert_standalone_workout_set(
    db: Session,
    *,
    user: User,
    payload: dict[str, Any],
) -> tuple[dict[str, Any], int]:
    session_id = payload.get("session_id") or payload.get("workout_session_id")
    if not session_id:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={"code": "session_id_required"},
        )
    session = _load_workout(db, str(session_id))
    if session is None or session.user_id != user.id:
        raise HTTPException(status_code=404, detail="Workout session not found")

    set_payload = dict(payload)
    if "plan_slot_id" not in set_payload and "plan_item_id" in set_payload:
        set_payload["plan_slot_id"] = set_payload["plan_item_id"]
    if "source_plan_slot_option_id" not in set_payload and "option_id" in set_payload:
        set_payload["source_plan_slot_option_id"] = set_payload["option_id"]
    set_payload["completed"] = bool(
        set_payload.get("completed", set_payload.get("completed_at") is not None)
    )
    request = WorkoutSetUpsert.model_validate(set_payload)
    item = _upsert_set(db, session=session, incoming=request, actor_user_id=user.id)
    if set_payload.get("equipment"):
        item.prescription_snapshot_json = {
            **_as_object(item.prescription_snapshot_json),
            "equipment_label": str(set_payload["equipment"]),
        }
    session.version += 1
    db.flush()
    set_copy = serialize_workout_set(item).model_dump(mode="json")
    record_sync_change(
        db,
        entity_type="workout_set",
        entity_id=item.id,
        entity_version=item.version,
        operation="UPSERT",
        payload=set_copy,
        actor_user_id=user.id,
    )
    parent_copy = serialize_workout(session).model_dump(mode="json")
    record_sync_change(
        db,
        entity_type="workout_session",
        entity_id=session.id,
        entity_version=session.version,
        operation="UPSERT",
        payload=parent_copy,
        actor_user_id=user.id,
    )
    return set_copy, item.version


def _apply_operation(
    db: Session,
    *,
    user: User,
    operation: SyncOperationIn,
    payload: dict[str, Any],
) -> tuple[dict[str, Any], int | None]:
    entity_type = canonical_entity_type(operation.entity_type)
    action = canonical_operation(operation.operation)
    entity_id = str(operation.entity_id)
    if action == "DELETE":
        return _delete_entity(
            db,
            user=user,
            entity_type=entity_type,
            entity_id=entity_id,
            payload=payload,
        )

    if entity_type == "workout_session":
        workout_payload = _normalize_workout_payload(payload)
        existing = _load_workout(db, entity_id)
        request = (
            WorkoutSessionPatch.model_validate(workout_payload)
            if existing is not None
            else WorkoutSessionUpsert.model_validate(workout_payload)
        )
        result = apply_workout_upsert(
            db,
            user=user,
            payload=request,
            create_only=False,
            idempotency_key=operation.idempotency_key,
        )
    elif entity_type == "workout_set":
        return _upsert_standalone_workout_set(db, user=user, payload=payload)
    elif entity_type == "daily_readiness":
        request = ReadinessUpsert.model_validate(payload)
        result = apply_readiness_upsert(db, user=user, payload=request)
    elif entity_type == "cardio_session":
        request = CardioSessionUpsert.model_validate(payload)
        result = apply_cardio_upsert(db, user=user, payload=request)
    else:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={"code": "unsupported_entity_type", "entity_type": entity_type},
        )
    body = result.model_dump(mode="json")
    return body, result.version


def _server_copy(
    db: Session,
    *,
    user_id: str,
    entity_type: str,
    entity_id: str,
) -> dict[str, Any] | None:
    item: Any
    serializer: Any
    if entity_type == "workout_session":
        item, serializer = _load_workout(db, entity_id), serialize_workout
    elif entity_type == "daily_readiness":
        item, serializer = db.get(DailyReadiness, entity_id), serialize_readiness
    elif entity_type == "cardio_session":
        item, serializer = db.get(CardioSession, entity_id), serialize_cardio
    elif entity_type == "workout_set":
        item, serializer = db.get(WorkoutSet, entity_id), serialize_workout_set
    else:
        return None
    owner_id = item.session.user_id if entity_type == "workout_set" and item is not None else getattr(item, "user_id", None)
    if item is None or owner_id != user_id:
        return None
    return serializer(item).model_dump(mode="json")


def _error_text(detail: Any) -> str:
    if isinstance(detail, str):
        return detail
    if isinstance(detail, dict):
        return str(detail.get("message") or detail.get("code") or "Operation rejected")
    return "Operation rejected"


def _remember_failed_operation(
    db: Session,
    *,
    user_id: str,
    operation: SyncOperationIn,
    payload: dict[str, Any],
    result: SyncBatchItemResult,
) -> None:
    try:
        store_idempotent_response(
            db,
            user_id=user_id,
            key=operation.idempotency_key,
            payload=payload,
            status_code=200,
            body={"_sync_result": result.model_dump(mode="json")},
            resource_type=canonical_entity_type(operation.entity_type),
            resource_id=str(operation.entity_id),
        )
        db.commit()
    except (IdempotencyConflictError, IntegrityError):
        db.rollback()


def _process_operation(
    db: Session,
    *,
    user: User,
    operation: SyncOperationIn,
) -> SyncBatchItemResult:
    entity_type = canonical_entity_type(operation.entity_type)
    raw_payload = operation.payload or {}
    try:
        payload = ensure_entity_id(raw_payload, str(operation.entity_id))
    except HTTPException as error:
        result = SyncBatchItemResult(
            id=operation.id,
            client_outbox_id=operation.client_outbox_id,
            status="invalid",
            error=_error_text(error.detail),
        )
        _remember_failed_operation(
            db, user_id=user.id, operation=operation, payload=raw_payload, result=result
        )
        return result

    try:
        replay = find_idempotent_response(
            db,
            user_id=user.id,
            key=operation.idempotency_key,
            payload=payload,
        )
        if replay is not None:
            if isinstance(replay.body, dict) and "_sync_result" in replay.body:
                return SyncBatchItemResult.model_validate(replay.body["_sync_result"])
            replay_body = replay.body if isinstance(replay.body, dict) else None
            return SyncBatchItemResult(
                id=operation.id,
                client_outbox_id=operation.client_outbox_id,
                status="duplicate",
                server_version=(replay_body or {}).get("version"),
                server_copy=replay_body,
            )
    except IdempotencyConflictError as error:
        db.rollback()
        before = _server_copy(
            db,
            user_id=user.id,
            entity_type=entity_type,
            entity_id=str(operation.entity_id),
        )
        record_conflict_audit(
            db,
            actor_user_id=user.id,
            entity_type=entity_type,
            entity_id=str(operation.entity_id),
            before=before,
            attempted=payload,
            reason="idempotency_key_reused",
        )
        db.commit()
        return SyncBatchItemResult(
            id=operation.id,
            client_outbox_id=operation.client_outbox_id,
            status="conflict",
            error=str(error),
            server_version=(before or {}).get("version"),
            server_copy=before,
        )

    try:
        body, server_version = _apply_operation(
            db,
            user=user,
            operation=operation,
            payload=payload,
        )
        result = SyncBatchItemResult(
            id=operation.id,
            client_outbox_id=operation.client_outbox_id,
            status="accepted",
            server_version=server_version,
        )
        store_idempotent_response(
            db,
            user_id=user.id,
            key=operation.idempotency_key,
            payload=payload,
            status_code=200,
            body=body,
            resource_type=entity_type,
            resource_id=str(operation.entity_id),
        )
        db.commit()
        return result
    except (ValidationError, ValueError, TypeError) as error:
        db.rollback()
        if isinstance(error, ValidationError):
            message = "; ".join(str(item["msg"]) for item in error.errors()[:5])
        else:
            message = str(error) or "Operation payload is invalid"
        result = SyncBatchItemResult(
            id=operation.id,
            client_outbox_id=operation.client_outbox_id,
            status="invalid",
            error=message,
        )
        _remember_failed_operation(
            db, user_id=user.id, operation=operation, payload=payload, result=result
        )
        return result
    except HTTPException as error:
        # Version-conflict helpers commit their audit before raising. Other errors
        # have no accepted mutation and are safe to roll back.
        db.rollback()
        detail = error.detail if isinstance(error.detail, dict) else {}
        server_copy = detail.get("server_copy") or _server_copy(
            db,
            user_id=user.id,
            entity_type=entity_type,
            entity_id=str(operation.entity_id),
        )
        result = SyncBatchItemResult(
            id=operation.id,
            client_outbox_id=operation.client_outbox_id,
            status="conflict" if error.status_code == 409 else "invalid",
            error=_error_text(error.detail),
            server_version=(server_copy or {}).get("version"),
            server_copy=server_copy,
        )
        _remember_failed_operation(
            db, user_id=user.id, operation=operation, payload=payload, result=result
        )
        return result
    except IntegrityError:
        db.rollback()
        before = _server_copy(
            db,
            user_id=user.id,
            entity_type=entity_type,
            entity_id=str(operation.entity_id),
        )
        record_conflict_audit(
            db,
            actor_user_id=user.id,
            entity_type=entity_type,
            entity_id=str(operation.entity_id),
            before=before,
            attempted=payload,
            reason="database_constraint_conflict",
        )
        db.commit()
        result = SyncBatchItemResult(
            id=operation.id,
            client_outbox_id=operation.client_outbox_id,
            status="conflict",
            error="The operation conflicts with an existing server record",
            server_version=(before or {}).get("version"),
            server_copy=before,
        )
        _remember_failed_operation(
            db, user_id=user.id, operation=operation, payload=payload, result=result
        )
        return result


def _outer_idempotency_conflict(error: IdempotencyConflictError) -> HTTPException:
    return HTTPException(
        status_code=status.HTTP_409_CONFLICT,
        detail={
            "code": "idempotency_key_reused",
            "message": str(error),
            "idempotency_key": error.key,
        },
    )


@router.post("/batch", response_model=SyncBatchOut)
def sync_batch(
    payload: SyncBatchIn,
    idempotency_key: str = Header(alias="Idempotency-Key", min_length=1, max_length=128),
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user),
) -> SyncBatchOut:
    try:
        replay = find_idempotent_response(
            db,
            user_id=current_user.id,
            key=idempotency_key,
            payload=payload,
            scope="sync_batch",
        )
    except IdempotencyConflictError as error:
        db.rollback()
        record_conflict_audit(
            db,
            actor_user_id=current_user.id,
            entity_type="sync_batch",
            entity_id=str(payload.batch_id),
            before=None,
            attempted=payload.model_dump(mode="json"),
            reason="idempotency_key_reused",
        )
        db.commit()
        raise _outer_idempotency_conflict(error) from error
    if replay is not None:
        return SyncBatchOut.model_validate(replay.body)

    results = [
        _process_operation(db, user=current_user, operation=operation)
        for operation in payload.operations
    ]
    accepted = [
        operation.client_outbox_id or operation.id
        for operation, result in zip(payload.operations, results, strict=True)
        if result.status.lower() in {"accepted", "applied", "success", "duplicate"}
    ]
    response = SyncBatchOut(
        batch_id=payload.batch_id,
        results=results,
        accepted_outbox_ids=accepted,
        cursor=str(latest_sync_sequence(db)),
    )
    try:
        store_idempotent_response(
            db,
            user_id=current_user.id,
            key=idempotency_key,
            payload=payload,
            status_code=200,
            body=response,
            scope="sync_batch",
            resource_type="sync_batch",
            resource_id=str(payload.batch_id),
        )
        db.commit()
    except IntegrityError:
        # A concurrent identical retry may win the unique-key race. Re-read it.
        db.rollback()
        try:
            replay = find_idempotent_response(
                db,
                user_id=current_user.id,
                key=idempotency_key,
                payload=payload,
                scope="sync_batch",
            )
        except IdempotencyConflictError as error:
            record_conflict_audit(
                db,
                actor_user_id=current_user.id,
                entity_type="sync_batch",
                entity_id=str(payload.batch_id),
                before=None,
                attempted=payload.model_dump(mode="json"),
                reason="idempotency_key_reused",
            )
            db.commit()
            raise _outer_idempotency_conflict(error) from error
        if replay is not None:
            return SyncBatchOut.model_validate(replay.body)
        raise
    return response

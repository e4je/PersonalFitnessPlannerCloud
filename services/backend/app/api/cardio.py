from __future__ import annotations

import base64
import json
from datetime import UTC, date, datetime
from typing import Any

from fastapi import APIRouter, Depends, Header, HTTPException, Query, status
from sqlalchemy import and_, or_, select
from sqlalchemy.orm import Session

from app.api.dependencies import get_current_user
from app.db.session import get_db
from app.models import CardioSession, User
from app.schemas.workouts import CardioSessionOut, CardioSessionPage, CardioSessionUpsert
from app.services.idempotency import (
    IdempotencyConflictError,
    find_idempotent_response,
    store_idempotent_response,
)
from app.services.sync import record_conflict_audit, record_sync_change, version_conflict


router = APIRouter(prefix="/cardio-sessions", tags=["cardio sessions"])


def _metrics(value: Any) -> dict[str, Any]:
    if isinstance(value, dict):
        return dict(value)
    if isinstance(value, str):
        try:
            parsed = json.loads(value)
            return dict(parsed) if isinstance(parsed, dict) else {}
        except json.JSONDecodeError:
            return {}
    return {}


def serialize_cardio(item: CardioSession) -> CardioSessionOut:
    duration_seconds = item.duration_seconds
    distance_meters = float(item.distance_meters) if item.distance_meters is not None else None
    source = item.source_device or "android"
    return CardioSessionOut(
        id=item.id,
        user_id=item.user_id,
        client_id=item.client_id,
        source=source,
        source_device=source,
        client_version=item.client_version,
        local_date=item.local_date,
        activity=item.activity_type,
        activity_type=item.activity_type,
        duration_minutes=max(1, round(duration_seconds / 60)),
        duration_seconds=duration_seconds,
        distance_km=distance_meters / 1000 if distance_meters is not None else None,
        distance_meters=distance_meters,
        average_heart_rate=item.average_heart_rate,
        calories=item.calories,
        notes=item.notes,
        started_at=item.started_at,
        completed_at=item.completed_at,
        metrics=_metrics(item.metrics_json),
        version=item.version,
        created_at=item.created_at,
        updated_at=item.updated_at,
        deleted_at=item.deleted_at,
    )


def _cardio_conflict(
    db: Session,
    *,
    user_id: str,
    item: CardioSession,
    payload: CardioSessionUpsert,
    reason: str,
) -> None:
    server_copy = serialize_cardio(item).model_dump(mode="json")
    db.rollback()
    record_conflict_audit(
        db,
        actor_user_id=user_id,
        entity_type="cardio_session",
        entity_id=item.id,
        before=server_copy,
        attempted=payload.model_dump(mode="json"),
        reason=reason,
    )
    db.commit()
    raise HTTPException(
        status_code=status.HTTP_409_CONFLICT,
        detail={"code": reason, "server_copy": server_copy},
    )


def apply_cardio_upsert(
    db: Session,
    *,
    user: User,
    payload: CardioSessionUpsert,
) -> CardioSessionOut:
    item_id = str(payload.id)
    item = db.get(CardioSession, item_id)
    if item is not None and item.user_id != user.id:
        raise HTTPException(status_code=404, detail="Cardio session not found")

    client_id = str(payload.client_id or payload.id)
    duplicate = db.scalar(
        select(CardioSession).where(
            CardioSession.user_id == user.id,
            CardioSession.client_id == client_id,
            CardioSession.id != item_id,
        )
    )
    if duplicate is not None:
        _cardio_conflict(
            db,
            user_id=user.id,
            item=duplicate,
            payload=payload,
            reason="duplicate_client_uuid",
        )

    if item is None:
        item = CardioSession(
            id=item_id,
            user_id=user.id,
            client_id=client_id,
            source_device=payload.source_device or payload.source or "android",
            client_version=payload.client_version,
            local_date=payload.local_date,
            activity_type=payload.activity_type,
            started_at=payload.started_at,
            completed_at=payload.completed_at,
            duration_seconds=payload.duration_seconds,
            distance_meters=payload.distance_meters,
            average_heart_rate=payload.average_heart_rate,
            calories=payload.calories,
            notes=payload.notes,
            metrics_json=payload.metrics,
            deleted_at=payload.deleted_at,
        )
        db.add(item)
    else:
        if payload.expected_version is not None and payload.expected_version != item.version:
            version_conflict(
                db,
                actor_user_id=user.id,
                entity_type="cardio_session",
                entity_id=item.id,
                expected_version=payload.expected_version,
                server_copy=serialize_cardio(item).model_dump(mode="json"),
                attempted=payload.model_dump(mode="json"),
            )
        item.client_id = client_id
        item.source_device = payload.source_device or payload.source or item.source_device
        item.client_version = payload.client_version
        item.local_date = payload.local_date
        item.activity_type = payload.activity_type
        item.started_at = payload.started_at
        item.completed_at = payload.completed_at
        item.duration_seconds = payload.duration_seconds
        item.distance_meters = payload.distance_meters
        item.average_heart_rate = payload.average_heart_rate
        item.calories = payload.calories
        item.notes = payload.notes
        item.metrics_json = payload.metrics
        item.deleted_at = payload.deleted_at
        item.version += 1

    db.flush()
    result = serialize_cardio(item)
    record_sync_change(
        db,
        entity_type="cardio_session",
        entity_id=item.id,
        entity_version=item.version,
        operation="DELETE" if item.deleted_at is not None else "UPSERT",
        payload=result.model_dump(mode="json"),
        actor_user_id=user.id,
    )
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


@router.post("", response_model=CardioSessionOut, status_code=status.HTTP_201_CREATED)
def create_cardio_session(
    payload: CardioSessionUpsert,
    idempotency_key: str = Header(alias="Idempotency-Key", min_length=1, max_length=128),
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user),
) -> CardioSessionOut:
    try:
        replay = find_idempotent_response(
            db, user_id=current_user.id, key=idempotency_key, payload=payload
        )
    except IdempotencyConflictError as error:
        raise _idempotency_conflict(error) from error
    if replay is not None:
        return CardioSessionOut.model_validate(replay.body)

    result = apply_cardio_upsert(db, user=current_user, payload=payload)
    store_idempotent_response(
        db,
        user_id=current_user.id,
        key=idempotency_key,
        payload=payload,
        status_code=201,
        body=result,
        resource_type="cardio_session",
        resource_id=str(result.id),
    )
    db.commit()
    return result


def _encode_cursor(item: CardioSession) -> str:
    raw = json.dumps([item.started_at.isoformat(), item.id], separators=(",", ":"))
    return base64.urlsafe_b64encode(raw.encode()).decode().rstrip("=")


def _decode_cursor(cursor: str) -> tuple[datetime, str]:
    try:
        padded = cursor + "=" * (-len(cursor) % 4)
        timestamp, item_id = json.loads(base64.urlsafe_b64decode(padded).decode())
        parsed = datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=UTC)
        return parsed, str(item_id)
    except (ValueError, TypeError, json.JSONDecodeError) as error:
        raise HTTPException(status_code=400, detail={"code": "invalid_cursor"}) from error


@router.get("", response_model=CardioSessionPage)
def list_cardio_sessions(
    cursor: str | None = None,
    limit: int = Query(default=50, ge=1, le=200),
    local_date_from: date | None = None,
    local_date_to: date | None = None,
    include_deleted: bool = False,
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user),
) -> CardioSessionPage:
    query = select(CardioSession).where(CardioSession.user_id == current_user.id)
    if not include_deleted:
        query = query.where(CardioSession.deleted_at.is_(None))
    if local_date_from is not None:
        query = query.where(CardioSession.local_date >= local_date_from)
    if local_date_to is not None:
        query = query.where(CardioSession.local_date <= local_date_to)
    if cursor:
        started_at, item_id = _decode_cursor(cursor)
        query = query.where(
            or_(
                CardioSession.started_at < started_at,
                and_(CardioSession.started_at == started_at, CardioSession.id < item_id),
            )
        )
    rows = list(
        db.scalars(
            query.order_by(CardioSession.started_at.desc(), CardioSession.id.desc()).limit(limit + 1)
        )
    )
    has_more = len(rows) > limit
    page = rows[:limit]
    return CardioSessionPage(
        items=[serialize_cardio(row) for row in page],
        cursor=cursor,
        next_cursor=_encode_cursor(page[-1]) if has_more and page else None,
        has_more=has_more,
    )

from __future__ import annotations

import base64
import json
from datetime import date
from typing import Any

from fastapi import APIRouter, Depends, Header, HTTPException, Query, status
from sqlalchemy import and_, or_, select
from sqlalchemy.orm import Session

from app.api.dependencies import get_current_user
from app.db.session import get_db
from app.models import DailyReadiness, User
from app.schemas.workouts import ReadinessOut, ReadinessPage, ReadinessUpsert
from app.services.idempotency import (
    IdempotencyConflictError,
    find_idempotent_response,
    store_idempotent_response,
)
from app.services.sync import record_conflict_audit, record_sync_change, version_conflict


router = APIRouter(prefix="/readiness", tags=["readiness"])


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


def serialize_readiness(item: DailyReadiness) -> ReadinessOut:
    metrics = _metrics(item.metrics_json)
    return ReadinessOut(
        id=item.id,
        user_id=item.user_id,
        local_date=item.local_date,
        fatigue_score=item.fatigue,
        sleep_quality=item.sleep_quality,
        pain_notes=metrics.get("pain_notes"),
        soreness=item.soreness,
        stress=item.stress,
        motivation=item.motivation,
        notes=item.notes,
        metrics={key: value for key, value in metrics.items() if key != "pain_notes"},
        version=item.version,
        created_at=item.created_at,
        updated_at=item.updated_at,
        deleted_at=item.deleted_at,
    )


def _readiness_conflict(
    db: Session,
    *,
    user_id: str,
    item: DailyReadiness,
    payload: ReadinessUpsert,
    reason: str,
) -> None:
    server_copy = serialize_readiness(item).model_dump(mode="json")
    db.rollback()
    record_conflict_audit(
        db,
        actor_user_id=user_id,
        entity_type="daily_readiness",
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


def apply_readiness_upsert(
    db: Session,
    *,
    user: User,
    payload: ReadinessUpsert,
) -> ReadinessOut:
    item_id = str(payload.id)
    # Readiness identity is the user's local date. Keep it immutable for
    # existing rows so concurrent updates always lock one canonical key and
    # cannot deadlock while swapping unique dates.
    existing = db.get(
        DailyReadiness,
        item_id,
        populate_existing=True,
        with_for_update=True,
    )
    if existing is not None and existing.user_id != user.id:
        raise HTTPException(status_code=404, detail="Readiness entry not found")
    if existing is not None and existing.local_date != payload.local_date:
        _readiness_conflict(
            db,
            user_id=user.id,
            item=existing,
            payload=payload,
            reason="readiness_date_immutable",
        )
    item = db.scalar(
        select(DailyReadiness)
        .where(
            DailyReadiness.user_id == user.id,
            DailyReadiness.local_date == payload.local_date,
        )
        .with_for_update()
        .execution_options(populate_existing=True)
    )
    if item is None and existing is not None:
        item = existing
    if item is not None and item.id != item_id:
        _readiness_conflict(
            db,
            user_id=user.id,
            item=item,
            payload=payload,
            reason="readiness_date_conflict",
        )

    if item is None:
        item = DailyReadiness(
            id=item_id,
            user_id=user.id,
            local_date=payload.local_date,
            fatigue=payload.fatigue_score,
            sleep_quality=payload.sleep_quality,
            soreness=payload.soreness,
            stress=payload.stress,
            motivation=payload.motivation,
            notes=payload.notes,
            metrics_json={**payload.metrics, "pain_notes": payload.pain_notes},
        )
        db.add(item)
    else:
        if payload.expected_version is not None and payload.expected_version != item.version:
            version_conflict(
                db,
                actor_user_id=user.id,
                entity_type="daily_readiness",
                entity_id=item.id,
                expected_version=payload.expected_version,
                server_copy=serialize_readiness(item).model_dump(mode="json"),
                attempted=payload.model_dump(mode="json"),
            )
        item.fatigue = payload.fatigue_score
        item.sleep_quality = payload.sleep_quality
        item.soreness = payload.soreness
        item.stress = payload.stress
        item.motivation = payload.motivation
        item.notes = payload.notes
        item.metrics_json = {**payload.metrics, "pain_notes": payload.pain_notes}
        item.deleted_at = None
        item.version += 1

    db.flush()
    result = serialize_readiness(item)
    record_sync_change(
        db,
        entity_type="daily_readiness",
        entity_id=item.id,
        entity_version=item.version,
        operation="UPSERT",
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


@router.post("", response_model=ReadinessOut, status_code=status.HTTP_201_CREATED)
def create_readiness(
    payload: ReadinessUpsert,
    idempotency_key: str = Header(alias="Idempotency-Key", min_length=1, max_length=128),
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user),
) -> ReadinessOut:
    try:
        replay = find_idempotent_response(
            db, user_id=current_user.id, key=idempotency_key, payload=payload
        )
    except IdempotencyConflictError as error:
        raise _idempotency_conflict(error) from error
    if replay is not None:
        return ReadinessOut.model_validate(replay.body)

    result = apply_readiness_upsert(db, user=current_user, payload=payload)
    store_idempotent_response(
        db,
        user_id=current_user.id,
        key=idempotency_key,
        payload=payload,
        status_code=201,
        body=result,
        resource_type="daily_readiness",
        resource_id=str(result.id),
    )
    db.commit()
    return result


def _encode_cursor(item: DailyReadiness) -> str:
    raw = json.dumps([item.local_date.isoformat(), item.id], separators=(",", ":"))
    return base64.urlsafe_b64encode(raw.encode()).decode().rstrip("=")


def _decode_cursor(cursor: str) -> tuple[date, str]:
    try:
        padded = cursor + "=" * (-len(cursor) % 4)
        local_date, item_id = json.loads(base64.urlsafe_b64decode(padded).decode())
        return date.fromisoformat(local_date), str(item_id)
    except (ValueError, TypeError, json.JSONDecodeError) as error:
        raise HTTPException(status_code=400, detail={"code": "invalid_cursor"}) from error


@router.get("", response_model=ReadinessPage)
def list_readiness(
    cursor: str | None = None,
    limit: int = Query(default=30, ge=1, le=200),
    local_date_from: date | None = None,
    local_date_to: date | None = None,
    include_deleted: bool = False,
    db: Session = Depends(get_db),
    current_user: User = Depends(get_current_user),
) -> ReadinessPage:
    query = select(DailyReadiness).where(DailyReadiness.user_id == current_user.id)
    if not include_deleted:
        query = query.where(DailyReadiness.deleted_at.is_(None))
    if local_date_from is not None:
        query = query.where(DailyReadiness.local_date >= local_date_from)
    if local_date_to is not None:
        query = query.where(DailyReadiness.local_date <= local_date_to)
    if cursor:
        cursor_date, item_id = _decode_cursor(cursor)
        query = query.where(
            or_(
                DailyReadiness.local_date < cursor_date,
                and_(DailyReadiness.local_date == cursor_date, DailyReadiness.id < item_id),
            )
        )
    rows = list(
        db.scalars(
            query.order_by(DailyReadiness.local_date.desc(), DailyReadiness.id.desc()).limit(limit + 1)
        )
    )
    has_more = len(rows) > limit
    page = rows[:limit]
    return ReadinessPage(
        items=[serialize_readiness(row) for row in page],
        cursor=cursor,
        next_cursor=_encode_cursor(page[-1]) if has_more and page else None,
        has_more=has_more,
    )

from __future__ import annotations

import json
import re
from datetime import datetime, timedelta
from typing import Any
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from fastapi import HTTPException, status
from fastapi.encoders import jsonable_encoder
from sqlalchemy import and_, func, or_, select
from sqlalchemy.orm import Session

from app.core.config import settings
from app.db.base import utcnow
from app.models import AuditLog, PlanAssignment, SyncChange
from app.schemas.sync import SyncChangeOut, SyncChangesOut


PERSONAL_ENTITY_TYPES = {
    "workout_session",
    "workout_set",
    "daily_readiness",
    "cardio_session",
}

# Sync visibility is deliberately allow-listed.  Catalog entities are public to
# every authenticated client, personal records are visible only to their owner,
# and a plan becomes client-visible only as one complete published version.
GLOBAL_CATALOG_ENTITY_TYPES = {
    "muscle_group",
    "equipment",
    "exercise",
    "exercise_cue",
    "exercise_alternative",
    "exercise_equipment",
    "exercise_muscle_group",
}

ENTITY_TYPE_ALIASES = {
    "workout": "workout_session",
    "workout_sessions": "workout_session",
    "workout_sets": "workout_set",
    "readiness": "daily_readiness",
    "daily_readiness_entry": "daily_readiness",
    "daily_readiness_entries": "daily_readiness",
    "cardio": "cardio_session",
    "cardio_sessions": "cardio_session",
}


def canonical_entity_type(value: str) -> str:
    normalized = value.strip().lower().replace("-", "_")
    return ENTITY_TYPE_ALIASES.get(normalized, normalized)


def canonical_operation(value: str) -> str:
    normalized = value.strip().upper().replace("-", "_")
    if normalized in {"CREATE", "UPDATE", "PATCH", "PUT", "UPSERT"}:
        return "UPSERT"
    if normalized in {"DELETE", "REMOVE"}:
        return "DELETE"
    raise HTTPException(
        status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
        detail={"code": "unsupported_sync_operation", "operation": value},
    )


def json_value(value: Any, *, fallback: Any = None) -> Any:
    if value is None:
        return fallback
    if isinstance(value, str):
        try:
            return json.loads(value)
        except json.JSONDecodeError:
            return fallback
    return value


def record_sync_change(
    db: Session,
    *,
    entity_type: str,
    entity_id: str,
    entity_version: int,
    operation: str,
    payload: dict[str, Any] | None,
    actor_user_id: str | None,
    request_id: str | None = None,
) -> SyncChange:
    requested_operation = canonical_operation(operation)
    change = SyncChange(
        entity_type=canonical_entity_type(entity_type),
        entity_id=str(entity_id),
        entity_version=entity_version,
        # The append-only table distinguishes server create/update events. The
        # wire contract treats both as an upsert, so callers need not care which.
        operation="delete" if requested_operation == "DELETE" else "update",
        payload_json=jsonable_encoder(payload) if payload is not None else None,
        actor_user_id=actor_user_id,
        request_id=request_id,
        changed_at=utcnow(),
    )
    db.add(change)
    db.flush()
    return change


def record_conflict_audit(
    db: Session,
    *,
    actor_user_id: str,
    entity_type: str,
    entity_id: str,
    before: dict[str, Any] | None,
    attempted: dict[str, Any] | None,
    reason: str = "version_conflict",
    request_id: str | None = None,
) -> AuditLog:
    audit = AuditLog(
        actor_user_id=actor_user_id,
        action="SYNC_CONFLICT",
        entity_type=canonical_entity_type(entity_type),
        entity_id=str(entity_id),
        request_id=request_id,
        before_json=jsonable_encoder(before) if before is not None else None,
        after_json=None,
        metadata_json={"reason": reason, "attempted": jsonable_encoder(attempted)},
    )
    db.add(audit)
    db.flush()
    return audit


def version_conflict(
    db: Session,
    *,
    actor_user_id: str,
    entity_type: str,
    entity_id: str,
    expected_version: int,
    server_copy: dict[str, Any],
    attempted: dict[str, Any] | None,
    request_id: str | None = None,
) -> None:
    record_conflict_audit(
        db,
        actor_user_id=actor_user_id,
        entity_type=entity_type,
        entity_id=entity_id,
        before=server_copy,
        attempted=attempted,
        reason="version_conflict",
        request_id=request_id,
    )
    # Persist the audit even though the caller is about to abort the mutation.
    db.commit()
    raise HTTPException(
        status_code=status.HTTP_409_CONFLICT,
        detail={
            "code": "version_conflict",
            "message": "expected_version does not match the server version",
            "expected_version": expected_version,
            "server_version": server_copy.get("version"),
            "server_copy": server_copy,
        },
    )


def encode_sync_cursor(sequence: int) -> str:
    return str(max(0, sequence))


def decode_sync_cursor(cursor: str | None) -> int:
    if cursor is None or not cursor.strip():
        return 0
    try:
        value = int(cursor)
    except (TypeError, ValueError) as error:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={"code": "invalid_cursor", "message": "cursor is not valid"},
        ) from error
    if value < 0:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={"code": "invalid_cursor", "message": "cursor is not valid"},
        )
    return value


def _visible_change_predicate(
    user_id: str,
    *,
    include_unpublished_plans: bool = False,
    user_timezone: str = "UTC",
) -> Any:
    # Plan assignments are targeted server-authoritative records.  Their
    # ``actor_user_id`` is the administrator who made the assignment, not the
    # user who should receive it, so they must never fall through to either the
    # actor or generic global-catalog visibility rules.  Keeping ``user_id`` in
    # delete tombstone payloads lets the same predicate safely route deletes.
    assignment_for_user = and_(
        SyncChange.entity_type == "plan_assignment",
        SyncChange.payload_json["user_id"].as_string() == user_id,
    )
    personal_change = and_(
        SyncChange.entity_type.in_(PERSONAL_ENTITY_TYPES),
        SyncChange.actor_user_id == user_id,
    )
    catalog_change = SyncChange.entity_type.in_(GLOBAL_CATALOG_ENTITY_TYPES)
    try:
        local_date = datetime.now(ZoneInfo(user_timezone)).date()
    except ZoneInfoNotFoundError:
        local_date = datetime.now(ZoneInfo("UTC")).date()
    published_plan = and_(
        SyncChange.entity_type == "plan_version",
        SyncChange.payload_json["status"].as_string() == "published",
        or_(
            SyncChange.payload_json["is_system"].as_boolean().is_(True),
            SyncChange.payload_json["owner_user_id"].as_string() == user_id,
            select(PlanAssignment.id)
            .where(
                PlanAssignment.user_id == user_id,
                PlanAssignment.plan_version_id == SyncChange.entity_id,
                PlanAssignment.deleted_at.is_(None),
                PlanAssignment.status.in_(("scheduled", "active")),
                or_(
                    PlanAssignment.ends_on.is_(None),
                    PlanAssignment.ends_on >= local_date,
                ),
            )
            .exists(),
        ),
    )
    plan_change = (
        SyncChange.entity_type.in_(("training_plan", "plan_version"))
        if include_unpublished_plans
        else published_plan
    )
    return or_(
        assignment_for_user,
        personal_change,
        catalog_change,
        plan_change,
    )


def latest_sync_sequence(db: Session) -> int:
    return int(db.scalar(select(func.max(SyncChange.sequence))) or 0)


def get_incremental_changes(
    db: Session,
    *,
    user_id: str,
    cursor: str | None,
    limit: int,
    include_unpublished_plans: bool = False,
    user_timezone: str = "UTC",
) -> SyncChangesOut:
    sequence = decode_sync_cursor(cursor)
    visible = _visible_change_predicate(
        user_id,
        include_unpublished_plans=include_unpublished_plans,
        user_timezone=user_timezone,
    )
    cutoff = utcnow() - timedelta(days=settings.sync_retention_days)
    earliest_retained = db.scalar(
        select(func.min(SyncChange.sequence)).where(
            visible,
            SyncChange.changed_at >= cutoff,
        )
    )
    latest = latest_sync_sequence(db)
    latest_visible = int(
        db.scalar(select(func.max(SyncChange.sequence)).where(visible)) or 0
    )

    if sequence > latest:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={"code": "invalid_cursor", "message": "cursor is ahead of the server"},
        )

    # A missing cursor is a supported first pull. A supplied cursor predating the
    # retained window requires bootstrap so silently missing changes is impossible.
    retention_gap = (
        (earliest_retained is None and latest_visible > sequence)
        or (earliest_retained is not None and sequence < earliest_retained - 1)
    )
    if cursor not in (None, "") and retention_gap:
        next_cursor = encode_sync_cursor(latest)
        return SyncChangesOut(
            changes=[],
            cursor=cursor,
            next_cursor=next_cursor,
            has_more=False,
            full_resync_required=True,
        )

    rows = list(
        db.scalars(
            select(SyncChange)
            .where(
                visible,
                SyncChange.sequence > sequence,
                SyncChange.changed_at >= cutoff,
            )
            .order_by(SyncChange.sequence.asc())
            .limit(limit + 1)
        )
    )
    has_more = len(rows) > limit
    page = rows[:limit]
    if has_more and page:
        next_sequence = page[-1].sequence
    else:
        # Advance over changes that are deliberately invisible to this user.
        next_sequence = latest

    changes = [
        SyncChangeOut(
            id=row.id,
            entity_type=row.entity_type,
            entity_id=row.entity_id,
            operation="DELETE" if row.operation == "delete" else "UPSERT",
            version=row.entity_version,
            payload=row.payload_json,
            changed_at=row.changed_at,
        )
        for row in page
    ]
    return SyncChangesOut(
        changes=changes,
        cursor=cursor,
        next_cursor=encode_sync_cursor(next_sequence),
        has_more=has_more,
        full_resync_required=False,
    )


_CAMEL_BOUNDARY = re.compile(r"(?<!^)(?=[A-Z])")


def normalize_client_payload(value: Any) -> Any:
    """Accept Windows camelCase and Android snake_case inside generic sync JSON."""

    if isinstance(value, list):
        return [normalize_client_payload(item) for item in value]
    if not isinstance(value, dict):
        return value
    normalized: dict[str, Any] = {}
    for key, item in value.items():
        snake = _CAMEL_BOUNDARY.sub("_", str(key)).lower()
        normalized[snake] = normalize_client_payload(item)
    # Windows readiness/cardio domain records use these compact names.
    if "fatigue_score" not in normalized and "fatigue" in normalized:
        normalized["fatigue_score"] = normalized["fatigue"]
    return normalized


def ensure_entity_id(payload: dict[str, Any], entity_id: str) -> dict[str, Any]:
    normalized = normalize_client_payload(payload)
    payload_id = normalized.get("id")
    if payload_id is not None and str(payload_id).lower() != str(entity_id).lower():
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "code": "entity_id_mismatch",
                "message": "operation entity_id and payload id must match",
            },
        )
    normalized["id"] = str(entity_id)
    return normalized

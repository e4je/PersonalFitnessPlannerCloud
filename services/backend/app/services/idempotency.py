from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from datetime import timedelta
from typing import Any

from fastapi.encoders import jsonable_encoder
from sqlalchemy import select
from sqlalchemy.orm import Session

from app.db.base import utcnow
from app.models import IdempotencyKey


class IdempotencyConflictError(Exception):
    """The key exists, but was originally used for another request body."""

    def __init__(self, key: str) -> None:
        super().__init__("Idempotency-Key was already used with a different payload")
        self.key = key


@dataclass(frozen=True, slots=True)
class StoredIdempotentResponse:
    status_code: int
    body: Any
    resource_type: str | None = None
    resource_id: str | None = None


def canonical_payload(payload: Any) -> Any:
    """Convert Pydantic/UUID/date values to a stable JSON-compatible value."""

    if hasattr(payload, "model_dump"):
        payload = payload.model_dump(mode="json", exclude_none=False)
    return jsonable_encoder(payload)


def request_fingerprint(payload: Any) -> str:
    serialized = json.dumps(
        canonical_payload(payload),
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(serialized).hexdigest()


def find_idempotent_response(
    db: Session,
    *,
    user_id: str,
    key: str,
    payload: Any,
    scope: str = "mutation",
) -> StoredIdempotentResponse | None:
    record = db.scalar(
        select(IdempotencyKey).where(
            IdempotencyKey.user_id == user_id,
            IdempotencyKey.scope == scope,
            IdempotencyKey.key == key,
        )
    )
    if record is None:
        return None

    now = utcnow()
    if record.expires_at is not None and record.expires_at <= now:
        # Reusing an expired key is safe. Removing only this exact expired row also
        # avoids the unique constraint rejecting the new reservation.
        db.delete(record)
        db.flush()
        return None

    if record.request_hash != request_fingerprint(payload):
        raise IdempotencyConflictError(key)
    return StoredIdempotentResponse(
        status_code=record.response_status,
        body=record.response_body_json,
        resource_type=record.resource_type,
        resource_id=record.resource_id,
    )


def store_idempotent_response(
    db: Session,
    *,
    user_id: str,
    key: str,
    payload: Any,
    status_code: int,
    body: Any,
    scope: str = "mutation",
    resource_type: str | None = None,
    resource_id: str | None = None,
    ttl_days: int = 30,
) -> IdempotencyKey:
    """Stage an idempotency result in the caller's database transaction."""

    existing = db.scalar(
        select(IdempotencyKey).where(
            IdempotencyKey.user_id == user_id,
            IdempotencyKey.scope == scope,
            IdempotencyKey.key == key,
        )
    )
    fingerprint = request_fingerprint(payload)
    if existing is not None:
        if existing.request_hash != fingerprint:
            raise IdempotencyConflictError(key)
        return existing

    now = utcnow()
    record = IdempotencyKey(
        user_id=user_id,
        scope=scope,
        key=key,
        request_hash=fingerprint,
        response_status=status_code,
        response_body_json=canonical_payload(body),
        resource_type=resource_type,
        resource_id=resource_id,
        expires_at=now + timedelta(days=ttl_days),
        locked_at=now,
    )
    db.add(record)
    db.flush()
    return record


# Explicit aliases keep the service pleasant to use from tests and callers.
replay_or_none = find_idempotent_response
remember_response = store_idempotent_response

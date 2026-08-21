from __future__ import annotations

from datetime import date, datetime
from decimal import Decimal
from typing import Any, Generic, TypeVar

from sqlalchemy import Select, inspect, select, update
from sqlalchemy.orm import Session

from app.db.base import utcnow


ModelT = TypeVar("ModelT")


class RepositoryError(Exception):
    """Base class for errors that API layers can safely translate."""


class EntityNotFoundError(RepositoryError):
    def __init__(self, entity_type: str, entity_id: str) -> None:
        self.entity_type = entity_type
        self.entity_id = entity_id
        super().__init__(f"{entity_type} '{entity_id}' was not found")


class OptimisticLockError(RepositoryError):
    def __init__(self, server_copy: dict[str, Any] | None) -> None:
        self.server_copy = server_copy
        super().__init__("The resource was changed by another request")


def _json_value(value: Any) -> Any:
    if isinstance(value, (datetime, date)):
        return value.isoformat()
    if isinstance(value, Decimal):
        return float(value)
    return value


def entity_dict(entity: object, *, exclude: set[str] | None = None) -> dict[str, Any]:
    """Serialize mapped columns only, avoiding accidental lazy relationship loads."""

    hidden = exclude or set()
    mapper = inspect(entity).mapper
    return {
        attribute.key: _json_value(getattr(entity, attribute.key))
        for attribute in mapper.column_attrs
        if attribute.key not in hidden
    }


def mapped_column_names(model: type[object]) -> set[str]:
    return {attribute.key for attribute in inspect(model).mapper.column_attrs}


def model_from_values(model: type[ModelT], /, **values: Any) -> ModelT:
    """Construct a model while safely ignoring compatibility-only API fields."""

    allowed = mapped_column_names(model)
    return model(**{key: value for key, value in values.items() if key in allowed})


def active_select(model: type[ModelT]) -> Select[tuple[ModelT]]:
    statement = select(model)
    if "deleted_at" in mapped_column_names(model):
        statement = statement.where(getattr(model, "deleted_at").is_(None))
    return statement


def get_active(
    db: Session,
    model: type[ModelT],
    entity_id: str,
    *,
    for_update: bool = False,
) -> ModelT | None:
    statement = active_select(model).where(getattr(model, "id") == entity_id)
    if for_update:
        statement = statement.with_for_update()
    return db.scalar(statement)


def require_active(
    db: Session,
    model: type[ModelT],
    entity_id: str,
    *,
    for_update: bool = False,
) -> ModelT:
    entity = get_active(db, model, entity_id, for_update=for_update)
    if entity is None:
        raise EntityNotFoundError(model.__name__, entity_id)
    return entity


def optimistic_patch(
    db: Session,
    model: type[ModelT],
    entity_id: str,
    expected_version: int,
    values: dict[str, Any],
) -> ModelT:
    """Apply an atomic version-checked patch and return the refreshed row."""

    allowed = mapped_column_names(model) - {
        "id",
        "version",
        "created_at",
        "updated_at",
        "deleted_at",
    }
    patch = {key: value for key, value in values.items() if key in allowed}
    patch["version"] = expected_version + 1
    if "updated_at" in mapped_column_names(model):
        patch["updated_at"] = utcnow()

    conditions = [getattr(model, "id") == entity_id, getattr(model, "version") == expected_version]
    if "deleted_at" in mapped_column_names(model):
        conditions.append(getattr(model, "deleted_at").is_(None))
    result = db.execute(update(model).where(*conditions).values(**patch))
    if result.rowcount != 1:
        db.expire_all()
        current = get_active(db, model, entity_id)
        if current is None:
            raise EntityNotFoundError(model.__name__, entity_id)
        raise OptimisticLockError(entity_dict(current))

    db.flush()
    db.expire_all()
    entity = get_active(db, model, entity_id)
    if entity is None:  # defensive: the row cannot normally disappear in this transaction
        raise EntityNotFoundError(model.__name__, entity_id)
    return entity


def add_audit_log(
    db: Session,
    *,
    actor_user_id: str | None,
    action: str,
    entity_type: str,
    entity_id: str | None,
    before: dict[str, Any] | None = None,
    after: dict[str, Any] | None = None,
    request_id: str | None = None,
    ip_address: str | None = None,
    user_agent: str | None = None,
    metadata: dict[str, Any] | None = None,
) -> object:
    from app.models import AuditLog

    log = AuditLog(
        actor_user_id=actor_user_id,
        action=action[:64],
        entity_type=entity_type[:64],
        entity_id=entity_id,
        # RequestContextMiddleware accepts a wider correlation-id header for
        # tracing, while the audit schema is intentionally bounded. Truncate
        # before SQLAlchemy hands the value to a strict MySQL column so a
        # forged oversized header cannot turn an otherwise valid mutation into
        # a database error.
        request_id=request_id[:64] if request_id else None,
        ip_address=ip_address,
        user_agent=user_agent[:512] if user_agent else None,
        before_json=before,
        after_json=after,
        metadata_json=metadata,
    )
    db.add(log)
    return log


def add_sync_change(
    db: Session,
    *,
    entity_type: str,
    entity_id: str,
    entity_version: int,
    operation: str,
    payload: dict[str, Any] | None,
    actor_user_id: str | None = None,
    request_id: str | None = None,
) -> object:
    from app.models import SyncChange

    change = SyncChange(
        entity_type=entity_type,
        entity_id=entity_id,
        entity_version=entity_version,
        operation=operation,
        payload_json=payload,
        actor_user_id=actor_user_id,
        request_id=request_id[:64] if request_id else None,
    )
    db.add(change)
    return change


class Repository(Generic[ModelT]):
    """Small typed repository for the common active-row operations."""

    def __init__(self, model: type[ModelT]) -> None:
        self.model = model

    def get(self, db: Session, entity_id: str, *, for_update: bool = False) -> ModelT | None:
        return get_active(db, self.model, entity_id, for_update=for_update)

    def require(self, db: Session, entity_id: str, *, for_update: bool = False) -> ModelT:
        return require_active(db, self.model, entity_id, for_update=for_update)

    def patch(
        self,
        db: Session,
        entity_id: str,
        expected_version: int,
        values: dict[str, Any],
    ) -> ModelT:
        return optimistic_patch(db, self.model, entity_id, expected_version, values)

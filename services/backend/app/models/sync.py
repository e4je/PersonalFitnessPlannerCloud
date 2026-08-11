from __future__ import annotations

from datetime import datetime
from typing import Any

from sqlalchemy import BigInteger, CheckConstraint, ForeignKey, Index, Integer, JSON, String, Text, UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db.base import Base, SyncEntityMixin, UTCDateTime, utcnow, uuid4_str


class SyncChange(Base):
    """Append-only change feed row.

    ``sequence`` is deliberately the database identity while ``id`` remains a
    public UUID. This gives clients a compact monotonic cursor and keeps public
    identifiers portable. SQLite needs INTEGER (not BIGINT) for rowid-backed
    autoincrement, hence the type variant.
    """

    __tablename__ = "sync_changes"
    __table_args__ = (
        CheckConstraint(
            "operation IN ('create', 'update', 'delete')", name="operation_allowed"
        ),
        Index("ix_sync_changes_entity", "entity_type", "entity_id", "sequence"),
    )

    sequence: Mapped[int] = mapped_column(
        BigInteger().with_variant(Integer, "sqlite"), primary_key=True, autoincrement=True
    )
    id: Mapped[str] = mapped_column(String(36), nullable=False, unique=True, default=uuid4_str)
    entity_type: Mapped[str] = mapped_column(String(64), nullable=False, index=True)
    entity_id: Mapped[str] = mapped_column(String(36), nullable=False, index=True)
    entity_version: Mapped[int] = mapped_column(Integer, nullable=False)
    operation: Mapped[str] = mapped_column(String(16), nullable=False, index=True)
    payload_json: Mapped[dict[str, Any] | None] = mapped_column(
        "payload", JSON, nullable=True
    )
    actor_user_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="SET NULL"), nullable=True, index=True
    )
    request_id: Mapped[str | None] = mapped_column(String(64), nullable=True, index=True)
    changed_at: Mapped[datetime] = mapped_column(
        UTCDateTime(), nullable=False, default=utcnow, index=True
    )

    actor: Mapped[Any | None] = relationship("User")


class IdempotencyKey(SyncEntityMixin, Base):
    __tablename__ = "idempotency_keys"
    __table_args__ = (
        UniqueConstraint("user_id", "scope", "key", name="uq_idempotency_keys_user_scope_key"),
        CheckConstraint(
            "response_status IS NULL OR response_status BETWEEN 100 AND 599",
            name="response_status_range",
        ),
        Index("ix_idempotency_keys_expiry", "expires_at", "deleted_at"),
    )

    user_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True
    )
    scope: Mapped[str] = mapped_column(String(64), nullable=False)
    key: Mapped[str] = mapped_column(String(128), nullable=False)
    request_hash: Mapped[str] = mapped_column(String(128), nullable=False)
    response_status: Mapped[int | None] = mapped_column(Integer, nullable=True)
    response_body_json: Mapped[dict[str, Any] | list[Any] | None] = mapped_column(
        "response_body", JSON, nullable=True
    )
    resource_type: Mapped[str | None] = mapped_column(String(64), nullable=True)
    resource_id: Mapped[str | None] = mapped_column(String(36), nullable=True)
    expires_at: Mapped[datetime] = mapped_column(UTCDateTime(), nullable=False)
    locked_at: Mapped[datetime | None] = mapped_column(UTCDateTime(), nullable=True)

    user: Mapped[Any] = relationship("User")


class AuditLog(SyncEntityMixin, Base):
    __tablename__ = "audit_logs"
    __table_args__ = (
        Index("ix_audit_logs_entity_created", "entity_type", "entity_id", "created_at"),
        Index("ix_audit_logs_actor_created", "actor_user_id", "created_at"),
    )

    actor_user_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="SET NULL"), nullable=True, index=True
    )
    action: Mapped[str] = mapped_column(String(64), nullable=False, index=True)
    entity_type: Mapped[str] = mapped_column(String(64), nullable=False, index=True)
    entity_id: Mapped[str | None] = mapped_column(String(36), nullable=True, index=True)
    request_id: Mapped[str | None] = mapped_column(String(64), nullable=True, index=True)
    ip_address: Mapped[str | None] = mapped_column(String(45), nullable=True)
    user_agent: Mapped[str | None] = mapped_column(String(512), nullable=True)
    before_json: Mapped[dict[str, Any] | None] = mapped_column("before", JSON, nullable=True)
    after_json: Mapped[dict[str, Any] | None] = mapped_column("after", JSON, nullable=True)
    metadata_json: Mapped[dict[str, Any] | None] = mapped_column("metadata", JSON, nullable=True)

    actor: Mapped[Any | None] = relationship("User")


class SchemaVersion(SyncEntityMixin, Base):
    __tablename__ = "schema_versions"

    schema_version: Mapped[str] = mapped_column(String(64), nullable=False, unique=True)
    api_version: Mapped[str] = mapped_column(String(32), nullable=False)
    minimum_client_version: Mapped[str | None] = mapped_column(String(64), nullable=True)
    checksum: Mapped[str | None] = mapped_column(String(128), nullable=True)
    applied_at: Mapped[datetime] = mapped_column(UTCDateTime(), nullable=False, default=utcnow)
    notes: Mapped[str | None] = mapped_column(Text, nullable=True)

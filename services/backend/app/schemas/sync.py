from __future__ import annotations

from datetime import datetime
from typing import Any
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


class SyncModel(BaseModel):
    model_config = ConfigDict(extra="ignore", populate_by_name=True)


class SyncChangeOut(SyncModel):
    id: UUID
    entity_type: str
    entity_id: UUID
    operation: str = "UPSERT"
    version: int = 1
    payload: dict[str, Any] | None = None
    changed_at: datetime | None = None


class SyncChangesOut(SyncModel):
    changes: list[SyncChangeOut] = Field(default_factory=list)
    cursor: str | None = None
    next_cursor: str | None = None
    has_more: bool = False
    full_resync_required: bool = False


class SyncOperationIn(SyncModel):
    id: UUID
    client_outbox_id: UUID | None = None
    idempotency_key: str = Field(min_length=1, max_length=128)
    entity_type: str = Field(min_length=1, max_length=64)
    entity_id: UUID
    operation: str = Field(min_length=1, max_length=32)
    payload: dict[str, Any] | None = None

    @field_validator("idempotency_key")
    @classmethod
    def validate_idempotency_key(cls, value: str) -> str:
        normalized = value.strip()
        if not normalized or any(ord(char) < 0x20 or ord(char) == 0x7F for char in normalized):
            raise ValueError("idempotency_key contains control characters")
        return normalized

    @model_validator(mode="after")
    def normalize_wire_values(self) -> "SyncOperationIn":
        self.entity_type = self.entity_type.strip().lower().replace("-", "_")
        self.operation = self.operation.strip().upper().replace("-", "_")
        return self


class SyncBatchIn(SyncModel):
    # The wire contract historically allowed opaque client batch identifiers.
    # Keep that compatibility while bounding the value to the database's
    # resource-id width so oversized input cannot trigger a persistence error.
    batch_id: UUID | str
    sent_at: datetime
    operations: list[SyncOperationIn] = Field(min_length=1, max_length=100)

    @field_validator("batch_id")
    @classmethod
    def validate_batch_id(cls, value: UUID | str) -> UUID | str:
        text = str(value)
        if len(text) > 36 or any(ord(char) < 0x20 or ord(char) == 0x7F for char in text):
            raise ValueError("batch_id is invalid")
        return value

    @model_validator(mode="after")
    def validate_operation_ids(self) -> "SyncBatchIn":
        ids = [item.id for item in self.operations]
        if len(ids) != len(set(ids)):
            raise ValueError("operations contains duplicate ids")
        return self


class SyncBatchItemResult(SyncModel):
    id: UUID
    client_outbox_id: UUID | None = None
    status: str
    error: str | None = None
    server_version: int | None = None
    server_copy: dict[str, Any] | None = None


class SyncBatchOut(SyncModel):
    batch_id: UUID | str | None = None
    results: list[SyncBatchItemResult] = Field(default_factory=list)
    accepted_outbox_ids: list[UUID] = Field(default_factory=list)
    cursor: str | None = None

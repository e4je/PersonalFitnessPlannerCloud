from __future__ import annotations

from datetime import datetime
from typing import Any, Generic, TypeVar

from pydantic import BaseModel, ConfigDict, Field


class ORMModel(BaseModel):
    model_config = ConfigDict(from_attributes=True)


class SyncEntityOut(ORMModel):
    id: str
    version: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None = None


class ErrorDetail(BaseModel):
    code: str
    message: str
    server_copy: dict[str, Any] | None = None


class ErrorResponse(BaseModel):
    detail: ErrorDetail


T = TypeVar("T")


class CursorPage(BaseModel, Generic[T]):
    items: list[T] = Field(default_factory=list)
    cursor: str | None = None
    next_cursor: str | None = None
    has_more: bool = False

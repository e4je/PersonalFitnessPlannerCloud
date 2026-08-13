from __future__ import annotations

import json
import re
from datetime import date, datetime
from typing import Any, Self
from uuid import UUID
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


_IANA_ZONE_PATTERN = re.compile(r"^[A-Za-z0-9._+-]+(?:/[A-Za-z0-9._+-]+)+$")
_EXPECTED_VERSION_DESCRIPTION = (
    "Compatibility-period optimistic concurrency token. New entities and legacy clients may "
    "omit it; updates that provide it are checked atomically against the last server version. "
    "Clients should send the version from their latest server copy."
)


class ApiModel(BaseModel):
    """Common API model configuration used by the mobile and desktop clients."""

    model_config = ConfigDict(extra="ignore", populate_by_name=True)


class WorkoutSetUpsert(ApiModel):
    id: UUID
    plan_slot_id: UUID | None = None
    source_plan_slot_option_id: UUID | None = None
    exercise_id: UUID
    equipment_id: UUID | None = None
    set_number: int = Field(ge=1, le=1000)
    weight_kg: float | None = Field(default=None, ge=0, le=100_000)
    reps: int | None = Field(default=None, ge=0, le=100_000)
    duration_seconds: int | None = Field(default=None, ge=0, le=604_800)
    distance_meters: float | None = Field(default=None, ge=0)
    is_warmup: bool = False
    set_type: str | None = None
    rir: int | None = Field(default=None, ge=0, le=10)
    quality: str | None = Field(default=None, max_length=32)
    pain: bool = False
    notes: str | None = Field(default=None, max_length=4000)
    completed: bool = False
    completed_at: datetime | None = None
    deleted_at: datetime | None = None
    expected_version: int | None = Field(
        default=None,
        ge=1,
        description=_EXPECTED_VERSION_DESCRIPTION,
    )

    @field_validator("set_type")
    @classmethod
    def normalize_set_type(cls, value: str | None) -> str | None:
        return value.strip().upper() if value and value.strip() else None


class WorkoutSessionUpsert(ApiModel):
    id: UUID
    client_id: UUID | None = None
    source: str = Field(default="android", max_length=32)
    source_device: str | None = Field(default=None, max_length=32)
    client_version: str | None = Field(default=None, max_length=64)
    plan_assignment_id: UUID | None = None
    plan_version_id: UUID | None = None
    plan_day_id: UUID | None = None
    plan_day_code: str | None = Field(default=None, max_length=32)
    local_date: date
    timezone: str = Field(default="UTC", min_length=1, max_length=64)
    started_at: datetime
    completed_at: datetime | None = None
    status: str = Field(default="IN_PROGRESS", min_length=1, max_length=32)
    is_full_body: bool = True
    training_week: int | None = Field(default=None, ge=1, le=10_000)
    ab_state: str | None = Field(default=None, max_length=16)
    plan_snapshot_json: str = "{}"
    metadata: dict[str, Any] = Field(default_factory=dict)
    notes: str | None = Field(default=None, max_length=10_000)
    sets: list[WorkoutSetUpsert] = Field(default_factory=list, max_length=1000)
    deleted_at: datetime | None = None
    expected_version: int | None = Field(
        default=None,
        ge=1,
        description=_EXPECTED_VERSION_DESCRIPTION,
    )

    @field_validator("timezone")
    @classmethod
    def validate_timezone(cls, value: str) -> str:
        try:
            ZoneInfo(value)
        except ZoneInfoNotFoundError as error:
            # Minimal Windows Python distributions may not bundle the IANA
            # database. Preserve valid IANA identifiers (e.g. Asia/Shanghai)
            # and let deployments with tzdata installed perform the full check.
            if not _IANA_ZONE_PATTERN.fullmatch(value):
                raise ValueError("timezone must be a valid IANA timezone") from error
        return value

    @field_validator("status")
    @classmethod
    def normalize_status(cls, value: str) -> str:
        normalized = value.strip().upper().replace("-", "_")
        normalized = {
            "ACTIVE": "IN_PROGRESS",
            "FINISHED": "COMPLETED",
            "INTERRUPTED": "ENDED_EARLY",
        }.get(normalized, normalized)
        allowed = {"PLANNED", "IN_PROGRESS", "COMPLETED", "ENDED_EARLY", "CANCELLED", "DELETED"}
        if normalized not in allowed:
            raise ValueError(f"status must be one of {', '.join(sorted(allowed))}")
        return normalized

    @field_validator("source", "source_device")
    @classmethod
    def normalize_source(cls, value: str | None) -> str | None:
        normalized = value.strip().lower() if value and value.strip() else None
        if normalized is not None and normalized not in {"android", "windows", "web", "api"}:
            raise ValueError("source must be android, windows, web, or api")
        return normalized

    @field_validator("plan_snapshot_json")
    @classmethod
    def validate_snapshot(cls, value: str) -> str:
        try:
            parsed = json.loads(value)
        except (TypeError, json.JSONDecodeError) as error:
            raise ValueError("plan_snapshot_json must contain valid JSON") from error
        if not isinstance(parsed, dict):
            raise ValueError("plan_snapshot_json must contain a JSON object")
        return value

    @model_validator(mode="after")
    def validate_unique_set_ids(self) -> Self:
        ids = [item.id for item in self.sets]
        if len(ids) != len(set(ids)):
            raise ValueError("sets contains duplicate client UUIDs")
        return self


class WorkoutSessionPatch(ApiModel):
    id: UUID | None = None
    client_id: UUID | None = None
    source: str | None = Field(default=None, max_length=32)
    source_device: str | None = Field(default=None, max_length=32)
    client_version: str | None = Field(default=None, max_length=64)
    plan_assignment_id: UUID | None = None
    plan_version_id: UUID | None = None
    plan_day_id: UUID | None = None
    plan_day_code: str | None = Field(default=None, max_length=32)
    local_date: date | None = None
    timezone: str | None = Field(default=None, min_length=1, max_length=64)
    started_at: datetime | None = None
    completed_at: datetime | None = None
    status: str | None = Field(default=None, min_length=1, max_length=32)
    is_full_body: bool | None = None
    training_week: int | None = Field(default=None, ge=1, le=10_000)
    ab_state: str | None = Field(default=None, max_length=16)
    plan_snapshot_json: str | None = None
    metadata: dict[str, Any] | None = None
    notes: str | None = Field(default=None, max_length=10_000)
    sets: list[WorkoutSetUpsert] | None = Field(default=None, max_length=1000)
    deleted_at: datetime | None = None
    expected_version: int | None = Field(
        default=None,
        ge=1,
        description=_EXPECTED_VERSION_DESCRIPTION,
    )

    @field_validator("timezone")
    @classmethod
    def validate_timezone(cls, value: str | None) -> str | None:
        if value is None:
            return value
        try:
            ZoneInfo(value)
        except ZoneInfoNotFoundError as error:
            if not _IANA_ZONE_PATTERN.fullmatch(value):
                raise ValueError("timezone must be a valid IANA timezone") from error
        return value

    @field_validator("status")
    @classmethod
    def normalize_status(cls, value: str | None) -> str | None:
        if value is None:
            return None
        normalized = value.strip().upper().replace("-", "_")
        normalized = {
            "ACTIVE": "IN_PROGRESS",
            "FINISHED": "COMPLETED",
            "INTERRUPTED": "ENDED_EARLY",
        }.get(normalized, normalized)
        allowed = {"PLANNED", "IN_PROGRESS", "COMPLETED", "ENDED_EARLY", "CANCELLED", "DELETED"}
        if normalized not in allowed:
            raise ValueError(f"status must be one of {', '.join(sorted(allowed))}")
        return normalized

    @field_validator("source", "source_device")
    @classmethod
    def normalize_source(cls, value: str | None) -> str | None:
        normalized = value.strip().lower() if value and value.strip() else None
        if normalized is not None and normalized not in {"android", "windows", "web", "api"}:
            raise ValueError("source must be android, windows, web, or api")
        return normalized

    @field_validator("plan_snapshot_json")
    @classmethod
    def validate_snapshot(cls, value: str | None) -> str | None:
        if value is None:
            return value
        try:
            parsed = json.loads(value)
        except (TypeError, json.JSONDecodeError) as error:
            raise ValueError("plan_snapshot_json must contain valid JSON") from error
        if not isinstance(parsed, dict):
            raise ValueError("plan_snapshot_json must contain a JSON object")
        return value

    @model_validator(mode="after")
    def validate_unique_set_ids(self) -> Self:
        if self.sets is not None:
            ids = [item.id for item in self.sets]
            if len(ids) != len(set(ids)):
                raise ValueError("sets contains duplicate client UUIDs")
        return self


class WorkoutSetOut(ApiModel):
    id: UUID
    session_id: UUID
    plan_slot_id: UUID | None = None
    source_plan_slot_option_id: UUID | None = None
    exercise_id: UUID
    equipment_id: UUID | None = None
    set_number: int
    weight_kg: float | None = None
    reps: int | None = None
    duration_seconds: int | None = None
    distance_meters: float | None = None
    is_warmup: bool = False
    set_type: str = "WORKING"
    rir: int | None = None
    quality: str | None = None
    pain: bool = False
    notes: str | None = None
    completed: bool = False
    completed_at: datetime | None = None
    version: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None = None


class WorkoutSessionOut(ApiModel):
    id: UUID
    user_id: UUID
    client_id: UUID | None = None
    source: str = "android"
    source_device: str = "android"
    client_version: str | None = None
    plan_assignment_id: UUID | None = None
    plan_version_id: UUID | None = None
    plan_day_id: UUID | None = None
    plan_day_code: str | None = None
    local_date: date
    timezone: str = "UTC"
    started_at: datetime
    completed_at: datetime | None = None
    status: str = "IN_PROGRESS"
    is_full_body: bool = True
    training_week: int | None = None
    ab_state: str | None = None
    plan_snapshot_json: str = "{}"
    idempotency_key: str | None = None
    metadata: dict[str, Any] = Field(default_factory=dict)
    notes: str | None = None
    sets: list[WorkoutSetOut] = Field(default_factory=list)
    version: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None = None


class WorkoutSessionPage(ApiModel):
    items: list[WorkoutSessionOut] = Field(default_factory=list)
    cursor: str | None = None
    next_cursor: str | None = None
    has_more: bool = False


class ReadinessUpsert(ApiModel):
    id: UUID
    local_date: date = Field(
        description="Immutable per-entry local date; create a new UUID for another date."
    )
    fatigue_score: int = Field(ge=1, le=10)
    sleep_quality: int | None = Field(default=None, ge=1, le=5)
    pain_notes: str | None = Field(default=None, max_length=4000)
    soreness: int | None = Field(default=None, ge=1, le=5)
    stress: int | None = Field(default=None, ge=1, le=5)
    motivation: int | None = Field(default=None, ge=1, le=5)
    notes: str | None = Field(default=None, max_length=4000)
    metrics: dict[str, Any] = Field(default_factory=dict)
    expected_version: int | None = Field(
        default=None,
        ge=1,
        description=_EXPECTED_VERSION_DESCRIPTION,
    )


class ReadinessOut(ApiModel):
    id: UUID
    user_id: UUID
    local_date: date
    fatigue_score: int
    sleep_quality: int | None = None
    pain_notes: str | None = None
    soreness: int | None = None
    stress: int | None = None
    motivation: int | None = None
    notes: str | None = None
    metrics: dict[str, Any] = Field(default_factory=dict)
    version: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None = None


class ReadinessPage(ApiModel):
    items: list[ReadinessOut] = Field(default_factory=list)
    cursor: str | None = None
    next_cursor: str | None = None
    has_more: bool = False


class CardioSessionUpsert(ApiModel):
    id: UUID
    client_id: UUID | None = Field(
        default=None,
        description="Immutable idempotent client identity; defaults to id on creation.",
    )
    source: str = Field(default="android", max_length=32)
    source_device: str | None = Field(default=None, max_length=32)
    client_version: str | None = Field(default=None, max_length=64)
    local_date: date
    activity: str | None = Field(default=None, max_length=64)
    activity_type: str | None = Field(default=None, max_length=64)
    duration_minutes: int | None = Field(default=None, gt=0, le=10_080)
    duration_seconds: int | None = Field(default=None, gt=0, le=604_800)
    distance_km: float | None = Field(default=None, ge=0)
    distance_meters: float | None = Field(default=None, ge=0)
    average_heart_rate: int | None = Field(default=None, ge=20, le=300)
    calories: int | None = Field(default=None, ge=0, le=100_000)
    notes: str | None = Field(default=None, max_length=4000)
    started_at: datetime
    completed_at: datetime | None = None
    metrics: dict[str, Any] = Field(default_factory=dict)
    deleted_at: datetime | None = None
    expected_version: int | None = Field(
        default=None,
        ge=1,
        description=_EXPECTED_VERSION_DESCRIPTION,
    )

    @model_validator(mode="after")
    def normalize_compatibility_fields(self) -> Self:
        self.activity_type = (self.activity_type or self.activity or "").strip().lower()
        if not self.activity_type:
            raise ValueError("activity_type or activity is required")
        self.activity = self.activity or self.activity_type
        if self.duration_seconds is None:
            if self.duration_minutes is None:
                raise ValueError("duration_seconds or duration_minutes is required")
            self.duration_seconds = self.duration_minutes * 60
        if self.duration_minutes is None:
            self.duration_minutes = max(1, round(self.duration_seconds / 60))
        if self.distance_meters is None and self.distance_km is not None:
            self.distance_meters = self.distance_km * 1000
        if self.distance_km is None and self.distance_meters is not None:
            self.distance_km = self.distance_meters / 1000
        self.source = self.source.strip().lower() or "android"
        self.source_device = (self.source_device or self.source).strip().lower()
        if self.source_device not in {"android", "windows", "web", "api"}:
            raise ValueError("source must be android, windows, web, or api")
        return self


class CardioSessionOut(ApiModel):
    id: UUID
    user_id: UUID
    client_id: UUID | None = None
    source: str = "android"
    source_device: str = "android"
    client_version: str | None = None
    local_date: date
    activity: str
    activity_type: str
    duration_minutes: int
    duration_seconds: int
    distance_km: float | None = None
    distance_meters: float | None = None
    average_heart_rate: int | None = None
    calories: int | None = None
    notes: str | None = None
    started_at: datetime
    completed_at: datetime | None = None
    metrics: dict[str, Any] = Field(default_factory=dict)
    version: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None = None


class CardioSessionPage(ApiModel):
    items: list[CardioSessionOut] = Field(default_factory=list)
    cursor: str | None = None
    next_cursor: str | None = None
    has_more: bool = False

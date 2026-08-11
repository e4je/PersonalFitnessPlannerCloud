from __future__ import annotations

from datetime import date, datetime
from typing import Any

from pydantic import BaseModel, Field


class PlanSlotOptionOut(BaseModel):
    id: str
    plan_slot_id: str
    exercise_id: str
    equipment_id: str | None = None
    is_preferred: bool
    sort_order: int
    set_count: int
    rep_min: int = 0
    rep_max: int = 0
    rep_unit: str = "reps"
    duration_seconds_min: int | None = None
    duration_seconds_max: int | None = None
    rir_min: float | None = None
    rir_max: float | None = None
    is_per_side: bool = False
    prescription_text: str | None = None
    version: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None = None


class PlanSlotOut(BaseModel):
    id: str
    plan_day_id: str
    position: int
    body_part: str
    cues: str = ""
    options: list[PlanSlotOptionOut] = Field(default_factory=list)
    version: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None = None


class PlanDayOut(BaseModel):
    id: str
    plan_version_id: str
    code: str
    name: str
    sort_order: int
    slots: list[PlanSlotOut] = Field(default_factory=list)
    version: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None = None


class PlanVersionOut(BaseModel):
    id: str
    plan_id: str
    plan_name: str
    version_number: int
    status: str
    published_at: datetime | None = None
    snapshot_json: str | None = None
    weekly_frequency: int
    min_rest_days: int
    fatigue_threshold: int
    initial_reduced_weeks: int
    initial_set_count: int
    rules: dict[str, Any] = Field(default_factory=dict)
    days: list[PlanDayOut] = Field(default_factory=list)
    version: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None = None


class PlanAssignmentOut(BaseModel):
    id: str
    user_id: str
    plan_version_id: str
    start_local_date: date
    end_local_date: date | None = None
    is_active: bool
    status: str
    version: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None = None

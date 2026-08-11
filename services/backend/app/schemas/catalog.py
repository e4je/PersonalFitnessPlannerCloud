from __future__ import annotations

from datetime import datetime

from pydantic import BaseModel, Field

from app.schemas.common import CursorPage


class EquipmentOut(BaseModel):
    id: str
    code: str
    name: str
    category: str
    brand: str | None = None
    model: str | None = None
    notes: str | None = None
    version: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None = None


class ExerciseAlternativeOut(BaseModel):
    id: str
    exercise_id: str
    alternative_exercise_id: str
    sort_order: int = 0
    version: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None = None


class ExerciseOut(BaseModel):
    id: str
    code: str
    name: str
    body_part: str = ""
    equipment_id: str | None = None
    default_sets: int = 0
    rep_min: int = 0
    rep_max: int = 0
    rep_unit: str = "reps"
    cues: str = ""
    common_mistakes: str = ""
    definition_version: int = 1
    alternatives: list[ExerciseAlternativeOut] = Field(default_factory=list)
    version: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None = None


class EquipmentPage(CursorPage[EquipmentOut]):
    pass


class ExercisePage(CursorPage[ExerciseOut]):
    pass

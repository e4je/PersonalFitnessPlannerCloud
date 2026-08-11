from __future__ import annotations

from datetime import datetime
from typing import Any

from pydantic import BaseModel, Field

from app.schemas.catalog import EquipmentOut, ExerciseOut
from app.schemas.plans import PlanAssignmentOut, PlanVersionOut
from app.schemas.workouts import CardioSessionOut, ReadinessOut, WorkoutSessionOut


class BootstrapOut(BaseModel):
    user: dict[str, Any]
    permissions: list[str] = Field(default_factory=list)
    current_plan: PlanVersionOut | None = None
    plan_version: PlanVersionOut | None = None
    plan_versions: list[PlanVersionOut] = Field(default_factory=list)
    exercises: list[ExerciseOut] = Field(default_factory=list)
    equipment: list[EquipmentOut] = Field(default_factory=list)
    assignments: list[PlanAssignmentOut] = Field(default_factory=list)
    workout_sessions: list[WorkoutSessionOut] = Field(default_factory=list)
    readiness: list[ReadinessOut] = Field(default_factory=list)
    cardio_sessions: list[CardioSessionOut] = Field(default_factory=list)
    recommendation: dict[str, Any] = Field(default_factory=dict)
    cursor: str
    sync_cursor: str
    server_time: datetime
    api_version: str
    schema_version: str

"""SQLAlchemy model registry.

Import this module before using ``Base.metadata`` so every mapped table is
registered for Alembic, tests, and one-shot development database creation.
"""

from app.models.auth import RefreshToken, Role, SystemSetting, User, UserRole
from app.models.catalog import (
    Equipment,
    Exercise,
    ExerciseAlternative,
    ExerciseCue,
    ExerciseEquipment,
    ExerciseMuscleGroup,
    MuscleGroup,
)
from app.models.plans import (
    PlanAssignment,
    PlanDay,
    PlanSlot,
    PlanSlotOption,
    PlanVersion,
    TrainingPlan,
)
from app.models.sync import AuditLog, IdempotencyKey, SchemaVersion, SyncChange
from app.models.workouts import CardioSession, DailyReadiness, WorkoutSession, WorkoutSet

__all__ = [
    "AuditLog",
    "CardioSession",
    "DailyReadiness",
    "Equipment",
    "Exercise",
    "ExerciseAlternative",
    "ExerciseCue",
    "ExerciseEquipment",
    "ExerciseMuscleGroup",
    "IdempotencyKey",
    "MuscleGroup",
    "PlanAssignment",
    "PlanDay",
    "PlanSlot",
    "PlanSlotOption",
    "PlanVersion",
    "RefreshToken",
    "Role",
    "SchemaVersion",
    "SyncChange",
    "SystemSetting",
    "TrainingPlan",
    "User",
    "UserRole",
    "WorkoutSession",
    "WorkoutSet",
]

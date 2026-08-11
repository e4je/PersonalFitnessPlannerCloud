from __future__ import annotations

from datetime import date, datetime
from decimal import Decimal
from typing import Any

from sqlalchemy import CheckConstraint, Date, ForeignKey, Index, Integer, JSON, Numeric, String, Text, UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db.base import Base, SyncEntityMixin, UTCDateTime


class WorkoutSession(SyncEntityMixin, Base):
    __tablename__ = "workout_sessions"
    __table_args__ = (
        UniqueConstraint("user_id", "client_id", name="uq_workout_sessions_user_client"),
        CheckConstraint(
            "source_device IN ('android', 'windows', 'web', 'api')", name="source_device_allowed"
        ),
        CheckConstraint(
            "status IN ('planned', 'in_progress', 'completed', 'cancelled')",
            name="status_allowed",
        ),
        CheckConstraint("training_week IS NULL OR training_week > 0", name="training_week_positive"),
        CheckConstraint(
            "completed_at IS NULL OR started_at IS NULL OR completed_at >= started_at",
            name="time_range",
        ),
        Index("ix_workout_sessions_user_date", "user_id", "local_date"),
        Index("ix_workout_sessions_user_updated", "user_id", "updated_at"),
    )

    user_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True
    )
    client_id: Mapped[str | None] = mapped_column(String(36), nullable=True)
    source_device: Mapped[str] = mapped_column(String(16), nullable=False)
    client_version: Mapped[str | None] = mapped_column(String(64), nullable=True)
    plan_assignment_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("plan_assignments.id", ondelete="SET NULL"), nullable=True, index=True
    )
    plan_version_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("plan_versions.id", ondelete="SET NULL"), nullable=True, index=True
    )
    plan_day_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("plan_days.id", ondelete="SET NULL"), nullable=True, index=True
    )
    local_date: Mapped[date] = mapped_column(Date, nullable=False, index=True)
    status: Mapped[str] = mapped_column(String(16), nullable=False, default="in_progress", index=True)
    training_week: Mapped[int | None] = mapped_column(Integer, nullable=True)
    ab_state: Mapped[str | None] = mapped_column(String(16), nullable=True)
    started_at: Mapped[datetime | None] = mapped_column(UTCDateTime(), nullable=True)
    completed_at: Mapped[datetime | None] = mapped_column(UTCDateTime(), nullable=True)
    notes: Mapped[str | None] = mapped_column(Text, nullable=True)
    plan_snapshot_json: Mapped[dict[str, Any]] = mapped_column(
        "plan_snapshot", JSON, nullable=False, default=dict
    )
    metadata_json: Mapped[dict[str, Any]] = mapped_column(
        "metadata", JSON, nullable=False, default=dict
    )

    user: Mapped[Any] = relationship("User")
    plan_assignment: Mapped[Any | None] = relationship(
        "PlanAssignment", back_populates="workout_sessions"
    )
    plan_version: Mapped[Any | None] = relationship("PlanVersion")
    plan_day: Mapped[Any | None] = relationship("PlanDay")
    sets: Mapped[list[WorkoutSet]] = relationship(
        back_populates="session",
        cascade="all, delete-orphan",
        order_by="WorkoutSet.set_number",
    )


class WorkoutSet(SyncEntityMixin, Base):
    __tablename__ = "workout_sets"
    __table_args__ = (
        UniqueConstraint(
            "workout_session_id", "client_set_id", name="uq_workout_sets_session_client"
        ),
        CheckConstraint("set_number > 0", name="set_number_positive"),
        CheckConstraint(
            "set_type IN ('warmup', 'working', 'drop', 'failure', 'cardio', 'other')",
            name="set_type_allowed",
        ),
        CheckConstraint("weight_kg IS NULL OR weight_kg >= 0", name="weight_nonnegative"),
        CheckConstraint("reps IS NULL OR reps >= 0", name="reps_nonnegative"),
        CheckConstraint(
            "duration_seconds IS NULL OR duration_seconds >= 0", name="duration_nonnegative"
        ),
        CheckConstraint(
            "distance_meters IS NULL OR distance_meters >= 0", name="distance_nonnegative"
        ),
        CheckConstraint("rir IS NULL OR rir >= 0", name="rir_nonnegative"),
        Index("ix_workout_sets_session_number", "workout_session_id", "set_number"),
    )

    workout_session_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("workout_sessions.id", ondelete="CASCADE"), nullable=False, index=True
    )
    client_set_id: Mapped[str | None] = mapped_column(String(36), nullable=True)
    exercise_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("exercises.id", ondelete="SET NULL"), nullable=True, index=True
    )
    plan_slot_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("plan_slots.id", ondelete="SET NULL"), nullable=True, index=True
    )
    set_number: Mapped[int] = mapped_column(Integer, nullable=False)
    set_type: Mapped[str] = mapped_column(String(16), nullable=False, default="working")
    weight_kg: Mapped[Decimal | None] = mapped_column(Numeric(8, 2), nullable=True)
    reps: Mapped[int | None] = mapped_column(Integer, nullable=True)
    duration_seconds: Mapped[int | None] = mapped_column(Integer, nullable=True)
    distance_meters: Mapped[Decimal | None] = mapped_column(Numeric(10, 2), nullable=True)
    rir: Mapped[Decimal | None] = mapped_column(Numeric(4, 1), nullable=True)
    completed_at: Mapped[datetime | None] = mapped_column(UTCDateTime(), nullable=True)
    notes: Mapped[str | None] = mapped_column(Text, nullable=True)
    exercise_snapshot_json: Mapped[dict[str, Any]] = mapped_column(
        "exercise_snapshot", JSON, nullable=False, default=dict
    )
    prescription_snapshot_json: Mapped[dict[str, Any]] = mapped_column(
        "prescription_snapshot", JSON, nullable=False, default=dict
    )

    session: Mapped[WorkoutSession] = relationship(back_populates="sets")
    exercise: Mapped[Any | None] = relationship("Exercise")
    plan_slot: Mapped[Any | None] = relationship("PlanSlot")


class DailyReadiness(SyncEntityMixin, Base):
    __tablename__ = "daily_readiness"
    __table_args__ = (
        UniqueConstraint("user_id", "local_date", name="uq_daily_readiness_user_date"),
        CheckConstraint(
            "sleep_quality IS NULL OR sleep_quality BETWEEN 1 AND 5", name="sleep_quality_range"
        ),
        CheckConstraint("fatigue IS NULL OR fatigue BETWEEN 1 AND 10", name="fatigue_range"),
        CheckConstraint("soreness IS NULL OR soreness BETWEEN 1 AND 5", name="soreness_range"),
        CheckConstraint("stress IS NULL OR stress BETWEEN 1 AND 5", name="stress_range"),
        CheckConstraint(
            "motivation IS NULL OR motivation BETWEEN 1 AND 5", name="motivation_range"
        ),
        Index("ix_daily_readiness_user_updated", "user_id", "updated_at"),
    )

    user_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True
    )
    local_date: Mapped[date] = mapped_column(Date, nullable=False, index=True)
    sleep_quality: Mapped[int | None] = mapped_column(Integer, nullable=True)
    fatigue: Mapped[int | None] = mapped_column(Integer, nullable=True)
    soreness: Mapped[int | None] = mapped_column(Integer, nullable=True)
    stress: Mapped[int | None] = mapped_column(Integer, nullable=True)
    motivation: Mapped[int | None] = mapped_column(Integer, nullable=True)
    notes: Mapped[str | None] = mapped_column(Text, nullable=True)
    metrics_json: Mapped[dict[str, Any]] = mapped_column("metrics", JSON, nullable=False, default=dict)

    user: Mapped[Any] = relationship("User")


class CardioSession(SyncEntityMixin, Base):
    __tablename__ = "cardio_sessions"
    __table_args__ = (
        UniqueConstraint("user_id", "client_id", name="uq_cardio_sessions_user_client"),
        CheckConstraint(
            "source_device IN ('android', 'windows', 'web', 'api')", name="source_device_allowed"
        ),
        CheckConstraint("duration_seconds >= 0", name="duration_nonnegative"),
        CheckConstraint(
            "distance_meters IS NULL OR distance_meters >= 0", name="distance_nonnegative"
        ),
        CheckConstraint(
            "average_heart_rate IS NULL OR average_heart_rate > 0", name="heart_rate_positive"
        ),
        CheckConstraint("calories IS NULL OR calories >= 0", name="calories_nonnegative"),
        CheckConstraint(
            "completed_at IS NULL OR started_at IS NULL OR completed_at >= started_at",
            name="time_range",
        ),
        Index("ix_cardio_sessions_user_date", "user_id", "local_date"),
        Index("ix_cardio_sessions_user_updated", "user_id", "updated_at"),
    )

    user_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True
    )
    client_id: Mapped[str | None] = mapped_column(String(36), nullable=True)
    source_device: Mapped[str] = mapped_column(String(16), nullable=False)
    client_version: Mapped[str | None] = mapped_column(String(64), nullable=True)
    local_date: Mapped[date] = mapped_column(Date, nullable=False, index=True)
    activity_type: Mapped[str] = mapped_column(String(64), nullable=False, index=True)
    started_at: Mapped[datetime | None] = mapped_column(UTCDateTime(), nullable=True)
    completed_at: Mapped[datetime | None] = mapped_column(UTCDateTime(), nullable=True)
    duration_seconds: Mapped[int] = mapped_column(Integer, nullable=False)
    distance_meters: Mapped[Decimal | None] = mapped_column(Numeric(10, 2), nullable=True)
    average_heart_rate: Mapped[int | None] = mapped_column(Integer, nullable=True)
    calories: Mapped[Decimal | None] = mapped_column(Numeric(10, 2), nullable=True)
    notes: Mapped[str | None] = mapped_column(Text, nullable=True)
    metrics_json: Mapped[dict[str, Any]] = mapped_column("metrics", JSON, nullable=False, default=dict)

    user: Mapped[Any] = relationship("User")

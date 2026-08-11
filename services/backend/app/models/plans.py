from __future__ import annotations

from datetime import date, datetime
from typing import Any

from sqlalchemy import Boolean, CheckConstraint, Date, ForeignKey, Index, Integer, JSON, Numeric, String, Text, UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db.base import Base, SyncEntityMixin, UTCDateTime, utcnow


class TrainingPlan(SyncEntityMixin, Base):
    __tablename__ = "training_plans"
    __table_args__ = (Index("ix_training_plans_owner_active", "owner_user_id", "is_active"),)

    owner_user_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="SET NULL"), nullable=True, index=True
    )
    name: Mapped[str] = mapped_column(String(160), nullable=False, index=True)
    description: Mapped[str | None] = mapped_column(Text, nullable=True)
    goal: Mapped[str | None] = mapped_column(String(120), nullable=True)
    is_system: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    is_active: Mapped[bool] = mapped_column(Boolean, nullable=False, default=True)

    owner: Mapped[Any | None] = relationship("User", foreign_keys=[owner_user_id])
    versions: Mapped[list[PlanVersion]] = relationship(
        back_populates="plan",
        cascade="all, delete-orphan",
        order_by="PlanVersion.version_number",
    )


class PlanVersion(SyncEntityMixin, Base):
    __tablename__ = "plan_versions"
    __table_args__ = (
        UniqueConstraint(
            "training_plan_id", "version_number", name="uq_plan_versions_plan_number"
        ),
        CheckConstraint("version_number > 0", name="version_number_positive"),
        CheckConstraint(
            "status IN ('draft', 'published', 'archived')", name="status_allowed"
        ),
        CheckConstraint(
            "weekly_frequency BETWEEN 1 AND 7", name="weekly_frequency_range"
        ),
        CheckConstraint("min_rest_days >= 0", name="min_rest_days_nonnegative"),
        CheckConstraint(
            "fatigue_threshold BETWEEN 1 AND 10", name="fatigue_threshold_range"
        ),
        CheckConstraint("initial_reduced_weeks >= 0", name="initial_reduced_weeks_nonnegative"),
        CheckConstraint("initial_set_count > 0", name="initial_set_count_positive"),
        Index("ix_plan_versions_plan_status", "training_plan_id", "status"),
    )

    training_plan_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("training_plans.id", ondelete="CASCADE"), nullable=False, index=True
    )
    version_number: Mapped[int] = mapped_column(Integer, nullable=False)
    status: Mapped[str] = mapped_column(String(16), nullable=False, default="draft", index=True)
    weekly_frequency: Mapped[int] = mapped_column(Integer, nullable=False, default=3)
    min_rest_days: Mapped[int] = mapped_column(Integer, nullable=False, default=1)
    fatigue_threshold: Mapped[int] = mapped_column(Integer, nullable=False, default=8)
    initial_reduced_weeks: Mapped[int] = mapped_column(Integer, nullable=False, default=2)
    initial_set_count: Mapped[int] = mapped_column(Integer, nullable=False, default=2)
    config_json: Mapped[dict[str, Any]] = mapped_column(JSON, nullable=False, default=dict)
    changelog: Mapped[str | None] = mapped_column(Text, nullable=True)
    published_at: Mapped[datetime | None] = mapped_column(UTCDateTime(), nullable=True, index=True)
    published_by_user_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="SET NULL"), nullable=True, index=True
    )

    plan: Mapped[TrainingPlan] = relationship(back_populates="versions")
    published_by: Mapped[Any | None] = relationship("User", foreign_keys=[published_by_user_id])
    days: Mapped[list[PlanDay]] = relationship(
        back_populates="plan_version",
        cascade="all, delete-orphan",
        order_by="PlanDay.sort_order",
    )
    assignments: Mapped[list[PlanAssignment]] = relationship(back_populates="plan_version")

    @property
    def is_published(self) -> bool:
        return self.status == "published"

    def assert_mutable(self) -> None:
        if self.status != "draft":
            raise ValueError("only draft plan versions may be modified")

    def publish(self, published_by_user_id: str | None = None) -> None:
        self.assert_mutable()
        if not self.days:
            raise ValueError("a plan version must contain at least one day before publishing")
        self.status = "published"
        self.published_at = utcnow()
        self.published_by_user_id = published_by_user_id
        self.version += 1


class PlanDay(SyncEntityMixin, Base):
    __tablename__ = "plan_days"
    __table_args__ = (
        UniqueConstraint("plan_version_id", "day_code", name="uq_plan_days_version_code"),
        UniqueConstraint("plan_version_id", "sort_order", name="uq_plan_days_version_order"),
    )

    plan_version_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("plan_versions.id", ondelete="CASCADE"), nullable=False, index=True
    )
    day_code: Mapped[str] = mapped_column(String(32), nullable=False)
    name: Mapped[str] = mapped_column(String(160), nullable=False)
    sort_order: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    notes: Mapped[str | None] = mapped_column(Text, nullable=True)

    plan_version: Mapped[PlanVersion] = relationship(back_populates="days")
    slots: Mapped[list[PlanSlot]] = relationship(
        back_populates="day",
        cascade="all, delete-orphan",
        order_by="PlanSlot.sort_order",
    )


class PlanSlot(SyncEntityMixin, Base):
    __tablename__ = "plan_slots"
    __table_args__ = (
        UniqueConstraint("plan_day_id", "sort_order", name="uq_plan_slots_day_order"),
        CheckConstraint("sort_order >= 0", name="sort_order_nonnegative"),
    )

    plan_day_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("plan_days.id", ondelete="CASCADE"), nullable=False, index=True
    )
    name: Mapped[str] = mapped_column(String(160), nullable=False)
    target_muscle_group_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("muscle_groups.id", ondelete="SET NULL"), nullable=True, index=True
    )
    sort_order: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    notes: Mapped[str | None] = mapped_column(Text, nullable=True)
    selection_rule_json: Mapped[dict[str, Any]] = mapped_column(JSON, nullable=False, default=dict)

    day: Mapped[PlanDay] = relationship(back_populates="slots")
    target_muscle_group: Mapped[Any | None] = relationship(
        "MuscleGroup", back_populates="plan_slots"
    )
    options: Mapped[list[PlanSlotOption]] = relationship(
        back_populates="slot",
        cascade="all, delete-orphan",
        order_by="PlanSlotOption.sort_order",
    )


class PlanSlotOption(SyncEntityMixin, Base):
    __tablename__ = "plan_slot_options"
    __table_args__ = (
        UniqueConstraint("plan_slot_id", "exercise_id", name="uq_plan_slot_options_exercise"),
        UniqueConstraint("plan_slot_id", "sort_order", name="uq_plan_slot_options_order"),
        CheckConstraint("set_count > 0", name="set_count_positive"),
        CheckConstraint("reps_min IS NULL OR reps_min >= 0", name="reps_min_nonnegative"),
        CheckConstraint("reps_max IS NULL OR reps_max >= 0", name="reps_max_nonnegative"),
        CheckConstraint(
            "reps_min IS NULL OR reps_max IS NULL OR reps_min <= reps_max", name="reps_range"
        ),
        CheckConstraint(
            "duration_seconds_min IS NULL OR duration_seconds_min >= 0",
            name="duration_min_nonnegative",
        ),
        CheckConstraint(
            "duration_seconds_max IS NULL OR duration_seconds_max >= 0",
            name="duration_max_nonnegative",
        ),
        CheckConstraint(
            "duration_seconds_min IS NULL OR duration_seconds_max IS NULL "
            "OR duration_seconds_min <= duration_seconds_max",
            name="duration_range",
        ),
        CheckConstraint("rir_min IS NULL OR rir_min >= 0", name="rir_min_nonnegative"),
        CheckConstraint("rir_max IS NULL OR rir_max >= 0", name="rir_max_nonnegative"),
        CheckConstraint(
            "rir_min IS NULL OR rir_max IS NULL OR rir_min <= rir_max", name="rir_range"
        ),
    )

    plan_slot_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("plan_slots.id", ondelete="CASCADE"), nullable=False, index=True
    )
    exercise_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("exercises.id", ondelete="RESTRICT"), nullable=False, index=True
    )
    is_preferred: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False, index=True)
    sort_order: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    set_count: Mapped[int] = mapped_column(Integer, nullable=False)
    reps_min: Mapped[int | None] = mapped_column(Integer, nullable=True)
    reps_max: Mapped[int | None] = mapped_column(Integer, nullable=True)
    duration_seconds_min: Mapped[int | None] = mapped_column(Integer, nullable=True)
    duration_seconds_max: Mapped[int | None] = mapped_column(Integer, nullable=True)
    rir_min: Mapped[float | None] = mapped_column(Numeric(4, 1), nullable=True)
    rir_max: Mapped[float | None] = mapped_column(Numeric(4, 1), nullable=True)
    is_per_side: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    prescription_json: Mapped[dict[str, Any]] = mapped_column(JSON, nullable=False, default=dict)

    slot: Mapped[PlanSlot] = relationship(back_populates="options")
    exercise: Mapped[Any] = relationship("Exercise", back_populates="plan_options")


class PlanAssignment(SyncEntityMixin, Base):
    __tablename__ = "plan_assignments"
    __table_args__ = (
        UniqueConstraint(
            "user_id", "plan_version_id", "starts_on", name="uq_plan_assignments_user_version_start"
        ),
        CheckConstraint(
            "status IN ('scheduled', 'active', 'completed', 'cancelled')", name="status_allowed"
        ),
        CheckConstraint("ends_on IS NULL OR ends_on >= starts_on", name="date_range"),
        Index("ix_plan_assignments_user_status_start", "user_id", "status", "starts_on"),
    )

    user_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True
    )
    plan_version_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("plan_versions.id", ondelete="RESTRICT"), nullable=False, index=True
    )
    status: Mapped[str] = mapped_column(String(16), nullable=False, default="scheduled", index=True)
    starts_on: Mapped[date] = mapped_column(Date, nullable=False)
    ends_on: Mapped[date | None] = mapped_column(Date, nullable=True)
    assigned_at: Mapped[datetime] = mapped_column(UTCDateTime(), nullable=False, default=utcnow)
    assigned_by_user_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="SET NULL"), nullable=True, index=True
    )
    settings_json: Mapped[dict[str, Any]] = mapped_column(JSON, nullable=False, default=dict)

    user: Mapped[Any] = relationship("User", foreign_keys=[user_id])
    assigned_by: Mapped[Any | None] = relationship("User", foreign_keys=[assigned_by_user_id])
    plan_version: Mapped[PlanVersion] = relationship(back_populates="assignments")
    workout_sessions: Mapped[list[Any]] = relationship(
        "WorkoutSession", back_populates="plan_assignment"
    )

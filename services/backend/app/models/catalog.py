from __future__ import annotations

from typing import Any

from sqlalchemy import Boolean, CheckConstraint, ForeignKey, Index, Integer, JSON, String, Text, UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db.base import Base, SyncEntityMixin


class MuscleGroup(SyncEntityMixin, Base):
    __tablename__ = "muscle_groups"

    code: Mapped[str] = mapped_column(String(64), nullable=False, unique=True)
    name: Mapped[str] = mapped_column(String(120), nullable=False)
    body_region: Mapped[str | None] = mapped_column(String(64), nullable=True, index=True)
    description: Mapped[str | None] = mapped_column(Text, nullable=True)
    sort_order: Mapped[int] = mapped_column(Integer, nullable=False, default=0)

    exercise_links: Mapped[list[ExerciseMuscleGroup]] = relationship(
        back_populates="muscle_group", cascade="all, delete-orphan"
    )
    plan_slots: Mapped[list[Any]] = relationship("PlanSlot", back_populates="target_muscle_group")


class Equipment(SyncEntityMixin, Base):
    __tablename__ = "equipment"
    __table_args__ = (Index("ix_equipment_category_active", "category", "is_active"),)

    code: Mapped[str] = mapped_column(String(64), nullable=False, unique=True)
    name: Mapped[str] = mapped_column(String(120), nullable=False, index=True)
    category: Mapped[str] = mapped_column(String(64), nullable=False, index=True)
    brand: Mapped[str | None] = mapped_column(String(120), nullable=True)
    model: Mapped[str | None] = mapped_column(String(120), nullable=True)
    description: Mapped[str | None] = mapped_column(Text, nullable=True)
    notes: Mapped[str | None] = mapped_column(Text, nullable=True)
    is_active: Mapped[bool] = mapped_column(Boolean, nullable=False, default=True)
    metadata_json: Mapped[dict[str, Any]] = mapped_column(
        "metadata", JSON, nullable=False, default=dict
    )

    exercise_links: Mapped[list[ExerciseEquipment]] = relationship(
        back_populates="equipment", cascade="all, delete-orphan"
    )


class Exercise(SyncEntityMixin, Base):
    __tablename__ = "exercises"
    __table_args__ = (
        CheckConstraint("default_sets IS NULL OR default_sets > 0", name="default_sets_positive"),
        CheckConstraint("rep_min IS NULL OR rep_min >= 0", name="rep_min_nonnegative"),
        CheckConstraint("rep_max IS NULL OR rep_max >= 0", name="rep_max_nonnegative"),
        CheckConstraint(
            "rep_min IS NULL OR rep_max IS NULL OR rep_min <= rep_max", name="rep_range"
        ),
        Index("ix_exercises_body_active", "body_part", "is_active"),
    )

    code: Mapped[str] = mapped_column(String(64), nullable=False, unique=True)
    name: Mapped[str] = mapped_column(String(160), nullable=False, index=True)
    description: Mapped[str | None] = mapped_column(Text, nullable=True)
    body_part: Mapped[str | None] = mapped_column(String(64), nullable=True, index=True)
    movement_pattern: Mapped[str | None] = mapped_column(String(64), nullable=True, index=True)
    difficulty: Mapped[str | None] = mapped_column(String(32), nullable=True)
    default_sets: Mapped[int | None] = mapped_column(Integer, nullable=True)
    rep_min: Mapped[int | None] = mapped_column(Integer, nullable=True)
    rep_max: Mapped[int | None] = mapped_column(Integer, nullable=True)
    rep_unit: Mapped[str] = mapped_column(String(16), nullable=False, default="reps")
    is_unilateral: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    is_active: Mapped[bool] = mapped_column(Boolean, nullable=False, default=True)
    created_by_user_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="SET NULL"), nullable=True, index=True
    )
    common_mistakes_json: Mapped[list[str]] = mapped_column(
        "common_mistakes", JSON, nullable=False, default=list
    )
    metadata_json: Mapped[dict[str, Any]] = mapped_column(
        "metadata", JSON, nullable=False, default=dict
    )

    created_by: Mapped[Any | None] = relationship("User")
    cues: Mapped[list[ExerciseCue]] = relationship(
        back_populates="exercise",
        cascade="all, delete-orphan",
        order_by="ExerciseCue.sort_order",
    )
    alternatives: Mapped[list[ExerciseAlternative]] = relationship(
        back_populates="exercise",
        cascade="all, delete-orphan",
        foreign_keys="ExerciseAlternative.exercise_id",
        order_by="ExerciseAlternative.priority",
    )
    alternative_for: Mapped[list[ExerciseAlternative]] = relationship(
        back_populates="alternative_exercise",
        foreign_keys="ExerciseAlternative.alternative_exercise_id",
    )
    muscle_group_links: Mapped[list[ExerciseMuscleGroup]] = relationship(
        back_populates="exercise", cascade="all, delete-orphan"
    )
    equipment_links: Mapped[list[ExerciseEquipment]] = relationship(
        back_populates="exercise", cascade="all, delete-orphan"
    )
    plan_options: Mapped[list[Any]] = relationship("PlanSlotOption", back_populates="exercise")


class ExerciseCue(SyncEntityMixin, Base):
    __tablename__ = "exercise_cues"
    __table_args__ = (
        UniqueConstraint("exercise_id", "sort_order", name="uq_exercise_cues_exercise_order"),
    )

    exercise_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("exercises.id", ondelete="CASCADE"), nullable=False, index=True
    )
    text: Mapped[str] = mapped_column(Text, nullable=False)
    sort_order: Mapped[int] = mapped_column(Integer, nullable=False, default=0)

    exercise: Mapped[Exercise] = relationship(back_populates="cues")


class ExerciseAlternative(SyncEntityMixin, Base):
    __tablename__ = "exercise_alternatives"
    __table_args__ = (
        UniqueConstraint(
            "exercise_id", "alternative_exercise_id", name="uq_exercise_alternatives_pair"
        ),
        CheckConstraint("exercise_id <> alternative_exercise_id", name="different_exercises"),
    )

    exercise_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("exercises.id", ondelete="CASCADE"), nullable=False, index=True
    )
    alternative_exercise_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("exercises.id", ondelete="CASCADE"), nullable=False, index=True
    )
    priority: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    notes: Mapped[str | None] = mapped_column(Text, nullable=True)

    exercise: Mapped[Exercise] = relationship(
        back_populates="alternatives", foreign_keys=[exercise_id]
    )
    alternative_exercise: Mapped[Exercise] = relationship(
        back_populates="alternative_for", foreign_keys=[alternative_exercise_id]
    )


class ExerciseMuscleGroup(SyncEntityMixin, Base):
    __tablename__ = "exercise_muscle_groups"
    __table_args__ = (
        UniqueConstraint(
            "exercise_id", "muscle_group_id", name="uq_exercise_muscle_groups_pair"
        ),
        Index("ix_exercise_muscle_groups_primary", "muscle_group_id", "is_primary"),
    )

    exercise_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("exercises.id", ondelete="CASCADE"), nullable=False, index=True
    )
    muscle_group_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("muscle_groups.id", ondelete="CASCADE"), nullable=False, index=True
    )
    is_primary: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)

    exercise: Mapped[Exercise] = relationship(back_populates="muscle_group_links")
    muscle_group: Mapped[MuscleGroup] = relationship(back_populates="exercise_links")


class ExerciseEquipment(SyncEntityMixin, Base):
    __tablename__ = "exercise_equipment"
    __table_args__ = (
        UniqueConstraint("exercise_id", "equipment_id", name="uq_exercise_equipment_pair"),
        CheckConstraint("quantity > 0", name="quantity_positive"),
    )

    exercise_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("exercises.id", ondelete="CASCADE"), nullable=False, index=True
    )
    equipment_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("equipment.id", ondelete="CASCADE"), nullable=False, index=True
    )
    is_required: Mapped[bool] = mapped_column(Boolean, nullable=False, default=True)
    quantity: Mapped[int] = mapped_column(Integer, nullable=False, default=1)
    notes: Mapped[str | None] = mapped_column(Text, nullable=True)

    exercise: Mapped[Exercise] = relationship(back_populates="equipment_links")
    equipment: Mapped[Equipment] = relationship(back_populates="exercise_links")

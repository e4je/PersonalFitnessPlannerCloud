from __future__ import annotations

from datetime import date

from sqlalchemy import select
from sqlalchemy.orm import Session, joinedload, selectinload

from app.models import Exercise, PlanAssignment, PlanDay, PlanSlot, PlanSlotOption, PlanVersion, TrainingPlan
from app.seed.default_data import DEFAULT_PLAN


def plan_load_options() -> tuple[object, ...]:
    return (
        joinedload(PlanVersion.plan),
        selectinload(PlanVersion.days)
        .selectinload(PlanDay.slots)
        .selectinload(PlanSlot.options)
        .selectinload(PlanSlotOption.exercise)
        .selectinload(Exercise.equipment_links),
    )


def get_plan_version(db: Session, plan_version_id: str) -> PlanVersion | None:
    return db.scalar(
        select(PlanVersion)
        .where(PlanVersion.id == plan_version_id, PlanVersion.deleted_at.is_(None))
        .options(*plan_load_options())
    )


def get_current_assignment(
    db: Session, user_id: str, local_date: date
) -> PlanAssignment | None:
    return db.scalar(
        select(PlanAssignment)
        .where(
            PlanAssignment.user_id == user_id,
            PlanAssignment.deleted_at.is_(None),
            PlanAssignment.status.in_(("active", "scheduled")),
            PlanAssignment.starts_on <= local_date,
            (PlanAssignment.ends_on.is_(None) | (PlanAssignment.ends_on >= local_date)),
        )
        .order_by(PlanAssignment.starts_on.desc(), PlanAssignment.created_at.desc())
        .options(selectinload(PlanAssignment.plan_version).options(*plan_load_options()))
        .limit(1)
    )


def get_current_plan(
    db: Session, user_id: str, local_date: date
) -> tuple[PlanVersion | None, PlanAssignment | None]:
    assignment = get_current_assignment(db, user_id, local_date)
    if assignment is not None:
        return assignment.plan_version, assignment
    fallback = db.scalar(
        select(PlanVersion)
        .join(TrainingPlan, TrainingPlan.id == PlanVersion.training_plan_id)
        .where(
            PlanVersion.id == DEFAULT_PLAN["plan_version_id"],
            PlanVersion.status == "published",
            PlanVersion.deleted_at.is_(None),
            TrainingPlan.is_system.is_(True),
            TrainingPlan.is_active.is_(True),
            TrainingPlan.deleted_at.is_(None),
        )
        .options(*plan_load_options())
        .limit(1)
    )
    if fallback is not None:
        return fallback, None
    fallback = db.scalar(
        select(PlanVersion)
        .join(TrainingPlan, TrainingPlan.id == PlanVersion.training_plan_id)
        .where(
            PlanVersion.status == "published",
            PlanVersion.deleted_at.is_(None),
            TrainingPlan.is_system.is_(True),
            TrainingPlan.is_active.is_(True),
            TrainingPlan.deleted_at.is_(None),
        )
        .order_by(PlanVersion.published_at.desc(), PlanVersion.version_number.desc())
        .options(*plan_load_options())
        .limit(1)
    )
    return fallback, None

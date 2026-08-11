from __future__ import annotations

from typing import Annotated, Any

from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.orm import Session, selectinload

from app.api.cardio import serialize_cardio
from app.api.dependencies import CurrentUser, permissions_for_user
from app.api.readiness import serialize_readiness
from app.api.recommendation import build_today_recommendation, local_today
from app.api.workouts import serialize_workout
from app.core.config import settings
from app.db.base import utcnow
from app.db.session import get_db
from app.models import (
    CardioSession,
    DailyReadiness,
    Equipment,
    Exercise,
    PlanAssignment,
    PlanVersion,
    WorkoutSession,
)
from app.schemas.bootstrap import BootstrapOut
from app.services.plan_queries import get_current_plan, plan_load_options
from app.services.serialization import (
    assignment_to_dict,
    equipment_to_dict,
    exercise_to_dict,
    plan_version_to_dict,
    user_to_dict,
)
from app.services.sync import encode_sync_cursor, latest_sync_sequence


router = APIRouter(tags=["bootstrap"])


@router.get("/bootstrap", response_model=BootstrapOut)
def bootstrap(
    current_user: CurrentUser,
    db: Annotated[Session, Depends(get_db)],
) -> dict[str, Any]:
    today = local_today(current_user.timezone)
    plan_version, _current_assignment = get_current_plan(db, current_user.id, today)
    exercises = list(
        db.scalars(
            select(Exercise)
            .where(Exercise.deleted_at.is_(None), Exercise.is_active.is_(True))
            .options(
                selectinload(Exercise.cues),
                selectinload(Exercise.alternatives),
                selectinload(Exercise.equipment_links),
            )
            .order_by(Exercise.name)
        ).unique()
    )
    equipment = list(
        db.scalars(
            select(Equipment)
            .where(Equipment.deleted_at.is_(None), Equipment.is_active.is_(True))
            .order_by(Equipment.name)
        )
    )
    assignments = list(
        db.scalars(
            select(PlanAssignment)
            .where(
                PlanAssignment.user_id == current_user.id,
                PlanAssignment.deleted_at.is_(None),
            )
            .order_by(PlanAssignment.starts_on.desc())
        )
    )
    referenced_plan_version_ids = {item.plan_version_id for item in assignments}
    if plan_version is not None:
        referenced_plan_version_ids.add(plan_version.id)
    plan_versions = (
        list(
            db.scalars(
                select(PlanVersion)
                .where(PlanVersion.id.in_(referenced_plan_version_ids))
                .options(*plan_load_options())
                .order_by(PlanVersion.training_plan_id, PlanVersion.version_number)
            ).unique()
        )
        if referenced_plan_version_ids
        else []
    )
    workouts = list(
        db.scalars(
            select(WorkoutSession)
            .where(
                WorkoutSession.user_id == current_user.id,
                WorkoutSession.deleted_at.is_(None),
            )
            .options(selectinload(WorkoutSession.sets))
            .order_by(WorkoutSession.local_date.desc(), WorkoutSession.updated_at.desc())
        ).unique()
    )
    readiness_rows = list(
        db.scalars(
            select(DailyReadiness)
            .where(
                DailyReadiness.user_id == current_user.id,
                DailyReadiness.deleted_at.is_(None),
            )
            .order_by(DailyReadiness.local_date.desc())
        )
    )
    cardio_rows = list(
        db.scalars(
            select(CardioSession)
            .where(
                CardioSession.user_id == current_user.id,
                CardioSession.deleted_at.is_(None),
            )
            .order_by(CardioSession.local_date.desc(), CardioSession.updated_at.desc())
        )
    )
    cursor = encode_sync_cursor(latest_sync_sequence(db))
    serialized_plan = plan_version_to_dict(plan_version) if plan_version is not None else None
    return {
        "user": {
            **user_to_dict(current_user),
            "roles": sorted(role.name for role in current_user.roles),
        },
        "permissions": permissions_for_user(db, current_user),
        "current_plan": serialized_plan,
        "plan_version": serialized_plan,
        "plan_versions": [plan_version_to_dict(item) for item in plan_versions],
        "exercises": [exercise_to_dict(item) for item in exercises],
        "equipment": [equipment_to_dict(item) for item in equipment],
        "assignments": [assignment_to_dict(item) for item in assignments],
        "workout_sessions": [serialize_workout(item).model_dump(mode="json") for item in workouts],
        "readiness": [serialize_readiness(item).model_dump(mode="json") for item in readiness_rows],
        "cardio_sessions": [serialize_cardio(item).model_dump(mode="json") for item in cardio_rows],
        "recommendation": build_today_recommendation(db, current_user.id, today),
        "cursor": cursor,
        "sync_cursor": cursor,
        "server_time": utcnow(),
        "api_version": settings.api_version,
        "schema_version": settings.schema_version,
    }

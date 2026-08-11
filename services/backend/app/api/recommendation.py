from __future__ import annotations

from datetime import date, datetime, timedelta
from typing import Annotated, Any
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from fastapi import APIRouter, Depends
from sqlalchemy import func, or_, select
from sqlalchemy.orm import Session

from app.api.dependencies import CurrentUser
from app.db.session import get_db
from app.models import DailyReadiness, WorkoutSession
from app.services.plan_queries import get_current_plan
from app.services.recommendation import api_recommendation_state, recommend_strength_session


router = APIRouter(tags=["recommendation"])


def local_today(timezone_name: str) -> date:
    try:
        return datetime.now(ZoneInfo(timezone_name)).date()
    except ZoneInfoNotFoundError:
        return datetime.now(ZoneInfo("UTC")).date()


def build_today_recommendation(db: Session, user_id: str, today: date) -> dict[str, Any]:
    plan_version, assignment = get_current_plan(db, user_id, today)
    if plan_version is None:
        return {"should_train": False, "reason": "no_plan", "local_date": today}

    completed_workouts = list(
        db.scalars(
            select(WorkoutSession)
            .where(
                WorkoutSession.user_id == user_id,
                WorkoutSession.status == "completed",
                WorkoutSession.deleted_at.is_(None),
            )
            .order_by(WorkoutSession.local_date.asc(), WorkoutSession.completed_at.asc())
        )
    )
    latest = completed_workouts[-1] if completed_workouts else None
    readiness = db.scalar(
        select(DailyReadiness)
        .where(
            DailyReadiness.user_id == user_id,
            DailyReadiness.deleted_at.is_(None),
        )
        .order_by(DailyReadiness.local_date.desc())
        .limit(1)
    )

    if assignment is not None:
        started = assignment.starts_on
    else:
        # System-plan fallback has no assignment start date.  Derive a stable
        # origin from persisted workout history so the adaptation period does
        # not reset to week 1 on every request.  NULL covers legacy sessions
        # recorded before plan-version linkage became mandatory.
        started = db.scalar(
            select(func.min(WorkoutSession.local_date)).where(
                WorkoutSession.user_id == user_id,
                WorkoutSession.status == "completed",
                WorkoutSession.deleted_at.is_(None),
                or_(
                    WorkoutSession.plan_version_id == plan_version.id,
                    WorkoutSession.plan_version_id.is_(None),
                ),
            )
        ) or today
    training_week = max(1, ((today - started).days // 7) + 1)
    week_start = today - timedelta(days=today.weekday())
    completed_this_week = sum(
        week_start <= item.local_date <= today for item in completed_workouts
    )
    last_state = (latest.ab_state or "B").upper() if latest is not None else "B"
    decision = recommend_strength_session(
        today=today,
        completed_workouts=[
            {
                "local_date": item.local_date,
                "plan_code": item.ab_state,
                "is_full_body": bool((item.metadata_json or {}).get("is_full_body", True)),
            }
            for item in completed_workouts
        ],
        fatigue_score=(
            readiness.fatigue
            if readiness is not None and readiness.local_date == today
            else None
        ),
        weekly_limit=plan_version.weekly_frequency,
        fatigue_threshold=plan_version.fatigue_threshold,
        minimum_rest_days=plan_version.min_rest_days,
    )
    should_train, reasons = api_recommendation_state(decision)

    return {
        "local_date": today,
        "should_train": should_train,
        "reasons": reasons,
        "session": decision["session"],
        "decision_reason": decision["reason"],
        "plan_version_id": plan_version.id,
        "training_day": decision["next_strength_day"],
        "current_ab_state": last_state,
        "weekly_max_sessions": plan_version.weekly_frequency,
        "completed_sessions_this_week": completed_this_week,
        "minimum_rest_days": plan_version.min_rest_days,
        "fatigue_threshold": plan_version.fatigue_threshold,
        "current_training_week": training_week,
        "initial_reduced_weeks": plan_version.initial_reduced_weeks,
        "effective_set_cap": (
            plan_version.initial_set_count
            if training_week <= plan_version.initial_reduced_weeks
            else None
        ),
        "latest_workout_local_date": latest.local_date if latest is not None else None,
        "latest_readiness": {
            "local_date": readiness.local_date,
            "fatigue_score": readiness.fatigue,
            "sleep_quality": readiness.sleep_quality,
        }
        if readiness is not None
        else None,
    }


@router.get("/recommendation/today")
def recommendation_today(
    current_user: CurrentUser,
    db: Annotated[Session, Depends(get_db)],
) -> dict[str, Any]:
    return build_today_recommendation(db, current_user.id, local_today(current_user.timezone))

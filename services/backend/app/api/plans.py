from __future__ import annotations

from datetime import datetime
from typing import Annotated
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from fastapi import APIRouter, Depends, HTTPException, status
from sqlalchemy.orm import Session

from app.api.dependencies import CurrentUser, role_names
from app.db.session import get_db
from app.schemas.plans import PlanVersionOut
from app.services.plan_queries import can_access_plan_version, get_current_plan, get_plan_version
from app.services.serialization import plan_version_to_dict


router = APIRouter(tags=["plans"])


def _today(timezone_name: str):
    try:
        return datetime.now(ZoneInfo(timezone_name)).date()
    except ZoneInfoNotFoundError:
        return datetime.now(ZoneInfo("UTC")).date()


@router.get("/plans/current", response_model=PlanVersionOut)
def current_plan(
    current_user: CurrentUser,
    db: Annotated[Session, Depends(get_db)],
) -> dict[str, object]:
    plan_version, _assignment = get_current_plan(db, current_user.id, _today(current_user.timezone))
    if plan_version is None:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail={"code": "plan_not_found", "message": "No current published plan is available"},
        )
    return plan_version_to_dict(plan_version)


@router.get("/plans/{plan_version_id}", response_model=PlanVersionOut)
def plan_detail(
    plan_version_id: str,
    current_user: CurrentUser,
    db: Annotated[Session, Depends(get_db)],
) -> dict[str, object]:
    plan_version = get_plan_version(db, plan_version_id)
    is_admin = current_user.is_superuser or "admin" in {
        name.casefold() for name in role_names(db, current_user)
    }
    if plan_version is None or not can_access_plan_version(
        db,
        plan_version,
        user_id=current_user.id,
        is_admin=is_admin,
        local_date=_today(current_user.timezone),
    ):
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail={"code": "plan_not_found", "message": "Plan version was not found"},
        )
    return plan_version_to_dict(plan_version)

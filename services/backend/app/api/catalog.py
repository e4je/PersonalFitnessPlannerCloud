from __future__ import annotations

from typing import Annotated

from fastapi import APIRouter, Depends, Query
from sqlalchemy import select
from sqlalchemy.orm import Session, selectinload

from app.api.dependencies import CurrentUser
from app.db.session import get_db
from app.models import Equipment, Exercise
from app.schemas.catalog import EquipmentPage, ExercisePage
from app.services.cursors import decode_cursor, encode_cursor
from app.services.serialization import equipment_to_dict, exercise_to_dict


router = APIRouter(tags=["catalog"])


@router.get("/exercises", response_model=ExercisePage)
def list_exercises(
    _current_user: CurrentUser,
    db: Annotated[Session, Depends(get_db)],
    cursor: str | None = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 200,
) -> dict[str, object]:
    decoded = decode_cursor(cursor)
    query = (
        select(Exercise)
        .where(Exercise.deleted_at.is_(None), Exercise.is_active.is_(True))
        .options(
            selectinload(Exercise.cues),
            selectinload(Exercise.alternatives),
            selectinload(Exercise.equipment_links),
        )
        .order_by(Exercise.id)
        .limit(limit + 1)
    )
    if decoded.get("id"):
        query = query.where(Exercise.id > str(decoded["id"]))
    rows = list(db.scalars(query).unique().all())
    has_more = len(rows) > limit
    page = rows[:limit]
    next_cursor = encode_cursor({"id": page[-1].id}) if has_more and page else None
    return {
        "items": [exercise_to_dict(item) for item in page],
        "cursor": cursor,
        "next_cursor": next_cursor,
        "has_more": has_more,
    }


@router.get("/equipment", response_model=EquipmentPage)
def list_equipment(
    _current_user: CurrentUser,
    db: Annotated[Session, Depends(get_db)],
    cursor: str | None = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 200,
) -> dict[str, object]:
    decoded = decode_cursor(cursor)
    query = (
        select(Equipment)
        .where(Equipment.deleted_at.is_(None), Equipment.is_active.is_(True))
        .order_by(Equipment.id)
        .limit(limit + 1)
    )
    if decoded.get("id"):
        query = query.where(Equipment.id > str(decoded["id"]))
    rows = list(db.scalars(query).all())
    has_more = len(rows) > limit
    page = rows[:limit]
    next_cursor = encode_cursor({"id": page[-1].id}) if has_more and page else None
    return {
        "items": [equipment_to_dict(item) for item in page],
        "cursor": cursor,
        "next_cursor": next_cursor,
        "has_more": has_more,
    }

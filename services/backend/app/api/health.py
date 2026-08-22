from __future__ import annotations

from fastapi import APIRouter, HTTPException, status
from sqlalchemy import text
from sqlalchemy.exc import SQLAlchemyError

from app.db.session import DatabaseNotConfiguredError, get_engine


router = APIRouter(tags=["health"])


@router.get("/health/live", summary="Process liveness")
def liveness() -> dict[str, str]:
    return {"status": "ok"}


@router.get("/health/ready", summary="Database readiness")
def readiness() -> dict[str, str]:
    try:
        with get_engine().connect() as connection:
            connection.execute(text("SELECT 1"))
    except DatabaseNotConfiguredError as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={
                "code": "setup_required",
                "message": "Database setup has not been completed",
            },
        ) from exc
    except SQLAlchemyError as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={"code": "not_ready", "message": "Database is unavailable"},
        ) from exc
    return {"status": "ok"}

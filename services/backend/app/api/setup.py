from __future__ import annotations

import hashlib
import hmac

from fastapi import APIRouter, HTTPException, Request, Response, status

from app.core.config import APPLICATION_DATABASE_NAME, settings
from app.db.session import is_database_configured
from app.schemas.setup import DatabaseSetupRequest, DatabaseSetupResponse, SetupStatusResponse
from app.services.auth import LoginRateLimiter
from app.services.first_run_setup import (
    SetupAlreadyConfiguredError,
    SetupConnectionError,
    SetupInProgressError,
    SetupInitializationError,
    SetupPersistenceError,
    ensure_setup_token,
    initialize_database,
)


router = APIRouter(prefix="/setup", tags=["first-run setup"])
setup_rate_limiter = LoginRateLimiter(min(settings.login_attempts_per_minute, 10))


def _client_key(request: Request) -> str:
    address = request.client.host if request.client else "unknown"
    return "setup:" + hashlib.sha256(address.encode("utf-8")).hexdigest()


@router.get("/status", response_model=SetupStatusResponse)
def setup_status(response: Response) -> SetupStatusResponse:
    configured = is_database_configured()
    response.headers["Cache-Control"] = "no-store"
    return SetupStatusResponse(
        configured=configured,
        setup_required=not configured,
        database_name=APPLICATION_DATABASE_NAME,
        token_required=not configured,
        # This endpoint remains anonymous after setup is complete. Return only
        # generic form defaults, never configured infrastructure identifiers.
        default_host="mysql",
        default_port=3306,
        default_username="fitness",
    )


@router.post(
    "/database",
    response_model=DatabaseSetupResponse,
    status_code=status.HTTP_201_CREATED,
)
def setup_database(
    payload: DatabaseSetupRequest,
    request: Request,
    response: Response,
) -> DatabaseSetupResponse:
    if is_database_configured():
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail={"code": "setup_complete", "message": "Database setup is already complete"},
        )

    rate_key = _client_key(request)
    allowed, retry_after = setup_rate_limiter.allowed(rate_key)
    if not allowed:
        raise HTTPException(
            status_code=status.HTTP_429_TOO_MANY_REQUESTS,
            detail={"code": "setup_rate_limited", "message": "Too many setup attempts"},
            headers={"Retry-After": str(retry_after)},
        )
    setup_rate_limiter.register_attempt(rate_key)

    try:
        expected_token = ensure_setup_token()
    except SetupPersistenceError as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={"code": "setup_storage_unavailable", "message": str(exc)},
        ) from None
    provided_token = payload.setup_token.get_secret_value()
    if not 1 <= len(provided_token) <= 512 or not hmac.compare_digest(
        provided_token.encode("utf-8"),
        expected_token.encode("utf-8"),
    ):
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail={"code": "invalid_setup_token", "message": "The setup token is invalid"},
        )

    password = payload.password.get_secret_value()
    if not password or len(password) > 1024:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
            detail={
                "code": "database_password_invalid",
                "message": "Database password must contain 1 to 1024 characters",
            },
        )
    try:
        result = initialize_database(
            host=payload.host,
            port=payload.port,
            username=payload.username,
            password=password,
        )
    except SetupAlreadyConfiguredError as exc:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail={"code": "setup_complete", "message": str(exc)},
        ) from None
    except SetupInProgressError as exc:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail={"code": "setup_in_progress", "message": str(exc)},
        ) from None
    except SetupConnectionError as exc:
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY,
            detail={"code": "database_connection_failed", "message": str(exc)},
        ) from None
    except SetupInitializationError as exc:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail={"code": "database_initialization_failed", "message": str(exc)},
        ) from None
    except SetupPersistenceError as exc:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail={"code": "setup_persistence_failed", "message": str(exc)},
        ) from None

    setup_rate_limiter.reset(rate_key)
    response.headers["Cache-Control"] = "no-store"
    return DatabaseSetupResponse(
        database_created=result.database_created,
        mysql_version=result.mysql_version,
        database_collation=result.database_collation,
        existing_table_count=result.existing_table_count,
        table_count=result.table_count,
        alembic_revision=result.alembic_revision,
        seed_status=result.seed_status,
    )

from __future__ import annotations

from typing import Annotated

from fastapi import APIRouter, Depends, HTTPException, Request, Response, status
from sqlalchemy.orm import Session

from app.api.dependencies import (
    get_current_user,
    get_db,
    permissions_for_user,
    role_names,
)
from app.core.config import settings
from app.models import User
from app.repositories.common import add_audit_log
from app.schemas.auth import (
    LoginRequest,
    LogoutRequest,
    MessageResponse,
    RefreshRequest,
    TokenResponse,
    UserResponse,
)
from app.services.auth import (
    RefreshTokenError,
    RefreshTokenReplayError,
    TokenBundle,
    authenticate_user,
    issue_token_pair,
    login_ip_rate_key,
    login_rate_key,
    login_rate_limiter,
    revoke_for_logout,
    rotate_refresh_token,
)


router = APIRouter(tags=["authentication"])


def _client_ip(request: Request) -> str | None:
    # Do not trust X-Forwarded-For unless trusted-proxy middleware is configured.
    return request.client.host if request.client else None


def _request_id(request: Request) -> str | None:
    return request.headers.get("X-Request-ID")


def _token_response(bundle: TokenBundle) -> TokenResponse:
    return TokenResponse(
        access_token=bundle.access_token,
        refresh_token=bundle.refresh_token,
        expires_in=settings.access_token_minutes * 60,
        expires_at=int(bundle.access_expires_at.timestamp()),
    )


def _invalid_credentials() -> HTTPException:
    return HTTPException(
        status_code=status.HTTP_401_UNAUTHORIZED,
        detail={"code": "invalid_credentials", "message": "Email or password is incorrect"},
        headers={"WWW-Authenticate": "Bearer"},
    )


@router.post("/auth/login", response_model=TokenResponse)
def login(
    payload: LoginRequest,
    request: Request,
    response: Response,
    db: Annotated[Session, Depends(get_db)],
) -> TokenResponse:
    client_ip = _client_ip(request)
    account_rate_key = login_rate_key(client_ip, str(payload.email))
    ip_rate_key = login_ip_rate_key(client_ip)
    account_allowed, account_retry_after = login_rate_limiter.allowed(account_rate_key)
    ip_allowed, ip_retry_after = login_rate_limiter.allowed(ip_rate_key)
    if not account_allowed or not ip_allowed:
        raise HTTPException(
            status_code=status.HTTP_429_TOO_MANY_REQUESTS,
            detail={
                "code": "login_rate_limited",
                "message": "Too many failed login attempts; try again later",
            },
            headers={"Retry-After": str(max(account_retry_after, ip_retry_after))},
        )

    user = authenticate_user(db, str(payload.email), payload.password)
    if user is None:
        # Enforce both per-account and per-source limits. A combined
        # ``ip+email`` bucket alone can be bypassed by cycling email values.
        login_rate_limiter.register_failure(account_rate_key)
        login_rate_limiter.register_failure(ip_rate_key)
        raise _invalid_credentials()

    login_rate_limiter.reset(account_rate_key)
    bundle = issue_token_pair(
        db,
        user,
        ip_address=client_ip,
        user_agent=request.headers.get("User-Agent") or payload.device_name,
    )
    add_audit_log(
        db,
        actor_user_id=user.id,
        action="auth.login",
        entity_type="user",
        entity_id=user.id,
        request_id=_request_id(request),
        ip_address=client_ip,
        user_agent=request.headers.get("User-Agent"),
    )
    db.commit()
    response.headers["Cache-Control"] = "no-store"
    response.headers["Pragma"] = "no-cache"
    return _token_response(bundle)


@router.post("/auth/refresh", response_model=TokenResponse)
def refresh(
    payload: RefreshRequest,
    request: Request,
    response: Response,
    db: Annotated[Session, Depends(get_db)],
) -> TokenResponse:
    try:
        bundle = rotate_refresh_token(
            db,
            payload.refresh_token,
            ip_address=_client_ip(request),
            user_agent=request.headers.get("User-Agent"),
        )
    except RefreshTokenError as exc:
        if exc.persist_changes:
            db.commit()
        else:
            db.rollback()
        code = "refresh_token_replayed" if isinstance(exc, RefreshTokenReplayError) else "invalid_refresh_token"
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail={"code": code, "message": str(exc)},
            headers={"WWW-Authenticate": "Bearer"},
        ) from exc
    db.commit()
    response.headers["Cache-Control"] = "no-store"
    response.headers["Pragma"] = "no-cache"
    return _token_response(bundle)


@router.post("/auth/logout", response_model=MessageResponse)
def logout(
    request: Request,
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
    payload: LogoutRequest | None = None,
) -> MessageResponse:
    revoke_for_logout(db, current_user, payload.refresh_token if payload else None)
    add_audit_log(
        db,
        actor_user_id=current_user.id,
        action="auth.logout",
        entity_type="user",
        entity_id=current_user.id,
        request_id=_request_id(request),
        ip_address=_client_ip(request),
        user_agent=request.headers.get("User-Agent"),
    )
    db.commit()
    return MessageResponse(message="Logged out")


@router.get("/me", response_model=UserResponse)
def me(
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
) -> UserResponse:
    return UserResponse(
        id=current_user.id,
        email=current_user.email,
        username=getattr(current_user, "username", None),
        display_name=current_user.display_name or current_user.username or current_user.email,
        timezone=current_user.timezone,
        weight_unit=current_user.weight_unit,
        is_active=current_user.is_active,
        is_superuser=bool(getattr(current_user, "is_superuser", False)),
        roles=role_names(db, current_user),
        permissions=permissions_for_user(db, current_user),
        version=current_user.version,
        created_at=current_user.created_at,
        updated_at=current_user.updated_at,
        deleted_at=current_user.deleted_at,
    )

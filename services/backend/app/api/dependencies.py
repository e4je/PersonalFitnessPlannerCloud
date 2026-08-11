from __future__ import annotations

import json
from collections.abc import Callable
from typing import Annotated, Any

from fastapi import Depends, HTTPException, status
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core.security import InvalidAccessToken, decode_access_token
from app.db.session import get_db
from app.models import Role, User, UserRole


bearer_scheme = HTTPBearer(auto_error=False)


def _unauthorized(detail: str = "Authentication credentials are invalid") -> HTTPException:
    return HTTPException(
        status_code=status.HTTP_401_UNAUTHORIZED,
        detail={"code": "unauthorized", "message": detail},
        headers={"WWW-Authenticate": "Bearer"},
    )


def get_current_user(
    credentials: Annotated[HTTPAuthorizationCredentials | None, Depends(bearer_scheme)],
    db: Annotated[Session, Depends(get_db)],
) -> User:
    if credentials is None or credentials.scheme.lower() != "bearer":
        raise _unauthorized("A Bearer access token is required")
    try:
        payload = decode_access_token(credentials.credentials)
    except InvalidAccessToken as exc:
        raise _unauthorized(str(exc)) from exc

    conditions = [User.id == payload["sub"], User.is_active.is_(True)]
    if hasattr(User, "deleted_at"):
        conditions.append(User.deleted_at.is_(None))
    user = db.scalar(select(User).where(*conditions))
    if user is None:
        raise _unauthorized("The user no longer exists or is inactive")
    return user


def get_user_roles(db: Session, user_id: str) -> list[Role]:
    """Load current roles from the DB; JWT role claims are never authoritative."""

    conditions = [UserRole.user_id == user_id]
    if hasattr(UserRole, "deleted_at"):
        conditions.append(UserRole.deleted_at.is_(None))
    if hasattr(Role, "deleted_at"):
        conditions.append(Role.deleted_at.is_(None))
    return list(
        db.scalars(
            select(Role)
            .join(UserRole, UserRole.role_id == Role.id)
            .where(*conditions)
            .order_by(Role.name)
        ).all()
    )


def role_names(db: Session, user: User) -> list[str]:
    if bool(getattr(user, "is_superuser", False)):
        return ["admin", "superuser"]
    return [role.name for role in get_user_roles(db, user.id)]


def _role_permissions(role: Role) -> set[str]:
    raw: Any = getattr(role, "permissions_json", None)
    if isinstance(raw, str):
        try:
            raw = json.loads(raw)
        except json.JSONDecodeError:
            return set()
    if isinstance(raw, list):
        return {str(item) for item in raw}
    if isinstance(raw, dict):
        return {str(key) for key, enabled in raw.items() if enabled}
    return set()


def permissions_for_user(db: Session, user: User) -> list[str]:
    if bool(getattr(user, "is_superuser", False)):
        return ["*"]
    permissions: set[str] = set()
    for role in get_user_roles(db, user.id):
        permissions.update(_role_permissions(role))
    return sorted(permissions)


def require_roles(*required_roles: str) -> Callable[..., User]:
    expected = {item.casefold() for item in required_roles}

    def dependency(
        current_user: Annotated[User, Depends(get_current_user)],
        db: Annotated[Session, Depends(get_db)],
    ) -> User:
        if bool(getattr(current_user, "is_superuser", False)):
            return current_user
        assigned = {item.casefold() for item in role_names(db, current_user)}
        if not assigned.intersection(expected):
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail={
                    "code": "forbidden",
                    "message": "The requested operation requires an administrator role",
                    "required_roles": sorted(expected),
                },
            )
        return current_user

    return dependency


def require_permissions(*required_permissions: str) -> Callable[..., User]:
    expected = set(required_permissions)

    def dependency(
        current_user: Annotated[User, Depends(get_current_user)],
        db: Annotated[Session, Depends(get_db)],
    ) -> User:
        assigned = set(permissions_for_user(db, current_user))
        if "*" not in assigned and not expected.issubset(assigned):
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail={
                    "code": "forbidden",
                    "message": "The requested operation requires additional permissions",
                    "required_permissions": sorted(expected),
                },
            )
        return current_user

    return dependency


CurrentUser = Annotated[User, Depends(get_current_user)]
AdminUser = Annotated[User, Depends(require_roles("admin"))]

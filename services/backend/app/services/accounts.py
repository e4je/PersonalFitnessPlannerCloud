from __future__ import annotations

import re
from collections.abc import Iterable
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.core.security import hash_password
from app.db.base import utcnow, uuid4_str
from app.models import Role, User, UserRole


class AccountValidationError(ValueError):
    pass


USERNAME_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]{2,63}$")


def normalize_email(email: str) -> str:
    return email.strip().casefold()


def normalize_username(username: str) -> str:
    value = username.strip()
    if not USERNAME_PATTERN.fullmatch(value):
        raise AccountValidationError(
            "Username must be 3-64 characters and contain only letters, numbers, '.', '_' or '-'."
        )
    return value.casefold()


def validate_password(password: str) -> str:
    if len(password) < 12:
        raise AccountValidationError("Password must contain at least 12 characters")
    if len(password) > 1024:
        raise AccountValidationError("Password is too long")
    if password.isspace() or len(set(password)) == 1:
        raise AccountValidationError("Password is too weak")
    return password


def validate_timezone(timezone: str) -> str:
    value = timezone.strip()
    try:
        return ZoneInfo(value).key
    except ZoneInfoNotFoundError as exc:
        raise AccountValidationError(f"Unknown IANA timezone: {value}") from exc


def _role(db: Session, name: str) -> Role:
    role = db.scalar(
        select(Role).where(func.lower(Role.name) == name.casefold(), Role.deleted_at.is_(None))
    )
    if role is None:
        raise AccountValidationError(f"Role '{name}' is not configured")
    return role


def ensure_unique_account(db: Session, *, email: str, username: str) -> None:
    if db.scalar(select(User.id).where(func.lower(User.email) == email, User.deleted_at.is_(None))):
        raise AccountValidationError("An account with this email already exists")
    if db.scalar(
        select(User.id).where(func.lower(User.username) == username, User.deleted_at.is_(None))
    ):
        raise AccountValidationError("An account with this username already exists")


def create_account(
    db: Session,
    *,
    email: str,
    username: str,
    password: str,
    display_name: str,
    timezone: str,
    weight_unit: str,
    role_names: Iterable[str] = ("user",),
    is_superuser: bool = False,
) -> User:
    normalized_email = normalize_email(email)
    if normalized_email.count("@") != 1 or any(character.isspace() for character in normalized_email):
        raise AccountValidationError("A valid email address is required")
    normalized_username = normalize_username(username)
    clean_password = validate_password(password)
    clean_timezone = validate_timezone(timezone)
    normalized_weight = weight_unit.strip().upper()
    if normalized_weight not in {"KG", "LB"}:
        raise AccountValidationError("Weight unit must be KG or LB")
    clean_name = display_name.strip()
    if not clean_name:
        raise AccountValidationError("Display name is required")
    ensure_unique_account(db, email=normalized_email, username=normalized_username)
    roles = [_role(db, name) for name in dict.fromkeys(role_names)]
    if not roles:
        roles = [_role(db, "user")]
    user = User(
        id=uuid4_str(),
        email=normalized_email,
        username=normalized_username,
        password_hash=hash_password(clean_password),
        display_name=clean_name,
        timezone=clean_timezone,
        weight_unit=normalized_weight,
        is_active=True,
        is_superuser=is_superuser,
    )
    user.roles.extend(roles)
    db.add(user)
    db.flush()
    return user


def active_role_names(db: Session, user_id: str) -> list[str]:
    return list(
        db.scalars(
            select(Role.name)
            .join(UserRole, UserRole.role_id == Role.id)
            .where(
                UserRole.user_id == user_id,
                UserRole.deleted_at.is_(None),
                Role.deleted_at.is_(None),
            )
            .order_by(Role.name)
        ).all()
    )


def replace_user_roles(db: Session, user: User, names: Iterable[str], *, assigned_by: str) -> list[str]:
    requested = {name.strip().casefold() for name in names if name.strip()}
    if not requested:
        requested = {"user"}
    role_rows = {name: _role(db, name) for name in requested}
    links = list(db.scalars(select(UserRole).where(UserRole.user_id == user.id)).all())
    by_role = {link.role_id: link for link in links}
    for name, role in role_rows.items():
        link = by_role.get(role.id)
        if link is None:
            db.add(
                UserRole(
                    id=uuid4_str(),
                    user_id=user.id,
                    role_id=role.id,
                    assigned_at=utcnow(),
                    assigned_by_user_id=assigned_by,
                )
            )
        elif link.deleted_at is not None:
            link.deleted_at = None
            link.version += 1
            link.assigned_by_user_id = assigned_by
    for link in links:
        role_name = next((role.name for role in role_rows.values() if role.id == link.role_id), None)
        if role_name is None and link.deleted_at is None:
            link.deleted_at = utcnow()
            link.version += 1
    db.flush()
    return active_role_names(db, user.id)

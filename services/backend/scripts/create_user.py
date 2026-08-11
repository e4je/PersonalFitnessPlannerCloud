from __future__ import annotations

import argparse
import getpass
import os
from datetime import datetime
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core.security import hash_password
from app.db.session import SessionLocal
from app.models import AuditLog, PlanAssignment, PlanVersion, Role, User, UserRole
from app.seed.default_data import DEFAULT_PLAN


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Create or refresh a standard user")
    parser.add_argument("--email", default=os.getenv("USER_EMAIL"))
    parser.add_argument("--username", default=os.getenv("USER_USERNAME"))
    parser.add_argument("--display-name", default=os.getenv("USER_DISPLAY_NAME"))
    parser.add_argument("--timezone", default=os.getenv("USER_TIMEZONE", "Asia/Shanghai"))
    parser.add_argument(
        "--weight-unit",
        choices=("KG", "LB"),
        default=os.getenv("USER_WEIGHT_UNIT", "KG"),
    )
    parser.add_argument("--password-env", default="USER_PASSWORD")
    parser.add_argument("--update-password", action="store_true")
    return parser.parse_args()


def ensure_standard_user(
    db: Session,
    *,
    email: str,
    username: str | None,
    display_name: str | None,
    timezone: str,
    weight_unit: str,
    password: str | None = None,
    update_password: bool = False,
) -> tuple[User, bool, bool]:
    """Create/update one ordinary account without silently rotating its password."""

    normalized_email = email.strip().casefold()
    if not normalized_email or "@" not in normalized_email:
        raise ValueError("A valid email address is required")
    try:
        ZoneInfo(timezone)
    except ZoneInfoNotFoundError as exc:
        raise ValueError(f"Unknown IANA timezone: {timezone}") from exc
    if weight_unit not in {"KG", "LB"}:
        raise ValueError("Weight unit must be KG or LB")

    role = db.scalar(select(Role).where(Role.name == "user", Role.deleted_at.is_(None)))
    if role is None:
        raise ValueError("User role is missing; run scripts/seed_default_plan.py first")
    user = db.scalar(select(User).where(User.email == normalized_email))
    created = user is None
    if user is not None and user.is_superuser:
        raise ValueError("Existing account is a superuser; refusing to treat it as ordinary")
    normalized_username = (
        username
        or (user.username if user is not None else normalized_email.split("@", 1)[0])
    ).strip()
    normalized_display_name = (
        display_name
        or (user.display_name if user is not None else normalized_username)
    ).strip()
    if not normalized_username or len(normalized_username) > 64:
        raise ValueError("Username must contain 1 to 64 characters")
    username_owner = db.scalar(select(User).where(User.username == normalized_username))
    if username_owner is not None and username_owner is not user:
        raise ValueError("Username is already in use")

    needs_password = created or update_password
    if needs_password and (password is None or len(password) < 12):
        raise ValueError("User password must contain at least 12 characters")

    changed = False
    if user is None:
        user = User(
            email=normalized_email,
            username=normalized_username,
            password_hash=hash_password(password or ""),
            display_name=normalized_display_name,
            timezone=timezone,
            weight_unit=weight_unit,
            is_active=True,
            is_superuser=False,
        )
        db.add(user)
        db.flush()
        changed = True
    else:
        updates = {
            "username": normalized_username,
            "display_name": normalized_display_name,
            "timezone": timezone,
            "weight_unit": weight_unit,
            "is_active": True,
            "deleted_at": None,
        }
        for field, value in updates.items():
            if getattr(user, field) != value:
                setattr(user, field, value)
                changed = True
        if update_password:
            user.password_hash = hash_password(password or "")
            changed = True

    role_link = db.scalar(
        select(UserRole).where(
            UserRole.user_id == user.id,
            UserRole.role_id == role.id,
        )
    )
    if role_link is None:
        db.add(UserRole(user_id=user.id, role_id=role.id))
        changed = True
    elif role_link.deleted_at is not None:
        role_link.deleted_at = None
        role_link.version += 1
        changed = True

    current_assignment = db.scalar(
        select(PlanAssignment).where(
            PlanAssignment.user_id == user.id,
            PlanAssignment.deleted_at.is_(None),
            PlanAssignment.status.in_(("active", "scheduled")),
        )
    )
    if current_assignment is None:
        default_version = db.get(PlanVersion, DEFAULT_PLAN["plan_version_id"])
        if default_version is None or default_version.status != "published":
            raise ValueError(
                "Canonical default plan is missing; run scripts/seed_default_plan.py first"
            )
        db.add(
            PlanAssignment(
                user_id=user.id,
                plan_version_id=default_version.id,
                status="active",
                starts_on=datetime.now(ZoneInfo(timezone)).date(),
                settings_json={"source": "create_user_default"},
            )
        )
        changed = True
    if changed and not created:
        user.version += 1
    if changed:
        db.add(
            AuditLog(
                actor_user_id=user.id,
                action="USER_CREATED" if created else "USER_UPDATED",
                entity_type="user",
                entity_id=user.id,
                after_json={"email": user.email, "is_superuser": False},
            )
        )
        db.flush()
    return user, created, changed


def main() -> None:
    args = parse_args()
    email = (args.email or input("User email: ")).strip().casefold()

    try:
        with SessionLocal.begin() as db:
            existing = db.scalar(select(User).where(User.email == email))
            password: str | None = None
            if existing is None or args.update_password:
                password = os.getenv(args.password_env) or getpass.getpass("User password: ")
            user, created, changed = ensure_standard_user(
                db,
                email=email,
                username=args.username,
                display_name=args.display_name,
                timezone=args.timezone,
                weight_unit=args.weight_unit,
                password=password,
                update_password=args.update_password,
            )
            user_id = user.id
    except ValueError as exc:
        raise SystemExit(str(exc)) from exc
    state = "created" if created else "updated" if changed else "unchanged"
    print(f"standard_user_ready state={state} id={user_id} email={email}")


if __name__ == "__main__":
    main()

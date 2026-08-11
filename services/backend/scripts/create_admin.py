from __future__ import annotations

import argparse
import getpass
import os
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from sqlalchemy import select

from app.core.security import hash_password
from app.db.session import SessionLocal
from app.models import AuditLog, Role, User


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Create or promote an administrator")
    parser.add_argument("--email", default=os.getenv("ADMIN_EMAIL"))
    parser.add_argument("--username", default=os.getenv("ADMIN_USERNAME"))
    parser.add_argument("--display-name", default=os.getenv("ADMIN_DISPLAY_NAME", "Administrator"))
    parser.add_argument("--timezone", default=os.getenv("ADMIN_TIMEZONE", "Asia/Shanghai"))
    parser.add_argument("--weight-unit", choices=("KG", "LB"), default=os.getenv("ADMIN_WEIGHT_UNIT", "KG"))
    parser.add_argument("--password-env", default="ADMIN_PASSWORD")
    parser.add_argument("--update-password", action="store_true")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    email = (args.email or input("Admin email: ")).strip().casefold()
    username = (args.username or email.split("@", 1)[0]).strip()
    password = os.getenv(args.password_env) or getpass.getpass("Admin password: ")
    if len(password) < 12:
        raise SystemExit("Admin password must contain at least 12 characters")
    try:
        ZoneInfo(args.timezone)
    except ZoneInfoNotFoundError as exc:
        raise SystemExit(f"Unknown IANA timezone: {args.timezone}") from exc

    with SessionLocal.begin() as db:
        role = db.scalar(select(Role).where(Role.name == "admin", Role.deleted_at.is_(None)))
        if role is None:
            raise SystemExit("Admin role is missing; run scripts/seed_default_plan.py first")
        user = db.scalar(select(User).where(User.email == email))
        created = user is None
        if user is None:
            user = User(
                email=email,
                username=username,
                password_hash=hash_password(password),
                display_name=args.display_name,
                timezone=args.timezone,
                weight_unit=args.weight_unit,
                is_active=True,
                is_superuser=True,
            )
            db.add(user)
            db.flush()
        else:
            user.is_active = True
            user.is_superuser = True
            user.display_name = args.display_name
            user.timezone = args.timezone
            user.weight_unit = args.weight_unit
            if args.update_password:
                user.password_hash = hash_password(password)
            user.version += 1
        if role not in user.roles:
            user.roles.append(role)
        db.add(
            AuditLog(
                actor_user_id=user.id,
                action="ADMIN_CREATED" if created else "ADMIN_PROMOTED",
                entity_type="user",
                entity_id=user.id,
                after_json={"email": user.email, "is_superuser": True},
            )
        )
        user_id = user.id
    print(f"administrator_ready id={user_id} email={email}")


if __name__ == "__main__":
    main()

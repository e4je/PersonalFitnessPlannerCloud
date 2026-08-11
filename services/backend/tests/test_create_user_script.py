from __future__ import annotations

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.core.security import verify_password
from app.models import AuditLog, PlanAssignment
from app.seed.default_data import DEFAULT_PLAN
from app.seed.default_plan import seed_default_plan
from scripts.create_user import ensure_standard_user


def test_create_user_is_idempotent_and_does_not_rotate_password_by_default(
    db_session: Session,
) -> None:
    seed_default_plan(db_session)
    original_password = "First-User-Password-2026!"
    user, created, changed = ensure_standard_user(
        db_session,
        email="controlled-user@example.test",
        username="controlled_user",
        display_name="Controlled User",
        timezone="Asia/Shanghai",
        weight_unit="KG",
        password=original_password,
    )
    db_session.commit()
    original_hash = user.password_hash

    same_user, created_again, changed_again = ensure_standard_user(
        db_session,
        email="controlled-user@example.test",
        username="controlled_user",
        display_name="Controlled User",
        timezone="Asia/Shanghai",
        weight_unit="KG",
        password="Ignored-New-Password-2026!",
    )
    db_session.commit()

    assert created is True and changed is True
    assert created_again is False and changed_again is False
    assert same_user.id == user.id
    assert same_user.password_hash == original_hash
    assert verify_password(original_password, same_user.password_hash)
    assignment = db_session.scalar(
        select(PlanAssignment).where(PlanAssignment.user_id == user.id)
    )
    assert assignment is not None
    assert assignment.plan_version_id == DEFAULT_PLAN["plan_version_id"]
    assert assignment.status == "active"
    assert db_session.scalar(
        select(func.count(PlanAssignment.id)).where(PlanAssignment.user_id == user.id)
    ) == 1
    assert db_session.scalar(
        select(func.count(AuditLog.id)).where(AuditLog.entity_type == "user")
    ) == 1


def test_create_user_rotates_password_only_when_explicit(db_session: Session) -> None:
    seed_default_plan(db_session)
    user, _, _ = ensure_standard_user(
        db_session,
        email="rotate-user@example.test",
        username="rotate_user",
        display_name="Rotate User",
        timezone="Asia/Shanghai",
        weight_unit="KG",
        password="Original-User-Password-2026!",
    )
    db_session.commit()
    old_hash = user.password_hash

    updated, created, changed = ensure_standard_user(
        db_session,
        email="rotate-user@example.test",
        username="rotate_user",
        display_name="Rotate User",
        timezone="Asia/Shanghai",
        weight_unit="KG",
        password="Rotated-User-Password-2026!",
        update_password=True,
    )
    db_session.commit()

    assert created is False and changed is True
    assert updated.password_hash != old_hash
    assert verify_password("Rotated-User-Password-2026!", updated.password_hash)

from __future__ import annotations

from typing import Any

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.db.base import uuid4_str
from app.models import SystemSetting


REGISTRATION_ENABLED_KEY = "registration_enabled"


def _setting_value(row: SystemSetting | None, default: Any) -> Any:
    if row is None:
        return default
    value = row.value_json
    if isinstance(value, dict) and "value" in value:
        return value["value"]
    return value


def get_setting(db: Session, key: str, default: Any = None) -> Any:
    row = db.scalar(select(SystemSetting).where(SystemSetting.key == key))
    return _setting_value(row, default)


def registration_is_enabled(db: Session) -> bool:
    return bool(get_setting(db, REGISTRATION_ENABLED_KEY, True))


def set_registration_enabled(
    db: Session,
    enabled: bool,
    *,
    actor_user_id: str | None = None,
) -> SystemSetting:
    row = db.scalar(
        select(SystemSetting)
        .where(SystemSetting.key == REGISTRATION_ENABLED_KEY)
        .with_for_update()
    )
    if row is None:
        row = SystemSetting(
            id=uuid4_str(),
            key=REGISTRATION_ENABLED_KEY,
            value_json={"value": bool(enabled)},
            description="Allow unauthenticated visitors to create standard accounts",
            updated_by_user_id=actor_user_id,
        )
        db.add(row)
    else:
        row.value_json = {"value": bool(enabled)}
        row.updated_by_user_id = actor_user_id
    db.flush()
    return row

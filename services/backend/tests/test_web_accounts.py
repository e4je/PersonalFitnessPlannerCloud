from __future__ import annotations

from collections.abc import Callable

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core.security import create_access_token
from app.models import AuditLog, User


def _registration_payload(suffix: str = "one") -> dict[str, str]:
    return {
        "email": f"web-{suffix}@example.test",
        "username": f"web_{suffix}",
        "display_name": f"Web {suffix}",
        "password": "Web-Account-Password-2026!",
        "timezone": "Asia/Shanghai",
        "weight_unit": "KG",
    }


def test_public_registration_can_be_disabled_and_is_audited(
    client: TestClient,
    db_session: Session,
    admin_headers: dict[str, str],
    user_factory: Callable[..., User],
) -> None:
    user_factory(role_name="user")
    status = client.get("/api/v1/auth/registration-status")
    assert status.status_code == 200
    assert status.json() == {"enabled": True}

    created = client.post("/api/v1/auth/register", json=_registration_payload())
    assert created.status_code == 201, created.text
    assert created.json()["access_token"]

    duplicate = client.post("/api/v1/auth/register", json=_registration_payload())
    assert duplicate.status_code == 409

    disabled = client.patch(
        "/api/v1/admin/settings/registration",
        headers=admin_headers,
        json={"enabled": False},
    )
    assert disabled.status_code == 200
    assert disabled.json()["enabled"] is False

    rejected = client.post(
        "/api/v1/auth/register",
        json=_registration_payload("blocked"),
    )
    assert rejected.status_code == 403
    assert rejected.json()["detail"]["code"] == "registration_disabled"
    assert db_session.scalar(
        select(AuditLog).where(AuditLog.action == "admin.registration_setting.update")
    ) is not None


def test_registration_rate_limit_counts_successful_attempts(
    client: TestClient,
    user_factory: Callable[..., User],
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from app.services.auth import login_rate_limiter

    user_factory(role_name="user")
    monkeypatch.setattr(login_rate_limiter, "limit", 2)

    assert client.post("/api/v1/auth/register", json=_registration_payload("rate-1")).status_code == 201
    assert client.post("/api/v1/auth/register", json=_registration_payload("rate-2")).status_code == 201
    limited = client.post("/api/v1/auth/register", json=_registration_payload("rate-3"))

    assert limited.status_code == 429
    assert limited.json()["detail"]["code"] == "registration_rate_limited"


def test_admin_can_create_and_deactivate_standard_account(
    client: TestClient,
    admin_headers: dict[str, str],
    user_factory: Callable[..., User],
) -> None:
    user_factory(role_name="user")
    payload = {
        "email": "created-by-admin@example.test",
        "username": "created_by_admin",
        "display_name": "Created by Admin",
        "password": "Admin-Created-Password-2026!",
        "timezone": "Asia/Shanghai",
        "weight_unit": "KG",
        "roles": ["user"],
    }
    created = client.post("/api/v1/admin/users", headers=admin_headers, json=payload)
    assert created.status_code == 201, created.text
    user = created.json()
    assert user["roles"] == ["user"]

    listed = client.get("/api/v1/admin/users", headers=admin_headers)
    assert listed.status_code == 200
    assert any(item["id"] == user["id"] for item in listed.json()["items"])

    patched = client.patch(
        f"/api/v1/admin/users/{user['id']}",
        headers=admin_headers,
        json={"expected_version": user["version"], "is_active": False},
    )
    assert patched.status_code == 200, patched.text
    assert patched.json()["is_active"] is False


def test_normal_admin_cannot_modify_another_privileged_account(
    client: TestClient,
    admin_headers: dict[str, str],
    user_factory: Callable[..., User],
) -> None:
    target = user_factory(
        email="protected-admin@example.test",
        username="protected_admin",
        role_name="admin",
    )

    response = client.patch(
        f"/api/v1/admin/users/{target.id}",
        headers=admin_headers,
        json={
            "expected_version": target.version,
            "is_active": False,
        },
    )

    assert response.status_code == 403
    assert response.json()["detail"]["code"] == "superuser_required"


def test_only_superuser_can_grant_admin_and_last_admin_is_protected(
    client: TestClient,
    db_session: Session,
    admin_headers: dict[str, str],
    admin_user: User,
    user_factory: Callable[..., User],
) -> None:
    target = user_factory(email="role-target@example.test", username="role_target")
    denied = client.patch(
        f"/api/v1/admin/users/{target.id}",
        headers=admin_headers,
        json={"expected_version": target.version, "roles": ["admin"]},
    )
    assert denied.status_code == 403
    assert denied.json()["detail"]["code"] == "superuser_required"

    # Leave only the superuser below as an active privileged account for the
    # final-admin protection assertion. The earlier permission assertion used
    # the normal administrator while it was still active.
    admin_user.is_active = False
    db_session.commit()

    superuser = user_factory(
        email="last-superuser@example.test",
        username="last_superuser",
        role_name="admin",
        is_superuser=True,
    )
    superuser_headers = {"Authorization": f"Bearer {create_access_token(superuser.id)[0]}"}
    # A superuser may manage roles, but the final privileged account is still
    # protected even when changing only the role list.
    protected = client.patch(
        f"/api/v1/admin/users/{superuser.id}",
        headers=superuser_headers,
        json={"expected_version": superuser.version, "roles": ["user"]},
    )
    assert protected.status_code == 409
    assert protected.json()["detail"]["code"] == "last_admin_protected"


def test_web_console_is_same_origin_static_entry(client: TestClient) -> None:
    response = client.get("/web/")
    assert response.status_code == 200
    assert "Personal Fitness Planner" in response.text
    assert "/web/app.js" in response.text
    script = client.get("/web/app.js")
    assert script.status_code == 200
    assert "registration-status" in script.text

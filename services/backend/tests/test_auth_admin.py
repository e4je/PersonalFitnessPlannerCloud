from __future__ import annotations

from datetime import date
from typing import Any

import pytest
from fastapi.testclient import TestClient
from pydantic import ValidationError
from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core.security import hash_refresh_token
from app.models import AuditLog, Equipment, Exercise, RefreshToken, User
from app.schemas.admin import PlanVersionCreate, PlanVersionPatch


TEST_PASSWORD = "Correct-Horse-Battery-Staple-2026!"


def _valid_plan_version_payload(exercise: Exercise) -> dict[str, Any]:
    def day(code: str, sort_order: int) -> dict[str, Any]:
        return {
            "day_code": code,
            "name": f"Day {code}",
            "sort_order": sort_order,
            "slots": [
                {
                    "name": "Full body compound",
                    "sort_order": 0,
                    "selection_rule_json": {"choose": 1},
                    "options": [
                        {
                            "exercise_id": exercise.id,
                            "is_preferred": True,
                            "sort_order": 0,
                            "set_count": 3,
                            "reps_min": 8,
                            "reps_max": 12,
                            "rir_min": 2,
                            "rir_max": 3,
                        }
                    ],
                }
            ],
        }

    return {
        "weekly_frequency": 3,
        "min_rest_days": 1,
        "fatigue_threshold": 8,
        "initial_reduced_weeks": 2,
        "initial_set_count": 2,
        "config_json": {"sequence": ["A", "B"]},
        "changelog": "Initial test version",
        "days": [day("A", 0), day("B", 1)],
    }


def _create_plan_with_draft(
    client: TestClient,
    admin_headers: dict[str, str],
    exercise: Exercise,
) -> tuple[dict[str, Any], dict[str, Any]]:
    plan_response = client.post(
        "/api/v1/admin/plans",
        headers=admin_headers,
        json={
            "name": "Test A/B Plan",
            "description": "Plan lifecycle integration test",
            "goal": "hypertrophy",
        },
    )
    assert plan_response.status_code == 201, plan_response.text
    plan = plan_response.json()

    version_response = client.post(
        f"/api/v1/admin/plans/{plan['id']}/versions",
        headers=admin_headers,
        json=_valid_plan_version_payload(exercise),
    )
    assert version_response.status_code == 201, version_response.text
    return plan, version_response.json()


def _publish(
    client: TestClient,
    admin_headers: dict[str, str],
    version: dict[str, Any],
) -> dict[str, Any]:
    response = client.post(
        f"/api/v1/admin/plan-versions/{version['id']}/publish",
        headers=admin_headers,
        json={"expected_version": version["version"]},
    )
    assert response.status_code == 200, response.text
    return response.json()


def test_plan_fatigue_threshold_matches_ten_point_readiness_scale() -> None:
    assert PlanVersionCreate().fatigue_threshold == 8
    assert PlanVersionCreate(fatigue_threshold=10).fatigue_threshold == 10
    assert PlanVersionPatch(expected_version=1, fatigue_threshold=10).fatigue_threshold == 10
    with pytest.raises(ValidationError):
        PlanVersionCreate(fatigue_threshold=11)


def test_login_success_failure_and_me(
    client: TestClient,
    normal_user: User,
) -> None:
    failed = client.post(
        "/api/v1/auth/login",
        json={"email": normal_user.email, "password": "definitely-wrong"},
    )
    assert failed.status_code == 401
    assert failed.json()["detail"]["code"] == "invalid_credentials"
    assert failed.headers["www-authenticate"] == "Bearer"

    logged_in = client.post(
        "/api/v1/auth/login",
        json={
            "email": normal_user.email.upper(),
            "password": TEST_PASSWORD,
            "device_name": "pytest",
        },
    )
    assert logged_in.status_code == 200, logged_in.text
    tokens = logged_in.json()
    assert tokens["token_type"] == "Bearer"
    assert tokens["access_token"]
    assert tokens["refresh_token"]
    assert tokens["expires_in"] > 0

    me = client.get(
        "/api/v1/me",
        headers={"Authorization": f"Bearer {tokens['access_token']}"},
    )
    assert me.status_code == 200, me.text
    assert me.json()["id"] == normal_user.id
    assert me.json()["roles"] == ["user"]


def test_refresh_rotation_detects_replay_and_revokes_token_family(
    client: TestClient,
    db_session: Session,
    normal_user: User,
) -> None:
    login = client.post(
        "/api/v1/auth/login",
        json={"email": normal_user.email, "password": TEST_PASSWORD},
    )
    assert login.status_code == 200, login.text
    first_plaintext = login.json()["refresh_token"]
    first = db_session.scalar(
        select(RefreshToken).where(
            RefreshToken.token_hash == hash_refresh_token(first_plaintext)
        )
    )
    assert first is not None
    assert first.token_hash != first_plaintext

    rotated = client.post(
        "/api/v1/auth/refresh",
        json={"refresh_token": first_plaintext},
    )
    assert rotated.status_code == 200, rotated.text
    second_plaintext = rotated.json()["refresh_token"]
    assert second_plaintext and second_plaintext != first_plaintext

    db_session.expire_all()
    first = db_session.get(RefreshToken, first.id)
    second = db_session.scalar(
        select(RefreshToken).where(
            RefreshToken.token_hash == hash_refresh_token(second_plaintext)
        )
    )
    assert first is not None and second is not None
    assert first.revoked_at is not None
    assert first.replaced_by_id == second.id
    assert first.family_id == second.family_id
    assert second.revoked_at is None

    replay = client.post(
        "/api/v1/auth/refresh",
        json={"refresh_token": first_plaintext},
    )
    assert replay.status_code == 401
    assert replay.json()["detail"]["code"] == "refresh_token_replayed"

    db_session.expire_all()
    family = list(
        db_session.scalars(
            select(RefreshToken).where(RefreshToken.family_id == first.family_id)
        ).all()
    )
    assert len(family) == 2
    assert all(token.revoked_at is not None for token in family)
    replay_audit = db_session.scalar(
        select(AuditLog).where(
            AuditLog.action == "auth.refresh_replay_detected",
            AuditLog.entity_id == first.family_id,
        )
    )
    assert replay_audit is not None

    revoked_replacement = client.post(
        "/api/v1/auth/refresh",
        json={"refresh_token": second_plaintext},
    )
    assert revoked_replacement.status_code == 401


def test_logout_revokes_refresh_token(
    client: TestClient,
    db_session: Session,
    normal_user: User,
) -> None:
    login = client.post(
        "/api/v1/auth/login",
        json={"email": normal_user.email, "password": TEST_PASSWORD},
    )
    assert login.status_code == 200, login.text
    tokens = login.json()

    logout = client.post(
        "/api/v1/auth/logout",
        headers={"Authorization": f"Bearer {tokens['access_token']}"},
        json={"refresh_token": tokens["refresh_token"]},
    )
    assert logout.status_code == 200, logout.text
    assert logout.json() == {"message": "Logged out"}

    db_session.expire_all()
    stored = db_session.scalar(
        select(RefreshToken).where(
            RefreshToken.token_hash == hash_refresh_token(tokens["refresh_token"])
        )
    )
    assert stored is not None and stored.revoked_at is not None
    rejected = client.post(
        "/api/v1/auth/refresh",
        json={"refresh_token": tokens["refresh_token"]},
    )
    assert rejected.status_code == 401


def test_admin_catalog_rbac_and_management(
    client: TestClient,
    user_headers: dict[str, str],
    admin_headers: dict[str, str],
) -> None:
    equipment_payload = {
        "code": "admin-cable-stack",
        "name": "Admin Cable Stack",
        "category": "machine",
        "brand": "Test Brand",
    }
    forbidden = client.post(
        "/api/v1/admin/equipment",
        headers=user_headers,
        json=equipment_payload,
    )
    assert forbidden.status_code == 403
    assert forbidden.json()["detail"]["code"] == "forbidden"
    assert forbidden.json()["detail"]["required_roles"] == ["admin"]

    created_equipment = client.post(
        "/api/v1/admin/equipment",
        headers=admin_headers,
        json=equipment_payload,
    )
    assert created_equipment.status_code == 201, created_equipment.text
    equipment = created_equipment.json()
    assert equipment["name"] == equipment_payload["name"]

    patched_equipment = client.patch(
        f"/api/v1/admin/equipment/{equipment['id']}",
        headers=admin_headers,
        json={
            "expected_version": equipment["version"],
            "notes": "Maintained through the admin API",
        },
    )
    assert patched_equipment.status_code == 200, patched_equipment.text
    assert patched_equipment.json()["version"] == equipment["version"] + 1

    created_exercise = client.post(
        "/api/v1/admin/exercises",
        headers=admin_headers,
        json={
            "code": "admin-cable-row",
            "name": "Admin Cable Row",
            "body_part": "back",
            "movement_pattern": "horizontal_pull",
            "equipment_ids": [equipment["id"]],
            "cues": ["Brace the torso", "Pull elbows back"],
            "common_mistakes": "Shrugging\nExcessive momentum",
            "default_sets": 3,
            "rep_min": 8,
            "rep_max": 12,
        },
    )
    assert created_exercise.status_code == 201, created_exercise.text
    exercise = created_exercise.json()
    assert exercise["equipment_ids"] == [equipment["id"]]
    assert exercise["cues"] == "Brace the torso\nPull elbows back"
    assert len(exercise["cue_items"]) == 2


def test_draft_can_be_published_but_published_version_is_immutable(
    client: TestClient,
    admin_headers: dict[str, str],
    catalog_items: tuple[Exercise, Equipment],
) -> None:
    exercise, _equipment = catalog_items
    _plan, draft = _create_plan_with_draft(client, admin_headers, exercise)
    assert draft["status"] == "draft"
    assert [day["code"] for day in draft["days"]] == ["A", "B"]

    published = _publish(client, admin_headers, draft)
    assert published["status"] == "published"
    assert published["published_at"] is not None
    assert published["version"] == draft["version"] + 1

    immutable = client.patch(
        f"/api/v1/admin/plan-versions/{published['id']}",
        headers=admin_headers,
        json={
            "expected_version": published["version"],
            "weekly_frequency": 4,
        },
    )
    assert immutable.status_code == 409
    assert immutable.json()["detail"]["code"] == "published_plan_immutable"


def test_new_version_copies_published_tree_and_can_be_assigned(
    client: TestClient,
    normal_user: User,
    admin_headers: dict[str, str],
    catalog_items: tuple[Exercise, Equipment],
) -> None:
    exercise, _equipment = catalog_items
    plan, draft = _create_plan_with_draft(client, admin_headers, exercise)
    published_v1 = _publish(client, admin_headers, draft)

    new_version_response = client.post(
        f"/api/v1/admin/plans/{plan['id']}/versions",
        headers=admin_headers,
        json={
            "base_plan_version_id": published_v1["id"],
            "changelog": "Progression for the next block",
        },
    )
    assert new_version_response.status_code == 201, new_version_response.text
    draft_v2 = new_version_response.json()
    assert draft_v2["status"] == "draft"
    assert draft_v2["version_number"] == published_v1["version_number"] + 1
    assert len(draft_v2["days"]) == len(published_v1["days"])
    assert draft_v2["days"][0]["slots"][0]["options"][0]["exercise_id"] == exercise.id

    published_v2 = _publish(client, admin_headers, draft_v2)
    assignment_response = client.post(
        "/api/v1/admin/assignments",
        headers=admin_headers,
        json={
            "user_id": normal_user.id,
            "plan_version_id": published_v2["id"],
            "status": "active",
            "starts_on": date.today().isoformat(),
            "settings_json": {"source": "pytest"},
        },
    )
    assert assignment_response.status_code == 201, assignment_response.text
    assignment = assignment_response.json()
    assert assignment["user_id"] == normal_user.id
    assert assignment["plan_version_id"] == published_v2["id"]
    assert assignment["status"] == "active"


def test_stale_admin_patch_returns_409_and_writes_conflict_audit(
    client: TestClient,
    admin_headers: dict[str, str],
) -> None:
    created_response = client.post(
        "/api/v1/admin/equipment",
        headers=admin_headers,
        json={
            "code": "conflict-equipment",
            "name": "Conflict Equipment",
            "category": "machine",
        },
    )
    assert created_response.status_code == 201, created_response.text
    created = created_response.json()

    first_patch = client.patch(
        f"/api/v1/admin/equipment/{created['id']}",
        headers=admin_headers,
        json={"expected_version": created["version"], "name": "Updated Equipment"},
    )
    assert first_patch.status_code == 200, first_patch.text
    server_copy = first_patch.json()

    request_id = "pytest-stale-write"
    stale_patch = client.patch(
        f"/api/v1/admin/equipment/{created['id']}",
        headers={**admin_headers, "X-Request-ID": request_id},
        json={"expected_version": created["version"], "name": "Lost Update"},
    )
    assert stale_patch.status_code == 409
    detail = stale_patch.json()["detail"]
    assert detail["code"] == "version_conflict"
    assert detail["server_copy"]["version"] == server_copy["version"]
    assert detail["server_copy"]["name"] == "Updated Equipment"

    audits = client.get(
        "/api/v1/admin/audit-logs",
        headers=admin_headers,
        params={"action": "admin.version_conflict", "entity_type": "equipment"},
    )
    assert audits.status_code == 200, audits.text
    matching = [
        item
        for item in audits.json()["items"]
        if item["entity_id"] == created["id"] and item["request_id"] == request_id
    ]
    assert len(matching) == 1
    assert matching[0]["metadata_json"] == {"reason": "optimistic_lock_failed"}

    persisted = client.get(
        "/api/v1/admin/audit-logs",
        headers=admin_headers,
        params={"action": "admin.version_conflict"},
    )
    assert persisted.status_code == 200

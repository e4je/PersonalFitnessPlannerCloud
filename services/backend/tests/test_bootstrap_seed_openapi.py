from __future__ import annotations

import hashlib
from datetime import UTC, date, datetime, timedelta
from uuid import uuid4

from fastapi.testclient import TestClient
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.models import (
    CardioSession,
    DailyReadiness,
    Equipment,
    Exercise,
    PlanAssignment,
    PlanDay,
    PlanSlot,
    PlanSlotOption,
    PlanVersion,
    SyncChange,
    TrainingPlan,
    User,
    WorkoutSession,
)
from app.seed.default_plan import seed_default_plan
from app.seed.default_data import CANONICAL_PLAN


def test_default_seed_has_canonical_counts_and_is_idempotent(db_session: Session) -> None:
    first = seed_default_plan(db_session)
    before_cursor = db_session.scalar(select(func.max(SyncChange.sequence)))
    second = seed_default_plan(db_session)
    after_cursor = db_session.scalar(select(func.max(SyncChange.sequence)))

    assert first == {
        "status": "created",
        "plan_id": first["plan_id"],
        "plan_version_id": first["plan_version_id"],
        "days": 2,
        "slots": 16,
        "options": 79,
        "exercises": 66,
        "equipment": 52,
    }
    assert second["status"] == "already_seeded"
    assert second["plan_id"] == first["plan_id"]
    assert second["plan_version_id"] == first["plan_version_id"]
    assert db_session.scalar(select(func.count(Exercise.id))) == 66
    assert db_session.scalar(select(func.count(Equipment.id))) == 52
    assert db_session.scalar(select(func.count(PlanSlot.id))) == 16
    assert db_session.scalar(select(func.count(PlanSlotOption.id))) == 79
    assert after_cursor == before_cursor

    canonical_days = CANONICAL_PLAN["days"]
    canonical_slots = [slot for day in canonical_days for slot in day["slots"]]
    canonical_options = [option for slot in canonical_slots for option in slot["options"]]
    assert db_session.get(TrainingPlan, CANONICAL_PLAN["plan_id"]) is not None
    assert db_session.get(PlanVersion, CANONICAL_PLAN["plan_version_id"]) is not None
    assert {item.id for item in db_session.scalars(select(PlanDay))} == {
        day["day_id"] for day in canonical_days
    }
    assert {item.id for item in db_session.scalars(select(PlanSlot))} == {
        slot["slot_id"] for slot in canonical_slots
    }
    assert {item.id for item in db_session.scalars(select(PlanSlotOption))} == {
        option["option_id"] for option in canonical_options
    }
    assert {item.id for item in db_session.scalars(select(Exercise))} == {
        option["exercise_id"] for option in canonical_options
    }
    assert {item.id for item in db_session.scalars(select(Equipment))} == {
        option["equipment_id"] for option in canonical_options
    }


def test_seed_preserves_legacy_uuid4_history_and_creates_canonical_identity(
    db_session: Session,
) -> None:
    first_option = CANONICAL_PLAN["days"][0]["slots"][0]["options"][0]
    equipment_name = first_option["equipment"]
    exercise_name = first_option["exercise_name"]
    legacy_equipment = Equipment(
        id=str(uuid4()),
        code=f"equipment-{hashlib.sha256(equipment_name.encode()).hexdigest()[:16]}",
        name=equipment_name,
        category="single",
        is_active=True,
        metadata_json={"raw_requirement": equipment_name},
    )
    legacy_exercise = Exercise(
        id=str(uuid4()),
        code=f"exercise-{hashlib.sha256(exercise_name.encode()).hexdigest()[:16]}",
        name=exercise_name,
        difficulty="beginner",
        rep_unit="reps",
        is_active=True,
        metadata_json={"seed": "beginner-recomposition-full-body-ab"},
    )
    legacy_plan = TrainingPlan(
        id=str(uuid4()),
        name=CANONICAL_PLAN["name"],
        is_system=True,
        is_active=True,
    )
    legacy_version = PlanVersion(
        id=str(uuid4()),
        training_plan_id=legacy_plan.id,
        version_number=1,
        status="published",
        weekly_frequency=3,
        min_rest_days=1,
        fatigue_threshold=8,
        initial_reduced_weeks=2,
        initial_set_count=2,
        config_json={"seed_code": "legacy-random-uuid-seed"},
        published_at=datetime.now(UTC),
    )
    db_session.add_all([legacy_equipment, legacy_exercise, legacy_plan, legacy_version])
    db_session.commit()

    result = seed_default_plan(db_session)

    assert result["status"] == "created"
    assert result["plan_id"] == CANONICAL_PLAN["plan_id"]
    assert result["plan_version_id"] == CANONICAL_PLAN["plan_version_id"]
    assert db_session.get(TrainingPlan, legacy_plan.id) is legacy_plan
    assert db_session.get(PlanVersion, legacy_version.id) is legacy_version
    assert legacy_version.status == "published"
    assert legacy_equipment.code.startswith("legacy-equipment-")
    assert legacy_exercise.code.startswith("legacy-exercise-")
    assert db_session.get(Equipment, first_option["equipment_id"]) is not None
    assert db_session.get(Exercise, first_option["exercise_id"]) is not None
    assert db_session.scalar(select(func.count(PlanSlotOption.id))) == 79


def test_bootstrap_returns_authoritative_catalog_and_current_plan(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
) -> None:
    seeded = seed_default_plan(db_session)
    version = db_session.get(PlanVersion, seeded["plan_version_id"])
    assert version is not None and version.status == "published"
    db_session.add(
        PlanAssignment(
            user_id=normal_user.id,
            plan_version_id=version.id,
            status="active",
            starts_on=date.today(),
            settings_json={},
        )
    )
    db_session.commit()

    response = client.get("/api/v1/bootstrap", headers=user_headers)

    assert response.status_code == 200, response.text
    payload = response.json()
    assert payload["user"]["id"] == normal_user.id
    assert payload["user"]["roles"] == ["user"]
    assert "workouts:write" in payload["permissions"]
    assert payload["current_plan"]["id"] == version.id
    assert payload["plan_version"]["days"][0]["slots"]
    assert len(payload["exercises"]) == 66
    assert len(payload["equipment"]) == 52
    assert len(payload["assignments"]) == 1
    assert payload["cursor"] == payload["sync_cursor"]
    assert payload["api_version"]
    assert payload["schema_version"]


def test_bootstrap_includes_every_plan_version_referenced_by_assignments(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
) -> None:
    seeded = seed_default_plan(db_session)
    current_version = db_session.get(PlanVersion, seeded["plan_version_id"])
    assert current_version is not None
    historical_version = PlanVersion(
        training_plan_id=current_version.training_plan_id,
        version_number=current_version.version_number + 1,
        status="archived",
        weekly_frequency=2,
        min_rest_days=1,
        fatigue_threshold=7,
        initial_reduced_weeks=1,
        initial_set_count=1,
        config_json={},
    )
    db_session.add(historical_version)
    db_session.flush()
    db_session.add_all(
        [
            PlanAssignment(
                user_id=normal_user.id,
                plan_version_id=historical_version.id,
                status="completed",
                starts_on=date.today() - timedelta(days=30),
                ends_on=date.today() - timedelta(days=1),
                settings_json={},
            ),
            PlanAssignment(
                user_id=normal_user.id,
                plan_version_id=current_version.id,
                status="active",
                starts_on=date.today(),
                settings_json={},
            ),
        ]
    )
    db_session.commit()

    response = client.get("/api/v1/bootstrap", headers=user_headers)

    assert response.status_code == 200, response.text
    payload = response.json()
    assignment_version_ids = {item["plan_version_id"] for item in payload["assignments"]}
    bootstrap_version_ids = {item["id"] for item in payload["plan_versions"]}
    assert payload["current_plan"]["id"] == current_version.id
    assert assignment_version_ids == {current_version.id, historical_version.id}
    assert assignment_version_ids <= bootstrap_version_ids


def test_bootstrap_returns_complete_active_personal_history(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
) -> None:
    today = date.today()
    now = datetime.now(UTC)
    db_session.add_all(
        [
            WorkoutSession(
                user_id=normal_user.id,
                client_id=None,
                source_device="android",
                local_date=today - timedelta(days=index),
                status="completed",
                started_at=now - timedelta(days=index),
                completed_at=now - timedelta(days=index) + timedelta(hours=1),
                plan_snapshot_json={},
                metadata_json={"timezone": normal_user.timezone},
            )
            for index in range(21)
        ]
        + [
            DailyReadiness(
                user_id=normal_user.id,
                local_date=today - timedelta(days=index),
                fatigue=5,
                metrics_json={},
            )
            for index in range(15)
        ]
        + [
            CardioSession(
                user_id=normal_user.id,
                client_id=None,
                source_device="android",
                local_date=today,
                activity_type="walk",
                started_at=now,
                completed_at=now + timedelta(minutes=30),
                duration_seconds=1800,
                metrics_json={},
            )
        ]
    )
    db_session.commit()

    response = client.get("/api/v1/bootstrap", headers=user_headers)

    assert response.status_code == 200, response.text
    payload = response.json()
    assert len(payload["workout_sessions"]) == 21
    assert len(payload["readiness"]) == 15
    assert len(payload["cardio_sessions"]) == 1


def test_soft_deleted_catalog_row_is_not_listed(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
) -> None:
    visible = Equipment(code="visible", name="Visible", category="machine", is_active=True)
    deleted = Equipment(code="deleted", name="Deleted", category="machine", is_active=True)
    db_session.add_all([visible, deleted])
    db_session.flush()
    original_version = deleted.version
    deleted.soft_delete()
    db_session.commit()

    response = client.get("/api/v1/equipment", headers=user_headers)

    assert response.status_code == 200, response.text
    ids = {item["id"] for item in response.json()["items"]}
    assert visible.id in ids
    assert deleted.id not in ids
    assert deleted.deleted_at is not None
    assert deleted.version == original_version + 1


def test_openapi_contains_required_contract_and_auth_scheme(client: TestClient) -> None:
    response = client.get("/openapi.json")
    assert response.status_code == 200
    document = response.json()

    required_paths = {
        "/api/v1/auth/login",
        "/api/v1/auth/refresh",
        "/api/v1/auth/logout",
        "/api/v1/me",
        "/api/v1/bootstrap",
        "/api/v1/plans/current",
        "/api/v1/exercises",
        "/api/v1/equipment",
        "/api/v1/workout-sessions",
        "/api/v1/readiness",
        "/api/v1/cardio-sessions",
        "/api/v1/sync/changes",
        "/api/v1/sync/batch",
        "/api/v1/admin/exercises",
        "/api/v1/admin/plan-versions/{version_id}/publish",
    }
    assert required_paths.issubset(document["paths"])
    security_schemes = document["components"]["securitySchemes"]
    assert any(item.get("scheme") == "bearer" for item in security_schemes.values())
    assert document["info"]["version"]


def test_protected_bootstrap_rejects_anonymous_client(client: TestClient) -> None:
    response = client.get("/api/v1/bootstrap")
    assert response.status_code == 401
    assert response.json()["detail"]["code"] == "unauthorized"

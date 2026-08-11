from __future__ import annotations

from collections.abc import Callable
from datetime import UTC, date, datetime, timedelta
from typing import Any
from uuid import uuid4

from fastapi.testclient import TestClient
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.core.config import settings
from app.core.security import create_access_token
from app.db.base import utcnow
from app.models import (
    AuditLog,
    CardioSession,
    DailyReadiness,
    IdempotencyKey,
    PlanAssignment,
    PlanVersion,
    SyncChange,
    TrainingPlan,
    User,
    WorkoutSession,
    WorkoutSet,
)
from app.seed.default_plan import seed_default_plan


def _request_headers(auth: dict[str, str], key: str) -> dict[str, str]:
    return {**auth, "Idempotency-Key": key}


def _authorization_headers(user: User) -> dict[str, str]:
    token, _expires_at = create_access_token(user.id)
    return {"Authorization": f"Bearer {token}"}


def test_plan_assignment_changes_are_visible_only_to_payload_user(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
    user_factory: Callable[..., User],
) -> None:
    other_user = user_factory()
    administrator = user_factory(role_name="admin", permissions=["*"])
    assignment_id = str(uuid4())
    target_payload = {
        "id": assignment_id,
        "user_id": normal_user.id,
        "plan_version_id": str(uuid4()),
    }
    db_session.add_all(
        [
            SyncChange(
                entity_type="plan_assignment",
                entity_id=assignment_id,
                entity_version=1,
                operation="create",
                payload_json=target_payload,
                actor_user_id=administrator.id,
            ),
            SyncChange(
                entity_type="plan_assignment",
                entity_id=assignment_id,
                entity_version=2,
                operation="delete",
                payload_json={**target_payload, "deleted_at": utcnow().isoformat()},
                actor_user_id=administrator.id,
            ),
            SyncChange(
                entity_type="equipment",
                entity_id=str(uuid4()),
                entity_version=1,
                operation="create",
                payload_json={"name": "Globally visible equipment"},
                actor_user_id=administrator.id,
            ),
        ]
    )
    db_session.commit()

    target = client.get("/api/v1/sync/changes", headers=user_headers)
    other = client.get(
        "/api/v1/sync/changes", headers=_authorization_headers(other_user)
    )
    admin = client.get(
        "/api/v1/sync/changes", headers=_authorization_headers(administrator)
    )

    assert target.status_code == other.status_code == admin.status_code == 200
    target_assignments = [
        change
        for change in target.json()["changes"]
        if change["entity_type"] == "plan_assignment"
    ]
    assert [change["operation"] for change in target_assignments] == ["UPSERT", "DELETE"]
    assert all(change["payload"]["user_id"] == normal_user.id for change in target_assignments)
    assert not any(
        change["entity_type"] == "plan_assignment" for change in other.json()["changes"]
    )
    assert not any(
        change["entity_type"] == "plan_assignment" for change in admin.json()["changes"]
    )
    assert any(change["entity_type"] == "equipment" for change in other.json()["changes"])
    assert any(change["entity_type"] == "equipment" for change in admin.json()["changes"])


def test_sync_hides_draft_and_private_plan_changes_from_standard_users(
    client: TestClient,
    db_session: Session,
    user_headers: dict[str, str],
    admin_user: User,
) -> None:
    plan_id = str(uuid4())
    draft_id = str(uuid4())
    published_id = str(uuid4())
    db_session.add_all(
        [
            SyncChange(
                entity_type="training_plan",
                entity_id=plan_id,
                entity_version=1,
                operation="create",
                payload_json={"id": plan_id, "owner_user_id": admin_user.id},
                actor_user_id=admin_user.id,
            ),
            SyncChange(
                entity_type="plan_version",
                entity_id=draft_id,
                entity_version=1,
                operation="create",
                payload_json={"id": draft_id, "status": "draft"},
                actor_user_id=admin_user.id,
            ),
            SyncChange(
                entity_type="plan_version",
                entity_id=published_id,
                entity_version=2,
                operation="update",
                payload_json={
                    "id": published_id,
                    "status": "published",
                    "is_system": True,
                    "owner_user_id": None,
                    "days": [],
                },
                actor_user_id=admin_user.id,
            ),
        ]
    )
    db_session.commit()

    standard = client.get("/api/v1/sync/changes", headers=user_headers)
    admin = client.get(
        "/api/v1/sync/changes", headers=_authorization_headers(admin_user)
    )

    assert standard.status_code == admin.status_code == 200
    standard_ids = {change["entity_id"] for change in standard.json()["changes"]}
    assert published_id in standard_ids
    assert plan_id not in standard_ids
    assert draft_id not in standard_ids
    admin_ids = {change["entity_id"] for change in admin.json()["changes"]}
    assert {plan_id, draft_id, published_id} <= admin_ids


def test_private_published_plan_is_visible_only_to_owner_or_assignee(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
    user_factory: Callable[..., User],
) -> None:
    owner = user_factory()
    assignee = user_factory()
    plan = TrainingPlan(
        owner_user_id=owner.id,
        name="Private published plan",
        goal="private",
        is_system=False,
        is_active=True,
    )
    version = PlanVersion(
        training_plan_id=plan.id,
        version_number=1,
        status="published",
        weekly_frequency=3,
        min_rest_days=1,
        fatigue_threshold=8,
        initial_reduced_weeks=2,
        initial_set_count=2,
        config_json={},
    )
    plan.versions.append(version)
    db_session.add(plan)
    db_session.flush()
    db_session.add(
        PlanAssignment(
            user_id=assignee.id,
            plan_version_id=version.id,
            status="active",
            starts_on=date.today(),
            settings_json={},
        )
    )
    db_session.add_all(
        [
            SyncChange(
                entity_type="training_plan",
                entity_id=plan.id,
                entity_version=1,
                operation="create",
                payload_json={
                    "id": plan.id,
                    "is_system": False,
                    "owner_user_id": owner.id,
                },
                actor_user_id=owner.id,
            ),
            SyncChange(
                entity_type="plan_version",
                entity_id=version.id,
                entity_version=1,
                operation="update",
                payload_json={
                    "id": version.id,
                    "status": "published",
                    "is_system": False,
                    "owner_user_id": owner.id,
                    "days": [],
                },
                actor_user_id=owner.id,
            ),
        ]
    )
    db_session.commit()

    unrelated = client.get("/api/v1/sync/changes", headers=user_headers)
    owner_feed = client.get(
        "/api/v1/sync/changes", headers=_authorization_headers(owner)
    )
    assignee_feed = client.get(
        "/api/v1/sync/changes", headers=_authorization_headers(assignee)
    )

    assert unrelated.status_code == owner_feed.status_code == assignee_feed.status_code == 200
    assert version.id not in {item["entity_id"] for item in unrelated.json()["changes"]}
    assert version.id in {item["entity_id"] for item in owner_feed.json()["changes"]}
    assert version.id in {item["entity_id"] for item in assignee_feed.json()["changes"]}
    # training_plan records stay admin-only; the authorized published version
    # already carries its plan metadata as one immutable client snapshot.
    assert all(
        item["entity_type"] != "training_plan"
        for feed in (unrelated, owner_feed, assignee_feed)
        for item in feed.json()["changes"]
    )


def test_workout_rejects_another_users_plan_assignment(
    client: TestClient,
    db_session: Session,
    user_headers: dict[str, str],
    user_factory: Callable[..., User],
    workout_payload_factory: Callable[..., dict[str, Any]],
) -> None:
    seeded = seed_default_plan(db_session)
    other_user = user_factory()
    assignment = PlanAssignment(
        user_id=other_user.id,
        plan_version_id=seeded["plan_version_id"],
        status="active",
        starts_on=date.today(),
        settings_json={},
    )
    db_session.add(assignment)
    db_session.commit()
    payload = workout_payload_factory()
    payload["plan_assignment_id"] = assignment.id

    response = client.post(
        "/api/v1/workout-sessions",
        json=payload,
        headers=_request_headers(user_headers, "foreign-assignment-workout"),
    )

    assert response.status_code == 422
    assert response.json()["detail"]["code"] == "invalid_plan_reference"


def test_workout_accepts_only_one_authorized_plan_tree_and_rejects_mismatch(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
    workout_payload_factory: Callable[..., dict[str, Any]],
) -> None:
    seeded = seed_default_plan(db_session)
    version = db_session.get(PlanVersion, seeded["plan_version_id"])
    assert version is not None
    day = version.days[0]
    slot = day.slots[0]
    option = slot.options[0]
    assignment = PlanAssignment(
        user_id=normal_user.id,
        plan_version_id=version.id,
        status="active",
        starts_on=date.today(),
        settings_json={},
    )
    db_session.add(assignment)
    db_session.commit()

    payload = workout_payload_factory()
    payload.update(
        {
            "plan_assignment_id": assignment.id,
            "plan_version_id": version.id,
            "plan_day_id": day.id,
        }
    )
    payload["sets"][0].update(
        {
            "plan_slot_id": slot.id,
            "source_plan_slot_option_id": option.id,
            "exercise_id": option.exercise_id,
            "equipment_id": option.prescription_json["equipment_id"],
        }
    )
    created = client.post(
        "/api/v1/workout-sessions",
        json=payload,
        headers=_request_headers(user_headers, "authorized-plan-tree"),
    )

    assert created.status_code == 201, created.text
    result = created.json()
    assert result["plan_assignment_id"] == assignment.id
    assert result["plan_version_id"] == version.id
    assert result["plan_day_id"] == day.id
    assert result["sets"][0]["plan_slot_id"] == slot.id

    mismatch = client.patch(
        f"/api/v1/workout-sessions/{payload['id']}",
        json={
            "id": payload["id"],
            "expected_version": result["version"],
            "plan_version_id": str(uuid4()),
        },
        headers=_request_headers(user_headers, "mismatched-plan-tree"),
    )
    assert mismatch.status_code == 422
    assert mismatch.json()["detail"]["code"] == "invalid_plan_reference"


def test_sync_workout_rejects_foreign_assignment(
    client: TestClient,
    db_session: Session,
    user_headers: dict[str, str],
    user_factory: Callable[..., User],
    workout_payload_factory: Callable[..., dict[str, Any]],
) -> None:
    seeded = seed_default_plan(db_session)
    other_user = user_factory()
    assignment = PlanAssignment(
        user_id=other_user.id,
        plan_version_id=seeded["plan_version_id"],
        status="active",
        starts_on=date.today(),
        settings_json={},
    )
    db_session.add(assignment)
    db_session.commit()
    payload = workout_payload_factory()
    payload["plan_assignment_id"] = assignment.id
    body = {
        "batch_id": str(uuid4()),
        "sent_at": datetime.now(UTC).isoformat(),
        "operations": [
            {
                "id": str(uuid4()),
                "client_outbox_id": str(uuid4()),
                "idempotency_key": "sync-foreign-assignment-operation",
                "entity_type": "workout_session",
                "entity_id": payload["id"],
                "operation": "UPSERT",
                "payload": payload,
            }
        ],
    }

    response = client.post(
        "/api/v1/sync/batch",
        json=body,
        headers=_request_headers(user_headers, "sync-foreign-assignment-batch"),
    )

    assert response.status_code == 200, response.text
    assert response.json()["results"][0]["status"] == "invalid"
    assert "assignment" in response.json()["results"][0]["error"].lower()


def test_workout_create_replays_identical_idempotent_request(
    client: TestClient,
    db_session: Session,
    user_headers: dict[str, str],
    workout_payload_factory: Callable[..., dict[str, Any]],
) -> None:
    payload = workout_payload_factory()
    headers = _request_headers(user_headers, "workout-create-same")

    first = client.post("/api/v1/workout-sessions", json=payload, headers=headers)
    second = client.post("/api/v1/workout-sessions", json=payload, headers=headers)

    assert first.status_code == 201, first.text
    assert second.status_code == 201, second.text
    assert second.json() == first.json()
    assert db_session.scalar(select(func.count(WorkoutSession.id))) == 1
    assert db_session.scalar(select(func.count(WorkoutSet.id))) == 1
    assert db_session.scalar(select(func.count(IdempotencyKey.id))) == 1
    stored = db_session.get(WorkoutSession, payload["id"])
    assert stored is not None
    assert stored.plan_snapshot_json == {}
    assert stored.sets[0].exercise_snapshot_json["name"] == "Test Dumbbell Press"


def test_idempotency_key_reuse_with_changed_workout_is_conflict(
    client: TestClient,
    user_headers: dict[str, str],
    workout_payload_factory: Callable[..., dict[str, Any]],
) -> None:
    payload = workout_payload_factory()
    headers = _request_headers(user_headers, "workout-create-conflicting-body")
    first = client.post("/api/v1/workout-sessions", json=payload, headers=headers)
    payload["sets"][0]["reps"] = 3

    conflict = client.post("/api/v1/workout-sessions", json=payload, headers=headers)

    assert first.status_code == 201, first.text
    assert conflict.status_code == 409, conflict.text
    assert conflict.json()["detail"]["code"] == "idempotency_key_reused"


def test_duplicate_set_uuid_inside_payload_is_rejected_before_database(
    client: TestClient,
    db_session: Session,
    user_headers: dict[str, str],
    workout_payload_factory: Callable[..., dict[str, Any]],
) -> None:
    payload = workout_payload_factory()
    payload["sets"].append(dict(payload["sets"][0]))

    response = client.post(
        "/api/v1/workout-sessions",
        json=payload,
        headers=_request_headers(user_headers, "duplicate-set-in-body"),
    )

    assert response.status_code == 422
    assert db_session.scalar(select(func.count(WorkoutSession.id))) == 0


def test_set_uuid_cannot_be_reused_by_another_workout_and_conflict_is_audited(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
    workout_payload_factory: Callable[..., dict[str, Any]],
) -> None:
    shared_set_id = str(uuid4())
    first_payload = workout_payload_factory(set_id=shared_set_id)
    second_payload = workout_payload_factory(set_id=shared_set_id)
    first = client.post(
        "/api/v1/workout-sessions",
        json=first_payload,
        headers=_request_headers(user_headers, "first-workout-shared-set"),
    )

    conflict = client.post(
        "/api/v1/workout-sessions",
        json=second_payload,
        headers=_request_headers(user_headers, "second-workout-shared-set"),
    )

    assert first.status_code == 201, first.text
    assert conflict.status_code == 409, conflict.text
    assert conflict.json()["detail"]["code"] == "duplicate_set_uuid"
    audit = db_session.scalar(
        select(AuditLog).where(
            AuditLog.actor_user_id == normal_user.id,
            AuditLog.action == "SYNC_CONFLICT",
        )
    )
    assert audit is not None
    assert audit.metadata_json["reason"] == "duplicate_set_uuid"


def test_stale_workout_patch_returns_server_copy_and_persists_audit(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
    workout_payload_factory: Callable[..., dict[str, Any]],
) -> None:
    payload = workout_payload_factory()
    created = client.post(
        "/api/v1/workout-sessions",
        json=payload,
        headers=_request_headers(user_headers, "create-before-stale-patch"),
    )
    assert created.status_code == 201, created.text

    conflict = client.patch(
        f"/api/v1/workout-sessions/{payload['id']}",
        json={"notes": "stale", "expected_version": 99},
        headers=_request_headers(user_headers, "stale-patch"),
    )

    assert conflict.status_code == 409, conflict.text
    detail = conflict.json()["detail"]
    assert detail["code"] == "version_conflict"
    assert detail["server_copy"]["id"] == payload["id"]
    audit = db_session.scalar(
        select(AuditLog)
        .where(
            AuditLog.actor_user_id == normal_user.id,
            AuditLog.entity_id == payload["id"],
            AuditLog.action == "SYNC_CONFLICT",
        )
        .order_by(AuditLog.created_at.desc())
    )
    assert audit is not None
    assert audit.metadata_json["reason"] == "version_conflict"


def test_workout_delete_is_soft_and_idempotent(
    client: TestClient,
    db_session: Session,
    user_headers: dict[str, str],
    workout_payload_factory: Callable[..., dict[str, Any]],
) -> None:
    payload = workout_payload_factory()
    created = client.post(
        "/api/v1/workout-sessions",
        json=payload,
        headers=_request_headers(user_headers, "create-before-delete"),
    )
    assert created.status_code == 201, created.text
    version = created.json()["version"]
    headers = _request_headers(user_headers, "delete-workout-once")

    first = client.delete(
        f"/api/v1/workout-sessions/{payload['id']}?expected_version={version}",
        headers=headers,
    )
    replay = client.delete(
        f"/api/v1/workout-sessions/{payload['id']}?expected_version={version}",
        headers=headers,
    )

    assert first.status_code == 204, first.text
    assert replay.status_code == 204, replay.text
    assert client.get(
        f"/api/v1/workout-sessions/{payload['id']}", headers=user_headers
    ).status_code == 404
    tombstone = client.get(
        f"/api/v1/workout-sessions/{payload['id']}?include_deleted=true",
        headers=user_headers,
    )
    assert tombstone.status_code == 200
    assert tombstone.json()["status"] == "DELETED"
    stored = db_session.get(WorkoutSession, payload["id"])
    assert stored is not None and stored.deleted_at is not None
    assert stored.sets[0].deleted_at is not None


def test_incremental_sync_returns_created_workout_change(
    client: TestClient,
    user_headers: dict[str, str],
    workout_payload_factory: Callable[..., dict[str, Any]],
) -> None:
    payload = workout_payload_factory()
    created = client.post(
        "/api/v1/workout-sessions",
        json=payload,
        headers=_request_headers(user_headers, "create-for-change-feed"),
    )
    assert created.status_code == 201, created.text

    response = client.get("/api/v1/sync/changes?cursor=0", headers=user_headers)

    assert response.status_code == 200, response.text
    body = response.json()
    assert body["full_resync_required"] is False
    assert body["next_cursor"] != "0"
    assert any(
        item["entity_type"] == "workout_session" and item["entity_id"] == payload["id"]
        for item in body["changes"]
    )


def test_cursor_older_than_retained_window_requires_full_resync(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
) -> None:
    old = SyncChange(
        entity_type="workout_session",
        entity_id=str(uuid4()),
        entity_version=1,
        operation="update",
        actor_user_id=normal_user.id,
        changed_at=utcnow() - timedelta(days=settings.sync_retention_days + 2),
    )
    retained = SyncChange(
        entity_type="workout_session",
        entity_id=str(uuid4()),
        entity_version=1,
        operation="update",
        actor_user_id=normal_user.id,
        changed_at=utcnow(),
    )
    db_session.add_all([old, retained])
    db_session.commit()
    assert old.sequence < retained.sequence

    response = client.get("/api/v1/sync/changes?cursor=0", headers=user_headers)

    assert response.status_code == 200, response.text
    assert response.json()["changes"] == []
    assert response.json()["full_resync_required"] is True
    assert int(response.json()["next_cursor"]) >= retained.sequence


def test_sync_batch_replays_and_outer_key_conflict_is_audited(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
) -> None:
    readiness_id = str(uuid4())
    operation_id = str(uuid4())
    body = {
        "batch_id": str(uuid4()),
        "sent_at": datetime.now(UTC).isoformat(),
        "operations": [
            {
                "id": operation_id,
                "client_outbox_id": str(uuid4()),
                "idempotency_key": "readiness-operation-key",
                "entity_type": "daily_readiness",
                "entity_id": readiness_id,
                "operation": "UPSERT",
                "payload": {
                    "id": readiness_id,
                    "local_date": date.today().isoformat(),
                    "fatigue_score": 6,
                },
            }
        ],
    }
    headers = _request_headers(user_headers, "outer-sync-batch-key")

    first = client.post("/api/v1/sync/batch", json=body, headers=headers)
    replay = client.post("/api/v1/sync/batch", json=body, headers=headers)
    changed = {**body, "batch_id": str(uuid4())}
    conflict = client.post("/api/v1/sync/batch", json=changed, headers=headers)

    assert first.status_code == 200, first.text
    assert first.json()["results"][0]["status"] == "accepted"
    assert replay.status_code == 200
    assert replay.json() == first.json()
    assert db_session.get(DailyReadiness, readiness_id) is not None
    assert conflict.status_code == 409, conflict.text
    assert conflict.json()["detail"]["code"] == "idempotency_key_reused"
    audit = db_session.scalar(
        select(AuditLog)
        .where(
            AuditLog.actor_user_id == normal_user.id,
            AuditLog.entity_type == "sync_batch",
            AuditLog.action == "SYNC_CONFLICT",
        )
        .order_by(AuditLog.created_at.desc())
    )
    assert audit is not None
    assert audit.metadata_json["reason"] == "idempotency_key_reused"


def test_readiness_and_cardio_upserts_are_idempotent(
    client: TestClient,
    db_session: Session,
    user_headers: dict[str, str],
) -> None:
    now = datetime.now(UTC).replace(microsecond=0).isoformat()
    readiness = {
        "id": str(uuid4()),
        "local_date": date.today().isoformat(),
        "fatigue_score": 8,
        "sleep_quality": 4,
    }
    cardio = {
        "id": str(uuid4()),
        "source": "windows",
        "local_date": date.today().isoformat(),
        "activity": "walking",
        "duration_minutes": 30,
        "distance_km": 2.5,
        "started_at": now,
        "completed_at": now,
    }

    ready_headers = _request_headers(user_headers, "readiness-idempotency")
    cardio_headers = _request_headers(user_headers, "cardio-idempotency")
    ready_first = client.post("/api/v1/readiness", json=readiness, headers=ready_headers)
    ready_again = client.post("/api/v1/readiness", json=readiness, headers=ready_headers)
    cardio_first = client.post("/api/v1/cardio-sessions", json=cardio, headers=cardio_headers)
    cardio_again = client.post("/api/v1/cardio-sessions", json=cardio, headers=cardio_headers)

    assert ready_first.status_code == ready_again.status_code == 201
    assert ready_first.json() == ready_again.json()
    assert cardio_first.status_code == cardio_again.status_code == 201
    assert cardio_first.json() == cardio_again.json()
    assert db_session.scalar(select(func.count(DailyReadiness.id))) == 1
    assert db_session.scalar(select(func.count(CardioSession.id))) == 1

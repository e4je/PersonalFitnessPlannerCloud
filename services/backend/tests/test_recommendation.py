from __future__ import annotations

from datetime import datetime, timedelta

from fastapi.testclient import TestClient
from sqlalchemy.orm import Session

from app.api.recommendation import local_today
from app.models import DailyReadiness, User, WorkoutSession
from app.seed.default_plan import seed_default_plan


def test_recommendation_without_a_plan_is_explicit(
    client: TestClient,
    normal_user: User,
    user_headers: dict[str, str],
) -> None:
    response = client.get("/api/v1/recommendation/today", headers=user_headers)

    assert response.status_code == 200
    assert response.json()["should_train"] is False
    assert response.json()["reason"] == "no_plan"


def test_recommendation_applies_fatigue_ab_rotation_and_early_week_set_cap(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
) -> None:
    seed_default_plan(db_session)
    today = local_today(normal_user.timezone)
    db_session.add(
        DailyReadiness(
            user_id=normal_user.id,
            local_date=today,
            fatigue=8,
            sleep_quality=3,
            metrics_json={},
        )
    )
    db_session.commit()

    response = client.get("/api/v1/recommendation/today", headers=user_headers)

    assert response.status_code == 200, response.text
    result = response.json()
    assert result["should_train"] is False
    assert "fatigue_threshold_reached" in result["reasons"]
    assert result["current_ab_state"] == "B"
    assert result["training_day"] == "A"
    assert result["weekly_max_sessions"] == 3
    assert result["minimum_rest_days"] == 1
    assert result["fatigue_threshold"] == 8
    assert result["current_training_week"] == 1
    assert result["effective_set_cap"] == 2


def test_recommendation_blocks_back_to_back_strength_sessions(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
) -> None:
    seed_default_plan(db_session)
    today = local_today(normal_user.timezone)
    db_session.add(
        WorkoutSession(
            user_id=normal_user.id,
            source_device="android",
            local_date=today,
            status="completed",
            ab_state="A",
            started_at=datetime.now().astimezone(),
            completed_at=datetime.now().astimezone(),
            plan_snapshot_json={},
            metadata_json={},
        )
    )
    db_session.commit()

    response = client.get("/api/v1/recommendation/today", headers=user_headers)

    assert response.status_code == 200, response.text
    result = response.json()
    assert result["should_train"] is False
    assert "minimum_rest_not_met" in result["reasons"]
    assert result["current_ab_state"] == "A"
    assert result["training_day"] == "B"


def test_system_plan_fallback_derives_training_week_from_persisted_history(
    client: TestClient,
    db_session: Session,
    normal_user: User,
    user_headers: dict[str, str],
) -> None:
    seeded = seed_default_plan(db_session)
    today = local_today(normal_user.timezone)
    started = today - timedelta(days=15)
    db_session.add(
        WorkoutSession(
            user_id=normal_user.id,
            source_device="android",
            plan_version_id=seeded["plan_version_id"],
            local_date=started,
            status="completed",
            ab_state="A",
            started_at=datetime.now().astimezone() - timedelta(days=15),
            completed_at=datetime.now().astimezone() - timedelta(days=15),
            plan_snapshot_json={},
            metadata_json={"is_full_body": True},
        )
    )
    db_session.commit()

    response = client.get("/api/v1/recommendation/today", headers=user_headers)

    assert response.status_code == 200, response.text
    result = response.json()
    assert result["current_training_week"] == 3
    assert result["effective_set_cap"] is None
    assert result["session"] == "B"

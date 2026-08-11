from __future__ import annotations

import os
from collections.abc import Callable, Generator
from datetime import UTC, date, datetime
from pathlib import Path
from typing import Any
from uuid import uuid4

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import Engine, create_engine, event, select
from sqlalchemy.engine import make_url
from sqlalchemy.orm import Session, sessionmaker
from sqlalchemy.pool import StaticPool

# The application intentionally ships without reusable development credentials.
# Tests opt into an isolated, explicit signing key before importing app modules.
os.environ.setdefault("ENVIRONMENT", "test")
os.environ.setdefault("JWT_SECRET", "unit-test-only-signing-key-not-for-deployment")
os.environ.setdefault("DATABASE_URL", "sqlite+pysqlite:///:memory:")

import app.models  # noqa: E402,F401 -- register mappings after isolated test env
from app.core.config import settings  # noqa: E402
from app.core.security import create_access_token, hash_password  # noqa: E402
from app.db.base import Base  # noqa: E402
from app.db.session import get_db  # noqa: E402
from app.main import app  # noqa: E402
from app.models import Equipment, Exercise, ExerciseEquipment, Role, User  # noqa: E402
from app.services.auth import login_rate_limiter  # noqa: E402


TEST_PASSWORD = "Correct-Horse-Battery-Staple-2026!"


def _enable_sqlite_foreign_keys(dbapi_connection: object, _record: object) -> None:
    cursor = dbapi_connection.cursor()  # type: ignore[attr-defined]
    try:
        cursor.execute("PRAGMA foreign_keys=ON")
    finally:
        cursor.close()


@pytest.fixture
def db_engine() -> Generator[Engine, None, None]:
    """A brand-new in-memory schema for every fast test.

    StaticPool is required because TestClient executes sync endpoints in worker
    threads; all those threads must see the same in-memory SQLite connection.
    """

    engine = create_engine(
        "sqlite+pysqlite:///:memory:",
        connect_args={"check_same_thread": False},
        poolclass=StaticPool,
    )
    event.listen(engine, "connect", _enable_sqlite_foreign_keys)
    Base.metadata.create_all(engine)
    try:
        yield engine
    finally:
        Base.metadata.drop_all(engine)
        engine.dispose()


@pytest.fixture
def db_session(db_engine: Engine) -> Generator[Session, None, None]:
    factory = sessionmaker(bind=db_engine, autoflush=True, expire_on_commit=False)
    session = factory()
    try:
        yield session
    finally:
        session.rollback()
        session.close()


@pytest.fixture(autouse=True)
def _reset_process_local_auth_state() -> Generator[None, None, None]:
    # Failed-login state is intentionally process-local in the application. It
    # must not leak between otherwise isolated tests.
    with login_rate_limiter._lock:  # noqa: SLF001 - deliberate test reset
        login_rate_limiter._attempts.clear()  # noqa: SLF001
    yield
    with login_rate_limiter._lock:  # noqa: SLF001
        login_rate_limiter._attempts.clear()  # noqa: SLF001


@pytest.fixture
def client(db_session: Session) -> Generator[TestClient, None, None]:
    def override_database() -> Generator[Session, None, None]:
        yield db_session

    app.dependency_overrides[get_db] = override_database
    try:
        with TestClient(app) as test_client:
            yield test_client
    finally:
        app.dependency_overrides.pop(get_db, None)


@pytest.fixture
def user_factory(db_session: Session) -> Callable[..., User]:
    def create_user(
        *,
        email: str | None = None,
        username: str | None = None,
        password: str = TEST_PASSWORD,
        role_name: str = "user",
        permissions: list[str] | None = None,
        is_superuser: bool = False,
        timezone: str = "Asia/Shanghai",
    ) -> User:
        suffix = uuid4().hex[:10]
        role = db_session.scalar(select(Role).where(Role.name == role_name))
        if role is None:
            role = Role(
                name=role_name,
                description=f"Test {role_name} role",
                permissions_json=permissions
                or (["*"] if role_name == "admin" else ["sync:read", "workouts:read", "workouts:write"]),
                is_system=True,
            )
            db_session.add(role)
        user = User(
            email=email or f"user-{suffix}@example.test",
            username=username or f"user_{suffix}",
            password_hash=hash_password(password),
            display_name=f"Test User {suffix}",
            timezone=timezone,
            weight_unit="KG",
            is_active=True,
            is_superuser=is_superuser,
        )
        user.roles.append(role)
        db_session.add(user)
        db_session.commit()
        return user

    return create_user


@pytest.fixture
def normal_user(user_factory: Callable[..., User]) -> User:
    return user_factory(role_name="user")


@pytest.fixture
def admin_user(user_factory: Callable[..., User]) -> User:
    return user_factory(role_name="admin", permissions=["*"])


def authorization_headers(user: User) -> dict[str, str]:
    token, _expires_at = create_access_token(user.id)
    return {"Authorization": f"Bearer {token}"}


@pytest.fixture
def user_headers(normal_user: User) -> dict[str, str]:
    return authorization_headers(normal_user)


@pytest.fixture
def admin_headers(admin_user: User) -> dict[str, str]:
    return authorization_headers(admin_user)


@pytest.fixture
def catalog_items(db_session: Session) -> tuple[Exercise, Equipment]:
    equipment = Equipment(
        id=str(uuid4()),
        code=f"equipment-{uuid4().hex[:10]}",
        name="Test Dumbbell",
        category="free_weight",
        is_active=True,
        metadata_json={},
    )
    exercise = Exercise(
        id=str(uuid4()),
        code=f"exercise-{uuid4().hex[:10]}",
        name="Test Dumbbell Press",
        body_part="chest",
        difficulty="beginner",
        default_sets=3,
        rep_min=8,
        rep_max=12,
        rep_unit="reps",
        is_active=True,
        common_mistakes_json=[],
        metadata_json={},
    )
    exercise.equipment_links.append(
        ExerciseEquipment(equipment=equipment, is_required=True, quantity=1)
    )
    db_session.add(exercise)
    db_session.commit()
    return exercise, equipment


@pytest.fixture
def workout_payload_factory(
    catalog_items: tuple[Exercise, Equipment],
) -> Callable[..., dict[str, Any]]:
    exercise, equipment = catalog_items

    def build_payload(
        *,
        workout_id: str | None = None,
        set_id: str | None = None,
        reps: int = 10,
        status: str = "COMPLETED",
    ) -> dict[str, Any]:
        now = datetime.now(UTC).replace(microsecond=0)
        return {
            "id": workout_id or str(uuid4()),
            "client_id": workout_id or None,
            "source": "android",
            "client_version": "1.0-test",
            "local_date": date.today().isoformat(),
            "timezone": "Asia/Shanghai",
            "started_at": now.isoformat(),
            "completed_at": now.isoformat() if status == "COMPLETED" else None,
            "status": status,
            "is_full_body": True,
            "training_week": 1,
            "ab_state": "A",
            "plan_snapshot_json": "{}",
            "metadata": {"test": True},
            "sets": [
                {
                    "id": set_id or str(uuid4()),
                    "exercise_id": exercise.id,
                    "equipment_id": equipment.id,
                    "set_number": 1,
                    "weight_kg": 20,
                    "reps": reps,
                    "set_type": "WORKING",
                    "rir": 2,
                    "completed": True,
                    "completed_at": now.isoformat(),
                }
            ],
        }

    return build_payload


def validated_mysql_test_url() -> str | None:
    """Return TEST_DATABASE_URL only when it is unmistakably non-production."""

    raw = os.getenv("TEST_DATABASE_URL")
    if not raw:
        return None
    parsed = make_url(raw)
    if not parsed.drivername.startswith("mysql"):
        raise pytest.UsageError("TEST_DATABASE_URL must use a MySQL driver")
    database = (parsed.database or "").casefold()
    if "test" not in database:
        raise pytest.UsageError(
            "Refusing destructive integration setup: TEST_DATABASE_URL database name must contain 'test'"
        )
    if raw == settings.database_url:
        raise pytest.UsageError("TEST_DATABASE_URL must not equal the application DATABASE_URL")
    return raw


@pytest.fixture(scope="session")
def backend_root() -> Path:
    return Path(__file__).resolve().parents[1]

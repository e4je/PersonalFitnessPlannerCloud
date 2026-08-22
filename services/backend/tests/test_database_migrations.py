from __future__ import annotations

from pathlib import Path

import pytest
from alembic import command
from alembic.config import Config
from sqlalchemy import create_engine, inspect, select, text
from sqlalchemy.exc import DBAPIError
from sqlalchemy.orm import Session

from app.core.config import settings
from app.core.security import hash_password
from app.db.base import Base
from app.db.session import build_engine
from app.models import DailyReadiness, PlanSlotOption, PlanVersion, SystemSetting, User
from app.seed.default_plan import seed_default_plan
from tests.conftest import validated_mysql_test_url


EXPECTED_TABLES = set(Base.metadata.tables)
HEAD_REVISION = "20260823_0002"


def _alembic_config(root: Path) -> Config:
    return Config(str(root / "alembic.ini"))


def test_initial_alembic_revision_round_trip_and_matches_metadata(
    tmp_path: Path,
    backend_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    database_file = tmp_path / "alembic-test.sqlite3"
    database_url = f"sqlite+pysqlite:///{database_file.as_posix()}"
    monkeypatch.setattr(settings, "database_url", database_url)
    config = _alembic_config(backend_root)

    command.upgrade(config, "head")
    engine = create_engine(database_url)
    try:
        tables = set(inspect(engine).get_table_names())
        assert tables == EXPECTED_TABLES | {"alembic_version"}
        with engine.connect() as connection:
            revision = connection.execute(text("SELECT version_num FROM alembic_version")).scalar_one()
        assert revision == HEAD_REVISION

        # This catches drift between the explicit initial revision and ORM metadata.
        command.check(config)

        with Session(engine) as session:
            setting = session.scalar(select(SystemSetting).where(SystemSetting.key == "registration_enabled"))
            assert setting is not None and setting.value_json == {"value": True}
            seeded = seed_default_plan(session)
            assert seeded["options"] == 79
            assert len(session.scalars(select(PlanSlotOption)).all()) == 79

        command.downgrade(config, "base")
        assert inspect(engine).get_table_names() == ["alembic_version"]
    finally:
        engine.dispose()


def test_mysql_test_url_guard_rejects_non_test_database(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv(
        "TEST_DATABASE_URL", "mysql+pymysql://fitness:secret@127.0.0.1:3306/fitness"
    )
    with pytest.raises(pytest.UsageError, match="must contain 'test'"):
        validated_mysql_test_url()


@pytest.mark.mysql
def test_real_mysql8_migration_seed_json_checks_and_downgrade(
    backend_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Container integration test; skipped unless an explicit safe URL is supplied.

    The named database is treated as disposable. The URL validator requires the
    database name to contain ``test`` and rejects the application's normal URL.
    """

    database_url = validated_mysql_test_url()
    if database_url is None:
        pytest.skip("TEST_DATABASE_URL is not set; real MySQL 8 integration test skipped")

    monkeypatch.setattr(settings, "database_url", database_url)
    cleanup_engine = create_engine(database_url, pool_pre_ping=True)
    existing = set(inspect(cleanup_engine).get_table_names())
    unknown = existing - EXPECTED_TABLES - {"alembic_version"}
    if unknown:
        cleanup_engine.dispose()
        raise pytest.UsageError(
            "Refusing to clean TEST_DATABASE_URL because it contains unknown tables: "
            + ", ".join(sorted(unknown))
        )

    # Make a previously interrupted test run recoverable without touching any
    # database that failed the safety checks above.
    Base.metadata.drop_all(cleanup_engine)
    with cleanup_engine.begin() as connection:
        connection.execute(text("DROP TABLE IF EXISTS alembic_version"))
    cleanup_engine.dispose()

    config = _alembic_config(backend_root)
    command.upgrade(config, "head")
    engine = build_engine(database_url)
    try:
        with engine.connect() as connection:
            version_text = str(connection.execute(text("SELECT VERSION()")) .scalar_one())
            assert int(version_text.split(".", 1)[0]) >= 8
            assert connection.execute(text("SELECT @@session.time_zone")).scalar_one() == "+00:00"
        assert set(inspect(engine).get_table_names()) == EXPECTED_TABLES | {"alembic_version"}

        with Session(engine) as session:
            first = seed_default_plan(session)
            second = seed_default_plan(session)
            assert first["status"] == "created"
            assert second["status"] == "already_seeded"
            version = session.get(PlanVersion, first["plan_version_id"])
            assert isinstance(version.config_json, dict)
            assert version.config_json["fatigue_threshold"] == 8

            user = User(
                email="mysql-check@example.test",
                username="mysql_check",
                password_hash=hash_password("mysql-test-password"),
                display_name="MySQL Check",
            )
            session.add(user)
            session.flush()
            session.add(
                DailyReadiness(user_id=user.id, local_date=version.created_at.date(), fatigue=11)
            )
            # MySQL error 3819 is surfaced as OperationalError by PyMySQL,
            # while other drivers may classify a CHECK violation as
            # IntegrityError. Both are DBAPIError subclasses.
            with pytest.raises(DBAPIError):
                session.flush()
            session.rollback()

        command.check(config)
        engine.dispose()
        command.downgrade(config, "base")
        post_engine = create_engine(database_url)
        try:
            assert inspect(post_engine).get_table_names() == ["alembic_version"]
        finally:
            post_engine.dispose()
    finally:
        engine.dispose()

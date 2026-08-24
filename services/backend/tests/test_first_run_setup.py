from __future__ import annotations

import json
from dataclasses import asdict
from pathlib import Path
from typing import Any

import pytest
from fastapi import FastAPI
from fastapi.exceptions import RequestValidationError
from fastapi.testclient import TestClient
from sqlalchemy.engine import make_url

import app.api.setup as setup_api
import app.core.middleware as middleware_module
import app.services.first_run_setup as setup_service
from app.core.config import APPLICATION_DATABASE_NAME, RUNTIME_CONFIG_VERSION, settings
from app.core.errors import request_validation_exception_handler
from app.core.middleware import SetupRequiredMiddleware
from app.schemas.setup import DatabaseSetupRequest
from app.services.auth import LoginRateLimiter
from app.services.first_run_setup import (
    DatabaseDiscovery,
    DatabaseSetupResult,
    SetupInProgressError,
)


class _FakeConnection:
    def __init__(self, *, database_exists: bool) -> None:
        self.database_exists = database_exists
        self.executed_driver_sql: list[str] = []

    def __enter__(self) -> "_FakeConnection":
        return self

    def __exit__(self, *_args: object) -> None:
        return None

    def scalar(self, statement: object, _parameters: object = None) -> object:
        sql = str(statement)
        if "VERSION()" in sql:
            return "8.4.6"
        if "INFORMATION_SCHEMA.SCHEMATA" in sql and "COUNT" in sql:
            return 1 if self.database_exists else 0
        if "INFORMATION_SCHEMA.TABLES" in sql:
            return 7 if self.database_exists else 0
        if "DEFAULT_COLLATION_NAME" in sql:
            return "utf8mb4_0900_ai_ci"
        raise AssertionError(f"Unexpected SQL: {sql}")

    def exec_driver_sql(self, sql: str) -> None:
        self.executed_driver_sql.append(sql)


class _FakeEngine:
    def __init__(self, connection: _FakeConnection | None = None) -> None:
        self.connection = connection
        self.disposed = False

    def connect(self) -> _FakeConnection:
        assert self.connection is not None
        return self.connection

    def dispose(self) -> None:
        self.disposed = True


def test_database_setup_schema_never_accepts_a_database_name() -> None:
    fields = DatabaseSetupRequest.model_fields

    assert "database" not in fields
    assert "database_name" not in fields
    assert APPLICATION_DATABASE_NAME == "fitness"


def test_database_discovery_creates_only_the_fixed_schema() -> None:
    connection = _FakeConnection(database_exists=False)
    result = setup_service._inspect_or_create_database(_FakeEngine(connection))  # noqa: SLF001

    assert result.database_created is True
    assert result.mysql_version == "8.4.6"
    assert result.existing_table_count == 0
    assert connection.executed_driver_sql == [
        "CREATE DATABASE `fitness` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci"
    ]


def test_existing_fixed_schema_is_inspected_without_create() -> None:
    connection = _FakeConnection(database_exists=True)
    result = setup_service._inspect_or_create_database(_FakeEngine(connection))  # noqa: SLF001

    assert result.database_created is False
    assert result.existing_table_count == 7
    assert result.database_collation == "utf8mb4_0900_ai_ci"
    assert connection.executed_driver_sql == []


def test_private_runtime_config_is_atomic_and_contains_no_alternate_schema(
    tmp_path: Path,
    monkeypatch,
) -> None:
    runtime_path = tmp_path / "private" / "backend-config.json"
    monkeypatch.setattr(settings, "runtime_config_path", runtime_path)

    setup_service._atomic_write_runtime_config(  # noqa: SLF001
        host="mysql.internal",
        port=3307,
        username="fitness_app",
        password="Very-Private-Database-Password!",
        jwt_secret="A-Generated-JWT-Secret-That-Is-Longer-Than-32-Characters",
    )

    payload = json.loads(runtime_path.read_text(encoding="utf-8"))
    assert payload["version"] == RUNTIME_CONFIG_VERSION
    assert payload["database"]["name"] == APPLICATION_DATABASE_NAME
    assert payload["database"]["password"] == "Very-Private-Database-Password!"
    assert list(runtime_path.parent.glob("*.tmp")) == []


def test_generated_setup_token_is_stable_and_private_across_reads(
    tmp_path: Path,
    monkeypatch,
) -> None:
    runtime_path = tmp_path / "private" / "backend-config.json"
    monkeypatch.setattr(settings, "runtime_config_path", runtime_path)
    monkeypatch.setattr(settings, "setup_token", "")
    monkeypatch.delenv("SETUP_TOKEN", raising=False)

    first = setup_service.ensure_setup_token()
    settings.setup_token = ""  # simulate a second worker/process reading the file
    second = setup_service.ensure_setup_token()

    assert first == second
    assert len(first) >= 32
    assert runtime_path.with_name("setup-token").read_text(encoding="utf-8").strip() == first


def test_setup_middleware_blocks_business_api_but_keeps_wizard_available(monkeypatch) -> None:
    application = FastAPI()

    @application.get("/api/v1/private")
    def private() -> dict[str, bool]:
        return {"ok": True}

    @application.get("/api/v1/setup/status")
    def status() -> dict[str, bool]:
        return {"setup_required": True}

    application.add_middleware(SetupRequiredMiddleware)
    monkeypatch.setattr(middleware_module, "is_database_configured", lambda: False)

    with TestClient(application) as client:
        blocked = client.get("/api/v1/private")
        allowed = client.get("/api/v1/setup/status")

    assert blocked.status_code == 503
    assert blocked.json()["detail"]["code"] == "setup_required"
    assert allowed.status_code == 200


def test_setup_status_uses_deployment_defaults_without_leaking_configured_host(
    monkeypatch,
) -> None:
    application = FastAPI()
    application.include_router(setup_api.router, prefix="/api/v1")
    state = {"configured": False}
    monkeypatch.setattr(
        setup_api,
        "is_database_configured",
        lambda: state["configured"],
    )
    monkeypatch.setattr(settings, "mysql_host", "mysql")
    monkeypatch.setattr(settings, "mysql_port", 3307)
    monkeypatch.setattr(settings, "mysql_user", "native_setup")

    with TestClient(application) as client:
        compose_first_run = client.get("/api/v1/setup/status")
        settings.mysql_host = "native-db.internal"
        native_first_run = client.get("/api/v1/setup/status")
        state["configured"] = True
        configured = client.get("/api/v1/setup/status")

    assert compose_first_run.status_code == 200
    assert compose_first_run.json()["default_host"] == "mysql"
    assert native_first_run.status_code == 200
    assert native_first_run.json()["default_host"] == "127.0.0.1"
    assert native_first_run.json()["default_port"] == 3306
    assert native_first_run.json()["default_username"] == "fitness"
    assert configured.status_code == 200
    assert configured.json()["default_host"] == "127.0.0.1"
    assert configured.json()["default_port"] == 3306
    assert configured.json()["default_username"] == "fitness"


def test_setup_api_initializes_without_returning_credentials(monkeypatch) -> None:
    application = FastAPI()
    application.include_router(setup_api.router, prefix="/api/v1")
    result = DatabaseSetupResult(
        database_created=False,
        mysql_version="8.4.6",
        database_collation="utf8mb4_0900_ai_ci",
        existing_table_count=4,
        table_count=31,
        alembic_revision="20260823_0002",
        seed_status="already_seeded",
    )
    captured: dict[str, Any] = {}

    def initialize(**kwargs: Any) -> DatabaseSetupResult:
        captured.update(kwargs)
        return result

    monkeypatch.setattr(setup_api, "is_database_configured", lambda: False)
    monkeypatch.setattr(setup_api, "ensure_setup_token", lambda: "one-time-setup-token-123456789")
    monkeypatch.setattr(setup_api, "initialize_database", initialize)
    monkeypatch.setattr(setup_api, "setup_rate_limiter", LoginRateLimiter(10))

    with TestClient(application) as client:
        response = client.post(
            "/api/v1/setup/database",
            json={
                "host": "mysql.internal",
                "port": 3306,
                "username": "fitness_app",
                "password": "database-secret-value",
                "setup_token": "one-time-setup-token-123456789",
            },
        )

    assert response.status_code == 201, response.text
    assert captured["password"] == "database-secret-value"
    assert captured["host"] == "mysql.internal"
    serialized = response.text
    assert "database-secret-value" not in serialized
    assert "one-time-setup-token" not in serialized
    assert response.json() == {
        "configured": True,
        "setup_required": False,
        "database_name": "fitness",
        **asdict(result),
    }


def test_setup_api_rejects_wrong_one_time_token(monkeypatch) -> None:
    application = FastAPI()
    application.include_router(setup_api.router, prefix="/api/v1")
    monkeypatch.setattr(setup_api, "is_database_configured", lambda: False)
    monkeypatch.setattr(setup_api, "ensure_setup_token", lambda: "correct-token-value-123456789")
    monkeypatch.setattr(setup_api, "setup_rate_limiter", LoginRateLimiter(10))

    with TestClient(application) as client:
        response = client.post(
            "/api/v1/setup/database",
            json={
                "host": "mysql",
                "port": 3306,
                "username": "fitness",
                "password": "database-secret-value",
                "setup_token": "wrong-token-value-123456789",
            },
        )

    assert response.status_code == 403
    assert response.json()["detail"]["code"] == "invalid_setup_token"
    assert "database-secret-value" not in response.text


def test_setup_api_is_permanently_closed_after_configuration(monkeypatch) -> None:
    application = FastAPI()
    application.include_router(setup_api.router, prefix="/api/v1")
    monkeypatch.setattr(setup_api, "is_database_configured", lambda: True)

    with TestClient(application) as client:
        response = client.post(
            "/api/v1/setup/database",
            json={
                "host": "attacker.invalid",
                "port": 3306,
                "username": "attacker",
                "password": "must-never-be-used",
                "setup_token": "must-never-be-checked-123456789",
            },
        )

    assert response.status_code == 409
    assert response.json()["detail"]["code"] == "setup_complete"
    assert "must-never" not in response.text


def test_setup_api_handles_non_ascii_token_without_leaking_password(monkeypatch) -> None:
    application = FastAPI()
    application.include_router(setup_api.router, prefix="/api/v1")
    monkeypatch.setattr(setup_api, "is_database_configured", lambda: False)
    monkeypatch.setattr(setup_api, "ensure_setup_token", lambda: "correct-token-value-123456789")
    monkeypatch.setattr(setup_api, "setup_rate_limiter", LoginRateLimiter(10))

    with TestClient(application) as client:
        response = client.post(
            "/api/v1/setup/database",
            json={
                "host": "mysql",
                "port": 3306,
                "username": "fitness",
                "password": "database-secret-value",
                "setup_token": "错误的初始化码",
            },
        )

    assert response.status_code == 403
    assert response.json()["detail"]["code"] == "invalid_setup_token"
    assert "database-secret-value" not in response.text


def test_request_validation_never_reflects_malformed_secret_values() -> None:
    application = FastAPI()
    application.add_exception_handler(
        RequestValidationError,
        request_validation_exception_handler,
    )
    application.include_router(setup_api.router, prefix="/api/v1")

    with TestClient(application) as client:
        response = client.post(
            "/api/v1/setup/database",
            json={
                "host": "mysql",
                "port": 3306,
                "username": "fitness",
                "password": ["malformed-database-secret"],
                "setup_token": "unused-token-value-123456789",
            },
        )

    assert response.status_code == 422
    assert "malformed-database-secret" not in response.text
    assert all("input" not in item for item in response.json()["detail"])


def test_initialize_database_orchestration_uses_fixed_url_and_persists_last(
    monkeypatch,
) -> None:
    server_engine = _FakeEngine()
    target_engine = _FakeEngine()
    events: list[str] = []
    persisted: dict[str, Any] = {}
    activated: dict[str, Any] = {}

    monkeypatch.setattr(setup_service, "is_database_configured", lambda: False)
    monkeypatch.setattr(setup_service, "_server_engine", lambda **_kwargs: server_engine)
    monkeypatch.setattr(
        setup_service,
        "_inspect_or_create_database",
        lambda _engine: DatabaseDiscovery("8.4.6", False, "utf8mb4_0900_ai_ci", 12),
    )
    monkeypatch.setattr(setup_service, "build_engine", lambda _url: target_engine)
    monkeypatch.setattr(setup_service, "_run_migrations", lambda _url: events.append("migrate"))
    monkeypatch.setattr(
        setup_service,
        "_seed_database",
        lambda _engine: events.append("seed") or {"status": "already_seeded"},
    )
    monkeypatch.setattr(setup_service, "_database_summary", lambda _engine: (31, "revision-head"))
    monkeypatch.setattr(
        setup_service,
        "_atomic_write_runtime_config",
        lambda **kwargs: (events.append("persist"), persisted.update(kwargs)),
    )
    monkeypatch.setattr(
        setup_service,
        "_activate_runtime_configuration",
        lambda **kwargs: (events.append("activate"), activated.update(kwargs)),
    )

    result = setup_service.initialize_database(
        host="mysql.internal",
        port=3307,
        username="fitness_app",
        password="p@ss:/?#%+",
    )

    assert events == ["migrate", "seed", "persist", "activate"]
    parsed = make_url(activated["database_url"])
    assert parsed.database == APPLICATION_DATABASE_NAME
    assert parsed.password == "p@ss:/?#%+"
    assert persisted["password"] == "p@ss:/?#%+"
    assert result.table_count == 31
    assert server_engine.disposed is True
    assert target_engine.disposed is True


def test_initialize_database_rejects_a_concurrent_attempt(monkeypatch) -> None:
    monkeypatch.setattr(setup_service, "is_database_configured", lambda: False)
    assert setup_service._initialization_lock.acquire(blocking=False)  # noqa: SLF001
    try:
        with pytest.raises(SetupInProgressError):
            setup_service.initialize_database(
                host="mysql",
                port=3306,
                username="fitness",
                password="unused-password",
            )
    finally:
        setup_service._initialization_lock.release()  # noqa: SLF001

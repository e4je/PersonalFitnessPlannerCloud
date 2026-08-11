from __future__ import annotations

from pathlib import Path

import pytest
import yaml
from pydantic import ValidationError
from sqlalchemy.engine import make_url

from app.core.config import VERSION_CONTRACT, Settings, _discover_env_files


def test_dotenv_discovery_supports_monorepo_and_shallow_container_paths() -> None:
    monorepo_backend = Path("/workspace/services/backend")
    assert _discover_env_files(monorepo_backend) == (
        str(monorepo_backend / ".env"),
        str(Path("/workspace/.env")),
    )
    assert _discover_env_files(Path("/app")) == (str(Path("/app/.env")),)


def test_runtime_versions_are_loaded_from_the_vendored_contract() -> None:
    config = Settings(_env_file=None)

    assert config.api_version == VERSION_CONTRACT["api_version"]
    assert config.schema_version == VERSION_CONTRACT["schema_version"]
    assert config.minimum_client_version == VERSION_CONTRACT["minimum_client_version"]


def test_mysql_fields_build_url_without_corrupting_reserved_password() -> None:
    password = "p@ss:/?#%+ with spaces"

    config = Settings(
        _env_file=None,
        database_url="",
        mysql_host="mysql",
        mysql_port=3307,
        mysql_user="fitness",
        mysql_password=password,
        mysql_database="fitness_test",
    )

    parsed = make_url(config.database_url)
    assert parsed.drivername == "mysql+pymysql"
    assert parsed.username == "fitness"
    assert parsed.password == password
    assert parsed.host == "mysql"
    assert parsed.port == 3307
    assert parsed.database == "fitness_test"
    assert parsed.query["charset"] == "utf8mb4"


def test_explicit_database_url_takes_precedence(monkeypatch) -> None:
    explicit_url = "sqlite+pysqlite:///:memory:"
    monkeypatch.setenv("DATABASE_URL", explicit_url)
    monkeypatch.setenv("MYSQL_PASSWORD", "would@otherwise:need/encoding")

    config = Settings(_env_file=None)

    assert config.database_url == explicit_url


def test_compose_injects_runtime_tuning_without_persistent_admin_secret() -> None:
    backend_root = Path(__file__).resolve().parents[1]
    compose = yaml.safe_load((backend_root / "docker-compose.yml").read_text(encoding="utf-8"))
    environment = compose["services"]["backend"]["environment"]

    expected = {
        "DATABASE_URL",
        "MYSQL_HOST",
        "MYSQL_PORT",
        "MYSQL_DATABASE",
        "MYSQL_USER",
        "MYSQL_PASSWORD",
        "JWT_SECRET",
        "JWT_ALGORITHM",
        "ACCESS_TOKEN_MINUTES",
        "REFRESH_TOKEN_DAYS",
        "CORS_ORIGINS",
        "LOG_LEVEL",
        "SQL_ECHO",
        "MAX_REQUEST_BODY_BYTES",
        "LOGIN_ATTEMPTS_PER_MINUTE",
        "SYNC_RETENTION_DAYS",
    }
    assert expected <= set(environment)
    assert not {"ADMIN_EMAIL", "ADMIN_PASSWORD", "ADMIN_DISPLAY_NAME"} & set(environment)
    assert environment["MYSQL_PASSWORD"].startswith("${MYSQL_PASSWORD:?")
    assert environment["JWT_SECRET"].startswith("${JWT_SECRET:?")

    mysql_environment = compose["services"]["mysql"]["environment"]
    assert mysql_environment["MYSQL_PASSWORD"].startswith("${MYSQL_PASSWORD:?")
    assert mysql_environment["MYSQL_ROOT_PASSWORD"].startswith("${MYSQL_ROOT_PASSWORD:?")

    alembic = (backend_root / "alembic.ini").read_text(encoding="utf-8")
    assert "sqlalchemy.url =\n" in alembic

    entrypoint = (backend_root / "docker-entrypoint.sh").read_text(encoding="utf-8")
    assert "scripts.create_admin" not in entrypoint
    assert "ADMIN_PASSWORD" not in entrypoint


@pytest.mark.parametrize(
    ("jwt_secret", "mysql_password"),
    [
        ("replace-" + "with-at-least-32-random-characters", "Strong-Db-Pass-2026!"),
        ("Strong-JWT-Signing-Key-For-Production-2026!", "fitness"),
        ("Strong-JWT-Signing-Key-For-Production-2026!", "fitness-local-" + "password"),
        ("Strong-JWT-Signing-Key-For-Production-2026!", "replace-" + "with-db-secret"),
    ],
)
def test_production_rejects_example_and_known_development_credentials(
    jwt_secret: str,
    mysql_password: str,
) -> None:
    with pytest.raises(ValidationError):
        Settings(
            _env_file=None,
            environment="production",
            database_url="",
            mysql_user="fitness",
            mysql_password=mysql_password,
            jwt_secret=jwt_secret,
        )


def test_production_accepts_explicit_strong_credentials() -> None:
    config = Settings(
        _env_file=None,
        environment="production",
        database_url="",
        mysql_user="fitness",
        mysql_password="Strong-Database-Password-2026!",
        jwt_secret="Strong-JWT-Signing-Key-For-Production-2026!",
        cors_origins=["https://fitness.example.test"],
    )

    assert make_url(config.database_url).password == "Strong-Database-Password-2026!"

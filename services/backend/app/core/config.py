from __future__ import annotations

import json
import os
import secrets
from functools import lru_cache
from pathlib import Path
from typing import Literal

from pydantic import Field, field_validator, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict
from sqlalchemy.engine import URL, make_url


_PLACEHOLDER_TOKENS = (
    "change-me",
    "development-only",
    "example",
    "placeholder",
    "replace-with",
)

_BACKEND_ROOT = Path(__file__).resolve().parents[2]
APPLICATION_DATABASE_NAME = "fitness"
RUNTIME_CONFIG_VERSION = 1
LOCAL_DATABASE_FILENAME = "fitness.db"
LOCAL_JWT_SECRET_FILENAME = "jwt-secret"


def _discover_env_files(backend_root: Path) -> tuple[str, ...]:
    """Find dotenv files without assuming the container has monorepo parents."""

    files = [backend_root / ".env"]
    repository_root = backend_root.parent.parent
    if repository_root / "services" / "backend" == backend_root:
        # The repository-level file is authoritative in the unified checkout.
        files.append(repository_root / ".env")
    return tuple(str(path) for path in files)


_ENV_FILES = _discover_env_files(_BACKEND_ROOT)


def _load_runtime_config(path: Path) -> dict[str, object] | None:
    """Load the private first-run configuration, failing closed if it is damaged."""

    if not path.is_file():
        return None
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
        database = payload["database"]
        if not isinstance(payload, dict) or payload.get("version") != RUNTIME_CONFIG_VERSION:
            raise ValueError("unsupported runtime configuration version")
        if not isinstance(database, dict):
            raise ValueError("database configuration must be an object")
        host = database["host"]
        port = database["port"]
        username = database["username"]
        password = database["password"]
        database_name = database["name"]
        jwt_secret = payload["jwt_secret"]
        if not isinstance(host, str) or not host.strip():
            raise ValueError("database host is invalid")
        if isinstance(port, bool) or not isinstance(port, int) or not 1 <= port <= 65535:
            raise ValueError("database port is invalid")
        if not isinstance(username, str) or not username.strip():
            raise ValueError("database username is invalid")
        if not isinstance(password, str) or not password:
            raise ValueError("database password is invalid")
        if database_name != APPLICATION_DATABASE_NAME:
            raise ValueError("database name does not match the application database")
        if not isinstance(jwt_secret, str) or len(jwt_secret) < 32:
            raise ValueError("JWT secret is invalid")
    except (OSError, KeyError, TypeError, ValueError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"Invalid private runtime configuration: {path}") from exc
    return payload


def build_mysql_database_url(
    *,
    host: str,
    port: int,
    username: str,
    password: str,
    database: str = APPLICATION_DATABASE_NAME,
) -> str:
    """Build a safely escaped PyMySQL URL from discrete trusted fields."""

    return URL.create(
        drivername="mysql+pymysql",
        username=username,
        password=password,
        host=host,
        port=port,
        database=database,
        query={"charset": "utf8mb4"},
    ).render_as_string(hide_password=False)


def build_sqlite_database_url(path: Path) -> str:
    """Build an absolute SQLite URL without hand-escaping platform paths."""

    resolved_path = path.expanduser()
    if not resolved_path.is_absolute():
        resolved_path = (_BACKEND_ROOT / resolved_path).resolve()
    return URL.create(
        drivername="sqlite+pysqlite",
        database=str(resolved_path),
    ).render_as_string(hide_password=False)


def _load_or_create_local_jwt_secret(path: Path) -> str:
    """Persist a stable signing key for the zero-configuration SQLite mode."""

    try:
        path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        if path.is_file():
            secret = path.read_text(encoding="utf-8").strip()
        else:
            secret = secrets.token_urlsafe(48)
            try:
                descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            except FileExistsError:
                secret = path.read_text(encoding="utf-8").strip()
            else:
                with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
                    stream.write(secret + "\n")
                    stream.flush()
                    os.fsync(stream.fileno())
        if len(secret) < 32 or _is_placeholder(secret):
            raise ValueError("stored JWT secret is invalid")
        os.chmod(path, 0o600)
        return secret
    except (OSError, ValueError) as exc:
        raise RuntimeError(
            f"Cannot securely persist the local JWT signing key: {path}"
        ) from exc


def _load_version_contract() -> dict[str, str]:
    path = Path(__file__).resolve().parents[2] / "contracts" / "schema-version.json"
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
        return {
            key: str(payload[key])
            for key in ("schema_version", "api_version", "minimum_client_version")
        }
    except (OSError, KeyError, TypeError, ValueError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"Invalid version contract: {path}") from exc


VERSION_CONTRACT = _load_version_contract()


def _is_placeholder(value: str, *, username: str | None = None) -> bool:
    normalized = value.strip().casefold()
    return (
        not normalized
        or (username is not None and normalized == username.strip().casefold())
        or normalized.endswith("-local-password")
        or any(token in normalized for token in _PLACEHOLDER_TOKENS)
    )


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        # The unified repository owns one root .env.  Using an absolute path
        # keeps CLI commands reliable regardless of their working directory.
        env_file=_ENV_FILES,
        env_file_encoding="utf-8",
        case_sensitive=False,
        extra="ignore",
    )

    app_name: str = "Personal Fitness Planner API"
    environment: Literal["development", "test", "production"] = "development"
    api_v1_prefix: str = "/api/v1"
    api_version: str = VERSION_CONTRACT["api_version"]
    schema_version: str = VERSION_CONTRACT["schema_version"]
    minimum_client_version: str = VERSION_CONTRACT["minimum_client_version"]
    # SQLite is the zero-configuration default. Set DATABASE_BACKEND=mysql to
    # retain the optional first-run MySQL wizard, or provide DATABASE_URL for a
    # fully explicit connection. Existing private MySQL runtime configs continue
    # to take precedence so upgrades never silently abandon server data.
    database_backend: Literal["sqlite", "mysql"] = "sqlite"
    database_url: str = Field(default="", repr=False)
    sqlite_database_path: Path | None = None
    mysql_host: str = "127.0.0.1"
    mysql_port: int = Field(default=3306, ge=1, le=65535)
    mysql_user: str = "fitness"
    mysql_password: str = Field(default="", repr=False)
    mysql_database: str = APPLICATION_DATABASE_NAME
    sql_echo: bool = False

    jwt_secret: str = Field(default="", repr=False)
    jwt_algorithm: str = "HS256"
    access_token_minutes: int = Field(default=15, ge=1, le=1440)
    refresh_token_days: int = Field(default=30, ge=1, le=365)

    cors_origins: list[str] = ["http://localhost", "http://127.0.0.1"]
    # Keep resource-protection settings bounded even when they come from an
    # operator-controlled environment variable.  An accidentally huge value
    # here would otherwise turn a deployment configuration mistake into a
    # straightforward memory/CPU denial of service.
    max_request_body_bytes: int = Field(default=2_097_152, ge=1024, le=64 * 1024 * 1024)
    login_attempts_per_minute: int = Field(default=10, ge=1, le=10_000)
    sync_retention_days: int = Field(default=90, ge=1, le=3_650)
    log_level: str = "INFO"
    runtime_config_path: Path = _BACKEND_ROOT / ".runtime" / "backend-config.json"
    setup_token: str = Field(default="", repr=False)

    @field_validator("jwt_algorithm", mode="before")
    @classmethod
    def validate_jwt_algorithm(cls, value: object) -> str:
        normalized = str(value).strip().upper()
        if normalized not in {"HS256", "HS384", "HS512"}:
            raise ValueError("JWT_ALGORITHM must be one of HS256, HS384, or HS512")
        return normalized

    @field_validator("cors_origins", mode="before")
    @classmethod
    def parse_cors_origins(cls, value: object) -> object:
        if isinstance(value, str) and not value.lstrip().startswith("["):
            return [part.strip() for part in value.split(",") if part.strip()]
        return value

    @field_validator("sqlite_database_path", mode="before")
    @classmethod
    def parse_optional_sqlite_database_path(cls, value: object) -> object:
        if isinstance(value, str) and not value.strip():
            return None
        return value

    @model_validator(mode="after")
    def resolve_database_url_and_validate_production(self) -> "Settings":
        configured_versions = {
            "api_version": self.api_version,
            "schema_version": self.schema_version,
            "minimum_client_version": self.minimum_client_version,
        }
        if configured_versions != VERSION_CONTRACT:
            raise ValueError(
                "API/schema versions must match contracts/schema-version.json"
            )
        explicit_database_url = self.database_url.strip()
        runtime_config = _load_runtime_config(self.runtime_config_path)
        runtime_database_loaded = False
        if runtime_config is not None and not self.jwt_secret.strip():
            self.jwt_secret = str(runtime_config["jwt_secret"])
        if runtime_config is not None and not explicit_database_url and not self.mysql_password:
            database = runtime_config["database"]
            assert isinstance(database, dict)  # validated by _load_runtime_config
            self.mysql_host = str(database["host"])
            self.mysql_port = int(database["port"])
            self.mysql_user = str(database["username"])
            self.mysql_password = str(database["password"])
            self.mysql_database = APPLICATION_DATABASE_NAME
            runtime_database_loaded = True

        explicit_database_url = self.database_url.strip()
        legacy_mysql_environment = (
            bool(self.mysql_password)
            and "database_backend" not in self.model_fields_set
        )
        if not explicit_database_url and self.mysql_password and (
            runtime_database_loaded
            or self.database_backend == "mysql"
            or legacy_mysql_environment
        ):
            self.database_url = build_mysql_database_url(
                host=self.mysql_host,
                port=self.mysql_port,
                username=self.mysql_user,
                password=self.mysql_password,
                database=self.mysql_database,
            )
        elif not explicit_database_url and self.database_backend == "sqlite":
            local_database_path = self.sqlite_database_path or self.runtime_config_path.with_name(
                LOCAL_DATABASE_FILENAME
            )
            self.sqlite_database_path = local_database_path
            self.database_url = build_sqlite_database_url(local_database_path)
        elif not explicit_database_url:
            # Explicit MySQL mode keeps the secured first-run Web wizard
            # available for operators who do not want the local SQLite file.
            self.database_url = ""
            self.mysql_database = APPLICATION_DATABASE_NAME

        database_configured = bool(self.database_url.strip())
        configured_url = make_url(self.database_url) if database_configured else None
        if configured_url is not None and configured_url.get_backend_name() not in {
            "mysql",
            "sqlite",
        }:
            raise ValueError("DATABASE_URL must use MySQL or SQLite")
        if configured_url is not None and configured_url.get_backend_name() == "sqlite":
            database_path = configured_url.database
            if not database_path:
                raise ValueError("SQLite DATABASE_URL must identify a database file")
            if database_path != ":memory:" and not database_path.startswith("file:"):
                try:
                    Path(database_path).expanduser().parent.mkdir(
                        parents=True,
                        exist_ok=True,
                        mode=0o700,
                    )
                except OSError as exc:
                    raise ValueError("SQLite database directory is not writable") from exc
        if (
            configured_url is not None
            and configured_url.get_backend_name() == "sqlite"
            and not self.jwt_secret.strip()
        ):
            self.jwt_secret = _load_or_create_local_jwt_secret(
                self.runtime_config_path.with_name(LOCAL_JWT_SECRET_FILENAME)
            )
        if database_configured and not self.jwt_secret.strip():
            raise ValueError("JWT_SECRET must be provided when the database is configured")
        if self.environment == "production":
            if self.jwt_secret and (
                len(self.jwt_secret) < 32 or _is_placeholder(self.jwt_secret)
            ):
                raise ValueError("JWT_SECRET must be a strong, non-default value in production")
            if configured_url is not None and configured_url.get_backend_name() == "mysql":
                database_password = configured_url.password
                database_username = configured_url.username
                if database_password is None or _is_placeholder(
                    database_password,
                    username=database_username,
                ):
                    raise ValueError(
                        "The application database password must be a non-placeholder value in production"
                    )
            if self.setup_token and (
                len(self.setup_token) < 24 or _is_placeholder(self.setup_token)
            ):
                raise ValueError("SETUP_TOKEN must be strong when provided in production")
            if "*" in self.cors_origins:
                raise ValueError("Wildcard CORS is forbidden in production")
        return self

    @property
    def database_configured(self) -> bool:
        return bool(self.database_url.strip())


@lru_cache
def get_settings() -> Settings:
    return Settings()


settings = get_settings()

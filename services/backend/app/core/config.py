from __future__ import annotations

import json
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


def _discover_env_files(backend_root: Path) -> tuple[str, ...]:
    """Find dotenv files without assuming the container has monorepo parents."""

    files = [backend_root / ".env"]
    repository_root = backend_root.parent.parent
    if repository_root / "services" / "backend" == backend_root:
        # The repository-level file is authoritative in the unified checkout.
        files.append(repository_root / ".env")
    return tuple(str(path) for path in files)


_ENV_FILES = _discover_env_files(_BACKEND_ROOT)


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
    # DATABASE_URL is an optional escape hatch for managed databases and tests.
    # When it is empty, build it from discrete fields so reserved characters in
    # MYSQL_PASSWORD are percent-encoded instead of being parsed as URL syntax.
    database_url: str = ""
    mysql_host: str = "127.0.0.1"
    mysql_port: int = Field(default=3306, ge=1, le=65535)
    mysql_user: str = "fitness"
    mysql_password: str = ""
    mysql_database: str = "fitness"
    sql_echo: bool = False

    jwt_secret: str = ""
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
        if not self.jwt_secret.strip():
            raise ValueError("JWT_SECRET must be provided explicitly")
        if not explicit_database_url and not self.mysql_password:
            raise ValueError(
                "MYSQL_PASSWORD must be provided when DATABASE_URL is not set"
            )
        if not explicit_database_url:
            self.database_url = URL.create(
                drivername="mysql+pymysql",
                username=self.mysql_user,
                password=self.mysql_password,
                host=self.mysql_host,
                port=self.mysql_port,
                database=self.mysql_database,
                query={"charset": "utf8mb4"},
            ).render_as_string(hide_password=False)
        if self.environment == "production":
            if len(self.jwt_secret) < 32 or _is_placeholder(self.jwt_secret):
                raise ValueError("JWT_SECRET must be a strong, non-default value in production")
            database_password = (
                make_url(explicit_database_url).password
                if explicit_database_url
                else self.mysql_password
            )
            database_username = (
                make_url(explicit_database_url).username
                if explicit_database_url
                else self.mysql_user
            )
            if database_password is None or _is_placeholder(
                database_password,
                username=database_username,
            ):
                raise ValueError(
                    "The application database password must be a non-placeholder value in production"
                )
            if "*" in self.cors_origins:
                raise ValueError("Wildcard CORS is forbidden in production")
        return self


@lru_cache
def get_settings() -> Settings:
    return Settings()


settings = get_settings()

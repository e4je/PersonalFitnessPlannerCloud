from __future__ import annotations

import json
import logging
import os
import secrets
import tempfile
from dataclasses import dataclass
from pathlib import Path
from threading import Lock
from time import sleep
from typing import Any

from alembic import command
from alembic.config import Config
from sqlalchemy import Engine, URL, create_engine, text
from sqlalchemy.exc import SQLAlchemyError
from sqlalchemy.orm import Session
from sqlalchemy.pool import NullPool

from app.core.config import (
    APPLICATION_DATABASE_NAME,
    RUNTIME_CONFIG_VERSION,
    build_mysql_database_url,
    settings,
)
from app.db.session import build_engine, configure_database, is_database_configured
from app.seed.default_plan import seed_default_plan


logger = logging.getLogger("app.setup")
_setup_token_lock = Lock()
_initialization_lock = Lock()


class SetupError(RuntimeError):
    pass


class SetupAlreadyConfiguredError(SetupError):
    pass


class SetupInProgressError(SetupError):
    pass


class SetupConnectionError(SetupError):
    pass


class SetupInitializationError(SetupError):
    pass


class SetupPersistenceError(SetupError):
    pass


@dataclass(frozen=True, slots=True)
class DatabaseDiscovery:
    mysql_version: str
    database_created: bool
    database_collation: str | None
    existing_table_count: int


@dataclass(frozen=True, slots=True)
class DatabaseSetupResult:
    database_created: bool
    mysql_version: str
    database_collation: str | None
    existing_table_count: int
    table_count: int
    alembic_revision: str
    seed_status: str


def _setup_token_path() -> Path:
    return settings.runtime_config_path.with_name("setup-token")


def _read_setup_token(path: Path) -> str:
    token = path.read_text(encoding="utf-8").strip()
    if len(token) < 24:
        raise SetupPersistenceError("The stored setup token is invalid")
    return token


def ensure_setup_token() -> str:
    """Return one stable, private token across restarts and worker processes."""

    if settings.setup_token:
        return settings.setup_token
    with _setup_token_lock:
        if settings.setup_token:
            return settings.setup_token
        path = _setup_token_path()
        try:
            if path.is_file():
                token = _read_setup_token(path)
            else:
                path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
                token = secrets.token_urlsafe(32)
                try:
                    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
                except FileExistsError:
                    # Another worker may still be flushing the newly created
                    # token. Retry briefly before treating the file as damaged.
                    for _attempt in range(5):
                        try:
                            token = _read_setup_token(path)
                            break
                        except (OSError, SetupPersistenceError):
                            sleep(0.02)
                    else:
                        token = _read_setup_token(path)
                else:
                    with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
                        stream.write(token + "\n")
                        stream.flush()
                        os.fsync(stream.fileno())
            os.chmod(path, 0o600)
        except (OSError, SetupPersistenceError) as exc:
            raise SetupPersistenceError(
                "The backend data directory is not writable; setup cannot persist securely"
            ) from exc
        settings.setup_token = token
        return token


def announce_first_run_setup() -> None:
    token = ensure_setup_token()
    # This is deliberately the only place the one-time token is disclosed. It
    # is never returned by an HTTP endpoint and becomes unusable after setup.
    logger.warning(
        "first_run_setup_required database=%s setup_token=%s",
        APPLICATION_DATABASE_NAME,
        token,
    )


def _server_engine(*, host: str, port: int, username: str, password: str) -> Engine:
    server_url = URL.create(
        drivername="mysql+pymysql",
        username=username,
        password=password,
        host=host,
        port=port,
        query={"charset": "utf8mb4"},
    )
    return create_engine(
        server_url,
        poolclass=NullPool,
        pool_pre_ping=True,
        isolation_level="AUTOCOMMIT",
        connect_args={"connect_timeout": 10, "read_timeout": 30, "write_timeout": 30},
    )


def _inspect_or_create_database(server_engine: Engine) -> DatabaseDiscovery:
    """Discover or create only the hard-coded application schema."""

    with server_engine.connect() as connection:
        mysql_version = str(connection.scalar(text("SELECT VERSION()")) or "unknown")
        database_exists = bool(
            connection.scalar(
                text(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.SCHEMATA "
                    "WHERE SCHEMA_NAME = :database_name"
                ),
                {"database_name": APPLICATION_DATABASE_NAME},
            )
        )
        if not database_exists:
            # The identifier is a source-code constant and never includes user
            # input. MySQL does not support binding schema identifiers.
            connection.exec_driver_sql(
                "CREATE DATABASE `fitness` CHARACTER SET utf8mb4 "
                "COLLATE utf8mb4_0900_ai_ci"
            )
        existing_table_count = int(
            connection.scalar(
                text(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                    "WHERE TABLE_SCHEMA = :database_name"
                ),
                {"database_name": APPLICATION_DATABASE_NAME},
            )
            or 0
        )
        database_collation = connection.scalar(
            text(
                "SELECT DEFAULT_COLLATION_NAME FROM INFORMATION_SCHEMA.SCHEMATA "
                "WHERE SCHEMA_NAME = :database_name"
            ),
            {"database_name": APPLICATION_DATABASE_NAME},
        )
    return DatabaseDiscovery(
        mysql_version=mysql_version,
        database_created=not database_exists,
        database_collation=str(database_collation) if database_collation else None,
        existing_table_count=existing_table_count,
    )


def _run_migrations(database_url: str) -> None:
    backend_root = Path(__file__).resolve().parents[2]
    config = Config(str(backend_root / "alembic.ini"))
    # Passing the URL through attributes avoids interpolation of percent-encoded
    # credentials and keeps this one setup attempt independent of global state.
    config.attributes["database_url"] = database_url
    command.upgrade(config, "head")


def _seed_database(target_engine: Engine) -> dict[str, Any]:
    with Session(target_engine) as database:
        return seed_default_plan(database)


def _database_summary(target_engine: Engine) -> tuple[int, str]:
    with target_engine.connect() as connection:
        table_count = int(
            connection.scalar(
                text(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                    "WHERE TABLE_SCHEMA = :database_name"
                ),
                {"database_name": APPLICATION_DATABASE_NAME},
            )
            or 0
        )
        revision = connection.scalar(text("SELECT version_num FROM alembic_version LIMIT 1"))
    if not revision:
        raise SetupInitializationError("Database migration revision is unavailable")
    return table_count, str(revision)


def _atomic_write_runtime_config(
    *,
    host: str,
    port: int,
    username: str,
    password: str,
    jwt_secret: str,
) -> None:
    path = settings.runtime_config_path
    payload = {
        "version": RUNTIME_CONFIG_VERSION,
        "database": {
            "host": host,
            "port": port,
            "username": username,
            "password": password,
            "name": APPLICATION_DATABASE_NAME,
        },
        "jwt_secret": jwt_secret,
    }
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.",
        suffix=".tmp",
        dir=path.parent,
        text=True,
    )
    temporary_path = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            os.chmod(temporary_path, 0o600)
            json.dump(payload, stream, ensure_ascii=False, sort_keys=True, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_path, path)
        os.chmod(path, 0o600)
    finally:
        if temporary_path.exists():
            temporary_path.unlink()


def _activate_runtime_configuration(
    *,
    database_url: str,
    host: str,
    port: int,
    username: str,
    password: str,
    jwt_secret: str,
) -> None:
    settings.database_url = database_url
    settings.mysql_host = host
    settings.mysql_port = port
    settings.mysql_user = username
    settings.mysql_password = password
    settings.mysql_database = APPLICATION_DATABASE_NAME
    settings.jwt_secret = jwt_secret
    configure_database(database_url)
    settings.setup_token = ""
    if not os.getenv("SETUP_TOKEN"):
        try:
            _setup_token_path().unlink(missing_ok=True)
        except OSError:
            logger.warning("setup_token_cleanup_failed")


def initialize_database(
    *,
    host: str,
    port: int,
    username: str,
    password: str,
) -> DatabaseSetupResult:
    """Create/migrate/seed the fixed database and activate it atomically."""

    if is_database_configured():
        raise SetupAlreadyConfiguredError("Database setup has already been completed")
    if not _initialization_lock.acquire(blocking=False):
        raise SetupInProgressError("Another database setup attempt is in progress")

    server_engine: Engine | None = None
    target_engine: Engine | None = None
    try:
        if is_database_configured():
            raise SetupAlreadyConfiguredError("Database setup has already been completed")
        database_url = build_mysql_database_url(
            host=host,
            port=port,
            username=username,
            password=password,
        )
        try:
            server_engine = _server_engine(
                host=host,
                port=port,
                username=username,
                password=password,
            )
            discovery = _inspect_or_create_database(server_engine)
        except SQLAlchemyError as exc:
            logger.error("database_setup_connection_failed error_type=%s", type(exc).__name__)
            raise SetupConnectionError(
                "Unable to connect to MySQL or create the application database"
            ) from None

        try:
            target_engine = build_engine(database_url)
            _run_migrations(database_url)
            seed_result = _seed_database(target_engine)
            table_count, alembic_revision = _database_summary(target_engine)
        except SetupError:
            raise
        except Exception as exc:
            logger.error("database_setup_initialization_failed error_type=%s", type(exc).__name__)
            raise SetupInitializationError(
                "The database was reached, but migrations or initial data failed"
            ) from None

        jwt_secret = settings.jwt_secret or secrets.token_urlsafe(48)
        try:
            _atomic_write_runtime_config(
                host=host,
                port=port,
                username=username,
                password=password,
                jwt_secret=jwt_secret,
            )
        except OSError:
            logger.error("database_setup_persistence_failed")
            raise SetupPersistenceError(
                "Database initialization succeeded, but the private configuration could not be saved"
            ) from None

        _activate_runtime_configuration(
            database_url=database_url,
            host=host,
            port=port,
            username=username,
            password=password,
            jwt_secret=jwt_secret,
        )
        return DatabaseSetupResult(
            database_created=discovery.database_created,
            mysql_version=discovery.mysql_version,
            database_collation=discovery.database_collation,
            existing_table_count=discovery.existing_table_count,
            table_count=table_count,
            alembic_revision=alembic_revision,
            seed_status=str(seed_result.get("status", "completed")),
        )
    finally:
        if target_engine is not None:
            target_engine.dispose()
        if server_engine is not None:
            server_engine.dispose()
        _initialization_lock.release()

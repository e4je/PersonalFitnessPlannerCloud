from __future__ import annotations

from collections.abc import Generator
from pathlib import Path
from threading import RLock

from sqlalchemy import Engine, create_engine, event
from sqlalchemy.engine import make_url
from sqlalchemy.orm import Session, sessionmaker

from app.core.config import settings


def build_engine(database_url: str | None = None) -> Engine:
    url = database_url or settings.database_url
    if not url:
        raise DatabaseNotConfiguredError("Database setup has not been completed")
    parsed_url = make_url(url)
    connect_args: dict[str, object] = {}
    if parsed_url.get_backend_name() == "sqlite":
        database_path = parsed_url.database
        if database_path and database_path != ":memory:" and not database_path.startswith("file:"):
            Path(database_path).expanduser().parent.mkdir(parents=True, exist_ok=True)
        connect_args["check_same_thread"] = False
        connect_args["timeout"] = 10
    elif parsed_url.drivername == "mysql+pymysql":
        connect_args["connect_timeout"] = 10

    engine = create_engine(
        url,
        pool_pre_ping=True,
        pool_recycle=1800,
        echo=settings.sql_echo,
        connect_args=connect_args,
    )

    if engine.dialect.name == "mysql":
        @event.listens_for(engine, "connect")
        def set_mysql_utc(dbapi_connection: object, _connection_record: object) -> None:
            cursor = dbapi_connection.cursor()  # type: ignore[attr-defined]
            try:
                cursor.execute("SET time_zone = '+00:00'")
            finally:
                cursor.close()

    if engine.dialect.name == "sqlite":
        @event.listens_for(engine, "connect")
        def configure_sqlite_connection(
            dbapi_connection: object,
            _connection_record: object,
        ) -> None:
            cursor = dbapi_connection.cursor()  # type: ignore[attr-defined]
            try:
                cursor.execute("PRAGMA foreign_keys=ON")
                cursor.execute("PRAGMA busy_timeout=10000")
                cursor.execute("PRAGMA journal_mode=WAL")
            finally:
                cursor.close()
    return engine


class DatabaseNotConfiguredError(RuntimeError):
    pass


_engine_lock = RLock()
engine: Engine | None = None
SessionLocal = sessionmaker(autoflush=False, expire_on_commit=False)


def configure_database(database_url: str) -> Engine:
    """Atomically install a new engine for future sessions."""

    new_engine = build_engine(database_url)
    global engine
    with _engine_lock:
        old_engine = engine
        engine = new_engine
        SessionLocal.configure(bind=new_engine)
    if old_engine is not None and old_engine is not new_engine:
        old_engine.dispose()
    return new_engine


def get_engine() -> Engine:
    with _engine_lock:
        if engine is None:
            raise DatabaseNotConfiguredError("Database setup has not been completed")
        return engine


def is_database_configured() -> bool:
    with _engine_lock:
        return engine is not None


if settings.database_configured:
    configure_database(settings.database_url)


def get_db() -> Generator[Session, None, None]:
    # Capture the engine at request start. Existing sessions retain their bind
    # if first-run setup installs the process-wide factory concurrently.
    db = SessionLocal(bind=get_engine())
    try:
        yield db
    finally:
        db.close()

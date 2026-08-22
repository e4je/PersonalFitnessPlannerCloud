from __future__ import annotations

from collections.abc import Generator
from threading import RLock

from sqlalchemy import Engine, create_engine, event
from sqlalchemy.orm import Session, sessionmaker

from app.core.config import settings


def build_engine(database_url: str | None = None) -> Engine:
    url = database_url or settings.database_url
    if not url:
        raise DatabaseNotConfiguredError("Database setup has not been completed")
    connect_args: dict[str, object] = {}
    if url.startswith("sqlite"):
        connect_args["check_same_thread"] = False
    elif url.startswith("mysql+pymysql"):
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
        def enable_sqlite_foreign_keys(dbapi_connection: object, _connection_record: object) -> None:
            cursor = dbapi_connection.cursor()  # type: ignore[attr-defined]
            try:
                cursor.execute("PRAGMA foreign_keys=ON")
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

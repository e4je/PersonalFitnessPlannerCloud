from __future__ import annotations

from logging.config import fileConfig

from alembic import context
from sqlalchemy import engine_from_config, pool

from app.core.config import settings
from app.db.base import Base
import app.models  # noqa: F401  # register every mapper with Base.metadata


config = context.config
runtime_url = config.attributes.get("database_url") or settings.database_url
if not runtime_url:
    raise RuntimeError("Database setup has not been completed")
config.set_main_option("sqlalchemy.url", str(runtime_url).replace("%", "%%"))

if config.config_file_name is not None:
    fileConfig(config.config_file_name)

target_metadata = Base.metadata


def run_migrations_offline() -> None:
    context.configure(
        url=config.get_main_option("sqlalchemy.url"),
        target_metadata=target_metadata,
        literal_binds=True,
        dialect_opts={"paramstyle": "named"},
        compare_type=True,
        compare_server_default=True,
        render_as_batch=False,
    )

    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online() -> None:
    connectable = engine_from_config(
        config.get_section(config.config_ini_section, {}),
        prefix="sqlalchemy.",
        poolclass=pool.NullPool,
    )

    with connectable.connect() as connection:
        if connection.dialect.name == "mysql":
            connection.exec_driver_sql("SET time_zone = '+00:00'")
        elif connection.dialect.name == "sqlite":
            connection.exec_driver_sql("PRAGMA foreign_keys=ON")
        # Driver-level setup statements trigger SQLAlchemy 2.x autobegin.
        # Finish that setup transaction before Alembic owns transaction scope;
        # otherwise SQLite can create all DDL but roll back the version stamp.
        connection.commit()

        context.configure(
            connection=connection,
            target_metadata=target_metadata,
            compare_type=True,
            compare_server_default=True,
            render_as_batch=connection.dialect.name == "sqlite",
        )

        with context.begin_transaction():
            context.run_migrations()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()

from __future__ import annotations

import argparse
import os
import sqlite3
from datetime import UTC, datetime
from pathlib import Path

from sqlalchemy.engine import make_url

from app.core.config import settings


def configured_sqlite_path() -> Path:
    url = make_url(settings.database_url)
    if url.get_backend_name() != "sqlite" or not url.database or url.database == ":memory:":
        raise RuntimeError("The configured backend database is not a persistent SQLite file")
    return Path(url.database).expanduser().resolve()


def backup_database(source: Path, destination: Path) -> Path:
    source = source.expanduser().resolve()
    destination = destination.expanduser().resolve()
    if source == destination:
        raise ValueError("Backup destination must differ from the live database")
    if not source.is_file():
        raise FileNotFoundError(f"SQLite database does not exist: {source}")
    destination.parent.mkdir(parents=True, exist_ok=True)

    # Reserve the final name without overwriting an operator's existing backup.
    with destination.open("xb"):
        pass
    try:
        source_uri = source.as_uri() + "?mode=ro"
        with sqlite3.connect(source_uri, uri=True, timeout=30) as source_connection:
            with sqlite3.connect(destination, timeout=30) as destination_connection:
                source_connection.backup(destination_connection)
                integrity = destination_connection.execute("PRAGMA integrity_check").fetchone()
                if integrity != ("ok",):
                    raise RuntimeError("SQLite integrity check failed for the new backup")
        os.chmod(destination, 0o600)
    except BaseException:
        destination.unlink(missing_ok=True)
        raise
    return destination


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Create a consistent live SQLite backup")
    output = parser.add_mutually_exclusive_group()
    output.add_argument("--output", type=Path, help="Exact backup file path")
    output.add_argument("--output-dir", type=Path, help="Directory for a timestamped backup")
    return parser.parse_args()


def main() -> None:
    arguments = parse_args()
    source = configured_sqlite_path()
    if arguments.output is not None:
        destination = arguments.output
    else:
        output_directory = arguments.output_dir or source.parent / "backups"
        timestamp = datetime.now(UTC).strftime("%Y%m%d-%H%M%S")
        destination = output_directory / f"fitness-{timestamp}.db"
    created = backup_database(source, destination)
    print(created)


if __name__ == "__main__":
    main()

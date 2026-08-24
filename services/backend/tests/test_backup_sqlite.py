from __future__ import annotations

import sqlite3
from pathlib import Path

import pytest

from scripts.backup_sqlite import backup_database


def test_backup_sqlite_creates_consistent_copy_without_overwrite(tmp_path: Path) -> None:
    source = tmp_path / "fitness.db"
    destination = tmp_path / "backups" / "fitness-copy.db"
    with sqlite3.connect(source) as database:
        database.execute("CREATE TABLE workouts (id INTEGER PRIMARY KEY, note TEXT NOT NULL)")
        database.execute("INSERT INTO workouts (note) VALUES ('训练记录')")

    created = backup_database(source, destination)

    assert created == destination.resolve()
    with sqlite3.connect(created) as database:
        assert database.execute("SELECT note FROM workouts").fetchone() == ("训练记录",)
        assert database.execute("PRAGMA integrity_check").fetchone() == ("ok",)
    with pytest.raises(FileExistsError):
        backup_database(source, destination)


def test_backup_sqlite_rejects_live_database_as_destination(tmp_path: Path) -> None:
    source = tmp_path / "fitness.db"
    source.touch()

    with pytest.raises(ValueError, match="must differ"):
        backup_database(source, source)

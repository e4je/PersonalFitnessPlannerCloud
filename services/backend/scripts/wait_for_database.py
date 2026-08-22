from __future__ import annotations

import os
import sys
from time import monotonic, sleep

from sqlalchemy import text
from sqlalchemy.exc import SQLAlchemyError

from app.db.session import build_engine


def _timeout_seconds() -> int:
    try:
        configured = int(os.getenv("DATABASE_WAIT_SECONDS", "120"))
    except ValueError:
        configured = 120
    return max(0, min(configured, 600))


def main() -> int:
    timeout = _timeout_seconds()
    deadline = monotonic() + timeout
    engine = build_engine()
    try:
        while True:
            try:
                with engine.connect() as connection:
                    connection.execute(text("SELECT 1"))
                print("database_reachable")
                return 0
            except SQLAlchemyError:
                remaining = deadline - monotonic()
                if remaining <= 0:
                    print("database_unavailable", file=sys.stderr)
                    return 1
                sleep(min(2.0, remaining))
    finally:
        engine.dispose()


if __name__ == "__main__":
    raise SystemExit(main())

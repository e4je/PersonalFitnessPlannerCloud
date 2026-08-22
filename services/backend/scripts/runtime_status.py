from __future__ import annotations

from app.core.config import APPLICATION_DATABASE_NAME, settings


def main() -> int:
    if settings.database_configured:
        print(f"database_configured name={APPLICATION_DATABASE_NAME}")
        return 0
    print(f"first_run_setup_required name={APPLICATION_DATABASE_NAME}")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())

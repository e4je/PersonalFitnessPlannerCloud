from __future__ import annotations

import json

from app.db.session import SessionLocal
from app.seed.default_plan import seed_default_plan


def main() -> None:
    with SessionLocal() as db:
        result = seed_default_plan(db)
    print(json.dumps(result, ensure_ascii=False, sort_keys=True))


if __name__ == "__main__":
    main()

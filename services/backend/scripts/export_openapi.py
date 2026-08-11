from __future__ import annotations

from pathlib import Path

import yaml

from app.main import app


def main() -> None:
    output = Path(__file__).resolve().parents[1] / "contracts" / "openapi.yaml"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        yaml.safe_dump(
            app.openapi(),
            allow_unicode=True,
            sort_keys=False,
            width=120,
        ),
        encoding="utf-8",
    )
    print(f"openapi_exported path={output}")


if __name__ == "__main__":
    main()

from __future__ import annotations

import base64
import json
from typing import Any

from fastapi import HTTPException, status


def encode_cursor(payload: dict[str, Any]) -> str:
    raw = json.dumps(payload, separators=(",", ":"), sort_keys=True).encode("utf-8")
    return base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def decode_cursor(cursor: str | None) -> dict[str, Any]:
    if cursor is None:
        return {}
    if len(cursor) > 512:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={"code": "invalid_cursor", "message": "Cursor is malformed"},
        )
    try:
        padded = cursor + "=" * (-len(cursor) % 4)
        value = json.loads(
            base64.b64decode(padded.encode("ascii"), altchars=b"-_", validate=True).decode("utf-8")
        )
        if not isinstance(value, dict):
            raise ValueError
        if "id" in value and (not isinstance(value["id"], str) or len(value["id"]) > 64):
            raise ValueError
        return value
    except (ValueError, UnicodeDecodeError, json.JSONDecodeError, UnicodeEncodeError) as exc:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={"code": "invalid_cursor", "message": "Cursor is malformed"},
        ) from exc

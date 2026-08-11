from __future__ import annotations

import hashlib
import hmac
import secrets
from datetime import UTC, datetime, timedelta
from typing import Any
from uuid import uuid4

import jwt
from argon2 import PasswordHasher
from argon2.exceptions import InvalidHashError, VerificationError, VerifyMismatchError

from app.core.config import settings


# Argon2id is the argon2-cffi default. Keeping one process-wide hasher also avoids
# silently changing parameters between password creation and verification calls.
_password_hasher = PasswordHasher()
_dummy_password_hash = _password_hasher.hash("not-a-real-user-password")


class InvalidAccessToken(ValueError):
    pass


def hash_password(password: str) -> str:
    if not password:
        raise ValueError("Password must not be empty")
    return _password_hasher.hash(password)


def verify_password(password: str, password_hash: str) -> bool:
    try:
        return _password_hasher.verify(password_hash, password)
    except (VerifyMismatchError, VerificationError, InvalidHashError, TypeError):
        return False


def verify_dummy_password(password: str) -> None:
    """Spend roughly the normal verification cost for an unknown account."""

    verify_password(password, _dummy_password_hash)


def password_needs_rehash(password_hash: str) -> bool:
    try:
        return _password_hasher.check_needs_rehash(password_hash)
    except (InvalidHashError, TypeError):
        return True


def create_access_token(
    user_id: str,
    *,
    roles: list[str] | tuple[str, ...] | None = None,
    display_name: str | None = None,
    email: str | None = None,
    now: datetime | None = None,
) -> tuple[str, datetime]:
    issued_at = (now or datetime.now(UTC)).astimezone(UTC)
    expires_at = issued_at + timedelta(minutes=settings.access_token_minutes)
    payload: dict[str, Any] = {
        "sub": user_id,
        "type": "access",
        "iat": issued_at,
        "exp": expires_at,
        "jti": str(uuid4()),
    }
    if roles is not None:
        # Informational only. Authorization always reloads roles from the database.
        payload["roles"] = sorted(set(roles))
    if display_name:
        payload["display_name"] = display_name
    if email:
        payload["email"] = email
    encoded = jwt.encode(payload, settings.jwt_secret, algorithm=settings.jwt_algorithm)
    return encoded, expires_at


def decode_access_token(token: str) -> dict[str, Any]:
    try:
        payload = jwt.decode(
            token,
            settings.jwt_secret,
            algorithms=[settings.jwt_algorithm],
            options={"require": ["sub", "type", "iat", "exp", "jti"]},
        )
    except jwt.PyJWTError as exc:
        raise InvalidAccessToken("Access token is invalid or expired") from exc
    if payload.get("type") != "access" or not isinstance(payload.get("sub"), str):
        raise InvalidAccessToken("Access token has an invalid type or subject")
    return payload


def generate_refresh_token() -> str:
    # 64 random bytes provide ample entropy for an opaque bearer credential.
    return secrets.token_urlsafe(64)


def hash_refresh_token(token: str) -> str:
    """Return a keyed, deterministic digest; refresh plaintext is never persisted."""

    return hmac.new(
        settings.jwt_secret.encode("utf-8"),
        token.encode("utf-8"),
        hashlib.sha256,
    ).hexdigest()

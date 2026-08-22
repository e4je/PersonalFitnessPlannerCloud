from __future__ import annotations

import hashlib
from collections import defaultdict, deque
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from threading import Lock
from time import monotonic
from uuid import uuid4

from sqlalchemy import func, select, update
from sqlalchemy.orm import Session

from app.core.config import settings
from app.core.security import (
    create_access_token,
    generate_refresh_token,
    hash_password,
    hash_refresh_token,
    password_needs_rehash,
    verify_dummy_password,
    verify_password,
)
from app.db.base import utcnow
from app.models import RefreshToken, Role, User, UserRole
from app.repositories.common import add_audit_log


class LoginRateLimiter:
    """A bounded process-local limiter.

    This deliberately has no distributed guarantees. Production deployments with
    multiple workers should place the same policy at the reverse proxy or Redis.
    """

    def __init__(
        self,
        limit: int,
        window_seconds: float = 60.0,
        *,
        max_keys: int = 10_000,
    ) -> None:
        self.limit = limit
        self.window_seconds = window_seconds
        self.max_keys = max(100, max_keys)
        self._attempts: dict[str, deque[float]] = defaultdict(deque)
        self._lock = Lock()

    def _prune(self, attempts: deque[float], now: float) -> None:
        cutoff = now - self.window_seconds
        while attempts and attempts[0] <= cutoff:
            attempts.popleft()

    def allowed(self, key: str) -> tuple[bool, int]:
        now = monotonic()
        with self._lock:
            self._evict(now, key)
            attempts = self._attempts[key]
            self._prune(attempts, now)
            if len(attempts) < self.limit:
                return True, 0
            retry_after = max(1, int(self.window_seconds - (now - attempts[0])) + 1)
            return False, retry_after

    def register_failure(self, key: str) -> None:
        """Record a rejected authentication attempt.

        Kept as a named compatibility wrapper because callers and tests use the
        more specific terminology for login failures.
        """

        self.register_attempt(key)

    def register_attempt(self, key: str) -> None:
        """Record one attempt, regardless of whether it eventually succeeds."""

        now = monotonic()
        with self._lock:
            self._evict(now, key)
            attempts = self._attempts[key]
            self._prune(attempts, now)
            attempts.append(now)

    def reset(self, key: str) -> None:
        with self._lock:
            self._attempts.pop(key, None)

    def _evict(self, now: float, key: str) -> None:
        """Bound attacker-controlled key cardinality."""

        if key in self._attempts and len(self._attempts) < self.max_keys:
            return
        for candidate, attempts in list(self._attempts.items()):
            self._prune(attempts, now)
            if not attempts:
                self._attempts.pop(candidate, None)
        if len(self._attempts) < self.max_keys or key in self._attempts:
            return
        oldest_key = min(self._attempts, key=lambda item: self._attempts[item][0])
        self._attempts.pop(oldest_key, None)


login_rate_limiter = LoginRateLimiter(settings.login_attempts_per_minute)


class RefreshTokenError(ValueError):
    def __init__(self, message: str, *, persist_changes: bool = False) -> None:
        self.persist_changes = persist_changes
        super().__init__(message)


class RefreshTokenReplayError(RefreshTokenError):
    pass


@dataclass(slots=True)
class TokenBundle:
    access_token: str
    refresh_token: str
    access_expires_at: datetime

    @property
    def expires_in(self) -> int:
        remaining = self.access_expires_at - datetime.now(UTC)
        return max(0, int(remaining.total_seconds()))


def normalize_email(email: str) -> str:
    return email.strip().casefold()


def login_rate_key(ip_address: str | None, email: str) -> str:
    # Keep request-derived values out of the in-memory limiter map.
    material = f"{ip_address or 'unknown'}:{normalize_email(email)}".encode("utf-8")
    return "account:" + hashlib.sha256(material).hexdigest()


def login_ip_rate_key(ip_address: str | None) -> str:
    return "ip:" + hashlib.sha256((ip_address or "unknown").encode("utf-8")).hexdigest()


def registration_ip_rate_key(ip_address: str | None) -> str:
    """Return a bounded key for unauthenticated account-creation attempts."""

    return "register-ip:" + hashlib.sha256((ip_address or "unknown").encode("utf-8")).hexdigest()


def authenticate_user(db: Session, email: str, password: str) -> User | None:
    normalized = normalize_email(email)
    conditions = [func.lower(User.email) == normalized]
    if hasattr(User, "deleted_at"):
        conditions.append(User.deleted_at.is_(None))
    user = db.scalar(select(User).where(*conditions))
    if user is None:
        verify_dummy_password(password)
        return None
    password_valid = verify_password(password, user.password_hash)
    if not bool(user.is_active) or not password_valid:
        return None
    if password_needs_rehash(user.password_hash):
        user.password_hash = hash_password(password)
    user.last_login_at = utcnow()
    return user


def role_names_for_user(db: Session, user: User) -> list[str]:
    if bool(getattr(user, "is_superuser", False)):
        return ["admin", "superuser"]
    conditions = [UserRole.user_id == user.id]
    if hasattr(UserRole, "deleted_at"):
        conditions.append(UserRole.deleted_at.is_(None))
    if hasattr(Role, "deleted_at"):
        conditions.append(Role.deleted_at.is_(None))
    return list(
        db.scalars(
            select(Role.name)
            .join(UserRole, UserRole.role_id == Role.id)
            .where(*conditions)
            .order_by(Role.name)
        ).all()
    )


def issue_token_pair(
    db: Session,
    user: User,
    *,
    family_id: str | None = None,
    ip_address: str | None = None,
    user_agent: str | None = None,
) -> TokenBundle:
    plaintext = generate_refresh_token()
    refresh = RefreshToken(
        user_id=user.id,
        token_hash=hash_refresh_token(plaintext),
        family_id=family_id or str(uuid4()),
        expires_at=utcnow() + timedelta(days=settings.refresh_token_days),
        created_by_ip=ip_address,
        user_agent=user_agent[:512] if user_agent else None,
    )
    db.add(refresh)
    access, expires_at = create_access_token(
        user.id,
        roles=role_names_for_user(db, user),
        display_name=user.display_name,
        email=user.email,
    )
    return TokenBundle(access, plaintext, expires_at)


def _find_refresh_token(db: Session, plaintext: str, *, for_update: bool) -> RefreshToken | None:
    statement = select(RefreshToken).where(RefreshToken.token_hash == hash_refresh_token(plaintext))
    if for_update:
        statement = statement.with_for_update()
    return db.scalar(statement)


def _revoke_family(db: Session, family_id: str, when: datetime) -> None:
    db.execute(
        update(RefreshToken)
        .where(RefreshToken.family_id == family_id, RefreshToken.revoked_at.is_(None))
        .values(revoked_at=when)
        .execution_options(synchronize_session=False)
    )


def rotate_refresh_token(
    db: Session,
    plaintext: str,
    *,
    ip_address: str | None = None,
    user_agent: str | None = None,
) -> TokenBundle:
    current = _find_refresh_token(db, plaintext, for_update=True)
    if current is None:
        raise RefreshTokenError("Refresh token is invalid")

    now = utcnow()
    if current.revoked_at is not None or current.replaced_by_id is not None:
        # Reuse of any rotated token is a credential-theft signal. Revoke every
        # descendant and sibling in the family, including the currently valid one.
        _revoke_family(db, current.family_id, now)
        add_audit_log(
            db,
            actor_user_id=current.user_id,
            action="auth.refresh_replay_detected",
            entity_type="refresh_token_family",
            entity_id=current.family_id,
            ip_address=ip_address,
            user_agent=user_agent,
        )
        db.flush()
        raise RefreshTokenReplayError(
            "Refresh token reuse was detected; the token family was revoked",
            persist_changes=True,
        )

    if current.expires_at <= now:
        current.revoked_at = now
        db.flush()
        raise RefreshTokenError("Refresh token has expired", persist_changes=True)

    conditions = [User.id == current.user_id, User.is_active.is_(True)]
    if hasattr(User, "deleted_at"):
        conditions.append(User.deleted_at.is_(None))
    user = db.scalar(select(User).where(*conditions))
    if user is None:
        _revoke_family(db, current.family_id, now)
        db.flush()
        raise RefreshTokenError("Refresh token user is inactive", persist_changes=True)

    bundle = issue_token_pair(
        db,
        user,
        family_id=current.family_id,
        ip_address=ip_address,
        user_agent=user_agent,
    )
    db.flush()  # assigns the replacement UUID before linking the old token
    replacement = _find_refresh_token(db, bundle.refresh_token, for_update=False)
    if replacement is None:  # pragma: no cover - defensive against a broken model hook
        raise RuntimeError("Failed to persist refresh-token rotation")
    current.revoked_at = now
    current.replaced_by_id = replacement.id
    db.flush()
    return bundle


def revoke_for_logout(db: Session, user: User, plaintext: str | None) -> None:
    now = utcnow()
    if plaintext:
        token = _find_refresh_token(db, plaintext, for_update=True)
        # Logout is idempotent and never reveals whether a supplied token exists.
        if token is not None and token.user_id == user.id:
            _revoke_family(db, token.family_id, now)
    else:
        db.execute(
            update(RefreshToken)
            .where(RefreshToken.user_id == user.id, RefreshToken.revoked_at.is_(None))
            .values(revoked_at=now)
            .execution_options(synchronize_session=False)
        )

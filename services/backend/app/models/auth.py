from __future__ import annotations

from datetime import datetime
from typing import Any
from sqlalchemy import Boolean, CheckConstraint, ForeignKey, Index, JSON, String, Text, UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db.base import Base, SyncEntityMixin, UTCDateTime, UUIDPrimaryKeyMixin, utcnow


class User(SyncEntityMixin, Base):
    __tablename__ = "users"
    __table_args__ = (
        CheckConstraint("weight_unit IN ('KG', 'LB')", name="weight_unit"),
        Index("ix_users_active_deleted", "is_active", "deleted_at"),
    )

    email: Mapped[str] = mapped_column(String(254), nullable=False, unique=True, index=True)
    username: Mapped[str] = mapped_column(String(64), nullable=False, unique=True, index=True)
    password_hash: Mapped[str] = mapped_column(String(255), nullable=False)
    display_name: Mapped[str] = mapped_column(String(120), nullable=False)
    timezone: Mapped[str] = mapped_column(String(64), nullable=False, default="Asia/Shanghai")
    weight_unit: Mapped[str] = mapped_column(String(8), nullable=False, default="KG")
    is_active: Mapped[bool] = mapped_column(Boolean, nullable=False, default=True)
    is_superuser: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    last_login_at: Mapped[datetime | None] = mapped_column(UTCDateTime(), nullable=True)

    roles: Mapped[list[Role]] = relationship(
        secondary="user_roles",
        primaryjoin="User.id == UserRole.user_id",
        secondaryjoin="Role.id == UserRole.role_id",
        back_populates="users",
        lazy="selectin",
        overlaps="role,user,user_role_links",
    )
    user_role_links: Mapped[list[UserRole]] = relationship(
        back_populates="user",
        cascade="all, delete-orphan",
        foreign_keys="UserRole.user_id",
        overlaps="roles,users",
    )
    refresh_tokens: Mapped[list[RefreshToken]] = relationship(
        back_populates="user", cascade="all, delete-orphan"
    )


class Role(SyncEntityMixin, Base):
    __tablename__ = "roles"

    name: Mapped[str] = mapped_column(String(64), nullable=False, unique=True)
    description: Mapped[str | None] = mapped_column(Text, nullable=True)
    permissions_json: Mapped[list[str]] = mapped_column(JSON, nullable=False, default=list)
    is_system: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)

    users: Mapped[list[User]] = relationship(
        secondary="user_roles",
        primaryjoin="Role.id == UserRole.role_id",
        secondaryjoin="User.id == UserRole.user_id",
        back_populates="roles",
        lazy="selectin",
        overlaps="role,user,user_role_links",
    )
    user_role_links: Mapped[list[UserRole]] = relationship(
        back_populates="role",
        cascade="all, delete-orphan",
        foreign_keys="UserRole.role_id",
        overlaps="roles,users",
    )


class UserRole(SyncEntityMixin, Base):
    __tablename__ = "user_roles"
    __table_args__ = (
        UniqueConstraint("user_id", "role_id", name="uq_user_roles_user_role"),
        Index("ix_user_roles_role_user", "role_id", "user_id"),
    )

    user_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True
    )
    role_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("roles.id", ondelete="CASCADE"), nullable=False, index=True
    )
    assigned_at: Mapped[datetime] = mapped_column(UTCDateTime(), nullable=False, default=utcnow)
    assigned_by_user_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="SET NULL"), nullable=True, index=True
    )

    user: Mapped[User] = relationship(
        back_populates="user_role_links",
        foreign_keys=[user_id],
        overlaps="roles,users",
    )
    role: Mapped[Role] = relationship(
        back_populates="user_role_links",
        foreign_keys=[role_id],
        overlaps="roles,users",
    )
    assigned_by: Mapped[User | None] = relationship(foreign_keys=[assigned_by_user_id])


class RefreshToken(SyncEntityMixin, Base):
    __tablename__ = "refresh_tokens"
    __table_args__ = (
        Index("ix_refresh_tokens_user_family", "user_id", "family_id"),
        Index("ix_refresh_tokens_expiry_revoked", "expires_at", "revoked_at"),
    )

    user_id: Mapped[str] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True
    )
    token_hash: Mapped[str] = mapped_column(String(128), nullable=False, unique=True)
    family_id: Mapped[str] = mapped_column(String(36), nullable=False, index=True)
    expires_at: Mapped[datetime] = mapped_column(UTCDateTime(), nullable=False)
    revoked_at: Mapped[datetime | None] = mapped_column(UTCDateTime(), nullable=True)
    replaced_by_id: Mapped[str | None] = mapped_column(
        String(36),
        ForeignKey("refresh_tokens.id", ondelete="SET NULL"),
        nullable=True,
        unique=True,
    )
    created_by_ip: Mapped[str | None] = mapped_column(String(45), nullable=True)
    user_agent: Mapped[str | None] = mapped_column(String(512), nullable=True)

    user: Mapped[User] = relationship(back_populates="refresh_tokens", foreign_keys=[user_id])
    replaced_by: Mapped[RefreshToken | None] = relationship(
        remote_side="RefreshToken.id",
        foreign_keys=[replaced_by_id],
        back_populates="replaces",
        uselist=False,
    )
    replaces: Mapped[RefreshToken | None] = relationship(
        foreign_keys="RefreshToken.replaced_by_id", back_populates="replaced_by", uselist=False
    )

    @property
    def is_revoked(self) -> bool:
        return self.revoked_at is not None


class SystemSetting(UUIDPrimaryKeyMixin, Base):
    """Small, audited operator settings stored outside the sync stream.

    Settings are deliberately not ``SyncEntity`` rows: they describe server
    policy (for example whether public registration is enabled), not user data
    that should ever be sent to a client change feed.
    """

    __tablename__ = "system_settings"
    __table_args__ = (
        UniqueConstraint("key", name="uq_system_settings_key"),
        Index("ix_system_settings_key", "key"),
    )

    key: Mapped[str] = mapped_column(String(64), nullable=False)
    value_json: Mapped[dict[str, Any]] = mapped_column(JSON, nullable=False, default=dict)
    description: Mapped[str | None] = mapped_column(Text, nullable=True)
    created_at: Mapped[datetime] = mapped_column(UTCDateTime(), nullable=False, default=utcnow)
    updated_at: Mapped[datetime] = mapped_column(
        UTCDateTime(), nullable=False, default=utcnow, onupdate=utcnow
    )
    updated_by_user_id: Mapped[str | None] = mapped_column(
        String(36), ForeignKey("users.id", ondelete="SET NULL"), nullable=True, index=True
    )

    updated_by: Mapped[User | None] = relationship("User", foreign_keys=[updated_by_user_id])

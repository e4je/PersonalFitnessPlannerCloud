"""Add operator settings used by the web account console.

Revision ID: 20260823_0002
Revises: 20260809_0001
"""

from __future__ import annotations

from collections.abc import Sequence
from datetime import UTC, datetime
from uuid import NAMESPACE_URL, uuid5

from alembic import op
import sqlalchemy as sa


revision: str = "20260823_0002"
down_revision: str | None = "20260809_0001"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.create_table(
        "system_settings",
        sa.Column("id", sa.String(length=36), nullable=False),
        sa.Column("key", sa.String(length=64), nullable=False),
        sa.Column("value_json", sa.JSON(), nullable=False),
        sa.Column("description", sa.Text(), nullable=True),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.Column("updated_by_user_id", sa.String(length=36), nullable=True),
        sa.ForeignKeyConstraint(
            ["updated_by_user_id"], ["users.id"], ondelete="SET NULL"
        ),
        sa.PrimaryKeyConstraint("id", name="pk_system_settings"),
        sa.UniqueConstraint("key", name="uq_system_settings_key"),
    )
    op.create_index("ix_system_settings_key", "system_settings", ["key"])
    op.create_index(
        "ix_system_settings_updated_by_user_id",
        "system_settings",
        ["updated_by_user_id"],
    )
    now = datetime.now(UTC).replace(tzinfo=None)
    op.bulk_insert(
        sa.table(
            "system_settings",
            sa.column("id", sa.String()),
            sa.column("key", sa.String()),
            sa.column("value_json", sa.JSON()),
            sa.column("description", sa.Text()),
            sa.column("created_at", sa.DateTime()),
            sa.column("updated_at", sa.DateTime()),
            sa.column("updated_by_user_id", sa.String()),
        ),
        [
            {
                "id": str(uuid5(NAMESPACE_URL, "personal-fitness-planner/registration-enabled")),
                "key": "registration_enabled",
                "value_json": {"value": True},
                "description": "Allow unauthenticated visitors to create standard accounts",
                "created_at": now,
                "updated_at": now,
                "updated_by_user_id": None,
            }
        ],
    )


def downgrade() -> None:
    # Dropping the table removes its foreign key and both indexes atomically.
    # MySQL rejects dropping the FK-supporting index while the constraint still
    # exists (error 1553), so do not issue separate DROP INDEX statements.
    op.drop_table("system_settings")

"""Create the complete Personal Fitness Planner schema.

Revision ID: 20260809_0001
Revises: None
Create Date: 2026-08-09
"""
from __future__ import annotations

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa


revision: str = "20260809_0001"
down_revision: str | None = None
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


SYNC_TABLES = (
    "users",
    "roles",
    "user_roles",
    "refresh_tokens",
    "muscle_groups",
    "equipment",
    "exercises",
    "exercise_cues",
    "exercise_alternatives",
    "exercise_muscle_groups",
    "exercise_equipment",
    "training_plans",
    "plan_versions",
    "plan_days",
    "plan_slots",
    "plan_slot_options",
    "plan_assignments",
    "workout_sessions",
    "workout_sets",
    "daily_readiness",
    "cardio_sessions",
    "idempotency_keys",
    "audit_logs",
    "schema_versions",
)

TABLES = (
    *SYNC_TABLES[:11],
    *SYNC_TABLES[11:21],
    "sync_changes",
    *SYNC_TABLES[21:],
)


def _sync_columns() -> list[sa.Column]:
    return [
        sa.Column("id", sa.String(length=36), nullable=False),
        sa.Column("version", sa.Integer(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.Column("deleted_at", sa.DateTime(), nullable=True),
    ]


def _pk(table: str) -> sa.PrimaryKeyConstraint:
    return sa.PrimaryKeyConstraint("id", name=f"pk_{table}")


def _fk(
    table: str,
    column: str,
    referred_table: str,
    *,
    referred_column: str = "id",
    ondelete: str | None = None,
) -> sa.ForeignKeyConstraint:
    return sa.ForeignKeyConstraint(
        [column],
        [f"{referred_table}.{referred_column}"],
        name=f"fk_{table}_{column}_{referred_table}",
        ondelete=ondelete,
    )


def _create_sync_indexes(table: str) -> None:
    op.create_index(f"ix_{table}_deleted_at", table, ["deleted_at"], unique=False)


def upgrade() -> None:
    op.create_table(
        "users",
        *_sync_columns(),
        sa.Column("email", sa.String(length=254), nullable=False),
        sa.Column("username", sa.String(length=64), nullable=False),
        sa.Column("password_hash", sa.String(length=255), nullable=False),
        sa.Column("display_name", sa.String(length=120), nullable=False),
        sa.Column("timezone", sa.String(length=64), nullable=False),
        sa.Column("weight_unit", sa.String(length=8), nullable=False),
        sa.Column("is_active", sa.Boolean(), nullable=False),
        sa.Column("is_superuser", sa.Boolean(), nullable=False),
        sa.Column("last_login_at", sa.DateTime(), nullable=True),
        sa.CheckConstraint("weight_unit IN ('KG', 'LB')", name=op.f('ck_users_weight_unit')),
        _pk("users"),
    )
    _create_sync_indexes("users")
    op.create_index("ix_users_email", "users", ["email"], unique=True)
    op.create_index("ix_users_username", "users", ["username"], unique=True)
    op.create_index("ix_users_active_deleted", "users", ["is_active", "deleted_at"])

    op.create_table(
        "roles",
        *_sync_columns(),
        sa.Column("name", sa.String(length=64), nullable=False),
        sa.Column("description", sa.Text(), nullable=True),
        sa.Column("permissions_json", sa.JSON(), nullable=False),
        sa.Column("is_system", sa.Boolean(), nullable=False),
        sa.UniqueConstraint("name", name="uq_roles_name"),
        _pk("roles"),
    )
    _create_sync_indexes("roles")

    op.create_table(
        "user_roles",
        *_sync_columns(),
        sa.Column("user_id", sa.String(length=36), nullable=False),
        sa.Column("role_id", sa.String(length=36), nullable=False),
        sa.Column("assigned_at", sa.DateTime(), nullable=False),
        sa.Column("assigned_by_user_id", sa.String(length=36), nullable=True),
        _fk("user_roles", "user_id", "users", ondelete="CASCADE"),
        _fk("user_roles", "role_id", "roles", ondelete="CASCADE"),
        _fk("user_roles", "assigned_by_user_id", "users", ondelete="SET NULL"),
        sa.UniqueConstraint("user_id", "role_id", name="uq_user_roles_user_role"),
        _pk("user_roles"),
    )
    _create_sync_indexes("user_roles")
    op.create_index("ix_user_roles_user_id", "user_roles", ["user_id"])
    op.create_index("ix_user_roles_role_id", "user_roles", ["role_id"])
    op.create_index("ix_user_roles_assigned_by_user_id", "user_roles", ["assigned_by_user_id"])
    op.create_index("ix_user_roles_role_user", "user_roles", ["role_id", "user_id"])

    op.create_table(
        "refresh_tokens",
        *_sync_columns(),
        sa.Column("user_id", sa.String(length=36), nullable=False),
        sa.Column("token_hash", sa.String(length=128), nullable=False),
        sa.Column("family_id", sa.String(length=36), nullable=False),
        sa.Column("expires_at", sa.DateTime(), nullable=False),
        sa.Column("revoked_at", sa.DateTime(), nullable=True),
        sa.Column("replaced_by_id", sa.String(length=36), nullable=True),
        sa.Column("created_by_ip", sa.String(length=45), nullable=True),
        sa.Column("user_agent", sa.String(length=512), nullable=True),
        _fk("refresh_tokens", "user_id", "users", ondelete="CASCADE"),
        _fk("refresh_tokens", "replaced_by_id", "refresh_tokens", ondelete="SET NULL"),
        sa.UniqueConstraint("token_hash", name="uq_refresh_tokens_token_hash"),
        sa.UniqueConstraint("replaced_by_id", name="uq_refresh_tokens_replaced_by_id"),
        _pk("refresh_tokens"),
    )
    _create_sync_indexes("refresh_tokens")
    op.create_index("ix_refresh_tokens_user_id", "refresh_tokens", ["user_id"])
    op.create_index("ix_refresh_tokens_family_id", "refresh_tokens", ["family_id"])
    op.create_index("ix_refresh_tokens_user_family", "refresh_tokens", ["user_id", "family_id"])
    op.create_index(
        "ix_refresh_tokens_expiry_revoked", "refresh_tokens", ["expires_at", "revoked_at"]
    )

    op.create_table(
        "muscle_groups",
        *_sync_columns(),
        sa.Column("code", sa.String(length=64), nullable=False),
        sa.Column("name", sa.String(length=120), nullable=False),
        sa.Column("body_region", sa.String(length=64), nullable=True),
        sa.Column("description", sa.Text(), nullable=True),
        sa.Column("sort_order", sa.Integer(), nullable=False),
        sa.UniqueConstraint("code", name="uq_muscle_groups_code"),
        _pk("muscle_groups"),
    )
    _create_sync_indexes("muscle_groups")
    op.create_index("ix_muscle_groups_body_region", "muscle_groups", ["body_region"])

    op.create_table(
        "equipment",
        *_sync_columns(),
        sa.Column("code", sa.String(length=64), nullable=False),
        sa.Column("name", sa.String(length=120), nullable=False),
        sa.Column("category", sa.String(length=64), nullable=False),
        sa.Column("brand", sa.String(length=120), nullable=True),
        sa.Column("model", sa.String(length=120), nullable=True),
        sa.Column("description", sa.Text(), nullable=True),
        sa.Column("notes", sa.Text(), nullable=True),
        sa.Column("is_active", sa.Boolean(), nullable=False),
        sa.Column("metadata", sa.JSON(), nullable=False),
        sa.UniqueConstraint("code", name="uq_equipment_code"),
        _pk("equipment"),
    )
    _create_sync_indexes("equipment")
    op.create_index("ix_equipment_name", "equipment", ["name"])
    op.create_index("ix_equipment_category", "equipment", ["category"])
    op.create_index("ix_equipment_category_active", "equipment", ["category", "is_active"])

    op.create_table(
        "exercises",
        *_sync_columns(),
        sa.Column("code", sa.String(length=64), nullable=False),
        sa.Column("name", sa.String(length=160), nullable=False),
        sa.Column("description", sa.Text(), nullable=True),
        sa.Column("body_part", sa.String(length=64), nullable=True),
        sa.Column("movement_pattern", sa.String(length=64), nullable=True),
        sa.Column("difficulty", sa.String(length=32), nullable=True),
        sa.Column("default_sets", sa.Integer(), nullable=True),
        sa.Column("rep_min", sa.Integer(), nullable=True),
        sa.Column("rep_max", sa.Integer(), nullable=True),
        sa.Column("rep_unit", sa.String(length=16), nullable=False),
        sa.Column("is_unilateral", sa.Boolean(), nullable=False),
        sa.Column("is_active", sa.Boolean(), nullable=False),
        sa.Column("created_by_user_id", sa.String(length=36), nullable=True),
        sa.Column("common_mistakes", sa.JSON(), nullable=False),
        sa.Column("metadata", sa.JSON(), nullable=False),
        _fk("exercises", "created_by_user_id", "users", ondelete="SET NULL"),
        sa.UniqueConstraint("code", name="uq_exercises_code"),
        sa.CheckConstraint(
            "default_sets IS NULL OR default_sets > 0", name=op.f('ck_exercises_default_sets_positive')
        ),
        sa.CheckConstraint("rep_min IS NULL OR rep_min >= 0", name=op.f('ck_exercises_rep_min_nonnegative')),
        sa.CheckConstraint("rep_max IS NULL OR rep_max >= 0", name=op.f('ck_exercises_rep_max_nonnegative')),
        sa.CheckConstraint(
            "rep_min IS NULL OR rep_max IS NULL OR rep_min <= rep_max",
            name=op.f('ck_exercises_rep_range'),
        ),
        _pk("exercises"),
    )
    _create_sync_indexes("exercises")
    op.create_index("ix_exercises_name", "exercises", ["name"])
    op.create_index("ix_exercises_body_part", "exercises", ["body_part"])
    op.create_index("ix_exercises_movement_pattern", "exercises", ["movement_pattern"])
    op.create_index("ix_exercises_created_by_user_id", "exercises", ["created_by_user_id"])
    op.create_index("ix_exercises_body_active", "exercises", ["body_part", "is_active"])

    op.create_table(
        "exercise_cues",
        *_sync_columns(),
        sa.Column("exercise_id", sa.String(length=36), nullable=False),
        sa.Column("text", sa.Text(), nullable=False),
        sa.Column("sort_order", sa.Integer(), nullable=False),
        _fk("exercise_cues", "exercise_id", "exercises", ondelete="CASCADE"),
        sa.UniqueConstraint(
            "exercise_id", "sort_order", name="uq_exercise_cues_exercise_order"
        ),
        _pk("exercise_cues"),
    )
    _create_sync_indexes("exercise_cues")
    op.create_index("ix_exercise_cues_exercise_id", "exercise_cues", ["exercise_id"])

    op.create_table(
        "exercise_alternatives",
        *_sync_columns(),
        sa.Column("exercise_id", sa.String(length=36), nullable=False),
        sa.Column("alternative_exercise_id", sa.String(length=36), nullable=False),
        sa.Column("priority", sa.Integer(), nullable=False),
        sa.Column("notes", sa.Text(), nullable=True),
        _fk("exercise_alternatives", "exercise_id", "exercises", ondelete="CASCADE"),
        _fk(
            "exercise_alternatives",
            "alternative_exercise_id",
            "exercises",
            ondelete="CASCADE",
        ),
        sa.UniqueConstraint(
            "exercise_id",
            "alternative_exercise_id",
            name="uq_exercise_alternatives_pair",
        ),
        sa.CheckConstraint(
            "exercise_id <> alternative_exercise_id",
            name=op.f('ck_exercise_alternatives_different_exercises'),
        ),
        _pk("exercise_alternatives"),
    )
    _create_sync_indexes("exercise_alternatives")
    op.create_index(
        "ix_exercise_alternatives_exercise_id", "exercise_alternatives", ["exercise_id"]
    )
    op.create_index(
        "ix_exercise_alternatives_alternative_exercise_id",
        "exercise_alternatives",
        ["alternative_exercise_id"],
    )

    op.create_table(
        "exercise_muscle_groups",
        *_sync_columns(),
        sa.Column("exercise_id", sa.String(length=36), nullable=False),
        sa.Column("muscle_group_id", sa.String(length=36), nullable=False),
        sa.Column("is_primary", sa.Boolean(), nullable=False),
        _fk("exercise_muscle_groups", "exercise_id", "exercises", ondelete="CASCADE"),
        _fk(
            "exercise_muscle_groups", "muscle_group_id", "muscle_groups", ondelete="CASCADE"
        ),
        sa.UniqueConstraint(
            "exercise_id", "muscle_group_id", name="uq_exercise_muscle_groups_pair"
        ),
        _pk("exercise_muscle_groups"),
    )
    _create_sync_indexes("exercise_muscle_groups")
    op.create_index(
        "ix_exercise_muscle_groups_exercise_id", "exercise_muscle_groups", ["exercise_id"]
    )
    op.create_index(
        "ix_exercise_muscle_groups_muscle_group_id",
        "exercise_muscle_groups",
        ["muscle_group_id"],
    )
    op.create_index(
        "ix_exercise_muscle_groups_primary",
        "exercise_muscle_groups",
        ["muscle_group_id", "is_primary"],
    )

    op.create_table(
        "exercise_equipment",
        *_sync_columns(),
        sa.Column("exercise_id", sa.String(length=36), nullable=False),
        sa.Column("equipment_id", sa.String(length=36), nullable=False),
        sa.Column("is_required", sa.Boolean(), nullable=False),
        sa.Column("quantity", sa.Integer(), nullable=False),
        sa.Column("notes", sa.Text(), nullable=True),
        _fk("exercise_equipment", "exercise_id", "exercises", ondelete="CASCADE"),
        _fk("exercise_equipment", "equipment_id", "equipment", ondelete="CASCADE"),
        sa.UniqueConstraint(
            "exercise_id", "equipment_id", name="uq_exercise_equipment_pair"
        ),
        sa.CheckConstraint("quantity > 0", name=op.f('ck_exercise_equipment_quantity_positive')),
        _pk("exercise_equipment"),
    )
    _create_sync_indexes("exercise_equipment")
    op.create_index(
        "ix_exercise_equipment_exercise_id", "exercise_equipment", ["exercise_id"]
    )
    op.create_index(
        "ix_exercise_equipment_equipment_id", "exercise_equipment", ["equipment_id"]
    )

    op.create_table(
        "training_plans",
        *_sync_columns(),
        sa.Column("owner_user_id", sa.String(length=36), nullable=True),
        sa.Column("name", sa.String(length=160), nullable=False),
        sa.Column("description", sa.Text(), nullable=True),
        sa.Column("goal", sa.String(length=120), nullable=True),
        sa.Column("is_system", sa.Boolean(), nullable=False),
        sa.Column("is_active", sa.Boolean(), nullable=False),
        _fk("training_plans", "owner_user_id", "users", ondelete="SET NULL"),
        _pk("training_plans"),
    )
    _create_sync_indexes("training_plans")
    op.create_index("ix_training_plans_owner_user_id", "training_plans", ["owner_user_id"])
    op.create_index("ix_training_plans_name", "training_plans", ["name"])
    op.create_index(
        "ix_training_plans_owner_active", "training_plans", ["owner_user_id", "is_active"]
    )

    op.create_table(
        "plan_versions",
        *_sync_columns(),
        sa.Column("training_plan_id", sa.String(length=36), nullable=False),
        sa.Column("version_number", sa.Integer(), nullable=False),
        sa.Column("status", sa.String(length=16), nullable=False),
        sa.Column("weekly_frequency", sa.Integer(), nullable=False),
        sa.Column("min_rest_days", sa.Integer(), nullable=False),
        sa.Column("fatigue_threshold", sa.Integer(), nullable=False),
        sa.Column("initial_reduced_weeks", sa.Integer(), nullable=False),
        sa.Column("initial_set_count", sa.Integer(), nullable=False),
        sa.Column("config_json", sa.JSON(), nullable=False),
        sa.Column("changelog", sa.Text(), nullable=True),
        sa.Column("published_at", sa.DateTime(), nullable=True),
        sa.Column("published_by_user_id", sa.String(length=36), nullable=True),
        _fk("plan_versions", "training_plan_id", "training_plans", ondelete="CASCADE"),
        _fk("plan_versions", "published_by_user_id", "users", ondelete="SET NULL"),
        sa.UniqueConstraint(
            "training_plan_id", "version_number", name="uq_plan_versions_plan_number"
        ),
        sa.CheckConstraint(
            "version_number > 0", name=op.f('ck_plan_versions_version_number_positive')
        ),
        sa.CheckConstraint(
            "status IN ('draft', 'published', 'archived')",
            name=op.f('ck_plan_versions_status_allowed'),
        ),
        sa.CheckConstraint(
            "weekly_frequency BETWEEN 1 AND 7",
            name=op.f('ck_plan_versions_weekly_frequency_range'),
        ),
        sa.CheckConstraint(
            "min_rest_days >= 0", name=op.f('ck_plan_versions_min_rest_days_nonnegative')
        ),
        sa.CheckConstraint(
            "fatigue_threshold BETWEEN 1 AND 10",
            name=op.f('ck_plan_versions_fatigue_threshold_range'),
        ),
        sa.CheckConstraint(
            "initial_reduced_weeks >= 0",
            name=op.f('ck_plan_versions_initial_reduced_weeks_nonnegative'),
        ),
        sa.CheckConstraint(
            "initial_set_count > 0", name=op.f('ck_plan_versions_initial_set_count_positive')
        ),
        _pk("plan_versions"),
    )
    _create_sync_indexes("plan_versions")
    op.create_index("ix_plan_versions_training_plan_id", "plan_versions", ["training_plan_id"])
    op.create_index("ix_plan_versions_status", "plan_versions", ["status"])
    op.create_index("ix_plan_versions_published_at", "plan_versions", ["published_at"])
    op.create_index(
        "ix_plan_versions_published_by_user_id", "plan_versions", ["published_by_user_id"]
    )
    op.create_index(
        "ix_plan_versions_plan_status", "plan_versions", ["training_plan_id", "status"]
    )

    op.create_table(
        "plan_days",
        *_sync_columns(),
        sa.Column("plan_version_id", sa.String(length=36), nullable=False),
        sa.Column("day_code", sa.String(length=32), nullable=False),
        sa.Column("name", sa.String(length=160), nullable=False),
        sa.Column("sort_order", sa.Integer(), nullable=False),
        sa.Column("notes", sa.Text(), nullable=True),
        _fk("plan_days", "plan_version_id", "plan_versions", ondelete="CASCADE"),
        sa.UniqueConstraint(
            "plan_version_id", "day_code", name="uq_plan_days_version_code"
        ),
        sa.UniqueConstraint(
            "plan_version_id", "sort_order", name="uq_plan_days_version_order"
        ),
        _pk("plan_days"),
    )
    _create_sync_indexes("plan_days")
    op.create_index("ix_plan_days_plan_version_id", "plan_days", ["plan_version_id"])

    op.create_table(
        "plan_slots",
        *_sync_columns(),
        sa.Column("plan_day_id", sa.String(length=36), nullable=False),
        sa.Column("name", sa.String(length=160), nullable=False),
        sa.Column("target_muscle_group_id", sa.String(length=36), nullable=True),
        sa.Column("sort_order", sa.Integer(), nullable=False),
        sa.Column("notes", sa.Text(), nullable=True),
        sa.Column("selection_rule_json", sa.JSON(), nullable=False),
        _fk("plan_slots", "plan_day_id", "plan_days", ondelete="CASCADE"),
        _fk(
            "plan_slots", "target_muscle_group_id", "muscle_groups", ondelete="SET NULL"
        ),
        sa.UniqueConstraint("plan_day_id", "sort_order", name="uq_plan_slots_day_order"),
        sa.CheckConstraint("sort_order >= 0", name=op.f('ck_plan_slots_sort_order_nonnegative')),
        _pk("plan_slots"),
    )
    _create_sync_indexes("plan_slots")
    op.create_index("ix_plan_slots_plan_day_id", "plan_slots", ["plan_day_id"])
    op.create_index(
        "ix_plan_slots_target_muscle_group_id", "plan_slots", ["target_muscle_group_id"]
    )

    op.create_table(
        "plan_slot_options",
        *_sync_columns(),
        sa.Column("plan_slot_id", sa.String(length=36), nullable=False),
        sa.Column("exercise_id", sa.String(length=36), nullable=False),
        sa.Column("is_preferred", sa.Boolean(), nullable=False),
        sa.Column("sort_order", sa.Integer(), nullable=False),
        sa.Column("set_count", sa.Integer(), nullable=False),
        sa.Column("reps_min", sa.Integer(), nullable=True),
        sa.Column("reps_max", sa.Integer(), nullable=True),
        sa.Column("duration_seconds_min", sa.Integer(), nullable=True),
        sa.Column("duration_seconds_max", sa.Integer(), nullable=True),
        sa.Column("rir_min", sa.Numeric(precision=4, scale=1), nullable=True),
        sa.Column("rir_max", sa.Numeric(precision=4, scale=1), nullable=True),
        sa.Column("is_per_side", sa.Boolean(), nullable=False),
        sa.Column("prescription_json", sa.JSON(), nullable=False),
        _fk("plan_slot_options", "plan_slot_id", "plan_slots", ondelete="CASCADE"),
        _fk("plan_slot_options", "exercise_id", "exercises", ondelete="RESTRICT"),
        sa.UniqueConstraint(
            "plan_slot_id", "exercise_id", name="uq_plan_slot_options_exercise"
        ),
        sa.UniqueConstraint(
            "plan_slot_id", "sort_order", name="uq_plan_slot_options_order"
        ),
        sa.CheckConstraint("set_count > 0", name=op.f('ck_plan_slot_options_set_count_positive')),
        sa.CheckConstraint(
            "reps_min IS NULL OR reps_min >= 0",
            name=op.f('ck_plan_slot_options_reps_min_nonnegative'),
        ),
        sa.CheckConstraint(
            "reps_max IS NULL OR reps_max >= 0",
            name=op.f('ck_plan_slot_options_reps_max_nonnegative'),
        ),
        sa.CheckConstraint(
            "reps_min IS NULL OR reps_max IS NULL OR reps_min <= reps_max",
            name=op.f('ck_plan_slot_options_reps_range'),
        ),
        sa.CheckConstraint(
            "duration_seconds_min IS NULL OR duration_seconds_min >= 0",
            name=op.f('ck_plan_slot_options_duration_min_nonnegative'),
        ),
        sa.CheckConstraint(
            "duration_seconds_max IS NULL OR duration_seconds_max >= 0",
            name=op.f('ck_plan_slot_options_duration_max_nonnegative'),
        ),
        sa.CheckConstraint(
            "duration_seconds_min IS NULL OR duration_seconds_max IS NULL "
            "OR duration_seconds_min <= duration_seconds_max",
            name=op.f('ck_plan_slot_options_duration_range'),
        ),
        sa.CheckConstraint(
            "rir_min IS NULL OR rir_min >= 0",
            name=op.f('ck_plan_slot_options_rir_min_nonnegative'),
        ),
        sa.CheckConstraint(
            "rir_max IS NULL OR rir_max >= 0",
            name=op.f('ck_plan_slot_options_rir_max_nonnegative'),
        ),
        sa.CheckConstraint(
            "rir_min IS NULL OR rir_max IS NULL OR rir_min <= rir_max",
            name=op.f('ck_plan_slot_options_rir_range'),
        ),
        _pk("plan_slot_options"),
    )
    _create_sync_indexes("plan_slot_options")
    op.create_index(
        "ix_plan_slot_options_plan_slot_id", "plan_slot_options", ["plan_slot_id"]
    )
    op.create_index("ix_plan_slot_options_exercise_id", "plan_slot_options", ["exercise_id"])
    op.create_index("ix_plan_slot_options_is_preferred", "plan_slot_options", ["is_preferred"])

    op.create_table(
        "plan_assignments",
        *_sync_columns(),
        sa.Column("user_id", sa.String(length=36), nullable=False),
        sa.Column("plan_version_id", sa.String(length=36), nullable=False),
        sa.Column("status", sa.String(length=16), nullable=False),
        sa.Column("starts_on", sa.Date(), nullable=False),
        sa.Column("ends_on", sa.Date(), nullable=True),
        sa.Column("assigned_at", sa.DateTime(), nullable=False),
        sa.Column("assigned_by_user_id", sa.String(length=36), nullable=True),
        sa.Column("settings_json", sa.JSON(), nullable=False),
        _fk("plan_assignments", "user_id", "users", ondelete="CASCADE"),
        _fk("plan_assignments", "plan_version_id", "plan_versions", ondelete="RESTRICT"),
        _fk("plan_assignments", "assigned_by_user_id", "users", ondelete="SET NULL"),
        sa.UniqueConstraint(
            "user_id",
            "plan_version_id",
            "starts_on",
            name="uq_plan_assignments_user_version_start",
        ),
        sa.CheckConstraint(
            "status IN ('scheduled', 'active', 'completed', 'cancelled')",
            name=op.f('ck_plan_assignments_status_allowed'),
        ),
        sa.CheckConstraint(
            "ends_on IS NULL OR ends_on >= starts_on", name=op.f('ck_plan_assignments_date_range')
        ),
        _pk("plan_assignments"),
    )
    _create_sync_indexes("plan_assignments")
    op.create_index("ix_plan_assignments_user_id", "plan_assignments", ["user_id"])
    op.create_index(
        "ix_plan_assignments_plan_version_id", "plan_assignments", ["plan_version_id"]
    )
    op.create_index("ix_plan_assignments_status", "plan_assignments", ["status"])
    op.create_index(
        "ix_plan_assignments_assigned_by_user_id", "plan_assignments", ["assigned_by_user_id"]
    )
    op.create_index(
        "ix_plan_assignments_user_status_start",
        "plan_assignments",
        ["user_id", "status", "starts_on"],
    )

    op.create_table(
        "workout_sessions",
        *_sync_columns(),
        sa.Column("user_id", sa.String(length=36), nullable=False),
        sa.Column("client_id", sa.String(length=36), nullable=True),
        sa.Column("source_device", sa.String(length=16), nullable=False),
        sa.Column("client_version", sa.String(length=64), nullable=True),
        sa.Column("plan_assignment_id", sa.String(length=36), nullable=True),
        sa.Column("plan_version_id", sa.String(length=36), nullable=True),
        sa.Column("plan_day_id", sa.String(length=36), nullable=True),
        sa.Column("local_date", sa.Date(), nullable=False),
        sa.Column("status", sa.String(length=16), nullable=False),
        sa.Column("training_week", sa.Integer(), nullable=True),
        sa.Column("ab_state", sa.String(length=16), nullable=True),
        sa.Column("started_at", sa.DateTime(), nullable=True),
        sa.Column("completed_at", sa.DateTime(), nullable=True),
        sa.Column("notes", sa.Text(), nullable=True),
        sa.Column("plan_snapshot", sa.JSON(), nullable=False),
        sa.Column("metadata", sa.JSON(), nullable=False),
        _fk("workout_sessions", "user_id", "users", ondelete="CASCADE"),
        _fk(
            "workout_sessions", "plan_assignment_id", "plan_assignments", ondelete="SET NULL"
        ),
        _fk("workout_sessions", "plan_version_id", "plan_versions", ondelete="SET NULL"),
        _fk("workout_sessions", "plan_day_id", "plan_days", ondelete="SET NULL"),
        sa.UniqueConstraint(
            "user_id", "client_id", name="uq_workout_sessions_user_client"
        ),
        sa.CheckConstraint(
            "source_device IN ('android', 'windows', 'web', 'api')",
            name=op.f('ck_workout_sessions_source_device_allowed'),
        ),
        sa.CheckConstraint(
            "status IN ('planned', 'in_progress', 'completed', 'cancelled')",
            name=op.f('ck_workout_sessions_status_allowed'),
        ),
        sa.CheckConstraint(
            "training_week IS NULL OR training_week > 0",
            name=op.f('ck_workout_sessions_training_week_positive'),
        ),
        sa.CheckConstraint(
            "completed_at IS NULL OR started_at IS NULL OR completed_at >= started_at",
            name=op.f('ck_workout_sessions_time_range'),
        ),
        _pk("workout_sessions"),
    )
    _create_sync_indexes("workout_sessions")
    op.create_index("ix_workout_sessions_user_id", "workout_sessions", ["user_id"])
    op.create_index(
        "ix_workout_sessions_plan_assignment_id", "workout_sessions", ["plan_assignment_id"]
    )
    op.create_index(
        "ix_workout_sessions_plan_version_id", "workout_sessions", ["plan_version_id"]
    )
    op.create_index("ix_workout_sessions_plan_day_id", "workout_sessions", ["plan_day_id"])
    op.create_index("ix_workout_sessions_local_date", "workout_sessions", ["local_date"])
    op.create_index("ix_workout_sessions_status", "workout_sessions", ["status"])
    op.create_index(
        "ix_workout_sessions_user_date", "workout_sessions", ["user_id", "local_date"]
    )
    op.create_index(
        "ix_workout_sessions_user_updated", "workout_sessions", ["user_id", "updated_at"]
    )

    op.create_table(
        "workout_sets",
        *_sync_columns(),
        sa.Column("workout_session_id", sa.String(length=36), nullable=False),
        sa.Column("client_set_id", sa.String(length=36), nullable=True),
        sa.Column("exercise_id", sa.String(length=36), nullable=True),
        sa.Column("plan_slot_id", sa.String(length=36), nullable=True),
        sa.Column("set_number", sa.Integer(), nullable=False),
        sa.Column("set_type", sa.String(length=16), nullable=False),
        sa.Column("weight_kg", sa.Numeric(precision=8, scale=2), nullable=True),
        sa.Column("reps", sa.Integer(), nullable=True),
        sa.Column("duration_seconds", sa.Integer(), nullable=True),
        sa.Column("distance_meters", sa.Numeric(precision=10, scale=2), nullable=True),
        sa.Column("rir", sa.Numeric(precision=4, scale=1), nullable=True),
        sa.Column("completed_at", sa.DateTime(), nullable=True),
        sa.Column("notes", sa.Text(), nullable=True),
        sa.Column("exercise_snapshot", sa.JSON(), nullable=False),
        sa.Column("prescription_snapshot", sa.JSON(), nullable=False),
        _fk("workout_sets", "workout_session_id", "workout_sessions", ondelete="CASCADE"),
        _fk("workout_sets", "exercise_id", "exercises", ondelete="SET NULL"),
        _fk("workout_sets", "plan_slot_id", "plan_slots", ondelete="SET NULL"),
        sa.UniqueConstraint(
            "workout_session_id", "client_set_id", name="uq_workout_sets_session_client"
        ),
        sa.CheckConstraint(
            "set_number > 0", name=op.f('ck_workout_sets_set_number_positive')
        ),
        sa.CheckConstraint(
            "set_type IN ('warmup', 'working', 'drop', 'failure', 'cardio', 'other')",
            name=op.f('ck_workout_sets_set_type_allowed'),
        ),
        sa.CheckConstraint(
            "weight_kg IS NULL OR weight_kg >= 0", name=op.f('ck_workout_sets_weight_nonnegative')
        ),
        sa.CheckConstraint(
            "reps IS NULL OR reps >= 0", name=op.f('ck_workout_sets_reps_nonnegative')
        ),
        sa.CheckConstraint(
            "duration_seconds IS NULL OR duration_seconds >= 0",
            name=op.f('ck_workout_sets_duration_nonnegative'),
        ),
        sa.CheckConstraint(
            "distance_meters IS NULL OR distance_meters >= 0",
            name=op.f('ck_workout_sets_distance_nonnegative'),
        ),
        sa.CheckConstraint("rir IS NULL OR rir >= 0", name=op.f('ck_workout_sets_rir_nonnegative')),
        _pk("workout_sets"),
    )
    _create_sync_indexes("workout_sets")
    op.create_index(
        "ix_workout_sets_workout_session_id", "workout_sets", ["workout_session_id"]
    )
    op.create_index("ix_workout_sets_exercise_id", "workout_sets", ["exercise_id"])
    op.create_index("ix_workout_sets_plan_slot_id", "workout_sets", ["plan_slot_id"])
    op.create_index(
        "ix_workout_sets_session_number", "workout_sets", ["workout_session_id", "set_number"]
    )

    op.create_table(
        "daily_readiness",
        *_sync_columns(),
        sa.Column("user_id", sa.String(length=36), nullable=False),
        sa.Column("local_date", sa.Date(), nullable=False),
        sa.Column("sleep_quality", sa.Integer(), nullable=True),
        sa.Column("fatigue", sa.Integer(), nullable=True),
        sa.Column("soreness", sa.Integer(), nullable=True),
        sa.Column("stress", sa.Integer(), nullable=True),
        sa.Column("motivation", sa.Integer(), nullable=True),
        sa.Column("notes", sa.Text(), nullable=True),
        sa.Column("metrics", sa.JSON(), nullable=False),
        _fk("daily_readiness", "user_id", "users", ondelete="CASCADE"),
        sa.UniqueConstraint(
            "user_id", "local_date", name="uq_daily_readiness_user_date"
        ),
        sa.CheckConstraint(
            "sleep_quality IS NULL OR sleep_quality BETWEEN 1 AND 5",
            name=op.f('ck_daily_readiness_sleep_quality_range'),
        ),
        sa.CheckConstraint(
            "fatigue IS NULL OR fatigue BETWEEN 1 AND 10",
            name=op.f('ck_daily_readiness_fatigue_range'),
        ),
        sa.CheckConstraint(
            "soreness IS NULL OR soreness BETWEEN 1 AND 5",
            name=op.f('ck_daily_readiness_soreness_range'),
        ),
        sa.CheckConstraint(
            "stress IS NULL OR stress BETWEEN 1 AND 5",
            name=op.f('ck_daily_readiness_stress_range'),
        ),
        sa.CheckConstraint(
            "motivation IS NULL OR motivation BETWEEN 1 AND 5",
            name=op.f('ck_daily_readiness_motivation_range'),
        ),
        _pk("daily_readiness"),
    )
    _create_sync_indexes("daily_readiness")
    op.create_index("ix_daily_readiness_user_id", "daily_readiness", ["user_id"])
    op.create_index("ix_daily_readiness_local_date", "daily_readiness", ["local_date"])
    op.create_index(
        "ix_daily_readiness_user_updated", "daily_readiness", ["user_id", "updated_at"]
    )

    op.create_table(
        "cardio_sessions",
        *_sync_columns(),
        sa.Column("user_id", sa.String(length=36), nullable=False),
        sa.Column("client_id", sa.String(length=36), nullable=True),
        sa.Column("source_device", sa.String(length=16), nullable=False),
        sa.Column("client_version", sa.String(length=64), nullable=True),
        sa.Column("local_date", sa.Date(), nullable=False),
        sa.Column("activity_type", sa.String(length=64), nullable=False),
        sa.Column("started_at", sa.DateTime(), nullable=True),
        sa.Column("completed_at", sa.DateTime(), nullable=True),
        sa.Column("duration_seconds", sa.Integer(), nullable=False),
        sa.Column("distance_meters", sa.Numeric(precision=10, scale=2), nullable=True),
        sa.Column("average_heart_rate", sa.Integer(), nullable=True),
        sa.Column("calories", sa.Numeric(precision=10, scale=2), nullable=True),
        sa.Column("notes", sa.Text(), nullable=True),
        sa.Column("metrics", sa.JSON(), nullable=False),
        _fk("cardio_sessions", "user_id", "users", ondelete="CASCADE"),
        sa.UniqueConstraint(
            "user_id", "client_id", name="uq_cardio_sessions_user_client"
        ),
        sa.CheckConstraint(
            "source_device IN ('android', 'windows', 'web', 'api')",
            name=op.f('ck_cardio_sessions_source_device_allowed'),
        ),
        sa.CheckConstraint(
            "duration_seconds >= 0", name=op.f('ck_cardio_sessions_duration_nonnegative')
        ),
        sa.CheckConstraint(
            "distance_meters IS NULL OR distance_meters >= 0",
            name=op.f('ck_cardio_sessions_distance_nonnegative'),
        ),
        sa.CheckConstraint(
            "average_heart_rate IS NULL OR average_heart_rate > 0",
            name=op.f('ck_cardio_sessions_heart_rate_positive'),
        ),
        sa.CheckConstraint(
            "calories IS NULL OR calories >= 0",
            name=op.f('ck_cardio_sessions_calories_nonnegative'),
        ),
        sa.CheckConstraint(
            "completed_at IS NULL OR started_at IS NULL OR completed_at >= started_at",
            name=op.f('ck_cardio_sessions_time_range'),
        ),
        _pk("cardio_sessions"),
    )
    _create_sync_indexes("cardio_sessions")
    op.create_index("ix_cardio_sessions_user_id", "cardio_sessions", ["user_id"])
    op.create_index("ix_cardio_sessions_local_date", "cardio_sessions", ["local_date"])
    op.create_index("ix_cardio_sessions_activity_type", "cardio_sessions", ["activity_type"])
    op.create_index(
        "ix_cardio_sessions_user_date", "cardio_sessions", ["user_id", "local_date"]
    )
    op.create_index(
        "ix_cardio_sessions_user_updated", "cardio_sessions", ["user_id", "updated_at"]
    )

    op.create_table(
        "sync_changes",
        sa.Column(
            "sequence",
            sa.BigInteger().with_variant(sa.Integer(), "sqlite"),
            autoincrement=True,
            nullable=False,
        ),
        sa.Column("id", sa.String(length=36), nullable=False),
        sa.Column("entity_type", sa.String(length=64), nullable=False),
        sa.Column("entity_id", sa.String(length=36), nullable=False),
        sa.Column("entity_version", sa.Integer(), nullable=False),
        sa.Column("operation", sa.String(length=16), nullable=False),
        sa.Column("payload", sa.JSON(), nullable=True),
        sa.Column("actor_user_id", sa.String(length=36), nullable=True),
        sa.Column("request_id", sa.String(length=64), nullable=True),
        sa.Column("changed_at", sa.DateTime(), nullable=False),
        _fk("sync_changes", "actor_user_id", "users", ondelete="SET NULL"),
        sa.PrimaryKeyConstraint("sequence", name="pk_sync_changes"),
        sa.UniqueConstraint("id", name="uq_sync_changes_id"),
        sa.CheckConstraint(
            "operation IN ('create', 'update', 'delete')",
            name=op.f('ck_sync_changes_operation_allowed'),
        ),
    )
    op.create_index("ix_sync_changes_entity_type", "sync_changes", ["entity_type"])
    op.create_index("ix_sync_changes_entity_id", "sync_changes", ["entity_id"])
    op.create_index("ix_sync_changes_operation", "sync_changes", ["operation"])
    op.create_index("ix_sync_changes_actor_user_id", "sync_changes", ["actor_user_id"])
    op.create_index("ix_sync_changes_request_id", "sync_changes", ["request_id"])
    op.create_index("ix_sync_changes_changed_at", "sync_changes", ["changed_at"])
    op.create_index(
        "ix_sync_changes_entity",
        "sync_changes",
        ["entity_type", "entity_id", "sequence"],
    )

    op.create_table(
        "idempotency_keys",
        *_sync_columns(),
        sa.Column("user_id", sa.String(length=36), nullable=False),
        sa.Column("scope", sa.String(length=64), nullable=False),
        sa.Column("key", sa.String(length=128), nullable=False),
        sa.Column("request_hash", sa.String(length=128), nullable=False),
        sa.Column("response_status", sa.Integer(), nullable=True),
        sa.Column("response_body", sa.JSON(), nullable=True),
        sa.Column("resource_type", sa.String(length=64), nullable=True),
        sa.Column("resource_id", sa.String(length=36), nullable=True),
        sa.Column("expires_at", sa.DateTime(), nullable=False),
        sa.Column("locked_at", sa.DateTime(), nullable=True),
        _fk("idempotency_keys", "user_id", "users", ondelete="CASCADE"),
        sa.UniqueConstraint(
            "user_id", "scope", "key", name="uq_idempotency_keys_user_scope_key"
        ),
        sa.CheckConstraint(
            "response_status IS NULL OR response_status BETWEEN 100 AND 599",
            name=op.f('ck_idempotency_keys_response_status_range'),
        ),
        _pk("idempotency_keys"),
    )
    _create_sync_indexes("idempotency_keys")
    op.create_index("ix_idempotency_keys_user_id", "idempotency_keys", ["user_id"])
    op.create_index(
        "ix_idempotency_keys_expiry", "idempotency_keys", ["expires_at", "deleted_at"]
    )

    op.create_table(
        "audit_logs",
        *_sync_columns(),
        sa.Column("actor_user_id", sa.String(length=36), nullable=True),
        sa.Column("action", sa.String(length=64), nullable=False),
        sa.Column("entity_type", sa.String(length=64), nullable=False),
        sa.Column("entity_id", sa.String(length=36), nullable=True),
        sa.Column("request_id", sa.String(length=64), nullable=True),
        sa.Column("ip_address", sa.String(length=45), nullable=True),
        sa.Column("user_agent", sa.String(length=512), nullable=True),
        sa.Column("before", sa.JSON(), nullable=True),
        sa.Column("after", sa.JSON(), nullable=True),
        sa.Column("metadata", sa.JSON(), nullable=True),
        _fk("audit_logs", "actor_user_id", "users", ondelete="SET NULL"),
        _pk("audit_logs"),
    )
    _create_sync_indexes("audit_logs")
    op.create_index("ix_audit_logs_actor_user_id", "audit_logs", ["actor_user_id"])
    op.create_index("ix_audit_logs_action", "audit_logs", ["action"])
    op.create_index("ix_audit_logs_entity_type", "audit_logs", ["entity_type"])
    op.create_index("ix_audit_logs_entity_id", "audit_logs", ["entity_id"])
    op.create_index("ix_audit_logs_request_id", "audit_logs", ["request_id"])
    op.create_index(
        "ix_audit_logs_entity_created",
        "audit_logs",
        ["entity_type", "entity_id", "created_at"],
    )
    op.create_index(
        "ix_audit_logs_actor_created", "audit_logs", ["actor_user_id", "created_at"]
    )

    op.create_table(
        "schema_versions",
        *_sync_columns(),
        sa.Column("schema_version", sa.String(length=64), nullable=False),
        sa.Column("api_version", sa.String(length=32), nullable=False),
        sa.Column("minimum_client_version", sa.String(length=64), nullable=True),
        sa.Column("checksum", sa.String(length=128), nullable=True),
        sa.Column("applied_at", sa.DateTime(), nullable=False),
        sa.Column("notes", sa.Text(), nullable=True),
        sa.UniqueConstraint("schema_version", name="uq_schema_versions_schema_version"),
        _pk("schema_versions"),
    )
    _create_sync_indexes("schema_versions")


def downgrade() -> None:
    for table in reversed(TABLES):
        op.drop_table(table)

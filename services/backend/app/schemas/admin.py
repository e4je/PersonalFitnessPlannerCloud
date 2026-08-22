from __future__ import annotations

from datetime import date, datetime
from typing import Any, Literal
from uuid import UUID

from pydantic import AliasChoices, BaseModel, ConfigDict, Field, field_validator, model_validator


JsonObject = dict[str, Any]
JsonValue = dict[str, Any] | list[Any] | str | int | float | bool | None


class AdminInput(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)


class NestedEntityInput(AdminInput):
    """Accept round-tripped client DTO metadata without using it for writes."""

    model_config = ConfigDict(extra="ignore", str_strip_whitespace=True)

    version: int | None = Field(default=None, ge=1)
    created_at: datetime | None = None
    updated_at: datetime | None = None
    deleted_at: datetime | None = None


class EquipmentCreate(AdminInput):
    id: UUID | None = None
    version: int | None = Field(default=None, ge=1)
    code: str | None = Field(default=None, min_length=1, max_length=64)
    name: str = Field(min_length=1, max_length=160)
    description: str = Field(default="", max_length=4000)
    category: str = Field(default="general", min_length=1, max_length=80)
    is_active: bool = True
    metadata_json: JsonObject | None = None
    # Compatibility fields used by the desktop client are folded into metadata.
    brand: str | None = Field(default=None, max_length=160)
    model: str | None = Field(default=None, max_length=160)
    notes: str | None = Field(default=None, max_length=4000)


class EquipmentPatch(AdminInput):
    id: UUID | None = None
    expected_version: int = Field(ge=1, validation_alias=AliasChoices("expected_version", "version"))
    code: str | None = Field(default=None, min_length=1, max_length=64)
    name: str | None = Field(default=None, min_length=1, max_length=160)
    description: str | None = Field(default=None, max_length=4000)
    category: str | None = Field(default=None, min_length=1, max_length=80)
    is_active: bool | None = None
    metadata_json: JsonObject | None = None
    brand: str | None = Field(default=None, max_length=160)
    model: str | None = Field(default=None, max_length=160)
    notes: str | None = Field(default=None, max_length=4000)

    @model_validator(mode="after")
    def require_change(self) -> "EquipmentPatch":
        if not (self.model_fields_set - {"id", "expected_version"}):
            raise ValueError("At least one field must be supplied")
        return self


class ExerciseCreate(AdminInput):
    id: UUID | None = None
    version: int | None = Field(default=None, ge=1)
    code: str | None = Field(default=None, min_length=1, max_length=64)
    name: str = Field(min_length=1, max_length=160)
    description: str = Field(default="", max_length=8000)
    body_part: str | None = Field(default=None, max_length=80)
    movement_pattern: str | None = Field(default=None, max_length=80)
    difficulty: str = Field(default="beginner", min_length=1, max_length=32)
    is_unilateral: bool = False
    is_active: bool = True
    metadata_json: JsonObject | None = None
    cues: str | list[str] | None = None
    common_mistakes: str | None = Field(default=None, max_length=8000)
    equipment_id: UUID | None = None
    equipment_ids: list[UUID] = Field(default_factory=list)
    alternative_exercise_ids: list[UUID] = Field(default_factory=list)
    equipment_name: str | None = Field(default=None, max_length=500)
    prescription: str | None = Field(default=None, max_length=4000)
    alternatives: str | list[str] | None = None
    default_sets: int | None = Field(default=None, ge=1, le=20)
    rep_min: int | None = Field(default=None, ge=1, le=10000)
    rep_max: int | None = Field(default=None, ge=1, le=10000)
    rep_unit: str | None = Field(default=None, max_length=32)

    @model_validator(mode="after")
    def validate_reps(self) -> "ExerciseCreate":
        if self.rep_min is not None and self.rep_max is not None and self.rep_max < self.rep_min:
            raise ValueError("rep_max must be greater than or equal to rep_min")
        return self


class ExercisePatch(AdminInput):
    id: UUID | None = None
    expected_version: int = Field(ge=1, validation_alias=AliasChoices("expected_version", "version"))
    code: str | None = Field(default=None, min_length=1, max_length=64)
    name: str | None = Field(default=None, min_length=1, max_length=160)
    description: str | None = Field(default=None, max_length=8000)
    body_part: str | None = Field(default=None, max_length=80)
    movement_pattern: str | None = Field(default=None, max_length=80)
    difficulty: str | None = Field(default=None, min_length=1, max_length=32)
    is_unilateral: bool | None = None
    is_active: bool | None = None
    metadata_json: JsonObject | None = None
    cues: str | list[str] | None = None
    common_mistakes: str | None = Field(default=None, max_length=8000)
    equipment_id: UUID | None = None
    equipment_ids: list[UUID] | None = None
    alternative_exercise_ids: list[UUID] | None = None
    equipment_name: str | None = Field(default=None, max_length=500)
    prescription: str | None = Field(default=None, max_length=4000)
    alternatives: str | list[str] | None = None
    default_sets: int | None = Field(default=None, ge=1, le=20)
    rep_min: int | None = Field(default=None, ge=1, le=10000)
    rep_max: int | None = Field(default=None, ge=1, le=10000)
    rep_unit: str | None = Field(default=None, max_length=32)

    @model_validator(mode="after")
    def validate_patch(self) -> "ExercisePatch":
        if not (self.model_fields_set - {"id", "expected_version"}):
            raise ValueError("At least one field must be supplied")
        if self.rep_min is not None and self.rep_max is not None and self.rep_max < self.rep_min:
            raise ValueError("rep_max must be greater than or equal to rep_min")
        return self


class PlanSlotOptionInput(NestedEntityInput):
    id: UUID | None = None
    plan_slot_id: UUID | None = None
    exercise_id: UUID
    is_preferred: bool = False
    sort_order: int = Field(default=0, ge=0)
    set_count: int = Field(default=1, ge=1, le=100)
    reps_min: int | None = Field(default=None, ge=0, le=10000, validation_alias=AliasChoices("reps_min", "rep_min"))
    reps_max: int | None = Field(default=None, ge=0, le=10000, validation_alias=AliasChoices("reps_max", "rep_max"))
    duration_seconds_min: int | None = Field(default=None, ge=0, le=86400)
    duration_seconds_max: int | None = Field(default=None, ge=0, le=86400)
    rir_min: int | None = Field(default=2, ge=0, le=10)
    rir_max: int | None = Field(default=3, ge=0, le=10)
    is_per_side: bool = False
    prescription_json: JsonObject | None = None
    # Wire compatibility fields retained in prescription_json by the service.
    equipment_id: UUID | None = None
    intro_set_count: int | None = Field(default=None, ge=1, le=100)
    intro_weeks: int | None = Field(default=None, ge=0, le=52)
    rep_unit: str | None = Field(default=None, max_length=32)
    rest_seconds: int | None = Field(default=None, ge=0, le=3600)
    exercise_name: str | None = Field(default=None, max_length=160)
    equipment: str | None = Field(default=None, max_length=500)


class PlanSlotInput(NestedEntityInput):
    id: UUID | None = None
    plan_day_id: UUID | None = None
    name: str = Field(
        min_length=1,
        max_length=160,
        validation_alias=AliasChoices("name", "body_part"),
    )
    target_muscle_group_id: UUID | None = None
    sort_order: int = Field(default=0, ge=0, validation_alias=AliasChoices("sort_order", "position"))
    notes: str = Field(default="", max_length=8000, validation_alias=AliasChoices("notes", "cues"))
    selection_rule_json: JsonObject | str | None = None
    options: list[PlanSlotOptionInput] = Field(default_factory=list)
    common_mistakes: str | None = Field(default=None, max_length=8000)
    seat_position: str | None = Field(default=None, max_length=256)
    bench_angle: str | None = Field(default=None, max_length=256)
    machine_number: str | None = Field(default=None, max_length=256)


class PlanDayInput(NestedEntityInput):
    id: UUID | None = None
    plan_version_id: UUID | None = None
    day_code: str = Field(
        min_length=1,
        max_length=16,
        validation_alias=AliasChoices("day_code", "code"),
    )
    name: str = Field(default="", max_length=160)
    sort_order: int = Field(default=0, ge=0)
    notes: str = Field(default="", max_length=8000)
    slots: list[PlanSlotInput] = Field(
        default_factory=list,
        validation_alias=AliasChoices("slots", "items"),
    )

    @field_validator("day_code")
    @classmethod
    def normalize_day_code(cls, value: str) -> str:
        return value.strip().upper()


class PlanCreate(AdminInput):
    id: UUID | None = None
    name: str = Field(min_length=1, max_length=160)
    description: str = Field(default="", max_length=8000)
    goal: str = Field(default="general_fitness", min_length=1, max_length=80)
    is_system: bool = False
    is_active: bool = True


class PlanVersionCreate(AdminInput):
    id: UUID | None = None
    plan_id: UUID | None = None
    plan_name: str | None = Field(default=None, max_length=160)
    version_number: int | None = Field(default=None, ge=1)
    status: str | None = Field(default=None, max_length=16)
    base_plan_version_id: UUID | None = None
    weekly_frequency: int = Field(default=3, ge=1, le=7)
    min_rest_days: int = Field(default=1, ge=0, le=14)
    fatigue_threshold: int = Field(default=8, ge=1, le=10)
    initial_reduced_weeks: int = Field(
        default=2,
        ge=0,
        le=52,
        validation_alias=AliasChoices("initial_reduced_weeks", "intro_weeks"),
    )
    initial_set_count: int = Field(
        default=2,
        ge=1,
        le=20,
        validation_alias=AliasChoices("initial_set_count", "intro_max_sets"),
    )
    config_json: JsonValue = Field(default_factory=dict, validation_alias=AliasChoices("config_json", "snapshot_json"))
    changelog: str = Field(default="", max_length=8000)
    days: list[PlanDayInput] = Field(default_factory=list)


class PlanVersionPatch(AdminInput):
    expected_version: int = Field(ge=1)
    weekly_frequency: int | None = Field(default=None, ge=1, le=7)
    min_rest_days: int | None = Field(default=None, ge=0, le=14)
    fatigue_threshold: int | None = Field(default=None, ge=1, le=10)
    initial_reduced_weeks: int | None = Field(
        default=None,
        ge=0,
        le=52,
        validation_alias=AliasChoices("initial_reduced_weeks", "intro_weeks"),
    )
    initial_set_count: int | None = Field(
        default=None,
        ge=1,
        le=20,
        validation_alias=AliasChoices("initial_set_count", "intro_max_sets"),
    )
    config_json: JsonValue = Field(default=None, validation_alias=AliasChoices("config_json", "snapshot_json"))
    changelog: str | None = Field(default=None, max_length=8000)
    days: list[PlanDayInput] | None = None

    @model_validator(mode="after")
    def require_change(self) -> "PlanVersionPatch":
        if not (self.model_fields_set - {"expected_version"}):
            raise ValueError("At least one field must be supplied")
        return self


class PlanVersionPublish(AdminInput):
    expected_version: int | None = Field(default=None, ge=1)


class AssignmentCreate(AdminInput):
    id: UUID | None = None
    user_id: UUID | None = None
    plan_version_id: UUID
    status: str = Field(default="active", min_length=1, max_length=32)
    starts_on: date = Field(validation_alias=AliasChoices("starts_on", "start_local_date"))
    ends_on: date | None = Field(default=None, validation_alias=AliasChoices("ends_on", "end_local_date"))
    assigned_at: datetime | None = None
    settings_json: JsonObject | None = None
    is_active: bool | None = None

    @model_validator(mode="after")
    def validate_dates(self) -> "AssignmentCreate":
        if self.ends_on is not None and self.ends_on < self.starts_on:
            raise ValueError("ends_on must not be before starts_on")
        return self


class AuditLogResponse(BaseModel):
    id: str
    actor_user_id: str | None = None
    action: str
    entity_type: str
    entity_id: str | None = None
    request_id: str | None = None
    ip_address: str | None = None
    before_json: JsonObject | None = None
    after_json: JsonObject | None = None
    metadata_json: JsonObject | None = None
    created_at: datetime


class AuditLogPage(BaseModel):
    items: list[AuditLogResponse]
    cursor: str | None = None
    next_cursor: str | None = None
    has_more: bool = False


class SyncStatusResponse(BaseModel):
    server_time: datetime
    status: str = "healthy"
    latest_sequence: int | None = None
    changes_last_24_hours: int = 0
    pending_operations: int = 0
    failed_operations: int = 0
    message: str | None = None


class RegistrationSettingPatch(AdminInput):
    enabled: bool


class RegistrationSettingResponse(BaseModel):
    key: str = "registration_enabled"
    enabled: bool
    updated_at: datetime | None = None
    updated_by_user_id: str | None = None


class AdminUserCreate(AdminInput):
    email: str = Field(min_length=3, max_length=254)
    username: str = Field(min_length=3, max_length=64)
    password: str = Field(min_length=12, max_length=1024)
    display_name: str = Field(min_length=1, max_length=120)
    timezone: str = Field(default="Asia/Shanghai", min_length=1, max_length=64)
    weight_unit: Literal["KG", "LB"] = "KG"
    roles: list[str] = Field(default_factory=lambda: ["user"])


class AdminUserPatch(AdminInput):
    expected_version: int = Field(ge=1)
    display_name: str | None = Field(default=None, min_length=1, max_length=120)
    timezone: str | None = Field(default=None, min_length=1, max_length=64)
    weight_unit: Literal["KG", "LB"] | None = None
    is_active: bool | None = None
    password: str | None = Field(default=None, min_length=12, max_length=1024)
    roles: list[str] | None = None

    @model_validator(mode="after")
    def require_change(self) -> "AdminUserPatch":
        if not (self.model_fields_set - {"expected_version"}):
            raise ValueError("At least one field must be supplied")
        return self


class AdminUserResponse(BaseModel):
    id: str
    email: str
    username: str
    display_name: str
    timezone: str
    weight_unit: str
    is_active: bool
    is_superuser: bool
    roles: list[str] = Field(default_factory=list)
    version: int
    created_at: datetime
    updated_at: datetime
    last_login_at: datetime | None = None


class AdminUserPage(BaseModel):
    items: list[AdminUserResponse]
    cursor: str | None = None
    next_cursor: str | None = None
    has_more: bool = False


class AdminPlanSummary(BaseModel):
    id: str
    plan_id: str
    plan_name: str
    version_number: int
    status: str
    version: int
    weekly_frequency: int
    updated_at: datetime
    published_at: datetime | None = None


class AdminPlanPage(BaseModel):
    items: list[AdminPlanSummary]
    cursor: str | None = None
    next_cursor: str | None = None
    has_more: bool = False


class AdminUserOverview(BaseModel):
    user: AdminUserResponse
    assignments: list[JsonObject] = Field(default_factory=list)
    plans: list[JsonObject] = Field(default_factory=list)
    workout_sessions: list[JsonObject] = Field(default_factory=list)
    readiness: list[JsonObject] = Field(default_factory=list)
    cardio_sessions: list[JsonObject] = Field(default_factory=list)

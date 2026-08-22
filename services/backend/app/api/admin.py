from __future__ import annotations

import re
import unicodedata
from datetime import UTC, datetime, timedelta
from typing import Annotated, Any, NoReturn

from fastapi import APIRouter, Depends, HTTPException, Query, Request, status
from sqlalchemy import func, or_, select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session, selectinload

from app.api.dependencies import get_current_user, get_db, require_roles
from app.db.base import uuid4_str
from app.models import (
    AuditLog,
    CardioSession,
    DailyReadiness,
    Equipment,
    Exercise,
    ExerciseAlternative,
    ExerciseCue,
    ExerciseEquipment,
    PlanAssignment,
    PlanVersion,
    RefreshToken,
    Role,
    SyncChange,
    SystemSetting,
    TrainingPlan,
    User,
    UserRole,
    WorkoutSession,
)
from app.repositories.common import (
    EntityNotFoundError,
    OptimisticLockError,
    add_audit_log,
    add_sync_change,
    entity_dict,
    optimistic_patch,
    require_active,
)
from app.schemas.admin import (
    AdminPlanPage,
    AdminPlanSummary,
    AdminUserCreate,
    AdminUserOverview,
    AdminUserPage,
    AdminUserPatch,
    AdminUserResponse,
    AssignmentCreate,
    AuditLogPage,
    AuditLogResponse,
    EquipmentCreate,
    EquipmentPatch,
    ExerciseCreate,
    ExercisePatch,
    PlanCreate,
    PlanVersionCreate,
    PlanVersionPatch,
    PlanVersionPublish,
    RegistrationSettingPatch,
    RegistrationSettingResponse,
    SyncStatusResponse,
)
from app.schemas.plans import PlanAssignmentOut
from app.services.plans import (
    PlanValidationError,
    PublishedPlanImmutableError,
    create_assignment,
    create_plan_version,
    patch_plan_version,
    publish_plan_version,
    serialize_plan_version,
)
from app.services.accounts import AccountValidationError, active_role_names, create_account, replace_user_roles, validate_password, validate_timezone
from app.services.serialization import (
    assignment_to_dict,
)
from app.services.system_settings import registration_is_enabled, set_registration_enabled
from app.api.cardio import serialize_cardio
from app.api.readiness import serialize_readiness
from app.api.workouts import serialize_workout


router = APIRouter(
    prefix="/admin",
    tags=["administration"],
    dependencies=[Depends(require_roles("admin"))],
)


def _request_context(request: Request) -> dict[str, str | None]:
    return {
        "request_id": request.headers.get("X-Request-ID"),
        "ip_address": request.client.host if request.client else None,
        "user_agent": request.headers.get("User-Agent"),
    }


def _domain_error(
    db: Session,
    exc: Exception,
    *,
    actor_user_id: str | None = None,
    request: Request | None = None,
    entity_type: str = "unknown",
    entity_id: str | None = None,
) -> NoReturn:
    db.rollback()
    if isinstance(exc, OptimisticLockError):
        if actor_user_id is not None:
            context = _request_context(request) if request is not None else {}
            add_audit_log(
                db,
                actor_user_id=actor_user_id,
                action="admin.version_conflict",
                entity_type=entity_type,
                entity_id=entity_id,
                after=exc.server_copy,
                metadata={"reason": "optimistic_lock_failed"},
                **context,
            )
            db.commit()
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail={
                "code": "version_conflict",
                "message": str(exc),
                "server_copy": exc.server_copy,
            },
        ) from exc
    if isinstance(exc, PublishedPlanImmutableError):
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail={"code": "published_plan_immutable", "message": str(exc)},
        ) from exc
    if isinstance(exc, PlanValidationError):
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "code": "plan_validation_failed",
                "message": str(exc),
                "issues": exc.issues,
            },
        ) from exc
    if isinstance(exc, EntityNotFoundError):
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail={"code": "not_found", "message": str(exc)},
        ) from exc
    if isinstance(exc, IntegrityError):
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail={
                "code": "integrity_conflict",
                "message": "The request conflicts with an existing resource",
            },
        ) from exc
    raise exc


def _safe_commit(db: Session) -> None:
    try:
        db.commit()
    except IntegrityError as exc:
        _domain_error(db, exc)


def _offset_cursor(cursor: str | None) -> int:
    try:
        return max(0, int(cursor or "0"))
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={"code": "cursor_invalid", "message": "Cursor must be numeric"},
        ) from exc


def _admin_user_value(db: Session, user: User) -> dict[str, Any]:
    return {
        "id": user.id,
        "email": user.email,
        "username": user.username,
        "display_name": user.display_name,
        "timezone": user.timezone,
        "weight_unit": user.weight_unit,
        "is_active": bool(user.is_active),
        "is_superuser": bool(user.is_superuser),
        "roles": active_role_names(db, user.id),
        "version": user.version,
        "created_at": user.created_at,
        "updated_at": user.updated_at,
        "last_login_at": user.last_login_at,
    }


def _audit_safe(value: Any) -> Any:
    """Convert response DTO values to JSON-compatible audit payloads."""

    if isinstance(value, datetime):
        return value.isoformat()
    if isinstance(value, dict):
        return {str(key): _audit_safe(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_audit_safe(item) for item in value]
    return value


def _admin_user_or_404(db: Session, user_id: str, *, for_update: bool = False) -> User:
    statement = select(User).where(User.id == user_id, User.deleted_at.is_(None))
    if for_update:
        statement = statement.with_for_update()
    user = db.scalar(statement)
    if user is None:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail={"code": "user_not_found", "message": "User was not found"},
        )
    return user


def _active_admin_count(db: Session) -> int:
    admin_role = db.scalar(
        select(Role).where(func.lower(Role.name) == "admin", Role.deleted_at.is_(None))
    )
    admin_user_ids = (
        select(UserRole.user_id).where(
            UserRole.role_id == admin_role.id,
            UserRole.deleted_at.is_(None),
        )
        if admin_role is not None
        else None
    )
    privileged = User.is_superuser.is_(True)
    if admin_user_ids is not None:
        privileged = or_(privileged, User.id.in_(admin_user_ids))
    return int(
        db.scalar(
            select(func.count(User.id)).where(
                User.deleted_at.is_(None),
                User.is_active.is_(True),
                privileged,
            )
        )
        or 0
    )


@router.get("/settings/registration", response_model=RegistrationSettingResponse)
def get_registration_setting(
    db: Annotated[Session, Depends(get_db)],
) -> RegistrationSettingResponse:
    row = db.scalar(
        select(SystemSetting).where(SystemSetting.key == "registration_enabled")
    )
    return RegistrationSettingResponse(
        enabled=registration_is_enabled(db),
        updated_at=row.updated_at if row else None,
        updated_by_user_id=row.updated_by_user_id if row else None,
    )


@router.patch("/settings/registration", response_model=RegistrationSettingResponse)
def patch_registration_setting(
    payload: RegistrationSettingPatch,
    request: Request,
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
) -> RegistrationSettingResponse:
    before = {"enabled": registration_is_enabled(db)}
    row = set_registration_enabled(db, payload.enabled, actor_user_id=current_user.id)
    add_audit_log(
        db,
        actor_user_id=current_user.id,
        action="admin.registration_setting.update",
        entity_type="system_setting",
        entity_id=row.id,
        before=before,
        after={"enabled": payload.enabled},
        **_request_context(request),
    )
    _safe_commit(db)
    return RegistrationSettingResponse(
        enabled=payload.enabled,
        updated_at=row.updated_at,
        updated_by_user_id=row.updated_by_user_id,
    )


@router.get("/users", response_model=AdminUserPage)
def list_users(
    db: Annotated[Session, Depends(get_db)],
    cursor: str | None = Query(default=None),
    limit: int = Query(default=50, ge=1, le=200),
    query: str | None = Query(default=None, max_length=120),
) -> AdminUserPage:
    offset = _offset_cursor(cursor)
    conditions = [User.deleted_at.is_(None)]
    if query and query.strip():
        needle = f"%{query.strip().casefold()}%"
        conditions.append(
            or_(
                func.lower(User.email).like(needle),
                func.lower(User.username).like(needle),
                func.lower(User.display_name).like(needle),
            )
        )
    rows = list(
        db.scalars(
            select(User)
            .where(*conditions)
            .order_by(User.created_at.desc(), User.id.desc())
            .offset(offset)
            .limit(limit + 1)
        ).all()
    )
    has_more = len(rows) > limit
    rows = rows[:limit]
    return AdminUserPage(
        items=[AdminUserResponse.model_validate(_admin_user_value(db, row)) for row in rows],
        cursor=cursor,
        next_cursor=str(offset + len(rows)) if has_more else None,
        has_more=has_more,
    )


@router.post("/users", response_model=AdminUserResponse, status_code=status.HTTP_201_CREATED)
def create_user(
    payload: AdminUserCreate,
    request: Request,
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
) -> AdminUserResponse:
    requested_roles = {item.casefold() for item in payload.roles} or {"user"}
    if "admin" in requested_roles and not current_user.is_superuser:
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail={"code": "superuser_required", "message": "Only a superuser may create administrators"},
        )
    try:
        user = create_account(
            db,
            email=payload.email,
            username=payload.username,
            password=payload.password,
            display_name=payload.display_name,
            timezone=payload.timezone,
            weight_unit=payload.weight_unit,
            role_names=requested_roles,
        )
        add_audit_log(
            db,
            actor_user_id=current_user.id,
            action="admin.user.create",
            entity_type="user",
            entity_id=user.id,
            after=_audit_safe(_admin_user_value(db, user)),
            **_request_context(request),
        )
        _safe_commit(db)
        return AdminUserResponse.model_validate(_admin_user_value(db, user))
    except AccountValidationError as exc:
        db.rollback()
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={"code": "account_rejected", "message": str(exc)},
        ) from exc


@router.patch("/users/{user_id}", response_model=AdminUserResponse)
def patch_user(
    user_id: str,
    payload: AdminUserPatch,
    request: Request,
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
) -> AdminUserResponse:
    user = _admin_user_or_404(db, user_id, for_update=True)
    if user.version != payload.expected_version:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail={"code": "version_conflict", "message": "User changed; reload before editing", "server_copy": _admin_user_value(db, user)},
        )
    current_roles = set(active_role_names(db, user.id))
    if (
        user.id != current_user.id
        and not current_user.is_superuser
        and (user.is_superuser or "admin" in current_roles)
    ):
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail={
                "code": "superuser_required",
                "message": "Only a superuser may modify another privileged account",
            },
        )
    if user.id == current_user.id and payload.is_active is False:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={"code": "self_deactivate_forbidden", "message": "You cannot deactivate your own account"},
        )
    requested_roles = current_roles
    if payload.roles is not None:
        requested_roles = {item.strip().casefold() for item in payload.roles if item.strip()}
        if not requested_roles:
            requested_roles = {"user"}
        if (
            "admin" in requested_roles
            and "admin" not in current_roles
            and not current_user.is_superuser
        ):
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail={"code": "superuser_required", "message": "Only a superuser may grant administrator access"},
            )
        if "admin" in current_roles and "admin" not in requested_roles and not current_user.is_superuser:
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail={"code": "superuser_required", "message": "Only a superuser may revoke administrator access"},
            )
    removing_privileged_access = (
        ("admin" in current_roles and (payload.is_active is False or "admin" not in requested_roles))
        or (user.is_superuser and payload.is_active is False)
    )
    if removing_privileged_access and _active_admin_count(db) <= 1:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail={"code": "last_admin_protected", "message": "The last active administrator cannot be deactivated"},
        )
    before = _admin_user_value(db, user)
    password_changed = payload.password is not None
    try:
        if payload.display_name is not None:
            user.display_name = payload.display_name
        if payload.timezone is not None:
            user.timezone = validate_timezone(payload.timezone)
        if payload.weight_unit is not None:
            user.weight_unit = payload.weight_unit
        if payload.is_active is not None:
            user.is_active = payload.is_active
        if password_changed:
            from app.core.security import hash_password

            user.password_hash = hash_password(validate_password(payload.password or ""))
            db.query(RefreshToken).filter(RefreshToken.user_id == user.id).update(
                {RefreshToken.revoked_at: datetime.now(UTC)}, synchronize_session=False
            )
        if payload.roles is not None:
            replace_user_roles(db, user, requested_roles, assigned_by=current_user.id)
        user.version += 1
        db.flush()
        after = _admin_user_value(db, user)
        add_audit_log(
            db,
            actor_user_id=current_user.id,
            action="admin.user.update",
            entity_type="user",
            entity_id=user.id,
            before=_audit_safe(before),
            after=_audit_safe(after),
            metadata={"password_changed": password_changed},
            **_request_context(request),
        )
        _safe_commit(db)
        return AdminUserResponse.model_validate(after)
    except AccountValidationError as exc:
        db.rollback()
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={"code": "account_rejected", "message": str(exc)},
        ) from exc


@router.get("/users/{user_id}/overview", response_model=AdminUserOverview)
def user_overview(
    user_id: str,
    db: Annotated[Session, Depends(get_db)],
) -> AdminUserOverview:
    user = _admin_user_or_404(db, user_id)
    assignments = list(
        db.scalars(
            select(PlanAssignment)
            .where(PlanAssignment.user_id == user.id, PlanAssignment.deleted_at.is_(None))
            .options(selectinload(PlanAssignment.plan_version).selectinload(PlanVersion.plan))
            .order_by(PlanAssignment.starts_on.desc())
            .limit(50)
        ).all()
    )
    plans: list[dict[str, Any]] = []
    seen_plans: set[str] = set()
    for assignment in assignments:
        version = assignment.plan_version
        if version is None or version.id in seen_plans:
            continue
        seen_plans.add(version.id)
        plans.append(
            {
                "id": version.id,
                "plan_id": version.training_plan_id,
                "plan_name": version.plan.name if version.plan else "",
                "version_number": version.version_number,
                "status": version.status,
                "version": version.version,
                "published_at": version.published_at,
            }
        )
    workouts = list(
        db.scalars(
            select(WorkoutSession)
            .where(WorkoutSession.user_id == user.id, WorkoutSession.deleted_at.is_(None))
            .options(selectinload(WorkoutSession.sets))
            .order_by(WorkoutSession.local_date.desc(), WorkoutSession.updated_at.desc())
            .limit(100)
        ).unique()
    )
    readiness = list(
        db.scalars(
            select(DailyReadiness)
            .where(DailyReadiness.user_id == user.id, DailyReadiness.deleted_at.is_(None))
            .order_by(DailyReadiness.local_date.desc())
            .limit(100)
        ).all()
    )
    cardio = list(
        db.scalars(
            select(CardioSession)
            .where(CardioSession.user_id == user.id, CardioSession.deleted_at.is_(None))
            .order_by(CardioSession.local_date.desc())
            .limit(100)
        ).all()
    )
    return AdminUserOverview(
        user=AdminUserResponse.model_validate(_admin_user_value(db, user)),
        assignments=[assignment_to_dict(item) for item in assignments],
        plans=plans,
        workout_sessions=[serialize_workout(item).model_dump(mode="json") for item in workouts],
        readiness=[serialize_readiness(item).model_dump(mode="json") for item in readiness],
        cardio_sessions=[serialize_cardio(item).model_dump(mode="json") for item in cardio],
    )


@router.get("/plans", response_model=AdminPlanPage)
def list_admin_plans(
    db: Annotated[Session, Depends(get_db)],
    cursor: str | None = Query(default=None),
    limit: int = Query(default=50, ge=1, le=200),
    status_filter: str | None = Query(default=None, alias="status", max_length=16),
) -> AdminPlanPage:
    offset = _offset_cursor(cursor)
    conditions = [PlanVersion.deleted_at.is_(None), TrainingPlan.deleted_at.is_(None)]
    if status_filter:
        conditions.append(PlanVersion.status == status_filter.casefold())
    versions = list(
        db.scalars(
            select(PlanVersion)
            .join(TrainingPlan, TrainingPlan.id == PlanVersion.training_plan_id)
            .where(*conditions)
            .options(selectinload(PlanVersion.plan))
            .order_by(PlanVersion.updated_at.desc(), PlanVersion.id.desc())
            .offset(offset)
            .limit(limit + 1)
        ).all()
    )
    has_more = len(versions) > limit
    versions = versions[:limit]
    items = [
        AdminPlanSummary(
            id=item.id,
            plan_id=item.training_plan_id,
            plan_name=item.plan.name if item.plan else "",
            version_number=item.version_number,
            status=item.status,
            version=item.version,
            weekly_frequency=item.weekly_frequency,
            updated_at=item.updated_at,
            published_at=item.published_at,
        )
        for item in versions
    ]
    return AdminPlanPage(
        items=items,
        cursor=cursor,
        next_cursor=str(offset + len(items)) if has_more else None,
        has_more=has_more,
    )


@router.get("/plan-versions/{version_id}")
def get_admin_plan_version(
    version_id: str,
    db: Annotated[Session, Depends(get_db)],
) -> dict[str, Any]:
    version = db.scalar(
        select(PlanVersion)
        .where(PlanVersion.id == version_id, PlanVersion.deleted_at.is_(None))
        .options(selectinload(PlanVersion.plan))
    )
    if version is None:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail={"code": "plan_not_found", "message": "Plan version was not found"},
        )
    return serialize_plan_version(version)


def _slug(value: str) -> str:
    normalized = unicodedata.normalize("NFKD", value).encode("ascii", "ignore").decode("ascii")
    return re.sub(r"[^a-z0-9]+", "-", normalized.casefold()).strip("-")


def _generated_code(db: Session, model: type[Any], name: str, requested: str | None, entity_id: str) -> str:
    if requested:
        return requested.strip().casefold()
    base = _slug(name) or f"item-{entity_id[:8]}"
    candidate = base[:64]
    suffix = 1
    while db.scalar(select(model.id).where(model.code == candidate)) is not None:
        tail = f"-{suffix}"
        candidate = f"{base[: 64 - len(tail)]}{tail}"
        suffix += 1
    return candidate


def _lines(value: str | list[str] | None) -> list[str]:
    if value is None:
        return []
    values = value if isinstance(value, list) else value.splitlines()
    return [item.strip() for item in values if item.strip()]


def _serialize_exercise(exercise: Exercise) -> dict[str, Any]:
    value = entity_dict(exercise)
    cues = [cue for cue in exercise.cues if cue.deleted_at is None]
    equipment_links = [link for link in exercise.equipment_links if link.deleted_at is None]
    alternatives = [item for item in exercise.alternatives if item.deleted_at is None]
    value.update(
        {
            "cues": "\n".join(cue.text for cue in cues),
            "cue_items": [entity_dict(cue) for cue in cues],
            "common_mistakes": "\n".join(exercise.common_mistakes_json),
            "equipment_id": equipment_links[0].equipment_id if equipment_links else None,
            "equipment_ids": [link.equipment_id for link in equipment_links],
            "alternative_exercise_ids": [item.alternative_exercise_id for item in alternatives],
            "alternatives": [entity_dict(item) for item in alternatives],
            "definition_version": exercise.version,
        }
    )
    return value


def _equipment_ids(payload: ExerciseCreate | ExercisePatch) -> list[str] | None:
    explicitly_set = "equipment_ids" in payload.model_fields_set or "equipment_id" in payload.model_fields_set
    if isinstance(payload, ExerciseCreate):
        explicitly_set = True
    if not explicitly_set:
        return None
    values = [str(item) for item in (payload.equipment_ids or [])]
    if payload.equipment_id:
        values.insert(0, str(payload.equipment_id))
    return list(dict.fromkeys(values))


def _resolved_equipment_ids(
    db: Session,
    payload: ExerciseCreate | ExercisePatch,
) -> list[str] | None:
    values = _equipment_ids(payload)
    if values is None:
        return None
    if not values and payload.equipment_name:
        equipment = db.scalar(
            select(Equipment).where(
                func.lower(Equipment.name) == payload.equipment_name.strip().casefold(),
                Equipment.is_active.is_(True),
                Equipment.deleted_at.is_(None),
            )
        )
        if equipment is not None:
            values.append(equipment.id)
    return values


def _exercise_metadata(
    payload: ExerciseCreate | ExercisePatch,
    existing: dict[str, Any] | None = None,
) -> dict[str, Any]:
    metadata = dict(existing or {})
    if "metadata_json" in payload.model_fields_set:
        metadata = dict(payload.metadata_json or {})
    compatibility = {
        "equipment_name": payload.equipment_name,
        "prescription": payload.prescription,
        "alternative_names": payload.alternatives,
    }
    for key, value in compatibility.items():
        if key in payload.model_fields_set and value is not None:
            metadata[key] = value
    return metadata


def _replace_exercise_relations(
    db: Session,
    exercise: Exercise,
    *,
    cues: str | list[str] | None | object = ...,
    equipment_ids: list[str] | None = None,
    alternatives: list[str] | None = None,
) -> None:
    clear_cues = cues is not ...
    clear_equipment = equipment_ids is not None
    clear_alternatives = alternatives is not None
    if clear_cues:
        exercise.cues.clear()
    if clear_equipment:
        exercise.equipment_links.clear()
    if clear_alternatives:
        exercise.alternatives.clear()
    if clear_cues or clear_equipment or clear_alternatives:
        db.flush()

    if clear_cues:
        for sort_order, text in enumerate(_lines(cues if cues is not ... else None)):
            exercise.cues.append(ExerciseCue(text=text, sort_order=sort_order))
    if equipment_ids is not None:
        for equipment_id in equipment_ids:
            equipment = require_active(db, Equipment, equipment_id)
            if not equipment.is_active:
                raise PlanValidationError(
                    [
                        {
                            "code": "equipment_inactive",
                            "path": "equipment_ids",
                            "message": f"Equipment '{equipment_id}' is inactive",
                        }
                    ]
                )
            exercise.equipment_links.append(
                ExerciseEquipment(equipment_id=equipment_id, is_required=True, quantity=1)
            )
    if alternatives is not None:
        for priority, alternative_id in enumerate(dict.fromkeys(alternatives)):
            if alternative_id == exercise.id:
                raise PlanValidationError(
                    [
                        {
                            "code": "alternative_self_reference",
                            "path": "alternative_exercise_ids",
                            "message": "An exercise cannot be its own alternative",
                        }
                    ]
                )
            alternative = require_active(db, Exercise, alternative_id)
            if not alternative.is_active:
                raise PlanValidationError(
                    [
                        {
                            "code": "alternative_inactive",
                            "path": "alternative_exercise_ids",
                            "message": f"Alternative exercise '{alternative_id}' is inactive",
                        }
                    ]
                )
            exercise.alternatives.append(
                ExerciseAlternative(alternative_exercise_id=alternative_id, priority=priority)
            )


@router.post("/equipment", status_code=status.HTTP_201_CREATED)
def create_equipment(
    payload: EquipmentCreate,
    request: Request,
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
) -> dict[str, Any]:
    entity_id = str(payload.id) if payload.id else uuid4_str()
    equipment = Equipment(
        id=entity_id,
        code=_generated_code(db, Equipment, payload.name, payload.code, entity_id),
        name=payload.name,
        description=payload.description or None,
        category=payload.category,
        brand=payload.brand,
        model=payload.model,
        notes=payload.notes,
        is_active=payload.is_active,
        metadata_json=payload.metadata_json or {},
    )
    db.add(equipment)
    try:
        db.flush()
        after = entity_dict(equipment)
        add_audit_log(
            db,
            actor_user_id=current_user.id,
            action="admin.equipment.create",
            entity_type="equipment",
            entity_id=equipment.id,
            after=after,
            **_request_context(request),
        )
        add_sync_change(
            db,
            entity_type="equipment",
            entity_id=equipment.id,
            entity_version=equipment.version,
            operation="create",
            payload=after,
            actor_user_id=current_user.id,
            request_id=request.headers.get("X-Request-ID"),
        )
        _safe_commit(db)
        return after
    except (IntegrityError, EntityNotFoundError, PlanValidationError) as exc:
        _domain_error(db, exc)


@router.patch("/equipment/{equipment_id}")
def patch_equipment(
    equipment_id: str,
    payload: EquipmentPatch,
    request: Request,
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
) -> dict[str, Any]:
    try:
        current = require_active(db, Equipment, equipment_id)
        before = entity_dict(current)
        values = payload.model_dump(exclude_unset=True)
        values.pop("expected_version", None)
        if "metadata_json" in values and values["metadata_json"] is None:
            values["metadata_json"] = {}
        equipment = optimistic_patch(db, Equipment, equipment_id, payload.expected_version, values)
        after = entity_dict(equipment)
        add_audit_log(
            db,
            actor_user_id=current_user.id,
            action="admin.equipment.update",
            entity_type="equipment",
            entity_id=equipment.id,
            before=before,
            after=after,
            **_request_context(request),
        )
        add_sync_change(
            db,
            entity_type="equipment",
            entity_id=equipment.id,
            entity_version=equipment.version,
            operation="update",
            payload=after,
            actor_user_id=current_user.id,
            request_id=request.headers.get("X-Request-ID"),
        )
        _safe_commit(db)
        return after
    except (EntityNotFoundError, OptimisticLockError, IntegrityError) as exc:
        _domain_error(
            db,
            exc,
            actor_user_id=current_user.id,
            request=request,
            entity_type="equipment",
            entity_id=equipment_id,
        )


@router.post("/exercises", status_code=status.HTTP_201_CREATED)
def create_exercise(
    payload: ExerciseCreate,
    request: Request,
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
) -> dict[str, Any]:
    entity_id = str(payload.id) if payload.id else uuid4_str()
    exercise = Exercise(
        id=entity_id,
        code=_generated_code(db, Exercise, payload.name, payload.code, entity_id),
        name=payload.name,
        description=payload.description or None,
        body_part=payload.body_part,
        movement_pattern=payload.movement_pattern,
        difficulty=payload.difficulty,
        default_sets=payload.default_sets,
        rep_min=payload.rep_min,
        rep_max=payload.rep_max,
        rep_unit=payload.rep_unit or "reps",
        is_unilateral=payload.is_unilateral,
        is_active=payload.is_active,
        created_by_user_id=current_user.id,
        common_mistakes_json=_lines(payload.common_mistakes),
        metadata_json=_exercise_metadata(payload),
    )
    db.add(exercise)
    try:
        _replace_exercise_relations(
            db,
            exercise,
            cues=payload.cues,
            equipment_ids=_resolved_equipment_ids(db, payload),
            alternatives=[str(item) for item in payload.alternative_exercise_ids],
        )
        db.flush()
        after = _serialize_exercise(exercise)
        add_audit_log(
            db,
            actor_user_id=current_user.id,
            action="admin.exercise.create",
            entity_type="exercise",
            entity_id=exercise.id,
            after=after,
            **_request_context(request),
        )
        add_sync_change(
            db,
            entity_type="exercise",
            entity_id=exercise.id,
            entity_version=exercise.version,
            operation="create",
            payload=after,
            actor_user_id=current_user.id,
            request_id=request.headers.get("X-Request-ID"),
        )
        _safe_commit(db)
        return after
    except (EntityNotFoundError, PlanValidationError, IntegrityError) as exc:
        _domain_error(db, exc)


@router.patch("/exercises/{exercise_id}")
def patch_exercise(
    exercise_id: str,
    payload: ExercisePatch,
    request: Request,
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
) -> dict[str, Any]:
    try:
        exercise = require_active(db, Exercise, exercise_id, for_update=True)
        if exercise.version != payload.expected_version:
            raise OptimisticLockError(_serialize_exercise(exercise))
        before = _serialize_exercise(exercise)
        values = payload.model_dump(exclude_unset=True)
        values.pop("expected_version", None)
        relation_fields = {
            "id",
            "cues",
            "equipment_id",
            "equipment_ids",
            "alternative_exercise_ids",
            "common_mistakes",
            "equipment_name",
            "prescription",
            "alternatives",
        }
        for key, value in values.items():
            if key in relation_fields:
                continue
            if key == "metadata_json" and value is None:
                value = {}
            if hasattr(exercise, key):
                setattr(exercise, key, value)
        if "common_mistakes" in values:
            exercise.common_mistakes_json = _lines(payload.common_mistakes)
        if {
            "metadata_json",
            "equipment_name",
            "prescription",
            "alternatives",
        }.intersection(payload.model_fields_set):
            exercise.metadata_json = _exercise_metadata(payload, exercise.metadata_json)
        equipment_ids = _resolved_equipment_ids(db, payload)
        alternatives = (
            [str(item) for item in payload.alternative_exercise_ids or []]
            if "alternative_exercise_ids" in payload.model_fields_set
            else None
        )
        cues: str | list[str] | None | object = (
            payload.cues if "cues" in payload.model_fields_set else ...
        )
        _replace_exercise_relations(
            db,
            exercise,
            cues=cues,
            equipment_ids=equipment_ids,
            alternatives=alternatives,
        )
        exercise.version += 1
        db.flush()
        after = _serialize_exercise(exercise)
        add_audit_log(
            db,
            actor_user_id=current_user.id,
            action="admin.exercise.update",
            entity_type="exercise",
            entity_id=exercise.id,
            before=before,
            after=after,
            **_request_context(request),
        )
        add_sync_change(
            db,
            entity_type="exercise",
            entity_id=exercise.id,
            entity_version=exercise.version,
            operation="update",
            payload=after,
            actor_user_id=current_user.id,
            request_id=request.headers.get("X-Request-ID"),
        )
        _safe_commit(db)
        return after
    except (EntityNotFoundError, OptimisticLockError, PlanValidationError, IntegrityError) as exc:
        _domain_error(
            db,
            exc,
            actor_user_id=current_user.id,
            request=request,
            entity_type="exercise",
            entity_id=exercise_id,
        )


@router.post("/plans", status_code=status.HTTP_201_CREATED)
def create_plan(
    payload: PlanCreate,
    request: Request,
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
) -> dict[str, Any]:
    plan = TrainingPlan(
        id=str(payload.id) if payload.id else uuid4_str(),
        owner_user_id=None if payload.is_system else current_user.id,
        name=payload.name,
        description=payload.description or None,
        goal=payload.goal,
        is_system=payload.is_system,
        is_active=payload.is_active,
    )
    db.add(plan)
    try:
        db.flush()
        after = entity_dict(plan)
        add_audit_log(
            db,
            actor_user_id=current_user.id,
            action="admin.plan.create",
            entity_type="training_plan",
            entity_id=plan.id,
            after=after,
            **_request_context(request),
        )
        add_sync_change(
            db,
            entity_type="training_plan",
            entity_id=plan.id,
            entity_version=plan.version,
            operation="create",
            payload=after,
            actor_user_id=current_user.id,
            request_id=request.headers.get("X-Request-ID"),
        )
        _safe_commit(db)
        return after
    except IntegrityError as exc:
        _domain_error(db, exc)


@router.post("/plans/{plan_id}/versions", status_code=status.HTTP_201_CREATED)
def create_version(
    plan_id: str,
    payload: PlanVersionCreate,
    request: Request,
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
) -> dict[str, Any]:
    try:
        version = create_plan_version(db, plan_id, payload)
        after = serialize_plan_version(version)
        add_audit_log(
            db,
            actor_user_id=current_user.id,
            action="admin.plan_version.create",
            entity_type="plan_version",
            entity_id=version.id,
            after=after,
            **_request_context(request),
        )
        add_sync_change(
            db,
            entity_type="plan_version",
            entity_id=version.id,
            entity_version=version.version,
            operation="create",
            payload=after,
            actor_user_id=current_user.id,
            request_id=request.headers.get("X-Request-ID"),
        )
        _safe_commit(db)
        return after
    except (EntityNotFoundError, PlanValidationError, IntegrityError) as exc:
        _domain_error(db, exc)


@router.patch("/plan-versions/{version_id}")
def patch_version(
    version_id: str,
    payload: PlanVersionPatch,
    request: Request,
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
) -> dict[str, Any]:
    try:
        version, before = patch_plan_version(db, version_id, payload)
        after = serialize_plan_version(version)
        add_audit_log(
            db,
            actor_user_id=current_user.id,
            action="admin.plan_version.update",
            entity_type="plan_version",
            entity_id=version.id,
            before=before,
            after=after,
            **_request_context(request),
        )
        add_sync_change(
            db,
            entity_type="plan_version",
            entity_id=version.id,
            entity_version=version.version,
            operation="update",
            payload=after,
            actor_user_id=current_user.id,
            request_id=request.headers.get("X-Request-ID"),
        )
        _safe_commit(db)
        return after
    except (
        EntityNotFoundError,
        OptimisticLockError,
        PublishedPlanImmutableError,
        IntegrityError,
    ) as exc:
        _domain_error(
            db,
            exc,
            actor_user_id=current_user.id,
            request=request,
            entity_type="plan_version",
            entity_id=version_id,
        )


@router.post("/plan-versions/{version_id}/publish")
def publish_version(
    version_id: str,
    request: Request,
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
    payload: PlanVersionPublish | None = None,
) -> dict[str, Any]:
    try:
        version, before = publish_plan_version(
            db,
            version_id,
            actor_user_id=current_user.id,
            expected_version=payload.expected_version if payload else None,
        )
        after = serialize_plan_version(version)
        add_audit_log(
            db,
            actor_user_id=current_user.id,
            action="admin.plan_version.publish",
            entity_type="plan_version",
            entity_id=version.id,
            before=before,
            after=after,
            **_request_context(request),
        )
        add_sync_change(
            db,
            entity_type="plan_version",
            entity_id=version.id,
            entity_version=version.version,
            operation="update",
            payload=after,
            actor_user_id=current_user.id,
            request_id=request.headers.get("X-Request-ID"),
        )
        _safe_commit(db)
        return after
    except (
        EntityNotFoundError,
        OptimisticLockError,
        PublishedPlanImmutableError,
        PlanValidationError,
        IntegrityError,
    ) as exc:
        _domain_error(
            db,
            exc,
            actor_user_id=current_user.id,
            request=request,
            entity_type="plan_version",
            entity_id=version_id,
        )


@router.post(
    "/assignments",
    status_code=status.HTTP_201_CREATED,
    response_model=PlanAssignmentOut,
)
def assign_plan(
    payload: AssignmentCreate,
    request: Request,
    current_user: Annotated[User, Depends(get_current_user)],
    db: Annotated[Session, Depends(get_db)],
) -> dict[str, Any]:
    try:
        assignment, previous_assignments = create_assignment(
            db, payload, actor_user_id=current_user.id
        )
        audit_after = entity_dict(assignment)
        after = PlanAssignmentOut.model_validate(
            assignment_to_dict(assignment)
        ).model_dump(mode="json")
        plan_after = serialize_plan_version(assignment.plan_version)
        add_audit_log(
            db,
            actor_user_id=current_user.id,
            action="admin.assignment.create",
            entity_type="plan_assignment",
            entity_id=assignment.id,
            after=audit_after,
            **_request_context(request),
        )
        # An active assignment supersedes every prior active row for the same
        # user. Emit those canonical updates first so incremental clients
        # converge before they activate the replacement assignment.
        for previous in previous_assignments:
            add_sync_change(
                db,
                entity_type="plan_assignment",
                entity_id=previous.id,
                entity_version=previous.version,
                operation="update",
                payload=PlanAssignmentOut.model_validate(
                    assignment_to_dict(previous)
                ).model_dump(mode="json"),
                actor_user_id=current_user.id,
                request_id=request.headers.get("X-Request-ID"),
            )
        db.flush()
        # Publishing may have happened long before this user's latest cursor.
        # Re-emit the immutable full plan snapshot now that the assignment makes
        # it visible. Flush it before the new assignment so clients always apply
        # the plan tree before the assignment that references it.
        add_sync_change(
            db,
            entity_type="plan_version",
            entity_id=assignment.plan_version_id,
            entity_version=assignment.plan_version.version,
            operation="update",
            payload=plan_after,
            actor_user_id=current_user.id,
            request_id=request.headers.get("X-Request-ID"),
        )
        db.flush()
        add_sync_change(
            db,
            entity_type="plan_assignment",
            entity_id=assignment.id,
            entity_version=assignment.version,
            operation="create",
            payload=after,
            actor_user_id=current_user.id,
            request_id=request.headers.get("X-Request-ID"),
        )
        _safe_commit(db)
        return after
    except (EntityNotFoundError, PlanValidationError, IntegrityError) as exc:
        _domain_error(db, exc)


@router.get("/audit-logs", response_model=AuditLogPage)
def audit_logs(
    db: Annotated[Session, Depends(get_db)],
    cursor: str | None = Query(default=None),
    limit: int = Query(default=50, ge=1, le=200),
    action: str | None = Query(default=None, max_length=64),
    entity_type: str | None = Query(default=None, max_length=64),
) -> AuditLogPage:
    try:
        offset = max(0, int(cursor or "0"))
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={"code": "cursor_invalid", "message": "Audit cursor must be numeric"},
        ) from exc
    conditions = [AuditLog.deleted_at.is_(None)]
    if action:
        conditions.append(AuditLog.action == action)
    if entity_type:
        conditions.append(AuditLog.entity_type == entity_type)
    rows = list(
        db.scalars(
            select(AuditLog)
            .where(*conditions)
            .order_by(AuditLog.created_at.desc(), AuditLog.id.desc())
            .offset(offset)
            .limit(limit + 1)
        ).all()
    )
    has_more = len(rows) > limit
    rows = rows[:limit]
    next_cursor = str(offset + len(rows)) if has_more else None
    return AuditLogPage(
        items=[AuditLogResponse.model_validate(entity_dict(row)) for row in rows],
        cursor=cursor,
        next_cursor=next_cursor,
        has_more=has_more,
    )


@router.get("/sync-status", response_model=SyncStatusResponse)
def sync_status(db: Annotated[Session, Depends(get_db)]) -> SyncStatusResponse:
    now = datetime.now(UTC)
    latest_sequence = db.scalar(select(func.max(SyncChange.sequence)))
    recent_count = db.scalar(
        select(func.count()).select_from(SyncChange).where(
            SyncChange.changed_at >= now - timedelta(hours=24)
        )
    )
    return SyncStatusResponse(
        server_time=now,
        latest_sequence=latest_sequence,
        changes_last_24_hours=int(recent_count or 0),
        # Sync ingestion is synchronous, so there is no hidden pending queue.
        pending_operations=0,
        failed_operations=0,
        status="healthy",
    )

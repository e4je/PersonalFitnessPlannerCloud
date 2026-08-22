from __future__ import annotations

import re

from pydantic import BaseModel, ConfigDict, Field, SecretStr, field_validator

from app.core.config import APPLICATION_DATABASE_NAME


_CONTROL_CHARACTERS = re.compile(r"[\x00-\x1f\x7f]")


class DatabaseSetupRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)

    host: str = Field(min_length=1, max_length=255, examples=["mysql"])
    port: int = Field(default=3306, ge=1, le=65535)
    username: str = Field(min_length=1, max_length=128, examples=["fitness"])
    password: SecretStr
    setup_token: SecretStr

    @field_validator("host")
    @classmethod
    def validate_host(cls, value: str) -> str:
        if (
            "://" in value
            or any(character in value for character in "/?#@")
            or _CONTROL_CHARACTERS.search(value)
        ):
            raise ValueError("host must be a hostname or IP address without a URL scheme")
        return value

    @field_validator("username")
    @classmethod
    def validate_username(cls, value: str) -> str:
        if _CONTROL_CHARACTERS.search(value):
            raise ValueError("username contains invalid control characters")
        return value


class SetupStatusResponse(BaseModel):
    configured: bool
    setup_required: bool
    database_name: str = APPLICATION_DATABASE_NAME
    token_required: bool
    default_host: str
    default_port: int
    default_username: str


class DatabaseSetupResponse(BaseModel):
    configured: bool = True
    setup_required: bool = False
    database_name: str = APPLICATION_DATABASE_NAME
    database_created: bool
    mysql_version: str
    database_collation: str | None
    existing_table_count: int
    table_count: int
    alembic_revision: str
    seed_status: str

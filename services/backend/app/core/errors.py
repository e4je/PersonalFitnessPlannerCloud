from __future__ import annotations

from fastapi import Request, status
from fastapi.encoders import jsonable_encoder
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse


async def request_validation_exception_handler(
    _request: Request,
    exc: RequestValidationError,
) -> JSONResponse:
    """Return useful locations/messages without reflecting submitted values."""

    errors: list[dict[str, object]] = []
    for original in exc.errors():
        sanitized = dict(original)
        sanitized.pop("input", None)
        errors.append(sanitized)
    return JSONResponse(
        status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
        content=jsonable_encoder({"detail": errors}),
    )

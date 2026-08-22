from __future__ import annotations

import logging
from collections import deque
from time import perf_counter
from uuid import uuid4

from starlette.datastructures import Headers, MutableHeaders
from starlette.middleware.httpsredirect import HTTPSRedirectMiddleware
from starlette.responses import JSONResponse
from starlette.types import ASGIApp, Message, Receive, Scope, Send

from app.core.config import settings
from app.db.session import is_database_configured


logger = logging.getLogger("app.request")


class SetupRequiredMiddleware:
    """Keep the Web wizard reachable while the business API has no database."""

    _allowed_exact_paths = frozenset(
        {
            "/",
            "/health/live",
            "/health/ready",
            "/api/v1/setup/status",
            "/api/v1/setup/database",
            "/docs",
            "/redoc",
            "/openapi.json",
        }
    )

    def __init__(self, app: ASGIApp) -> None:
        self.app = app

    @classmethod
    def _allowed(cls, path: str) -> bool:
        return path in cls._allowed_exact_paths or path == "/web" or path.startswith("/web/")

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if (
            scope["type"] == "http"
            and scope.get("method") != "OPTIONS"
            and not is_database_configured()
            and not self._allowed(str(scope.get("path", "")))
        ):
            await JSONResponse(
                status_code=503,
                content={
                    "detail": {
                        "code": "setup_required",
                        "message": "Complete database setup in /web/ before using the API",
                    }
                },
            )(scope, receive, send)
            return
        await self.app(scope, receive, send)


class ProductionHTTPSMiddleware:
    """Require HTTPS for application traffic without breaking local probes.

    Container orchestrators reach liveness and readiness endpoints over the
    private HTTP listener. All other HTTP/WebSocket traffic keeps Starlette's
    standard 307 HTTPS/WSS redirect behavior in production.
    """

    _probe_paths = frozenset({"/health/live", "/health/ready"})

    def __init__(self, app: ASGIApp) -> None:
        self.app = app
        self.https_redirect = HTTPSRedirectMiddleware(app)

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] == "http" and scope.get("path") in self._probe_paths:
            await self.app(scope, receive, send)
            return
        await self.https_redirect(scope, receive, send)


class RequestContextMiddleware:
    """Attach request context and enforce the body limit on actual ASGI bytes.

    ``Content-Length`` remains a useful fast-rejection hint, but it is not a
    security boundary: HTTP/1.1 chunked bodies and HTTP/2 streams may omit it.
    The middleware therefore preflights every ``http.request`` body fragment
    before forwarding the bounded stream to FastAPI/Starlette. Buffering is
    bounded by the configured limit and guarantees a stable 413 even when an
    endpoint itself does not consume the body.
    """

    def __init__(self, app: ASGIApp, max_body_bytes: int | None = None) -> None:
        self.app = app
        self.max_body_bytes = (
            settings.max_request_body_bytes if max_body_bytes is None else max_body_bytes
        )

    @staticmethod
    def _declared_content_length(headers: Headers) -> int | None:
        raw_value = headers.get("content-length")
        if raw_value is None:
            return None
        try:
            value = int(raw_value)
        except ValueError:
            # An invalid value must never turn into a middleware 500. The ASGI
            # byte counter below remains authoritative for the request.
            return None
        return value if value >= 0 else None

    @staticmethod
    def _too_large_response(request_id: str) -> JSONResponse:
        return JSONResponse(
            status_code=413,
            content={
                "detail": {
                    "code": "request_too_large",
                    "message": "Request body is too large",
                }
            },
            headers={"X-Request-ID": request_id},
        )

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return

        headers = Headers(scope=scope)
        request_id = self._safe_request_id(headers.get("X-Request-ID"))
        started = perf_counter()
        response_status: int | None = None

        async def send_with_context(message: Message) -> None:
            nonlocal response_status
            if message["type"] == "http.response.start":
                response_status = int(message["status"])
                response_headers = MutableHeaders(scope=message)
                response_headers["X-Request-ID"] = request_id
                # The API returns health and personal data. These defaults
                # prevent browser sniffing, framing, referrer leakage and
                # accidental intermediary caching while respecting an
                # endpoint's explicit Cache-Control choice.
                response_headers.setdefault("X-Content-Type-Options", "nosniff")
                response_headers.setdefault("X-Frame-Options", "DENY")
                response_headers.setdefault("Referrer-Policy", "no-referrer")
                response_headers.setdefault(
                    "Permissions-Policy",
                    "camera=(), microphone=(), geolocation=(), usb=()",
                )
                response_headers.setdefault("Cache-Control", "no-store")
                if settings.environment == "production":
                    response_headers.setdefault(
                        "Strict-Transport-Security",
                        "max-age=31536000; includeSubDomains",
                    )
            await send(message)

        declared_length = self._declared_content_length(headers)
        if declared_length is not None and declared_length > self.max_body_bytes:
            response_status = 413
            await self._too_large_response(request_id)(scope, receive, send_with_context)
            self._log_completed(scope, request_id, response_status, started)
            return

        body = bytearray()
        disconnected = False
        while True:
            message = await receive()
            if message["type"] == "http.request":
                chunk = message.get("body", b"")
                if len(chunk) > self.max_body_bytes - len(body):
                    response_status = 413
                    await self._too_large_response(request_id)(
                        scope,
                        receive,
                        send_with_context,
                    )
                    self._log_completed(scope, request_id, response_status, started)
                    return
                body.extend(chunk)
                if not message.get("more_body", False):
                    break
            elif message["type"] == "http.disconnect":
                disconnected = True
                break

        # Replay one bounded request event instead of preserving arbitrarily
        # many empty chunks, which would turn a byte limit into a message-count
        # memory risk. Preserve an early disconnect after any partial body.
        buffered_messages: deque[Message] = deque()
        if not disconnected:
            buffered_messages.append(
                {"type": "http.request", "body": bytes(body), "more_body": False}
            )
        else:
            if body:
                buffered_messages.append(
                    {"type": "http.request", "body": bytes(body), "more_body": True}
                )
            buffered_messages.append({"type": "http.disconnect"})

        async def receive_buffered() -> Message:
            if buffered_messages:
                return buffered_messages.popleft()
            return await receive()

        try:
            await self.app(scope, receive_buffered, send_with_context)
        finally:
            self._log_completed(scope, request_id, response_status, started)

    @staticmethod
    def _safe_request_id(value: str | None) -> str:
        if value is not None:
            candidate = value.strip()
            if 1 <= len(candidate) <= 128 and all(
                character.isalnum() or character in "-_.:" for character in candidate
            ):
                return candidate
        return str(uuid4())

    @staticmethod
    def _log_completed(
        scope: Scope,
        request_id: str,
        status_code: int | None,
        started: float,
    ) -> None:
        duration_ms = round((perf_counter() - started) * 1000, 2)
        logger.info(
            "request_completed",
            extra={
                "request_id": request_id,
                "method": scope.get("method"),
                "path": scope.get("path"),
                "status_code": status_code,
                "duration_ms": duration_ms,
            },
        )

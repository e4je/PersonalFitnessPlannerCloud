from __future__ import annotations

import logging
from time import perf_counter
from uuid import uuid4

from fastapi import Request
from starlette.middleware.base import BaseHTTPMiddleware, RequestResponseEndpoint
from starlette.middleware.httpsredirect import HTTPSRedirectMiddleware
from starlette.responses import JSONResponse, Response
from starlette.types import ASGIApp, Receive, Scope, Send

from app.core.config import settings


logger = logging.getLogger("app.request")


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


class RequestContextMiddleware(BaseHTTPMiddleware):
    async def dispatch(self, request: Request, call_next: RequestResponseEndpoint) -> Response:
        request_id = request.headers.get("X-Request-ID") or str(uuid4())
        content_length = request.headers.get("content-length")
        if content_length and int(content_length) > settings.max_request_body_bytes:
            return JSONResponse(
                status_code=413,
                content={"detail": {"code": "request_too_large", "message": "Request body is too large"}},
                headers={"X-Request-ID": request_id},
            )

        started = perf_counter()
        response = await call_next(request)
        duration_ms = round((perf_counter() - started) * 1000, 2)
        response.headers["X-Request-ID"] = request_id
        logger.info(
            "request_completed",
            extra={
                "request_id": request_id,
                "method": request.method,
                "path": request.url.path,
                "status_code": response.status_code,
                "duration_ms": duration_ms,
            },
        )
        return response

from __future__ import annotations

import asyncio
import json
from collections import deque
from collections.abc import Sequence
from typing import Any

from fastapi import FastAPI, Request
from fastapi.testclient import TestClient
from starlette.types import Message, Scope

from app.core.middleware import ProductionHTTPSMiddleware, RequestContextMiddleware


def _run_http_asgi(
    application: FastAPI,
    *,
    body_chunks: Sequence[bytes],
    headers: Sequence[tuple[bytes, bytes]] = (),
) -> list[Message]:
    scope: Scope = {
        "type": "http",
        "asgi": {"version": "3.0", "spec_version": "2.3"},
        "http_version": "1.1",
        "method": "POST",
        "scheme": "http",
        "path": "/echo",
        "raw_path": b"/echo",
        "query_string": b"",
        "root_path": "",
        "headers": list(headers),
        "client": ("127.0.0.1", 12345),
        "server": ("testserver", 80),
    }
    incoming: deque[Message] = deque(
        {
            "type": "http.request",
            "body": chunk,
            "more_body": index < len(body_chunks) - 1,
        }
        for index, chunk in enumerate(body_chunks)
    )
    if not incoming:
        incoming.append({"type": "http.request", "body": b"", "more_body": False})
    outgoing: list[Message] = []

    async def receive() -> Message:
        return incoming.popleft()

    async def send(message: Message) -> None:
        outgoing.append(message)

    asyncio.run(application(scope, receive, send))
    return outgoing


def _response(messages: Sequence[Message]) -> tuple[int, dict[str, str], dict[str, Any]]:
    start = next(message for message in messages if message["type"] == "http.response.start")
    body = b"".join(
        message.get("body", b"")
        for message in messages
        if message["type"] == "http.response.body"
    )
    headers = {
        key.decode("latin-1").lower(): value.decode("latin-1")
        for key, value in start["headers"]
    }
    return int(start["status"]), headers, json.loads(body)


def _limited_body_application(max_body_bytes: int = 8) -> FastAPI:
    application = FastAPI()

    @application.post("/echo")
    async def echo(request: Request) -> dict[str, int]:
        return {"length": len(await request.body())}

    application.add_middleware(RequestContextMiddleware, max_body_bytes=max_body_bytes)
    return application


def _limited_ignored_body_application(max_body_bytes: int = 8) -> FastAPI:
    application = FastAPI()

    @application.post("/echo")
    async def ignore_body() -> dict[str, bool]:
        return {"accepted": True}

    application.add_middleware(RequestContextMiddleware, max_body_bytes=max_body_bytes)
    return application


def test_production_https_redirect_keeps_private_health_probes_available() -> None:
    application = FastAPI()

    @application.get("/health/ready")
    def ready() -> dict[str, str]:
        return {"status": "ready"}

    @application.get("/api/example")
    def api_example() -> dict[str, str]:
        return {"status": "ok"}

    application.add_middleware(ProductionHTTPSMiddleware)

    with TestClient(application, base_url="http://testserver") as client:
        probe = client.get("/health/ready")
        assert probe.status_code == 200
        assert probe.json() == {"status": "ready"}

        redirected = client.get("/api/example", follow_redirects=False)
        assert redirected.status_code == 307
        assert redirected.headers["location"] == "https://testserver/api/example"

    with TestClient(application, base_url="https://testserver") as client:
        assert client.get("/api/example").status_code == 200


def test_request_body_limit_counts_chunks_without_content_length() -> None:
    messages = _run_http_asgi(
        _limited_body_application(),
        body_chunks=[b"1234", b"56789"],
        headers=[(b"transfer-encoding", b"chunked"), (b"x-request-id", b"chunk-test")],
    )

    status_code, headers, body = _response(messages)
    assert status_code == 413
    assert headers["x-request-id"] == "chunk-test"
    assert body == {
        "detail": {
            "code": "request_too_large",
            "message": "Request body is too large",
        }
    }


def test_request_body_limit_rejects_oversize_even_when_endpoint_ignores_body() -> None:
    messages = _run_http_asgi(
        _limited_ignored_body_application(),
        body_chunks=[b"1234", b"56789"],
        headers=[(b"transfer-encoding", b"chunked")],
    )

    status_code, _headers, body = _response(messages)
    assert status_code == 413
    assert body["detail"]["code"] == "request_too_large"


def test_request_body_limit_allows_normal_streamed_request() -> None:
    messages = _run_http_asgi(
        _limited_body_application(),
        body_chunks=[b"1234", b"5678"],
    )

    status_code, headers, body = _response(messages)
    assert status_code == 200
    assert headers["x-request-id"]
    assert body == {"length": 8}


def test_request_body_limit_fast_rejects_declared_oversize_body() -> None:
    messages = _run_http_asgi(
        _limited_body_application(),
        body_chunks=[],
        headers=[(b"content-length", b"9")],
    )

    status_code, _headers, body = _response(messages)
    assert status_code == 413
    assert body["detail"]["code"] == "request_too_large"


def test_request_context_replaces_unsafe_or_oversized_request_id() -> None:
    for supplied in (b"forged\nlog-entry", b"x" * 129):
        messages = _run_http_asgi(
            _limited_body_application(),
            body_chunks=[b""],
            headers=[(b"x-request-id", supplied)],
        )

        status_code, headers, _body = _response(messages)
        assert status_code == 200
        assert headers["x-request-id"] != supplied.decode("latin-1")
        assert "\n" not in headers["x-request-id"]
        assert len(headers["x-request-id"]) <= 128

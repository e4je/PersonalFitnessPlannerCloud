from __future__ import annotations

from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.core.middleware import ProductionHTTPSMiddleware


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

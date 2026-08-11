from __future__ import annotations

import os
from uuid import uuid4

import httpx


def main() -> None:
    base_url = os.getenv("SMOKE_BASE_URL", "http://127.0.0.1:8000").rstrip("/")
    email = os.getenv("SMOKE_EMAIL") or os.getenv("ADMIN_EMAIL")
    password = os.getenv("SMOKE_PASSWORD") or os.getenv("ADMIN_PASSWORD")
    with httpx.Client(base_url=base_url, timeout=20.0) as client:
        live = client.get("/health/live")
        live.raise_for_status()
        ready = client.get("/health/ready")
        ready.raise_for_status()
        if not email or not password:
            print("smoke_ok health_only=true (set SMOKE_EMAIL and SMOKE_PASSWORD for authenticated checks)")
            return

        login = client.post(
            "/api/v1/auth/login",
            json={"email": email, "password": password, "device_name": "smoke-test"},
        )
        login.raise_for_status()
        tokens = login.json()
        headers = {"Authorization": f"Bearer {tokens['access_token']}"}
        me = client.get("/api/v1/me", headers=headers)
        me.raise_for_status()
        bootstrap = client.get("/api/v1/bootstrap", headers=headers)
        bootstrap.raise_for_status()
        body = bootstrap.json()
        required = {"user", "exercises", "equipment", "sync_cursor", "server_time"}
        missing = required.difference(body)
        if missing:
            raise RuntimeError(f"bootstrap missing keys: {sorted(missing)}")
        sync = client.get("/api/v1/sync/changes", headers=headers)
        sync.raise_for_status()
        logout = client.post(
            "/api/v1/auth/logout",
            headers=headers,
            json={"refresh_token": tokens.get("refresh_token")},
        )
        logout.raise_for_status()
    print(
        "smoke_ok health=true auth=true bootstrap=true sync=true "
        f"request_id={uuid4()}"
    )


if __name__ == "__main__":
    main()

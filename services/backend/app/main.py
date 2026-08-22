from __future__ import annotations

from contextlib import asynccontextmanager
from pathlib import Path
from typing import AsyncIterator

from fastapi import FastAPI
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles

from app import __version__
from app.api import admin, auth, bootstrap, cardio, catalog, health, plans, readiness, recommendation, setup, sync, workouts
from app.core.config import settings
from app.core.errors import request_validation_exception_handler
from app.core.logging import configure_logging
from app.core.middleware import ProductionHTTPSMiddleware, RequestContextMiddleware, SetupRequiredMiddleware
from app.db.session import is_database_configured
from app.services.first_run_setup import announce_first_run_setup


@asynccontextmanager
async def lifespan(_app: FastAPI) -> AsyncIterator[None]:
    configure_logging()
    if not is_database_configured():
        announce_first_run_setup()
    yield


app = FastAPI(
    title=settings.app_name,
    version=settings.api_version,
    summary="Cloud API for synchronized personal training plans and workout records",
    description=(
        "Server-authoritative exercise catalog and immutable training plan versions, "
        "with offline-friendly idempotent workout synchronization."
    ),
    lifespan=lifespan,
    # Interactive API documentation exposes the complete authenticated route
    # surface. Keep it available for local/test workflows, but remove it from
    # production deployments unless an operator explicitly hosts a separate,
    # access-controlled copy of the schema.
    docs_url=None if settings.environment == "production" else "/docs",
    redoc_url=None if settings.environment == "production" else "/redoc",
    openapi_url=None if settings.environment == "production" else "/openapi.json",
)
app.add_exception_handler(RequestValidationError, request_validation_exception_handler)

app.add_middleware(SetupRequiredMiddleware)
app.add_middleware(RequestContextMiddleware)
app.add_middleware(
    CORSMiddleware,
    allow_origins=settings.cors_origins,
    # Authentication uses bearer tokens rather than cookies. Disabling
    # credentialed CORS removes ambient-cookie/CSRF behavior for browser calls.
    allow_credentials=False,
    allow_methods=["GET", "POST", "PATCH", "DELETE", "OPTIONS"],
    allow_headers=["Authorization", "Content-Type", "Idempotency-Key", "If-Match", "X-Request-ID"],
    expose_headers=["X-Request-ID", "ETag"],
)
if settings.environment == "production":
    app.add_middleware(ProductionHTTPSMiddleware)

app.include_router(health.router)
app.include_router(setup.router, prefix=settings.api_v1_prefix)
for api_router in (
    auth.router,
    bootstrap.router,
    plans.router,
    catalog.router,
    workouts.router,
    readiness.router,
    cardio.router,
    sync.router,
    recommendation.router,
    admin.router,
):
    app.include_router(api_router, prefix=settings.api_v1_prefix)

WEB_ROOT = Path(__file__).resolve().parent / "web"
if WEB_ROOT.is_dir():
    # The web console is a same-origin, bearer-token SPA. It contains no server
    # credentials and every data operation still goes through the authenticated
    # API/RBAC boundary above.
    app.mount("/web", StaticFiles(directory=WEB_ROOT, html=True), name="web")


@app.get("/", include_in_schema=False)
def root() -> dict[str, str]:
    return {
        "name": settings.app_name,
        "service_version": __version__,
        "api_version": settings.api_version,
        "docs": "/docs",
        "web": "/web/",
    }

# syntax=docker/dockerfile:1.7
FROM python:3.12.10-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PATH="/home/app/.local/bin:${PATH}"

ARG PIP_INDEX_URL=https://pypi.org/simple
WORKDIR /app

COPY requirements.lock ./requirements.lock
RUN python -m pip install --no-cache-dir --require-hashes --no-deps \
    --index-url "${PIP_INDEX_URL}" -r requirements.lock

COPY alembic.ini ./
COPY alembic ./alembic
COPY app ./app
COPY scripts ./scripts
COPY contracts ./contracts
COPY docker-entrypoint.sh ./docker-entrypoint.sh

RUN addgroup --system app && adduser --system --ingroup app --home /home/app app \
    && chmod +x /app/docker-entrypoint.sh \
    && chown -R app:app /app /home/app

USER app
EXPOSE 8000

ENTRYPOINT ["/app/docker-entrypoint.sh"]
CMD ["gunicorn", "app.main:app", "--bind", "0.0.0.0:8000", "--workers", "1", "--worker-class", "uvicorn.workers.UvicornWorker", "--access-logfile", "-", "--error-logfile", "-"]

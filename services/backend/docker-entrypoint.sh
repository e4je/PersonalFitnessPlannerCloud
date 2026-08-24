#!/bin/sh
set -eu
umask 077

if python -m scripts.runtime_status; then
  python -m scripts.wait_for_database

  if [ "${RUN_MIGRATIONS:-1}" = "1" ]; then
    alembic upgrade head
  fi

  if [ "${RUN_SEED:-1}" = "1" ]; then
    python -m scripts.seed_default_plan
  fi
else
  echo "Database is not configured; starting the first-run Web setup."
fi

exec "$@"

#!/bin/sh
set -eu

if [ "${RUN_MIGRATIONS:-1}" = "1" ]; then
  alembic upgrade head
fi

if [ "${RUN_SEED:-1}" = "1" ]; then
  python -m scripts.seed_default_plan
fi

exec "$@"

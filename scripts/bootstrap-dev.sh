#!/usr/bin/env sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
env_path="$repo_root/.env"

random_secret() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -base64 48 | tr -d '\n'
  else
    od -An -N48 -tx1 /dev/urandom | tr -d ' \n'
  fi
}

if [ ! -f "$env_path" ]; then
  umask 077
  jwt_secret=$(random_secret)
  sed \
    -e "s|^JWT_SECRET=$|JWT_SECRET=$jwt_secret|" \
    "$repo_root/.env.example" > "$env_path"
  printf '%s\n' "Created root .env with random local-only secrets."
fi

venv_python="$repo_root/services/backend/.venv/bin/python"
if [ ! -x "$venv_python" ]; then
  python3 -m venv "$repo_root/services/backend/.venv"
fi
"$venv_python" -m pip install -e "$repo_root/services/backend[dev]"

# Keep the POSIX bootstrap independent of PowerShell. The invariant validator
# runs before and after distributing the authoritative contract snapshots.
"$venv_python" "$repo_root/scripts/validate_contracts.py" --skip-snapshots
for target in \
  "$repo_root/apps/android/app/src/main/resources/default-training-plan.json" \
  "$repo_root/apps/windows/src/PersonalFitnessPlanner.Infrastructure/Data/default-training-plan.json" \
  "$repo_root/services/backend/contracts/default-training-plan.json"
do
  mkdir -p "$(dirname "$target")"
  cp "$repo_root/contracts/default-training-plan.json" "$target"
done
cp "$repo_root/contracts/schema-version.json" "$repo_root/services/backend/contracts/schema-version.json"
cp "$repo_root/contracts/default-training-plan.schema.json" "$repo_root/services/backend/contracts/default-training-plan.schema.json"
for target_dir in \
  "$repo_root/apps/android/app/src/test/resources/contracts" \
  "$repo_root/apps/windows/tests/PersonalFitnessPlanner.Tests/Contracts" \
  "$repo_root/services/backend/contracts/examples"
do
  mkdir -p "$target_dir"
  cp "$repo_root/contracts/examples/recommendation-cases.json" "$target_dir/"
  cp "$repo_root/contracts/examples/progression-cases.json" "$target_dir/"
done
"$venv_python" "$repo_root/scripts/validate_contracts.py"

docker compose --env-file "$env_path" -f "$repo_root/infra/docker-compose.yml" up -d --build backend
docker compose --env-file "$env_path" -f "$repo_root/infra/docker-compose.yml" ps backend

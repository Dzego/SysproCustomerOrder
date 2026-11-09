#!/usr/bin/env bash
# Shared settings and safety checks for the isolated learning environment.
# Every script sources this file. It never touches the original
# `syspro-sqlserver` container or the `sysprocustomerorder_syspro_sql_data` volume.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

readonly LEARNING_CONTAINER="syspro-sqlserver-learning"
readonly LEARNING_VOLUME="syspro_learning_sql_data"
readonly LEARNING_PORT="1434"
readonly LEARNING_DATABASE="SysproLearningDb"
readonly SQL_IMAGE="mcr.microsoft.com/mssql/server:2022-latest"
readonly API_URL="http://localhost:5204"
readonly WEB_URL="http://localhost:4200"

# Never to be used by these scripts.
readonly PROTECTED_CONTAINER="syspro-sqlserver"
readonly PROTECTED_VOLUME="sysprocustomerorder_syspro_sql_data"

# Credentials live outside the repository, readable only by the owner.
readonly SECRETS_DIR="${SYSPRO_LEARNING_SECRETS_DIR:-$HOME/.config/syspro-learning}"
readonly SECRETS_FILE="$SECRETS_DIR/sql.env"

# Generated content root for the API: its settings plus the deterministic import file.
readonly API_CONTENT_ROOT="$REPO_ROOT/.learning-env/api-content"

readonly MIN_FREE_MB=3000

fail() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

info() {
  printf '==> %s\n' "$*"
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "'$1' is required but was not found on PATH. $2"
}

# Refuses anything that names the protected resources, whatever the caller passed.
assert_not_protected() {
  [[ "$LEARNING_CONTAINER" != "$PROTECTED_CONTAINER" ]] || fail "Refusing to use the original container."
  [[ "$LEARNING_VOLUME" != "$PROTECTED_VOLUME" ]] || fail "Refusing to use the original volume."
  [[ "$LEARNING_PORT" != "1433" ]] || fail "Refusing to use port 1433, which belongs to the original container."
}

require_docker() {
  require_command docker "Install Docker Desktop."
  docker info >/dev/null 2>&1 || fail "Docker is not running. Start Docker Desktop and try again."
}

require_free_disk() {
  local free_mb
  free_mb=$(df -m "$REPO_ROOT" | awk 'NR==2 {print $4}')
  ((free_mb >= MIN_FREE_MB)) || fail "Only ${free_mb} MB free; at least ${MIN_FREE_MB} MB is needed."
  info "Free disk space: ${free_mb} MB"
}

# Loads LEARNING_SA_PASSWORD without printing it.
load_secrets() {
  [[ -f "$SECRETS_FILE" ]] || fail "No credentials at $SECRETS_FILE. Run scripts/learning-env/create-secrets.sh first."

  local mode
  mode=$(stat -f '%Lp' "$SECRETS_FILE" 2>/dev/null || stat -c '%a' "$SECRETS_FILE")
  [[ "$mode" == "600" ]] || fail "$SECRETS_FILE must be readable only by you (chmod 600)."

  # shellcheck disable=SC1090
  source "$SECRETS_FILE"
  [[ -n "${MSSQL_SA_PASSWORD:-}" ]] || fail "MSSQL_SA_PASSWORD is missing from $SECRETS_FILE."
}

# The only connection string these scripts build. It always names port 1434 and the learning database.
learning_connection_string() {
  printf 'Server=localhost,%s;Database=%s;User Id=sa;Password=%s;TrustServerCertificate=True;Encrypt=True' \
    "$LEARNING_PORT" "$LEARNING_DATABASE" "$MSSQL_SA_PASSWORD"
}

container_state() {
  # docker inspect prints an empty line as well as failing for a missing container.
  if docker container inspect "$LEARNING_CONTAINER" >/dev/null 2>&1; then
    docker container inspect --format '{{.State.Status}}' "$LEARNING_CONTAINER"
  else
    echo "missing"
  fi
}

# Runs a query inside the learning container, using the password from the container's own
# environment, so it never appears in this machine's process list or in logs.
learning_sql() {
  docker exec "$LEARNING_CONTAINER" /bin/bash -c \
    '/opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -h -1 -W -Q "$1"' _ "$1"
}

require_learning_db_running() {
  assert_not_protected
  require_docker
  [[ "$(container_state)" == "running" ]] || fail "$LEARNING_CONTAINER is not running. Run scripts/learning-env/db-start.sh."
}

require_api() {
  local status
  status=$(curl -s -o /dev/null -w '%{http_code}' "$API_URL/api/import/jobs/2147483647" || true)
  [[ "$status" == "404" ]] || fail "The API is not answering on $API_URL. Run scripts/learning-env/api-start.sh."
}

use_node22() {
  local nvm_node="$HOME/.nvm/versions/node/v22.23.4/bin"
  [[ -d "$nvm_node" ]] && export PATH="$nvm_node:$PATH"
  require_command node "Install Node.js 22."
  [[ "$(node -p 'process.versions.node.split(".")[0]')" == "22" ]] || fail "Node.js 22 is required; found $(node -v)."
}

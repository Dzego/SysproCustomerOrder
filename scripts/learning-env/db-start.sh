#!/usr/bin/env bash
# Creates (first run) or starts the isolated SQL Server container and waits until it accepts queries.
source "$(dirname "$0")/common.sh"

assert_not_protected
require_docker
require_free_disk
load_secrets

state=$(container_state)

if [[ "$state" == "missing" ]]; then
  # Port 1434 must be free, or the container could not bind it.
  if lsof -nP -iTCP:"$LEARNING_PORT" -sTCP:LISTEN >/dev/null 2>&1; then
    fail "Port $LEARNING_PORT is already in use by another program."
  fi

  if ! docker image inspect "$SQL_IMAGE" >/dev/null 2>&1; then
    fail "Image $SQL_IMAGE is not present locally. Pull it deliberately with: docker pull $SQL_IMAGE"
  fi

  info "Creating $LEARNING_CONTAINER on 127.0.0.1:$LEARNING_PORT with volume $LEARNING_VOLUME"
  # The password goes in through --env-file, so it is not on the command line.
  docker run -d \
    --name "$LEARNING_CONTAINER" \
    --platform linux/amd64 \
    --env-file "$SECRETS_FILE" \
    -e ACCEPT_EULA=Y \
    -e MSSQL_PID=Developer \
    -p "127.0.0.1:$LEARNING_PORT:1433" \
    -v "$LEARNING_VOLUME:/var/opt/mssql" \
    --memory 2g \
    --label syspro.environment=learning \
    "$SQL_IMAGE" >/dev/null
elif [[ "$state" != "running" ]]; then
  info "Starting existing $LEARNING_CONTAINER"
  docker start "$LEARNING_CONTAINER" >/dev/null
else
  info "$LEARNING_CONTAINER is already running"
fi

# The label proves this is the container these scripts created.
label=$(docker inspect --format '{{index .Config.Labels "syspro.environment"}}' "$LEARNING_CONTAINER")
[[ "$label" == "learning" ]] || fail "$LEARNING_CONTAINER exists but was not created by these scripts."

info "Waiting for SQL Server to accept queries"
for _ in $(seq 1 60); do
  if learning_sql "SET NOCOUNT ON; SELECT 1" >/dev/null 2>&1; then
    version=$(learning_sql "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('ProductVersion') AS varchar(20))")
    info "SQL Server $version is healthy on port $LEARNING_PORT"
    exit 0
  fi
  sleep 2
done

docker logs --tail 30 "$LEARNING_CONTAINER" >&2
fail "SQL Server did not become ready within two minutes."

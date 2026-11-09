#!/usr/bin/env bash
# Creates a random SA password for the learning container, outside the repository.
# It is never printed. An existing file is left unchanged.
source "$(dirname "$0")/common.sh"

if [[ -f "$SECRETS_FILE" ]]; then
  info "Credentials already exist at $SECRETS_FILE (left unchanged)."
  exit 0
fi

umask 077
mkdir -p "$SECRETS_DIR"

# Upper, lower, digits and a symbol satisfy SQL Server's password policy.
password="Lrn-$(openssl rand -base64 30 | tr -dc 'A-Za-z0-9' | head -c 28)-9a"
printf 'MSSQL_SA_PASSWORD=%s\n' "$password" >"$SECRETS_FILE"
chmod 600 "$SECRETS_FILE"
unset password

info "Created $SECRETS_FILE (mode 600). The password was not displayed."

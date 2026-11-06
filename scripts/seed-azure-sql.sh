#!/usr/bin/env bash
# Loads the verified seed CSVs into the demo's Azure SQL database (schema 'seed', replaced)
# and grants the studio's managed identity read access. Runs as you (Entra admin of the
# demo SQL server). Opens a temporary firewall rule for your current IP on that server only,
# and removes it on exit.
# Usage: ./scripts/seed-azure-sql.sh [--yes]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib-azure.sh"
require_demo_rg

server="$(azure_output sqlServerName)"
fqdn="$(azure_output sqlServerFqdn)"
database="$(azure_output sqlDatabaseName)"
identity="$(azure_output webIdentityName)"
rule="seed-client-temporary"

ip="$(curl -fsS https://api.ipify.org)"
echo "Allowing $ip on $server until this script exits."
az sql server firewall-rule create -g "$demo_rg" -s "$server" -n "$rule" --start-ip-address "$ip" --end-ip-address "$ip" -o none
trap 'az sql server firewall-rule delete -g "$demo_rg" -s "$server" -n "$rule" -o none && echo "Removed firewall rule $rule."' EXIT

# The serverless database may be paused; the first connection resumes it (up to a minute).
export CostToServe__Sql__ConnectionString="Server=tcp:$fqdn,1433;Database=$database;Encrypt=True;Connect Timeout=90;ConnectRetryCount=3;ConnectRetryInterval=10"
ASPNETCORE_ENVIRONMENT=Development dotnet run --project "$repo_root/src/CostToServe.Api" --no-launch-profile -- \
  seed-sql --reader-identity "$identity" "$@"

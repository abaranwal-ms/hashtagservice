#!/usr/bin/env bash
# Usage:
#   1. Fill in principalId in main.bicepparam (AAD object ID for RBAC assignment).
#   2. Ensure you are logged in: az login
#   3. Run: bash infra/cosmos/deploy.sh

set -euo pipefail

SUBSCRIPTION_ID="67e53100-61d9-49b5-8176-ad06015325bf"
RESOURCE_GROUP="hashtagservice"

az account set --subscription "$SUBSCRIPTION_ID"

az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "$(dirname "$0")/main.bicep" \
  --parameters "$(dirname "$0")/main.bicepparam"

#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# deploy.sh — HashtagService Event Hubs infrastructure deploy script
#
# What this script does
# ──────────────────────
#  1. Validates prerequisites (az CLI, python3)
#  2. Sets the correct Azure subscription
#  3. Resolves the current identity's Object ID for RBAC assignments
#  4. Creates the resource group if it does not yet exist
#  5. Deploys infra/main.bicep via az deployment group create
#  6. Reads deployment outputs
#  7. Patches appsettings.json in PostGenerator, HashtagExtractor, and
#     HashtagPersister with the live Event Hubs FQDN and Blob checkpoint URIs
#
# Usage
# ──────
#   cd <repo-root>/infra
#   ./deploy.sh
#
# Optional overrides (environment variables)
# ──────────────────────────────────────────
#   SUBSCRIPTION_ID   – override the default subscription
#   RESOURCE_GROUP    – override the default resource group name
#   LOCATION          – override the default Azure region
#   PRINCIPAL_ID      – skip auto-detection; use a specific principal ID
#   PRINCIPAL_TYPE    – User | ServicePrincipal | Group (default: User)
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

# ─── Defaults (can be overridden via env vars) ────────────────────────────────
SUBSCRIPTION_ID="${SUBSCRIPTION_ID:-67e53100-61d9-49b5-8176-ad06015325bf}"
RESOURCE_GROUP="${RESOURCE_GROUP:-hashtagservice}"
LOCATION="${LOCATION:-southindia}"
PRINCIPAL_TYPE="${PRINCIPAL_TYPE:-User}"

# ─── Paths ────────────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
BICEP_TEMPLATE="${SCRIPT_DIR}/main.bicep"
BICEP_PARAMS="${SCRIPT_DIR}/parameters/dev.bicepparam"
DEPLOYMENT_NAME="hashtagservice-infra-$(date +%Y%m%d%H%M%S)"

# ─── Console colours ─────────────────────────────────────────────────────────
GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; RED='\033[0;31m'; NC='\033[0m'
log()  { echo -e "${GREEN}[deploy]${NC} $*"; }
info() { echo -e "${CYAN}[info]${NC}   $*"; }
warn() { echo -e "${YELLOW}[warn]${NC}   $*"; }
err()  { echo -e "${RED}[error]${NC}  $*" >&2; exit 1; }

# ─── Banner ───────────────────────────────────────────────────────────────────
echo ""
echo -e "${CYAN}╔══════════════════════════════════════════════════════════════╗${NC}"
echo -e "${CYAN}║       HashtagService — Event Hubs Infrastructure Deploy      ║${NC}"
echo -e "${CYAN}╚══════════════════════════════════════════════════════════════╝${NC}"
echo ""

# ─── Pre-flight checks ────────────────────────────────────────────────────────
log "Checking prerequisites..."
command -v az      >/dev/null 2>&1 || err "Azure CLI not found. Install: https://aka.ms/installazurecli"
command -v python3 >/dev/null 2>&1 || err "python3 not found (required to patch appsettings.json files)."

# Verify the user is logged in
az account show >/dev/null 2>&1 || err "Not logged in to Azure. Run: az login"
log "Prerequisites satisfied."

# ─── Subscription ─────────────────────────────────────────────────────────────
log "Setting subscription to ${SUBSCRIPTION_ID}..."
az account set --subscription "${SUBSCRIPTION_ID}"
info "Active subscription: $(az account show --query name -o tsv) (${SUBSCRIPTION_ID})"

# ─── Resolve principal ID ─────────────────────────────────────────────────────
if [[ -z "${PRINCIPAL_ID:-}" ]]; then
  log "Resolving current identity's Object ID..."
  ACCOUNT_TYPE="$(az account show --query user.type -o tsv)"

  if [[ "${ACCOUNT_TYPE}" == "user" ]]; then
    PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv)"
    PRINCIPAL_TYPE="User"
  else
    # Service principal / managed identity path
    CLIENT_ID="$(az account show --query user.name -o tsv)"
    PRINCIPAL_ID="$(az ad sp show --id "${CLIENT_ID}" --query id -o tsv 2>/dev/null || echo '')"
    PRINCIPAL_TYPE="ServicePrincipal"
    if [[ -z "${PRINCIPAL_ID}" ]]; then
      err "Could not resolve Object ID for service principal '${CLIENT_ID}'. Set PRINCIPAL_ID manually."
    fi
  fi
fi

info "Principal ID   : ${PRINCIPAL_ID}"
info "Principal type : ${PRINCIPAL_TYPE}"

# ─── Resource group ───────────────────────────────────────────────────────────
log "Ensuring resource group '${RESOURCE_GROUP}' exists in '${LOCATION}'..."
az group create \
  --name     "${RESOURCE_GROUP}" \
  --location "${LOCATION}"       \
  --output   none
info "Resource group ready."

# ─── Bicep deployment ─────────────────────────────────────────────────────────
log "Starting deployment '${DEPLOYMENT_NAME}'..."
az deployment group create \
  --name           "${DEPLOYMENT_NAME}"  \
  --resource-group "${RESOURCE_GROUP}"   \
  --template-file  "${BICEP_TEMPLATE}"   \
  --parameters     "${BICEP_PARAMS}"     \
  --parameters     principalId="${PRINCIPAL_ID}" principalType="${PRINCIPAL_TYPE}" \
  --output         table

log "Deployment succeeded."

# ─── Retrieve outputs ─────────────────────────────────────────────────────────
log "Reading deployment outputs..."

get_output() {
  az deployment group show \
    --name           "${DEPLOYMENT_NAME}" \
    --resource-group "${RESOURCE_GROUP}"  \
    --query          "properties.outputs.${1}.value" \
    --output         tsv
}

EH_NAMESPACE_HOST="$(get_output ehNamespaceHost)"
STORAGE_ACCOUNT_NAME="$(get_output storageAccountName)"
EXTRACTOR_BLOB_URI="$(get_output extractorCheckpointBlobUri)"
PERSISTER_BLOB_URI="$(get_output persisterCheckpointBlobUri)"

echo ""
info "──────────────────────────────────────────────────────────────"
info "EH Namespace Host       : ${EH_NAMESPACE_HOST}"
info "Storage Account Name    : ${STORAGE_ACCOUNT_NAME}"
info "Extractor Blob URI      : ${EXTRACTOR_BLOB_URI}"
info "Persister Blob URI      : ${PERSISTER_BLOB_URI}"
info "──────────────────────────────────────────────────────────────"
echo ""

# ─── Patch appsettings.json files ─────────────────────────────────────────────
# Uses inline Python (stdlib json only) – no extra packages needed.
patch_appsettings() {
  local FILE="$1"
  local EH_HOST="$2"
  local BLOB_URI="${3:-}"

  if [[ ! -f "${FILE}" ]]; then
    warn "appsettings.json not found at '${FILE}' — skipping patch."
    return
  fi

  log "Patching ${FILE}..."
python3 <<PYEOF
import json, sys

file_path = "${FILE}"
eh_host   = "${EH_HOST}"
blob_uri  = "${BLOB_URI}"

with open(file_path, 'r') as fh:
    cfg = json.load(fh)

cfg.setdefault('EventHub', {})['Namespace'] = eh_host

if blob_uri:
    cfg.setdefault('CheckpointStorage', {})['BlobUri'] = blob_uri

with open(file_path, 'w') as fh:
    json.dump(cfg, fh, indent=2)
    fh.write('\n')

print(f"  EventHub:Namespace      = {eh_host}")
if blob_uri:
    print(f"  CheckpointStorage:BlobUri = {blob_uri}")
PYEOF
}

patch_appsettings \
  "${REPO_ROOT}/PostGenerator/appsettings.json"   \
  "${EH_NAMESPACE_HOST}"

patch_appsettings \
  "${REPO_ROOT}/HashtagExtractor/appsettings.json" \
  "${EH_NAMESPACE_HOST}"                           \
  "${EXTRACTOR_BLOB_URI}"

patch_appsettings \
  "${REPO_ROOT}/HashtagPersister/appsettings.json" \
  "${EH_NAMESPACE_HOST}"                           \
  "${PERSISTER_BLOB_URI}"

# ─── Done ─────────────────────────────────────────────────────────────────────
echo ""
log "✅  All done!"
echo ""
echo -e "${CYAN}Next steps:${NC}"
echo "  1. Verify role propagation (can take ~60 s):  az role assignment list --assignee ${PRINCIPAL_ID} -o table"
echo "  2. Run PostGenerator:       cd ${REPO_ROOT}/PostGenerator      && dotnet run"
echo "  3. Run HashtagExtractor:    cd ${REPO_ROOT}/HashtagExtractor   && dotnet run"
echo "  4. Run HashtagPersister:    cd ${REPO_ROOT}/HashtagPersister   && dotnet run"
echo "  5. Ensure 'az login' is active so DefaultAzureCredential can authenticate."
echo ""
echo -e "${CYAN}To tear down all resources:${NC}"
echo "  az group delete --name ${RESOURCE_GROUP} --yes --no-wait"
echo ""

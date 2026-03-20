# Usage:
#   1. Fill in principalId in main.bicepparam (AAD object ID for RBAC assignment).
#   2. Ensure you are logged in: az login
#   3. Run: .\infra\cosmos\deploy.ps1

$ErrorActionPreference = "Stop"

$TENANT_ID       = "4ef8450a-9048-4ba8-a0f1-e9be61c2ea71"
$SUBSCRIPTION_ID = "67e53100-61d9-49b5-8176-ad06015325bf"
$RESOURCE_GROUP  = "hashtagservice"

az login --tenant $TENANT_ID
az account set --subscription $SUBSCRIPTION_ID

az deployment group create `
  --resource-group $RESOURCE_GROUP `
  --template-file "$PSScriptRoot\main.bicep" `
  --parameters "$PSScriptRoot\main.bicepparam"

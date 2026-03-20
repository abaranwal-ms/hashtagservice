#!/usr/bin/env pwsh
# ─────────────────────────────────────────────────────────────────────────────
# deploy.ps1 — HashtagService Event Hubs infrastructure deploy script
#
# What this script does
# ──────────────────────
#  1. Validates prerequisites (az CLI; pwsh is implied by running this file)
#  2. Sets the correct Azure subscription
#  3. Resolves the current identity's Object ID for RBAC assignments
#  4. Creates the resource group if it does not yet exist
#  5. Deploys infra/kafka/main.bicep via az deployment group create
#  6. Reads deployment outputs
#  7. Patches appsettings.json in PostGenerator, HashtagExtractor, and
#     HashtagPersister with the live Event Hubs FQDN and Blob checkpoint URIs
#
# Requirements
# ─────────────
#   PowerShell 7+ (pwsh)   https://aka.ms/install-powershell
#   Azure CLI              https://aka.ms/installazurecli
#
# Usage
# ──────
#   cd <repo-root>/infra/kafka
#   pwsh ./deploy.ps1
#
# Optional parameter overrides
# ─────────────────────────────
#   -SubscriptionId   – override the default subscription
#   -ResourceGroup    – override the default resource group name
#   -Location         – override the default Azure region
#   -PrincipalId      – skip auto-detection; use a specific principal Object ID
#   -PrincipalType    – User | ServicePrincipal | Group (default: User)
#
# Example with overrides
# ───────────────────────
#   pwsh ./deploy.ps1 -ResourceGroup my-rg -Location westeurope
# ─────────────────────────────────────────────────────────────────────────────
#Requires -Version 7

[CmdletBinding()]
param(
    [string] $SubscriptionId = '67e53100-61d9-49b5-8176-ad06015325bf',
    [string] $ResourceGroup  = 'hashtagservice',
    [string] $Location       = 'southindia',
    [string] $PrincipalId    = '',
    [string] $PrincipalType  = 'User'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ─── Paths ────────────────────────────────────────────────────────────────────
$ScriptDir      = $PSScriptRoot
$RepoRoot       = (Resolve-Path (Join-Path $ScriptDir '../..')).Path
$BicepTemplate  = Join-Path $ScriptDir 'main.bicep'
$BicepParams    = Join-Path $ScriptDir 'parameters/dev.bicepparam'
$DeploymentName = "hashtagservice-infra-$(Get-Date -Format 'yyyyMMddHHmmss')"

# ─── Logging helpers ─────────────────────────────────────────────────────────
function Write-Log  { param([string]$Msg) Write-Host "[deploy] $Msg" -ForegroundColor Green  }
function Write-Info { param([string]$Msg) Write-Host "[info]   $Msg" -ForegroundColor Cyan   }
function Write-Warn { param([string]$Msg) Write-Host "[warn]   $Msg" -ForegroundColor Yellow }
function Write-Err  {
    param([string]$Msg)
    Write-Host "[error]  $Msg" -ForegroundColor Red
    exit 1
}

# ─── Banner ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║       HashtagService — Event Hubs Infrastructure Deploy      ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# ─── Pre-flight checks ────────────────────────────────────────────────────────
Write-Log "Checking prerequisites..."

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Err "Azure CLI not found. Install: https://aka.ms/installazurecli"
}

# Verify the user is logged in (suppress output; only the exit code matters)
$null = az account show 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Err "Not logged in to Azure. Run: az login"
}

Write-Log "Prerequisites satisfied."

# ─── Subscription ─────────────────────────────────────────────────────────────
Write-Log "Setting subscription to $SubscriptionId..."
az account set --subscription $SubscriptionId
if ($LASTEXITCODE -ne 0) { Write-Err "Failed to set subscription '$SubscriptionId'." }

$SubName = (az account show --query name -o tsv)
Write-Info "Active subscription: $SubName ($SubscriptionId)"

# ─── Resolve principal ID ─────────────────────────────────────────────────────
if ([string]::IsNullOrEmpty($PrincipalId)) {
    Write-Log "Resolving current identity's Object ID..."
    $AccountType = (az account show --query user.type -o tsv)

    if ($AccountType -eq 'user') {
        $PrincipalId   = (az ad signed-in-user show --query id -o tsv)
        $PrincipalType = 'User'
    }
    else {
        # Service principal / managed identity path
        $ClientId    = (az account show --query user.name -o tsv)
        $PrincipalId = (az ad sp show --id $ClientId --query id -o tsv 2>$null)
        $PrincipalType = 'ServicePrincipal'
        if ([string]::IsNullOrEmpty($PrincipalId)) {
            Write-Err "Could not resolve Object ID for service principal '$ClientId'. Set -PrincipalId manually."
        }
    }
}

Write-Info "Principal ID   : $PrincipalId"
Write-Info "Principal type : $PrincipalType"

# ─── Resource group ───────────────────────────────────────────────────────────
Write-Log "Ensuring resource group '$ResourceGroup' exists in '$Location'..."
az group create --name $ResourceGroup --location $Location --output none
if ($LASTEXITCODE -ne 0) { Write-Err "Failed to create/verify resource group '$ResourceGroup'." }
Write-Info "Resource group ready."

# ─── Bicep deployment ─────────────────────────────────────────────────────────
Write-Log "Starting deployment '$DeploymentName'..."
az deployment group create `
    --name           $DeploymentName  `
    --resource-group $ResourceGroup   `
    --template-file  $BicepTemplate   `
    --parameters     $BicepParams     `
    --parameters     "principalId=$PrincipalId" "principalType=$PrincipalType" `
    --output         table

if ($LASTEXITCODE -ne 0) { Write-Err "Deployment '$DeploymentName' failed." }
Write-Log "Deployment succeeded."

# ─── Retrieve outputs ─────────────────────────────────────────────────────────
Write-Log "Reading deployment outputs..."

function Get-DeploymentOutput {
    param([string]$OutputName)
    $value = (az deployment group show `
        --name           $DeploymentName `
        --resource-group $ResourceGroup  `
        --query          "properties.outputs.$OutputName.value" `
        --output         tsv)
    if ($LASTEXITCODE -ne 0) { Write-Err "Failed to read deployment output '$OutputName'." }
    return $value
}

$EhNamespaceHost    = Get-DeploymentOutput 'ehNamespaceHost'
$StorageAccountName = Get-DeploymentOutput 'storageAccountName'
$ExtractorBlobUri   = Get-DeploymentOutput 'extractorCheckpointBlobUri'
$PersisterBlobUri   = Get-DeploymentOutput 'persisterCheckpointBlobUri'

Write-Host ""
Write-Info "──────────────────────────────────────────────────────────────"
Write-Info "EH Namespace Host       : $EhNamespaceHost"
Write-Info "Storage Account Name    : $StorageAccountName"
Write-Info "Extractor Blob URI      : $ExtractorBlobUri"
Write-Info "Persister Blob URI      : $PersisterBlobUri"
Write-Info "──────────────────────────────────────────────────────────────"
Write-Host ""

# ─── Patch appsettings.json files ─────────────────────────────────────────────
# Uses PowerShell's built-in ConvertFrom-Json / ConvertTo-Json — no extra tools needed.
function Update-AppSettings {
    param(
        [string] $FilePath,
        [string] $EhHost,
        [string] $BlobUri = ''
    )

    if (-not (Test-Path $FilePath)) {
        Write-Warn "appsettings.json not found at '$FilePath' — skipping patch."
        return
    }

    Write-Log "Patching $FilePath..."
    $cfg = Get-Content $FilePath -Raw | ConvertFrom-Json

    # ── EventHub:Namespace ───────────────────────────────────────────────────
    if ($null -eq $cfg.PSObject.Properties['EventHub']) {
        $cfg | Add-Member -NotePropertyName 'EventHub' -NotePropertyValue ([PSCustomObject]@{}) -Force
    }
    $cfg.EventHub | Add-Member -NotePropertyName 'Namespace' -NotePropertyValue $EhHost -Force

    # ── CheckpointStorage:BlobUri (consumer services only) ───────────────────
    if (-not [string]::IsNullOrEmpty($BlobUri)) {
        if ($null -eq $cfg.PSObject.Properties['CheckpointStorage']) {
            $cfg | Add-Member -NotePropertyName 'CheckpointStorage' -NotePropertyValue ([PSCustomObject]@{}) -Force
        }
        $cfg.CheckpointStorage | Add-Member -NotePropertyName 'BlobUri' -NotePropertyValue $BlobUri -Force
    }

    # Write back — depth 10 handles all realistic nesting; UTF-8 without BOM; trailing newline.
    $jsonContent = $cfg | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText(
        $FilePath,
        ($jsonContent + [System.Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false)   # UTF-8 without BOM
    )

    Write-Info "  EventHub:Namespace        = $EhHost"
    if (-not [string]::IsNullOrEmpty($BlobUri)) {
        Write-Info "  CheckpointStorage:BlobUri = $BlobUri"
    }
}

Update-AppSettings `
    -FilePath (Join-Path $RepoRoot 'PostGenerator/appsettings.json') `
    -EhHost   $EhNamespaceHost

Update-AppSettings `
    -FilePath (Join-Path $RepoRoot 'HashtagExtractor/appsettings.json') `
    -EhHost   $EhNamespaceHost `
    -BlobUri  $ExtractorBlobUri

Update-AppSettings `
    -FilePath (Join-Path $RepoRoot 'HashtagPersister/appsettings.json') `
    -EhHost   $EhNamespaceHost `
    -BlobUri  $PersisterBlobUri

# ─── Done ─────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Log "✅  All done!"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Verify role propagation (can take ~60 s):  az role assignment list --assignee $PrincipalId -o table"
Write-Host "  2. Run PostGenerator:     cd `"$RepoRoot/PostGenerator`"    && dotnet run"
Write-Host "  3. Run HashtagExtractor:  cd `"$RepoRoot/HashtagExtractor`" && dotnet run"
Write-Host "  4. Run HashtagPersister:  cd `"$RepoRoot/HashtagPersister`" && dotnet run"
Write-Host "  5. Ensure 'az login' is active so DefaultAzureCredential can authenticate."
Write-Host ""
Write-Host "To tear down all resources:" -ForegroundColor Cyan
Write-Host "  az group delete --name $ResourceGroup --yes --no-wait"
Write-Host ""

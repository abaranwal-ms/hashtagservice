#!/usr/bin/env pwsh
# ─────────────────────────────────────────────────────────────────────────────
# cleanup.ps1 — HashtagService infrastructure cleanup script
#
# What this script does
# ──────────────────────
#  Selectively tears down Azure resources for the HashtagService project.
#  Choose a target to control how much gets deleted:
#
#  Target           What it deletes                              Monthly savings
#  ────────────     ─────────────────────────────────────────    ───────────────
#  Messaging        Checkpoints + Event Hubs namespace           ~$22/month (clean restart)
#  All              Entire resource group (everything)           ~$22/month
#
# Usage
# ──────
#   pwsh ./infra/cleanup.ps1                          # interactive menu
#   pwsh ./infra/cleanup.ps1 -Target Messaging        # checkpoints + EH namespace (clean restart)
#   pwsh ./infra/cleanup.ps1 -Target All              # delete entire resource group
#   pwsh ./infra/cleanup.ps1 -Target All -Force       # skip confirmation prompts
#
# Recreate after cleanup
# ───────────────────────
#   pwsh ./infra/kafka/deploy.ps1     # Event Hubs + Storage (~2 min)
#   pwsh ./infra/cosmos/deploy.ps1    # Cosmos DB (~3 min)
# ─────────────────────────────────────────────────────────────────────────────
#Requires -Version 7

[CmdletBinding()]
param(
    [ValidateSet('Messaging', 'All')]
    [string] $Target = '',

    [string] $SubscriptionId = '67e53100-61d9-49b5-8176-ad06015325bf',
    [string] $ResourceGroup  = 'hashtagservice',

    [string] $EventHubsNamespace  = 'hashtagservice-eh',
    [string] $CosmosAccountName   = 'hashtagservice-cosmos',

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ─── Logging helpers ─────────────────────────────────────────────────────────
function Write-Log  { param([string]$Msg) Write-Host "[cleanup] $Msg" -ForegroundColor Green  }
function Write-Info { param([string]$Msg) Write-Host "[info]    $Msg" -ForegroundColor Cyan   }
function Write-Warn { param([string]$Msg) Write-Host "[warn]    $Msg" -ForegroundColor Yellow }
function Write-Err  {
    param([string]$Msg)
    Write-Host "[error]   $Msg" -ForegroundColor Red
    exit 1
}

# ─── Banner ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Red
Write-Host "║         HashtagService — Infrastructure Cleanup              ║" -ForegroundColor Red
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Red
Write-Host ""

# ─── Pre-flight checks ────────────────────────────────────────────────────────
Write-Log "Checking prerequisites..."
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Err "Azure CLI not found. Install: https://aka.ms/installazurecli"
}
$null = az account show 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Err "Not logged in to Azure. Run: az login"
}
Write-Log "Prerequisites satisfied."

# ─── Subscription ─────────────────────────────────────────────────────────────
Write-Log "Setting subscription to $SubscriptionId..."
az account set --subscription $SubscriptionId
if ($LASTEXITCODE -ne 0) { Write-Err "Failed to set subscription." }

# ─── Interactive menu if no target specified ──────────────────────────────────
if ([string]::IsNullOrEmpty($Target)) {
    Write-Host ""
    Write-Host "  What would you like to clean up?" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  [1] Messaging     — Reset checkpoints + delete Event Hubs (~`$22/month savings)"
    Write-Host "  [2] All           — Delete entire resource group (⚠️ everything)"
    Write-Host "  [Q] Quit"
    Write-Host ""
    $choice = Read-Host "  Enter choice [1-2, Q]"

    $Target = switch ($choice) {
        '1' { 'Messaging' }
        '2' { 'All' }
        'Q' { Write-Host ""; Write-Log "Cancelled."; exit 0 }
        'q' { Write-Host ""; Write-Log "Cancelled."; exit 0 }
        default { Write-Err "Invalid choice: $choice" }
    }
    Write-Host ""
}

# ─── Confirmation ─────────────────────────────────────────────────────────────
function Confirm-Action {
    param([string]$Message)
    if ($Force) { return }
    Write-Host ""
    Write-Warn $Message
    $answer = Read-Host "  Continue? [y/N]"
    if ($answer -notin @('y', 'Y', 'yes', 'Yes')) {
        Write-Log "Cancelled."
        exit 0
    }
}

# ─── Resolve storage account name ────────────────────────────────────────────
function Get-StorageAccountName {
    $name = az storage account list `
        --resource-group $ResourceGroup `
        --query "[0].name" -o tsv 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($name)) {
        return $null
    }
    return $name
}

# ═════════════════════════════════════════════════════════════════════════════
#  CLEANUP TARGETS
# ═════════════════════════════════════════════════════════════════════════════

# ─── Checkpoints (internal helper, used by Messaging) ────────────────────────
function Clear-Checkpoints {
    Write-Log "Resetting consumer checkpoints..."

    $StorageAccount = Get-StorageAccountName
    if ($null -eq $StorageAccount) {
        Write-Warn "No storage account found in resource group '$ResourceGroup'. Nothing to reset."
        return
    }
    Write-Info "Storage account: $StorageAccount"

    $containers = @('persister-checkpoints', 'extractor-checkpoints')
    foreach ($container in $containers) {
        # Check if container exists
        $exists = az storage container exists `
            --account-name $StorageAccount `
            --name $container `
            --auth-mode login `
            --query exists -o tsv 2>$null
        if ($exists -ne 'true') {
            Write-Warn "Container '$container' does not exist — skipping."
            continue
        }

        Write-Log "Clearing $container..."

        az storage blob delete-batch `
            --account-name $StorageAccount `
            --source $container `
            --auth-mode login `
            --pattern "*/checkpoint/*" 2>$null | Out-Null

        az storage blob delete-batch `
            --account-name $StorageAccount `
            --source $container `
            --auth-mode login `
            --pattern "*/ownership/*" 2>$null | Out-Null

        Write-Info "  $container — cleared."
    }

    Write-Log "✅  Checkpoints reset. Restart consumer services to pick up from the beginning."
}

# ─── Messaging (Checkpoints + EventHubs) ─────────────────────────────────────
function Remove-Messaging {
    Confirm-Action "This will RESET checkpoints and DELETE the Event Hubs namespace '$EventHubsNamespace' (saves ~`$22/month)."
    Write-Log "Step 1/2: Resetting checkpoints..."
    Clear-Checkpoints
    Write-Host ""
    Write-Log "Step 2/2: Deleting Event Hubs namespace..."

    az eventhubs namespace delete `
        --name $EventHubsNamespace `
        --resource-group $ResourceGroup 2>$null

    if ($LASTEXITCODE -eq 0) {
        Write-Log "✅  Checkpoints reset + Event Hubs namespace deleted."
        Write-Info "Recreate with: pwsh ./infra/kafka/deploy.ps1"
        Write-Info "Checkpoints are already clean — no stale-offset errors on restart."
    } else {
        Write-Warn "Namespace '$EventHubsNamespace' may not exist or was already deleted."
    }
}

# ─── All (resource group) ────────────────────────────────────────────────────
function Remove-All {
    Confirm-Action "⚠️  This will DELETE the ENTIRE resource group '$ResourceGroup' — Event Hubs, Cosmos DB, Storage, RBAC, everything."
    Write-Log "Deleting resource group '$ResourceGroup'..."

    az group delete `
        --name $ResourceGroup `
        --yes `
        --no-wait 2>$null

    if ($LASTEXITCODE -eq 0) {
        Write-Log "✅  Resource group deletion initiated (runs in background)."
        Write-Info "Recreate with:"
        Write-Info "  pwsh ./infra/kafka/deploy.ps1   # Event Hubs + Storage (~2 min)"
        Write-Info "  pwsh ./infra/cosmos/deploy.ps1  # Cosmos DB (~3 min)"
    } else {
        Write-Warn "Resource group '$ResourceGroup' may not exist or was already deleted."
    }
}

# ─── Dispatch ─────────────────────────────────────────────────────────────────
Write-Log "Target: $Target"
Write-Host ""

switch ($Target) {
    'Messaging'   { Remove-Messaging }
    'All'         { Remove-All }
}

# ─── Summary ──────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║                     Cleanup Complete                         ║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""

# Cost reminder
if ($Target -in @('Messaging', 'All')) {
    Write-Info "💰 Event Hubs was the main cost driver (~`$22/month). That charge is now stopped."
}
if ($Target -eq 'Messaging') {
    Write-Info "💡 Recreate with: pwsh ./infra/kafka/deploy.ps1 — checkpoints are already clean."
}
if ($Target -eq 'All') {
    Write-Info "💡 Recreate with: pwsh ./infra/kafka/deploy.ps1 + pwsh ./infra/cosmos/deploy.ps1"
}
Write-Host ""
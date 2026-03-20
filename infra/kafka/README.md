# HashtagService — Event Hubs Infrastructure (`infra/kafka/`)

Bicep templates that provision every Azure resource required by the HashtagService
messaging layer (Azure Event Hubs + Azure Blob Storage for checkpoints) and wire up
RBAC so that `DefaultAzureCredential` works from day one—no connection strings needed.

---

## Directory layout

```
infra/
└── kafka/
    ├── main.bicep                   # Root orchestrator; imports the three modules
    ├── deploy.ps1                   # End-to-end deploy + appsettings.json patcher (PowerShell 7+)
    ├── parameters/
    │   └── dev.bicepparam           # Parameter values for the dev/learning environment
    ├── modules/
    │   ├── eventhubs.bicep          # Namespace, topics, consumer groups
    │   ├── storage.bicep            # Storage account + checkpoint containers
    │   └── rbac.bicep               # RBAC role assignments
    └── README.md                    # This file
```

---

## Resources provisioned

| Resource | Name | Notes |
|---|---|---|
| Event Hubs Namespace | `hashtagservice-eh` | Standard tier, 1 TU, Kafka-compatible |
| Event Hub | `posts-topic` | 3 partitions, 1-day retention |
| Event Hub | `hashtags-topic` | 3 partitions, 1-day retention |
| Consumer Group | `$Default` (both topics) | Declared explicitly for Bicep state ownership |
| Storage Account | `hashtsvcchk<8-char-suffix>` | Standard LRS; suffix derived from resource group ID |
| Blob Container | `extractor-checkpoints` | Used by HashtagExtractor (`HashTagCounter`) |
| Blob Container | `persister-checkpoints` | Used by HashtagPersister |
| Role Assignment | Azure Event Hubs Data Owner → namespace | Covers send + receive for all services |
| Role Assignment | Storage Blob Data Contributor → storage account | Covers EventProcessorClient checkpointing |

---

## Prerequisites

| Tool | Minimum version | Install |
|---|---|---|
| Azure CLI | 2.55 | https://aka.ms/installazurecli |
| PowerShell | 7.0 | https://aka.ms/install-powershell |
| Bicep (bundled with az) | 0.25 | `az bicep upgrade` |

Authenticate before running:

```powershell
az login
```

---

## Quick start

```powershell
cd infra/kafka
pwsh ./deploy.ps1
```

That's it. The script will:

1. Set the subscription to `67e53100-61d9-49b5-8176-ad06015325bf`
2. Auto-detect your Azure Object ID (`az ad signed-in-user show`)
3. Create the `hashtagservice` resource group in `southindia` if it doesn't exist
4. Run `az deployment group create` with `main.bicep` + `dev.bicepparam`
5. Read the deployment outputs
6. Patch `EventHub:Namespace` and `CheckpointStorage:BlobUri` in all three `appsettings.json` files

### Parameter overrides

| Parameter | Default | Purpose |
|---|---|---|
| `-SubscriptionId` | `67e53100-…` | Target subscription |
| `-ResourceGroup` | `hashtagservice` | Target resource group |
| `-Location` | `southindia` | Azure region |
| `-PrincipalId` | *(auto-detected)* | Skip `az ad signed-in-user show`; use a fixed principal |
| `-PrincipalType` | `User` | `User` / `ServicePrincipal` / `Group` |

Example with overrides:

```powershell
pwsh ./deploy.ps1 -ResourceGroup my-rg -Location westeurope
```

---

## Manual deployment (without deploy.ps1)

```powershell
# 1. Set subscription
az account set --subscription 67e53100-61d9-49b5-8176-ad06015325bf

# 2. Create resource group
az group create --name hashtagservice --location southindia

# 3. Get your principal ID
$MY_ID = (az ad signed-in-user show --query id -o tsv)

# 4. Deploy
az deployment group create `
  --name           hashtagservice-infra `
  --resource-group hashtagservice `
  --template-file  infra/kafka/main.bicep `
  --parameters     infra/kafka/parameters/dev.bicepparam `
  --parameters     "principalId=$MY_ID" principalType=User

# 5. Read outputs
az deployment group show `
  --name hashtagservice-infra `
  --resource-group hashtagservice `
  --query properties.outputs
```

---

## appsettings.json values set by deploy.ps1

After a successful deploy the script updates:

### PostGenerator/appsettings.json

| Key | Value |
|---|---|
| `EventHub:Namespace` | `hashtagservice-eh.servicebus.windows.net` |

### HashtagExtractor/appsettings.json

| Key | Value |
|---|---|
| `EventHub:Namespace` | `hashtagservice-eh.servicebus.windows.net` |
| `CheckpointStorage:BlobUri` | `https://hashtsvcchk<suffix>.blob.core.windows.net/extractor-checkpoints` |

### HashtagPersister/appsettings.json

| Key | Value |
|---|---|
| `EventHub:Namespace` | `hashtagservice-eh.servicebus.windows.net` |
| `CheckpointStorage:BlobUri` | `https://hashtsvcchk<suffix>.blob.core.windows.net/persister-checkpoints` |

---

## Authentication in the services

All three services use `DefaultAzureCredential` (from `Azure.Identity`).
In a local developer workflow, it resolves to the identity from `az login`.

```
PostGenerator      → EventHubProducerClient(namespace, "posts-topic",    new DefaultAzureCredential())
HashtagExtractor   → EventHubProducerClient(namespace, "hashtags-topic", new DefaultAzureCredential())
                   → EventProcessorClient(blobClient,  "$Default", namespace, "posts-topic",    cred)
HashtagPersister   → EventProcessorClient(blobClient,  "$Default", namespace, "hashtags-topic", cred)
```

Credential chain (in order of precedence):

1. `AZURE_CLIENT_ID` / `AZURE_CLIENT_SECRET` / `AZURE_TENANT_ID` environment variables (CI/CD)
2. Workload Identity (AKS)
3. Managed Identity
4. Azure CLI (`az login`) ← primary path for local development

---

## Scaling considerations

| Lever | Current value | When to change |
|---|---|---|
| Throughput Units | 1 | Increase if ingress > 1 MB/s or egress > 2 MB/s |
| Partition count | 3 | Matches `Service:ThreadCount = 3`; increase proportionally with parallelism |
| Auto-inflate | off | Enable `isAutoInflateEnabled = true` to handle burst traffic automatically |
| Message retention | 1 day | Extend in `messageRetentionInDays` if consumers need to replay events |

Partition count **cannot be decreased** after creation. To reduce partitions, recreate the Event Hub.

---

## Production least-privilege RBAC

For production workloads replace the single `Azure Event Hubs Data Owner` assignment
with per-service, per-topic grants. In `modules/rbac.bicep`, swap the single
`ehDataOwnerAssignment` resource for these three fine-grained assignments:

| Service | Role | Scope |
|---|---|---|
| PostGenerator | Azure Event Hubs Data **Sender** | `posts-topic` resource ID |
| HashtagExtractor | Azure Event Hubs Data **Receiver** | `posts-topic` resource ID |
| HashtagExtractor | Azure Event Hubs Data **Sender** | `hashtags-topic` resource ID |
| HashtagPersister | Azure Event Hubs Data **Receiver** | `hashtags-topic` resource ID |

Role definition IDs:

```
Data Sender   : 2b629674-e913-4e4d-b6b4-beabb96f432d
Data Receiver : a638d3c7-ab3a-418d-83e6-5f17a39d4fde
Data Owner    : f526a384-b230-433a-b45c-95f59c4a2dec  (current – dev only)
```

---

## Teardown

```bash
az group delete --name hashtagservice --yes --no-wait
```

> ⚠️  This deletes **all** resources in the resource group, including any Cosmos DB
> accounts created by the Database agent.  Confirm with the team before running.

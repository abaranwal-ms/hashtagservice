using './main.bicep'

// Replace with the AAD object ID of the managed identity or user that should
// have Cosmos DB Built-in Data Contributor access (run:
//   az ad signed-in-user show --query id -o tsv          # for your own user
//   az identity show -n <name> -g <rg> --query principalId -o tsv  # for a managed identity
// )
param principalId = '346af360-2e13-4bbf-b7ff-47f4865ce521'

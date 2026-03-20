// dev.bicepparam — parameter values for the HashtagService development/learning environment
//
// IMPORTANT: principalId is intentionally NOT set here.
// deploy.ps1 resolves it at runtime via:  az ad signed-in-user show --query id -o tsv
// and passes it as --parameters principalId="..." on the CLI invocation.
// This avoids committing user-specific Azure Object IDs into source control.

using '../main.bicep'

param location      = 'southindia'
param ehNamespaceName = 'hashtagservice-eh'
param principalId   = ''  // overridden at deploy time by deploy.ps1
param principalType = 'User'
param environmentTag = 'dev'

// ─────────────────────────────────────────────────────────────────────────────
// Tessera — Microsoft Entra setup (Infrastructure-as-Code)
//
// Provisions the TWO Entra application objects Tessera + the chat need:
//   1. the chat OIDC app  (LibreChat "Sign in with Microsoft" + token reuse)
//   2. the Tessera broker app + a FEDERATED credential (secretless Key Vault)
//
// Uses the Microsoft Graph Bicep extension (GA v1.0). Deploy with:
//   az deployment sub create -l westeurope -f main.bicep -p chatDomain=chat.example.com
//
// NOTE: the federated-identity-credential resource shape under the Graph Bicep
// extension is evolving; the `az` CLI fallback in README.md is the verified path
// if this resource fails to deploy. Review against https://aka.ms/graphbicep.
// Decision context: docs/adr/0011-identity-provider-sso.md
// ─────────────────────────────────────────────────────────────────────────────

targetScope = 'subscription'

extension microsoftGraphV1_0

@description('Public hostname of the chat (LibreChat), e.g. chat.example.com')
param chatDomain string

@description('OIDC issuer of the Kubernetes cluster (for Tessera workload identity federation).')
param clusterOidcIssuer string

@description('Kubernetes namespace:serviceaccount subject for the Tessera broker.')
param tesseraServiceAccountSubject string = 'system:serviceaccount:default:tessera'

// ── 1. Chat OIDC app ─────────────────────────────────────────────────────────
// signInAudience = AzureADandPersonalMicrosoftAccount so a user who NEVER used
// Azure but has a personal Microsoft account (live/outlook) can sign in, and
// work/school accounts also work. (ADR 0011 / specs/identity-azure-setup.md)
resource chatApp 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: 'tessera-chat'
  displayName: 'Tessera Chat (LibreChat)'
  signInAudience: 'AzureADandPersonalMicrosoftAccount'
  web: {
    redirectUris: [
      'https://${chatDomain}/oauth/callback/openid'
    ]
    implicitGrantSettings: {
      enableIdTokenIssuance: false
      enableAccessTokenIssuance: false
    }
  }
  // Expose an API + scope so OPENID_REUSE_TOKENS yields an ACCESS token whose
  // `aud` is THIS app id. In iteration 1 (Flow B / shared audience, ADR 0011) the
  // chat app IS the shared system audience: OPENID_SCOPE = api://<chatAppId>/.default
  // and Tessera validates `aud == chatAppId`. (Flow A — a dedicated Tessera API
  // app + On-Behalf-Of so `aud == api://tessera` — is the deferred strict upgrade;
  // it needs LibreChat fork work and is flaky for personal MSA. See ADR 0011.)
  api: {
    requestedAccessTokenVersion: 2
    oauth2PermissionScopes: [
      {
        id: guid('tessera-chat', 'access')
        adminConsentDescription: 'Allow the chat to call Tessera on behalf of the signed-in user.'
        adminConsentDisplayName: 'Access on behalf of user'
        userConsentDescription: 'Allow the chat to act on your behalf.'
        userConsentDisplayName: 'Act on your behalf'
        value: 'access'
        type: 'User'
        isEnabled: true
      }
    ]
  }
}

resource chatSp 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: chatApp.appId
}

// ── 2. Tessera broker app + workload-identity federation (NO client secret) ──
// Replaces the long-lived AZURE_CLIENT_SECRET with a federated credential that
// trusts the cluster's ServiceAccount token (ADR 0003).
resource tesseraApp 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: 'tessera-broker'
  displayName: 'Tessera Broker'
  signInAudience: 'AzureADMyOrg'

  // Federated identity credential — secretless access to Key Vault.
  // (If this child resource fails under the current Graph Bicep version, create
  // it with the `az ad app federated-credential create` command in README.md.)
  resource tesseraFic 'federatedIdentityCredentials@v1.0' = {
    name: 'tessera-broker/k8s-tessera'
    issuer: clusterOidcIssuer
    subject: tesseraServiceAccountSubject
    audiences: [
      'api://AzureADTokenExchange'
    ]
    description: 'Tessera broker → Key Vault, secretless (workload identity federation).'
  }
}

resource tesseraSp 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: tesseraApp.appId
}

// ── Outputs (feed LibreChat env + the Tessera deployment) ────────────────────
output chatAppId string = chatApp.appId
// LibreChat: OPENID_SCOPE — points at the chat app = the shared audience (Flow B).
output chatScope string = 'api://${chatApp.appId}/.default openid profile email offline_access'
// Tessera: the `aud` value to validate on the forwarded access token (Flow B).
output expectedTokenAudience string = chatApp.appId
output tesseraAppId string = tesseraApp.appId
output tesseraSpObjectId string = tesseraSp.id

// ─────────────────────────────────────────────────────────────────────────────
// Tessera — register a NON-HUMAN caller (CLI / automation / unattended job)
//
// A pure automation has NO end-user OIDC token. It authenticates as *itself* and
// requests a token for the shared system API app (the chat app — Flow B, ADR 0011),
// so the token's `aud` is one Tessera already validates, and it carries
// `roles: ["Tessera.Call"]` + `appid` = this caller's identity (the "WHO").
//
// SAFEST credential = WORKLOAD IDENTITY FEDERATION (a federated identity
// credential): the job presents an OIDC token its own platform mints (GitHub
// Actions, a Kubernetes ServiceAccount, another Azure workload), exchanges it for
// a short-lived Entra token, and holds NO client secret at all (ADR 0003). This
// template creates exactly that — there is deliberately no `passwordCredential`.
//
// Deploy (the caller app + its federated credential are safe to automate):
//   az deployment sub create -l westeurope -f automation-caller.bicep \
//     -p callerName=tessera-price-crawler \
//        systemApiAppId=<chatAppId output of main.bicep> \
//        systemSpObjectId=<chatSpObjectId output of main.bicep> \
//        systemAppRoleId=<automationAppRoleId output of main.bicep> \
//        federationIssuer=https://token.actions.githubusercontent.com \
//        federationSubject='repo:my-org/my-repo:ref:refs/heads/main'
//
// The app-role ASSIGNMENT (granting the caller the Tessera.Call role) is a
// privileged directory write that needs admin consent — see README "non-human
// caller" section for the `az` fallback if the Graph Bicep resource below is not
// yet supported in your extension version.
// ─────────────────────────────────────────────────────────────────────────────

targetScope = 'subscription'

extension microsoftGraphV1_0

@description('Display/unique name for this automation caller, e.g. tessera-price-crawler. Prefer ONE app per job for least privilege + clean attribution.')
param callerName string

@description('appId of the shared SYSTEM API app (chatAppId output of main.bicep) — the audience this caller requests a token for (Flow B).')
param systemApiAppId string

@description('Service-principal OBJECT id of the shared system app (chatSpObjectId output of main.bicep) — the resource the app role is assigned ON.')
param systemSpObjectId string

@description('Object id of the Tessera.Call app role on the system app (automationAppRoleId output of main.bicep).')
param systemAppRoleId string

@description('OIDC issuer that mints the workload token for this job (NO client secret). GitHub Actions: https://token.actions.githubusercontent.com ; or your cluster OIDC issuer.')
param federationIssuer string

@description('Subject the federated credential trusts. GitHub Actions: repo:ORG/REPO:ref:refs/heads/main (or :environment:prod). K8s: system:serviceaccount:NS:SA.')
param federationSubject string

// ── The non-human caller app (one per job; least privilege) ─────────────────
// signInAudience = AzureADMyOrg: this is a workload identity in your own tenant,
// not a sign-in app for external users.
resource callerApp 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: callerName
  displayName: callerName
  signInAudience: 'AzureADMyOrg'

  // It needs the Tessera.Call APPLICATION role on the system app so its app-only
  // token is accepted (least privilege — exactly one role, no Graph permissions).
  requiredResourceAccess: [
    {
      resourceAppId: systemApiAppId
      resourceAccess: [
        {
          id: systemAppRoleId
          type: 'Role' // application permission (app role), not a delegated scope
        }
      ]
    }
  ]

  // ── Workload identity federation — NO client secret to leak/rotate (ADR 0003).
  // The job exchanges its platform OIDC token for a short-lived Entra token.
  resource fic 'federatedIdentityCredentials@v1.0' = {
    name: '${callerName}/job-federation'
    issuer: federationIssuer
    subject: federationSubject
    audiences: [
      'api://AzureADTokenExchange'
    ]
    description: 'Secretless workload-identity federation for the ${callerName} automation caller.'
  }
}

resource callerSp 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: callerApp.appId
}

// ── App-role assignment (PRIVILEGED — needs admin). Grants this caller the
// Tessera.Call role on the system app. If this resource shape is unsupported in
// your Graph Bicep version, use the `az rest` fallback in README.md instead.
resource callerRoleAssignment 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: systemAppRoleId
  principalId: callerSp.id
  resourceId: systemSpObjectId
}

// ── Outputs ──────────────────────────────────────────────────────────────────
// The caller's WHO — Tessera authorizes this appId via a grant in grants.toml.
output callerAppId string = callerApp.appId
output callerSpObjectId string = callerSp.id
// The token this caller requests (client-credentials / WIF): scope → the shared
// system app, so `aud` is what Tessera validates (Flow B). NO secret involved.
output callerTokenScope string = 'api://${systemApiAppId}/.default'
// The `aud` Tessera will see and validate on this caller's token.
output expectedTokenAudience string = systemApiAppId

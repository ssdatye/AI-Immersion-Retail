---
title: "ADR 003: Entra sign-in and app roles"
description: "Increment 4 decisions: Entra ID sign-in for the What-If Studio, app roles FinanceAnalyst and RegionalOps, region scope and demo users."
ms.date: 2026-10-28
status: Accepted
---

## Context

Increment 4 replaces the local dev personas with real sign-in. Dana (FinanceAnalyst) sees
the whole network and approves. Luis (RegionalOps) sees only the Southeast and proposes and
executes actions. Both are demo users in the owner's default directory.

## Options considered

| Option                              | Pros                                         | Cons                                    | Effort | Risk       |
|-------------------------------------|----------------------------------------------|-----------------------------------------|--------|------------|
| SPA with MSAL, API validates tokens | No secrets; works locally and on App Service | Token handling in the browser           | Medium | Low        |
| App Service built-in authentication | Little code                                  | Azure-only; no local path; header trust | Low    | Medium     |
| Server-side OIDC with cookies       | No tokens in the browser                     | Needs a client secret; more server code | Medium | Low-medium |

Chosen: SPA with MSAL. It needs no secret to store, keeps local and Azure behavior the same,
and puts the caller's identity in every request the API traces.

## Decisions

* One app registration, `cost-to-serve-demo`: SPA redirect URIs, API scope
  `access_as_user`, v2 access tokens, app roles `FinanceAnalyst` and `RegionalOps`, and user
  assignment required. Tenant-wide consent covers the studio's scope and sign-in scopes.
* The browser uses MSAL (authorization code with PKCE) and `sessionStorage`. The API
  validates issuer, audience, signature and lifetime with JWT bearer authentication.
* Roles come only from the token's `roles` claim. A user with both roles is treated as
  FinanceAnalyst, so one person cannot both approve and execute.
* RegionalOps region comes from `Entra:RegionalOpsRegions`, keyed by object ID. A RegionalOps
  user with no region gets 403 and no data. Users without a studio role also get 403.
* The caller's object ID owns conversations and confirmation cards.
* When Entra is configured, dev personas are off and the `X-Demo-Persona` header is ignored.
  Without Entra, personas work only in Development.
* `GET /api/auth-config` gives the browser the tenant ID, client ID and scope. These are not
  secrets, but they stay out of git: setup writes them to git-ignored local settings.

## Demo users

`scripts/setup-entra.sh` creates `dana.demo@<domain>` and `luis.demo@<domain>` with
generated passwords. Users must change them at first sign-in, and they are written only to
`secrets/demo-users.local.txt` (git-ignored, mode 600). The script assigns the app roles and
writes Luis's region. MFA is recommended through Security defaults or Conditional Access.
`scripts/teardown-entra.sh` deletes exactly these objects.

The user approved the setup on October 28, 2026, and the script ran. It created the app
registration, both users and both role assignments, and granted tenant-wide consent for
`openid profile offline_access` and `access_as_user`. The tenant also gave the app's creator
a default-access assignment with no studio role; that account gets "No role assigned".
First sign-in, including each user's own new password, is left to the demo owner.

## Verification

* 10 API tests use test-signed tokens: role mapping, region scope, no role, no region, both
  roles, wrong audience, wrong issuer, ignored persona header and confirmation ownership.
* In the browser, Entra mode shows the sign-in screen and rejects persona headers. Dev mode
  still works unchanged.

## Consequences

* Against the real tenant, the API rejected requests with no token, a persona header, an
  unsigned token and a valid Azure token for another audience (all 401). The browser
  reached the Microsoft sign-in page with the studio's client, scope, redirect URI and
  PKCE, with no Entra error.
* Signing in as Dana and Luis end to end is verified once each user completes the first
  sign-in and password change.
* Increment 5 must add the App Service URL as a redirect URI
  (`./scripts/setup-entra.sh --redirect-uri https://<app>.azurewebsites.net`) and copy the
  Entra settings into App Service configuration.

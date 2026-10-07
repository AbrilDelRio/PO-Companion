# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

ASP.NET Core **.NET 10** Web API that integrates with Dynamics 365/Dataverse CRM to manage session registrations and project milestones. Three projects in the solution:

- **CclWebApiCore** — Main Web API (controllers, models, entry point)
- **CclCrmProxyCore** — Class library for Dataverse/CRM integration (entities, context, helpers)
- **CclWebApiCore.Tests** — xUnit unit tests (pure logic; no DB/CRM/network)

## Build & Run Commands

```bash
# Restore and build
dotnet build Integration-Session-Api.sln

# Run locally (HTTPS on port 10000, launches Swagger)
dotnet run --project CclWebApiCore

# Publish release build
dotnet publish CclWebApiCore -c Release -o out

# Unit tests (xUnit; no external deps)
dotnet test CclWebApiCore.Tests/CclWebApiCore.Tests.csproj

# Run a single test by name
dotnet test --filter "FullyQualifiedName~CRMHelperTests"

# Docker build — NuGet.config comes from Key Vault as a build secret, so it must be
# supplied locally too (the private CCL feed is needed for Shared.HealthCheck)
docker build -t integration-session-api -f CclWebApiCore/Dockerfile \
  --secret id=nugetconfig,src=$APPDATA/NuGet/NuGet.Config .
```

Unit tests live in `CclWebApiCore.Tests/` (xUnit): `HelperTests` (JSON/config helpers, SessionTrack mapping) and `CRMHelperTests` (the `GetCrmService` null-connection-string guard). They're pure logic — no database, CRM, or network — so they run anywhere. There is **no** integration-test coverage of `SubtaskCreator`/controllers: those need a live Dataverse `ServiceClient`. Manual testing is via HTML pages in `CclWebApiCore/TestPages/` and Swagger.

Local run serves HTTPS on `https://localhost:10000` (Development profile; `UseHttpsRedirection` is commented out in `Program.cs`).

## Architecture

**Entry point:** `CclWebApiCore/Program.cs` — Minimal hosting model (top-level statements), no Startup.cs. Loads `appsettings.json` and optional `secrets/appsettings.secrets.json`. Notable wiring:
- **Serilog** is the host logger, writing to **Console** (so `kubectl logs` isn't empty) and **InsightOps** (Rapid7, EU region; token from `Logentries:Token`). The InsightOps sink **throws at startup if the token is missing or not a Guid**. `Log.CloseAndFlush()` runs in a `finally` around `app.Run()` so buffered logs flush on shutdown. A startup line (`integration-session-api starting | env | version`) marks a live pod before the first request.
- **Health checks** via `AddCclHealthChecks`/`UseCclHealthChecks` (`CenterForCreativeLeadership.Shared.HealthCheck`) → `/_liveness` and `/_health`. **No dependency probe is registered on purpose**: CRM is reached per-request via `ServiceClient`, and a CRM auth ping would false-fail readiness on transient token blips. Helm liveness/readiness probes both target `/_liveness`.
- **App Insights** registered only when `!IsDevelopment()`, reading `ApplicationInsights:ConnectionString`. That value is **auto-provisioned and injected by the deploy pipeline** (`dashboard-domain` in `buildandpush.yml`) — don't add it to Key Vault by hand.
- **UsePathBase**: in Development only, the app is served under `/integration-session` (mirrors the APIM route; the ingress strips the prefix in deployed envs).
- `AddSystemWebAdapters()` + `AddHttpForwarder()` (YARP) are wired but the catch-all forwarder is commented out.

**Request flow:** Controller → CrmWorker/Helper → CRMHelper → Dataverse SDK (`IOrganizationService`)

**Key classes:**
- `SessionController` — Main API controller, unprefixed `[Route("")]`. The local `/integration-session` prefix comes from `UsePathBase` in `Program.cs`, **not** from route attributes — don't reintroduce the old `#if DEBUG` conditional `[Route]`.
- `BaseCrmController` — Base controller providing `IOrganizationService` via `CRMHelper.GetCrmService()`.
- `CrmWorker` — Orchestrates business logic (session track processing, project milestones).
- `CRMHelper` — Static utilities for CRM operations (queries, state changes, option sets).
- `Helper` — General utilities (JSON, config access, data transformation).
- `CclServiceContext` — Auto-generated LINQ-to-CRM context.

**Factory pattern:** `MagentoUpdate` base class with `construct()` factory method → `MagentoUpdate_Transfer`, `MagentoUpdate_Pending`, `MagentoUpdate_Cancel`.

**Subtask creation:** `CreateSubtasks` has a single implementation path — PSA (Project Service Automation), using `msdyn_PssUpdateV1` operation sets to manage milestone flags and WBS ordering. The older PO / Project-for-the-Web path was removed; don't reintroduce it.

## Data Access

All persistence is through Dynamics 365 Dataverse SDK — no SQL database. Connection string is in `CRMConnection` config key.

```csharp
using (var cclContext = new CclServiceContext(service))
{
    // LINQ queries against CRM entity sets
    // AddObject / UpdateObject / SaveChanges
}
```

Records are deactivated (state change), not hard-deleted.

## Configuration & Secrets

- **Local dev:** User Secrets (ID: `df291d90-ef75-4329-8493-4f71c78c698d`) or `secrets/appsettings.secrets.json`
- **Deployed:** Azure Key Vault, pulled during CI/CD per environment (QA/UAT/PROD)
- **NuGet:** The private CCL Azure DevOps feed (needed for `Shared.HealthCheck`) comes from **Key Vault**, not a repo file. `build-push-composite-action@v4.0` fetches the `nuget-config` secret from `ccl-integration-keyvault` and passes it as the Docker build secret `nugetconfig`, which the Dockerfile mounts during `dotnet restore`. There is deliberately **no repo-local `NuGet.config`** — don't add one back.

## Key Dependencies

- **Microsoft.PowerPlatform.Dataverse.Client** — CRM connectivity
- **Serilog + Serilog.Sinks.InsightOps** — Structured logging
- **Swashbuckle** — Swagger/OpenAPI docs
- **YARP** — Reverse proxy support
- **Newtonsoft.Json** — JSON serialization
- **Microsoft.AspNetCore.SystemWebAdapters** — legacy System.Web shims during the port
- **CenterForCreativeLeadership.Shared.HealthCheck** — `/_liveness` + `/_health` (private CCL feed)
- **Microsoft.ApplicationInsights.AspNetCore 2.23.0 + .Kubernetes 8.0.1** — stay on AppInsights 2.x (3.0 throws `TypeLoadException` with the Kubernetes enricher) and do **not** add a `KubernetesClient` pin (8.0.1 already resolves a non-vulnerable version)

**Transitive-vulnerability pins** — these exist only to force non-vulnerable versions of packages pulled in transitively. Keep them when bumping; dropping any re-exposes the CVE. `dotnet list package --vulnerable --include-transitive` should stay clean.

| Pin | Where | Pulled in by |
|---|---|---|
| `log4net` 3.3.0 | CclWebApiCore | Serilog.Sinks.InsightOps → R7Insight.Core |
| `Microsoft.OpenApi` 2.10.0 | CclWebApiCore | Swashbuckle (stay on 2.x; 3.x is a breaking rewrite) |
| `System.Security.Cryptography.Xml` 10.0.9 | **CclCrmProxyCore** | Dataverse SDK |

## API Endpoints

Paths below are the caller-facing (APIM) and local-dev shape. **In the container the routes are unprefixed** (`/GetContactInfo/{id}` etc.) — the `/integration-session` segment comes from `UsePathBase` locally and from APIM/ingress when deployed. Health endpoints are always at the root: `/_liveness`, `/_health`.

- `GET /integration-session/GetContactInfo/{contactId}` — Retrieve CRM contact
- `POST /integration-session/UploadSessionInfo` — Upload JSON session info
- `POST /integration-session/CreateSubtasks` — Create subtasks from parent task
- `POST /integration-session/CreateProjectMilestone` — Create project milestones

## Deployment

Docker multi-stage build → GitHub Actions CI/CD → Azure Container Registry → AKS (Helm charts in `CclWebApiCore/Helm/{qa|uat|prod}/`).

- **development** branch → **Tests** → builds to non-prod ACR → deploys to QA
- **master/main** branch → builds to prod ACR → deploys to UAT → PROD (with approvals)

The `Tests` job (`dotnet-unit-test-composite-action@v2.1`) gates `Build-and-Push-NP`, so a failing unit test blocks the QA deploy. Composite actions: unit-test `@v2.1`, build-push `@v4.0`, deploy-k8s-api `@v5.8` with `chart-version: 0.5.0`.

Chart 0.5.0 requires an `env:` block with `normal: []` / `secret: []` in **every** `values.yaml` — without it the deploy fails with a nil-pointer on `.Values.env.secret`.

## Conventions

- Nullable reference types enabled, implicit usings enabled
- Private fields prefixed with underscore (`_config`, `_logger`)
- Transaction IDs (8-char GUID substring) logged for request tracing
- Validation errors collected in `UploadResult.ProcessingErrors` list
- Custom `ValidationException` for business rule violations
- CclCrmProxyCore is strong-named (SagKey.snk)

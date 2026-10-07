# CopyProjectTemplate API integration

## Endpoint

`POST /CopyProjectTemplate`

This endpoint executes the copy synchronously. It returns `200 OK` after the copy finishes,
`500 Internal Server Error` when the copy fails, or `503 Service Unavailable` when the API
cannot connect to Dataverse. This integration does not use Service Bus, a queue, a background
worker, or an asynchronous `202 Accepted` response.

## Minimal request

```json
{
  "targetProjectId": "00000000-0000-0000-0000-000000000000"
}
```

The endpoint reads `mfd_poconfiguration` from the target project and resolves its
`mfd_code`. When no configuration is assigned, it preserves the plug-in's original
configuration code default: `1000`.

## Optional explicit configuration and impersonation

```json
{
  "targetProjectId": "00000000-0000-0000-0000-000000000000",
  "targetLogicalName": "msdyn_project",
  "configurationCode": "1000",
  "userId": "00000000-0000-0000-0000-000000000000",
  "organizationName": null
}
```

`userId` must be the Dataverse `systemuserid` of the user who triggered the copy. When it is
provided, the existing API `ServiceClient` sets `CallerId` so creates and updates execute on
behalf of that user. The application user must have Dataverse's **Act on Behalf of Another User**
privilege, and both users must have the privileges required by the copy operation.

When `userId` is omitted, all Dataverse operations execute as the API application user.

## Dataverse execution status

The transaction id returned in the API response is also persisted in Juan's custom table:
`mfd_pocopyprojectlog`.

The API writes these records through the existing `Helper.createLog` method:

- `CopyProjectTemplate started. TransactionId: XXXXXXXX`
- `CopyProjectTemplate completed. TransactionId: XXXXXXXX`
- `CopyProjectTemplate failed. TransactionId: XXXXXXXX. Error: ...`

The original copy implementation continues writing its own detailed step records to the same
table.

The controller additionally emits start / completed / failed entries through `ILogger`, which
Serilog routes to Rapid7 and App Insights, tagged with the same transaction id. The Dataverse
log table is unreachable exactly when Dataverse is the thing failing, so these are the only
records that survive a connectivity outage.

## Operational limits

Read this before pointing a caller at the endpoint.

- **The request blocks for the whole copy.** A template with thousands of tasks takes minutes.
  The nginx ingress has no `proxy-read-timeout` annotation, so it applies its 60 s default and
  returns `504` to the caller while the copy keeps running server-side. Raise
  `nginx.ingress.kubernetes.io/proxy-read-timeout` (and `proxy-send-timeout`) in the Helm
  `values.yaml`, and the matching APIM backend timeout, before this is usable for large templates.
- **The copy is not transactional and not idempotent.** Records are committed in
  `ExecuteMultipleRequest` batches of 250. A failure — or a pod eviction, an HPA scale-in, or a
  rollout during the copy — leaves the target project partially populated with no rollback, and
  re-POSTing the same `targetProjectId` copies everything a second time on top of what is already
  there. Reconcile against `mfd_pocopyprojectlog` before retrying.
- **Concurrency.** Two copies of the *same* target project running at once will interleave and
  corrupt each other. Serialize per project on the caller's side.
- **Memory.** The whole task list, the label index and the parent/child index are held in memory
  for the duration. The pod is capped at 640 Mi; very large templates can OOMKill it.
- **Impersonation is unauthenticated at the app layer.** `userId` is accepted from the request
  body and no authorization runs in the API (`app.UseAuthorization()` is commented out in
  `Program.cs`). Anyone who can reach the endpoint chooses which Dataverse user the copy executes
  as. The only controls are the APIM subscription key and the ingress IP allow-list.

## Integration boundaries

- The copied plug-in source is hosted under `Services/CopyProjectTemplate/Plugin`.
- `PluginExecutionServiceProvider` recreates the Dataverse services expected by
  `IPlugin.Execute(IServiceProvider)`.
- The two unused `.NET Framework` remoting imports were removed from the copied
  `ProjectClass.cs`; the compatibility marker file is no longer needed.
- Service Bus and Helm/Key Vault queue configuration are not applicable because this endpoint
  is synchronous and has no queue worker.
- No new configuration key, package reference or Helm value is required to deploy it as-is.

using System.Net;
using DC.CopyProyectFromTemplate.Models;
using DC.CopyProyectFromTemplate.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace DC.CopyProyectFromTemplate.Functions;

public sealed class TestConnectionFunction
{
    private readonly DataverseConnectionFactory connectionFactory;
    private readonly DataverseTokenProvider tokenProvider;
    private readonly DataverseTenantDiscovery tenantDiscovery;
    private readonly MppBlobStorage blobStorage;
    private readonly ILogger<TestConnectionFunction> logger;

    public TestConnectionFunction(
        DataverseConnectionFactory connectionFactory,
        DataverseTokenProvider tokenProvider,
        DataverseTenantDiscovery tenantDiscovery,
        MppBlobStorage blobStorage,
        ILogger<TestConnectionFunction> logger)
    {
        this.connectionFactory = connectionFactory;
        this.tokenProvider = tokenProvider;
        this.tenantDiscovery = tenantDiscovery;
        this.blobStorage = blobStorage;
        this.logger = logger;
    }

    [Function("DC_TestConnection")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(
            AuthorizationLevel.Function,
            "get",
            Route = "TestConnection")]
        HttpRequestData request)
    {
        CancellationToken cancellationToken = request.FunctionContext.CancellationToken;
        List<ConnectionCheck> checks = new()
        {
            new ConnectionCheck(
                "Azure Function",
                true,
                "The HTTP trigger is running and accepted the Function Key.")
        };

        // Optional: ?environmentUrl=...&environmentApiUrl=...&cloud=... checks one specific
        // environment instead of the legacy configured one.
        DataverseEnvironmentTarget environmentTarget = ReadEnvironmentFromQuery(request.Url.Query);

        ResolvedDataverseEnvironment? environment = null;

        if (!DataverseEnvironmentResolver.TryResolve(environmentTarget, out environment, out string? environmentError))
        {
            HttpResponseData badRequest = request.CreateResponse(HttpStatusCode.BadRequest);
            badRequest.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            await badRequest.WriteStringAsync(environmentError!);
            return badRequest;
        }

        DataverseCredentialPlan? plan = await TestDataverseAsync(checks, environment, cancellationToken);
        object? tenantDiscoveryReport = await DescribeTenantDiscoveryAsync(environment, plan, cancellationToken);
        await TestBlobStorageAsync(checks, cancellationToken);

        bool success = checks.All(check => check.Success);

        HttpResponseData response = request.CreateResponse(
            success
                ? HttpStatusCode.OK
                : HttpStatusCode.InternalServerError);

        await response.WriteAsJsonAsync(
            new
            {
                success,
                message = success
                    ? "The Azure Function, Dataverse connection, and Blob Storage connection are working."
                    : "The Azure Function is reachable, but one or more required connections failed.",
                environment = environment?.EnvironmentUrl,
                cloud = environment?.CloudName ?? "legacy",
                authority = environment?.Authority,
                credentialPath = environment == null ? "legacy" : plan?.PathName ?? "unresolved",
                tenantId = plan?.TenantId,
                tenantSource = plan?.TenantSource,
                clientId = plan?.ClientId,
                signInAuthority = plan?.Authority,
                tokenExchangeAudience = plan?.TokenExchangeAudience,
                hosting = tokenProvider.DescribeHosting(),
                tenantDiscovery = tenantDiscoveryReport,
                timestampUtc = DateTimeOffset.UtcNow,
                checks
            });

        return response;
    }

    private async Task<DataverseCredentialPlan?> TestDataverseAsync(
        List<ConnectionCheck> checks,
        ResolvedDataverseEnvironment? environment,
        CancellationToken cancellationToken)
    {
        DataverseCredentialPlan? plan = null;

        try
        {
            if (environment != null)
            {
                plan = await tokenProvider.ResolveAsync(environment, cancellationToken);
            }

            using var serviceClient = connectionFactory.CreateClient(environment);

            WhoAmIResponse whoAmI = (WhoAmIResponse)serviceClient.Execute(
                new WhoAmIRequest());

            string credentialPath = plan == null
                ? string.Empty
                : $" Credential path: {plan.PathName}; tenant {plan.TenantId} ({plan.TenantSource}).";

            checks.Add(new ConnectionCheck(
                "Dataverse",
                true,
                $"Connected successfully to '{connectionFactory.ResolveUrl(environment)}'. " +
                $"Organization ID: {whoAmI.OrganizationId}; Application User ID: {whoAmI.UserId}.{credentialPath}"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The TestConnection Dataverse check failed. Cause: {Cause}", ActivityDiagnostics.Flatten(ex));
            checks.Add(new ConnectionCheck(
                "Dataverse",
                false,
                ActivityDiagnostics.Flatten(ex)));
        }

        return plan;
    }

    /// <summary>
    /// Informational: runs the tenant discovery for any declared environment, whatever its
    /// credential path, so the discovery can be verified against a real environment before any
    /// secret is removed. It never changes the overall success of the check.
    /// </summary>
    private async Task<object?> DescribeTenantDiscoveryAsync(
        ResolvedDataverseEnvironment? environment,
        DataverseCredentialPlan? plan,
        CancellationToken cancellationToken)
    {
        if (environment == null)
        {
            return null;
        }

        try
        {
            DiscoveredTenant tenant = await tenantDiscovery.DiscoverAsync(environment, cancellationToken);

            return new
            {
                success = true,
                tenantId = tenant.TenantId,
                authorityHost = tenant.AuthorityHost,
                resourceId = tenant.ResourceId,
                matchesCredentialTenant = plan == null
                    ? (bool?)null
                    : string.Equals(plan.TenantId, tenant.TenantId, StringComparison.OrdinalIgnoreCase)
            };
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "The TestConnection tenant discovery failed for {Host}. Cause: {Cause}",
                environment.Host,
                ActivityDiagnostics.Flatten(ex));

            return new
            {
                success = false,
                message = ActivityDiagnostics.Flatten(ex)
            };
        }
    }

    private async Task TestBlobStorageAsync(
        List<ConnectionCheck> checks,
        CancellationToken cancellationToken)
    {
        try
        {
            await blobStorage.TestConnectionAsync(cancellationToken);

            checks.Add(new ConnectionCheck(
                "Blob Storage",
                true,
                $"Connected successfully to the '{blobStorage.ContainerName}' container."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The TestConnection Blob Storage check failed.");
            checks.Add(new ConnectionCheck(
                "Blob Storage",
                false,
                $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private static DataverseEnvironmentTarget ReadEnvironmentFromQuery(string query)
    {
        Dictionary<string, StringValues> parsed = QueryHelpers.ParseQuery(query);

        return new DataverseEnvironmentTarget
        {
            EnvironmentUrl = Read(parsed, "environmentUrl"),
            EnvironmentApiUrl = Read(parsed, "environmentApiUrl"),
            Cloud = Read(parsed, "cloud")
        };
    }

    private static string Read(Dictionary<string, StringValues> parsed, string name)
    {
        return parsed.TryGetValue(name, out StringValues value)
            ? value.FirstOrDefault()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private sealed record ConnectionCheck(
        string Name,
        bool Success,
        string Message);
}

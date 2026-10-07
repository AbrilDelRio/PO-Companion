using DC.CopyProyectFromTemplate.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace DC.CopyProyectFromTemplate.Services;

public sealed class DataverseConnectionFactory
{
    private readonly string? legacyConnectionString;
    private readonly string? legacyDataverseUrl;
    private readonly DataverseTokenProvider tokenProvider;
    private readonly ILogger<DataverseConnectionFactory> logger;

    public DataverseConnectionFactory(
        IConfiguration configuration,
        DataverseTokenProvider tokenProvider,
        ILogger<DataverseConnectionFactory> logger)
    {
        // Both settings stay OPTIONAL: an app that only serves callers that declare their
        // environment does not need the fixed legacy environment configured at all. The
        // legacy path only fails when it is actually taken.
        legacyDataverseUrl = configuration["DataverseUrl"]?.Trim().TrimEnd('/');
        legacyConnectionString = configuration["DataverseConnectionString"]?.Trim();

        if (string.IsNullOrWhiteSpace(legacyDataverseUrl))
        {
            legacyDataverseUrl = null;
        }

        if (string.IsNullOrWhiteSpace(legacyConnectionString))
        {
            legacyConnectionString = null;
        }

        this.tokenProvider = tokenProvider;
        this.logger = logger;
    }

    /// <summary>URL of the legacy (fixed) environment. Throws when it is not configured.</summary>
    public string DataverseUrl =>
        legacyDataverseUrl
        ?? throw new InvalidOperationException(
            "The required application setting 'DataverseUrl' is missing or empty, and the request " +
            "did not declare an environment (environmentUrl / environmentApiUrl / cloud).");

    /// <summary>Legacy path: the fixed environment from configuration.</summary>
    public ServiceClient CreateClient() => CreateClient((ResolvedDataverseEnvironment?)null);

    public ServiceClient CreateClient(DataverseEnvironmentTarget? target) =>
        CreateClient(Resolve(target));

    public ServiceClient CreateClient(ResolvedDataverseEnvironment? environment)
    {
        if (environment == null)
        {
            logger.LogInformation(
                "No environment was declared by the caller: taking the legacy route against the " +
                "configured environment {DataverseUrl}.",
                legacyDataverseUrl);

            return CreateLegacyClient();
        }

        DataverseCredentialPlan plan;

        try
        {
            // The isolated worker has no SynchronizationContext, and the ServiceClient constructor
            // blocks on the token function anyway.
            plan = tokenProvider.ResolveAsync(environment, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Dataverse credentials for {Host} ({Cloud}) could not be resolved. CorrelationId: {CorrelationId}. " +
                "Cause: {Cause}",
                environment.Host,
                environment.CloudName,
                environment.CorrelationId,
                ActivityDiagnostics.Flatten(ex));

            throw new InvalidOperationException(
                $"Dataverse credentials for '{environment.Host}' ({environment.CloudName}) could not be resolved. " +
                $"Cause: {ActivityDiagnostics.Flatten(ex)}",
                ex);
        }

        // One line per connection, so every publish states which path and which tenant it used.
        logger.LogInformation(
            "Connecting to Dataverse environment {Host} ({Cloud}). Credential path: {CredentialPath}. " +
            "Tenant: {TenantId} ({TenantSource}). Authority: {Authority}. ClientId: {ClientId}. " +
            "CorrelationId: {CorrelationId}.",
            environment.Host,
            environment.CloudName,
            plan.PathName,
            plan.TenantId,
            plan.TenantSource,
            plan.Authority,
            plan.ClientId,
            environment.CorrelationId);

        ServiceClient client;

        try
        {
            client = new ServiceClient(
                new Uri(environment.EnvironmentUrl),
                _ => tokenProvider.GetAccessTokenAsync(plan),
                useUniqueInstance: true,
                logger: null);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "The Dataverse client for {Host} ({Cloud}) could not be built via the {CredentialPath} path. " +
                "CorrelationId: {CorrelationId}. Cause: {Cause}",
                environment.Host,
                environment.CloudName,
                plan.PathName,
                environment.CorrelationId,
                ActivityDiagnostics.Flatten(ex));

            // The constructor throws DataverseConnectionException("Failed to connect to Dataverse"),
            // whose message says nothing: the reason (AADSTS code, 401, missing application user)
            // only exists in the inner chain, so it is flattened into the message here.
            throw new InvalidOperationException(
                $"Dataverse connection to '{environment.EnvironmentUrl}' ({environment.CloudName}) failed " +
                $"while building the client via the {plan.PathName} path. Authority: {plan.Authority}. " +
                $"Cause: {ActivityDiagnostics.Flatten(ex)}",
                ex);
        }

        return EnsureReady(client, environment.EnvironmentUrl);
    }

    /// <summary>URL to use for record links and logs, per request.</summary>
    public string ResolveUrl(DataverseEnvironmentTarget? target) =>
        Resolve(target)?.EnvironmentUrl ?? DataverseUrl;

    public string ResolveUrl(ResolvedDataverseEnvironment? environment) =>
        environment?.EnvironmentUrl ?? DataverseUrl;

    /// <summary>
    /// Re-resolves the DTO that travelled through Durable. The HTTP layer already validated it,
    /// so a failure here is a bug, not caller input.
    /// </summary>
    public static ResolvedDataverseEnvironment? Resolve(DataverseEnvironmentTarget? target)
    {
        if (!DataverseEnvironmentResolver.TryResolve(target, out ResolvedDataverseEnvironment? resolved, out string? error))
        {
            throw new InvalidOperationException($"Invalid Dataverse environment in the orchestration input: {error}");
        }

        return resolved;
    }

    private ServiceClient CreateLegacyClient()
    {
        if (legacyConnectionString == null)
        {
            throw new InvalidOperationException(
                "The required application setting 'DataverseConnectionString' is missing or empty, and the " +
                "request did not declare an environment (environmentUrl / environmentApiUrl / cloud).");
        }

        return EnsureReady(new ServiceClient(legacyConnectionString), legacyDataverseUrl ?? "(legacy)");
    }

    private static ServiceClient EnsureReady(ServiceClient client, string environmentUrl)
    {
        if (!client.IsReady)
        {
            // LastError alone is often empty; LastException carries the reason (expired secret,
            // application user missing from the environment, 401 from Dataverse).
            string lastError = client.LastError ?? string.Empty;
            Exception? lastException = client.LastException;

            string details = string.IsNullOrWhiteSpace(lastError)
                ? lastException?.Message ?? "Unknown Dataverse connection error."
                : lastError;

            client.Dispose();

            throw new InvalidOperationException(
                $"Dataverse connection to '{environmentUrl}' failed: {details}",
                lastException);
        }

        return client;
    }
}

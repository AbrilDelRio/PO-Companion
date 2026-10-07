using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace DC.CopyProyectFromTemplate.Services;

/// <summary>Tenant and sign-in authority of a Dataverse environment, as its 401 challenge states them.</summary>
public sealed record DiscoveredTenant(string AuthorityHost, string TenantId, string? ResourceId)
{
    public string Authority => $"{AuthorityHost}{TenantId}";
}

/// <summary>
/// Discovers the tenant of a Dataverse environment instead of configuring it. An anonymous GET to
/// the Web API answers 401 with the tenant's sign-in URI, for example:
///
///   Bearer authorization_uri=https://login.microsoftonline.com/{tenant}/oauth2/authorize,
///          resource_id=https://org.api.crm.dynamics.com/
///
/// The authority is checked against the cloud table before it is trusted, and the result is
/// cached per host for the life of the process. Safe by construction: the host was already
/// validated against the Dataverse suffixes, so a real Microsoft endpoint answers, and even a
/// wrong tenant grants nothing without an application user there.
/// </summary>
public sealed class DataverseTenantDiscovery
{
    private static readonly HttpClient Http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly ConcurrentDictionary<string, DiscoveredTenant> cache =
        new ConcurrentDictionary<string, DiscoveredTenant>(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<DataverseTenantDiscovery> logger;

    public DataverseTenantDiscovery(ILogger<DataverseTenantDiscovery> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// InvalidDataException: the environment answered, but not with a usable challenge, or its
    /// authority does not belong to the declared cloud (a 400 at the HTTP layer).
    /// InvalidOperationException: the request itself did not complete (logged in full).
    /// </summary>
    public async Task<DiscoveredTenant> DiscoverAsync(ResolvedDataverseEnvironment environment, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(environment.Host, out DiscoveredTenant? cached))
        {
            return cached;
        }

        string probeUrl = $"{environment.EnvironmentApiUrl}/api/data/v9.2/";
        HttpStatusCode status;
        string? challenge;

        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
            using HttpResponseMessage response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            status = response.StatusCode;
            challenge = response.Headers.NonValidated.TryGetValues("WWW-Authenticate", out HeaderStringValues values)
                ? string.Join(", ", values)
                : null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Tenant discovery for {Host} ({Cloud}) failed: the anonymous request to {ProbeUrl} did not " +
                "complete. CorrelationId: {CorrelationId}. Cause: {Cause}",
                environment.Host,
                environment.CloudName,
                probeUrl,
                environment.CorrelationId,
                ActivityDiagnostics.Flatten(ex));

            throw new InvalidOperationException(
                $"Tenant discovery for '{environment.Host}' failed: the request to {probeUrl} did not complete. " +
                $"Cause: {ActivityDiagnostics.Flatten(ex)}",
                ex);
        }

        if (status != HttpStatusCode.Unauthorized)
        {
            throw Reject(
                $"Tenant discovery for '{environment.Host}' expected HTTP 401 with a WWW-Authenticate challenge " +
                $"from {probeUrl}, but got HTTP {(int)status}.");
        }

        if (!TryParseChallenge(challenge, out DiscoveredTenant? tenant, out string? parseError))
        {
            throw Reject($"Tenant discovery for '{environment.Host}' could not read the challenge from {probeUrl}: {parseError}");
        }

        if (!TryValidateCloud(tenant!, environment.Cloud, out string? cloudError))
        {
            throw Reject($"Tenant discovery for '{environment.Host}': {cloudError}");
        }

        cache[environment.Host] = tenant!;

        logger.LogInformation(
            "Tenant discovered for {Host} ({Cloud}): {TenantId} at {AuthorityHost}. CorrelationId: {CorrelationId}.",
            environment.Host,
            environment.CloudName,
            tenant!.TenantId,
            tenant.AuthorityHost,
            environment.CorrelationId);

        return tenant;

        InvalidDataException Reject(string message)
        {
            logger.LogWarning(
                "{Message} Raw challenge: {Challenge}. CorrelationId: {CorrelationId}.",
                message,
                challenge ?? "(none)",
                environment.CorrelationId);

            return new InvalidDataException(message);
        }
    }

    /// <summary>
    /// Reads the tenant and the sign-in authority from a WWW-Authenticate challenge. Accepts quoted
    /// and unquoted parameter values; requires an https authorization_uri whose first path segment
    /// is a tenant GUID.
    /// </summary>
    public static bool TryParseChallenge(string? challenge, out DiscoveredTenant? tenant, out string? error)
    {
        tenant = null;
        error = null;

        if (string.IsNullOrWhiteSpace(challenge))
        {
            error = "the response carries no WWW-Authenticate header.";
            return false;
        }

        string text = challenge.Trim();

        if (text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring("Bearer ".Length);
        }

        Dictionary<string, string> parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string part in text.Split(','))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = part.Substring(0, separator).Trim();
            string value = part.Substring(separator + 1).Trim().Trim('"');

            parameters.TryAdd(key, value);
        }

        if (!parameters.TryGetValue("authorization_uri", out string? rawUri) &&
            !parameters.TryGetValue("authorization", out rawUri))
        {
            error = $"the WWW-Authenticate header has no authorization_uri: '{challenge}'.";
            return false;
        }

        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"authorization_uri '{rawUri}' is not an absolute https URL.";
            return false;
        }

        string? firstSegment = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (!Guid.TryParse(firstSegment, out Guid tenantId) || tenantId == Guid.Empty)
        {
            error = $"authorization_uri '{rawUri}' does not carry a tenant ID.";
            return false;
        }

        parameters.TryGetValue("resource_id", out string? resourceId);

        tenant = new DiscoveredTenant(
            $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}/",
            tenantId.ToString("D"),
            string.IsNullOrWhiteSpace(resourceId) ? null : resourceId);

        return true;
    }

    /// <summary>The discovered sign-in authority must be the declared cloud's, per the cloud table.</summary>
    public static bool TryValidateCloud(DiscoveredTenant tenant, DataverseCloud cloud, out string? error)
    {
        string expected = DataverseClouds.GetAuthority(cloud);

        if (string.Equals(tenant.AuthorityHost.TrimEnd('/'), expected.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            error = null;
            return true;
        }

        error =
            $"the environment signs in at '{tenant.AuthorityHost}', but the declared cloud " +
            $"{DataverseClouds.GetName(cloud)} signs in at '{expected}'. The environment and the cloud field " +
            "disagree; neither is assumed.";
        return false;
    }
}

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DC.CopyProyectFromTemplate.Services;

/// <summary>The caller's Function key does not belong to the tenant of the environment it named (an HTTP 403).</summary>
public sealed class TenantAccessDeniedException : Exception
{
    public TenantAccessDeniedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Ties each Function key to the tenant of the customer it was given to, so one shared Function App
/// serves several customers without one of them reaching another's environments.
///
///   TenantKeys:{tenantId}   the Function key(s) of that tenant, comma separated (two during a rotation)
///
/// On App Service the separator is "__" and a setting name cannot contain "-", so the tenant id is
/// written without its dashes: TenantKeys__ccfeb871538f4f5fb743877270df42e1. Any GUID format is accepted.
/// Each key must ALSO exist as a Function App key (App keys → Host keys): the host still checks it
/// first, and this guard then checks that the environment the request names lives in that key's tenant.
///
/// Off while no TenantKeys entry exists, so nothing changes until the first one is added. Once on, a
/// request that names no environment (the legacy route) is refused, because its tenant cannot be checked.
/// </summary>
public sealed class TenantAccessGuard
{
    public const string SectionName = "TenantKeys";

    private const string KeyHeaderName = "x-functions-key";
    private const string KeyQueryName = "code";

    private readonly IConfiguration configuration;
    private readonly DataverseTenantDiscovery tenantDiscovery;
    private readonly ILogger<TenantAccessGuard> logger;

    public TenantAccessGuard(
        IConfiguration configuration,
        DataverseTenantDiscovery tenantDiscovery,
        ILogger<TenantAccessGuard> logger)
    {
        this.configuration = configuration;
        this.tenantDiscovery = tenantDiscovery;
        this.logger = logger;
    }

    public bool IsEnabled => ReadTenantKeys().Count > 0;

    /// <summary>
    /// Returns when the request may act on <paramref name="environment"/>; throws
    /// <see cref="TenantAccessDeniedException"/> otherwise. A no-op while no TenantKeys entry exists.
    /// </summary>
    public async Task EnsureAllowedAsync(
        HttpRequestData request,
        ResolvedDataverseEnvironment? environment,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string[]> tenantKeys = ReadTenantKeys();

        if (tenantKeys.Count == 0)
        {
            return;
        }

        if (environment == null)
        {
            logger.LogWarning("Request refused: {Section} is configured and the request named no environment.", SectionName);

            throw new TenantAccessDeniedException(
                "This Function requires the environment of the request: send environmentUrl, environmentApiUrl and cloud.");
        }

        string? presentedKey = ReadPresentedKey(request);

        if (presentedKey == null)
        {
            throw new TenantAccessDeniedException("The request carries no Function key, or two different ones.");
        }

        DiscoveredTenant tenant;

        try
        {
            tenant = await tenantDiscovery.DiscoverAsync(environment, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // Fail closed: when the tenant cannot be read, the key cannot be checked.
            throw new TenantAccessDeniedException(
                $"The tenant of '{environment.Host}' could not be verified, so the request was refused. {ex.Message}");
        }

        if (!tenantKeys.TryGetValue(tenant.TenantId, out string[]? allowedKeys) ||
            !allowedKeys.Any(allowed => FixedTimeEquals(allowed, presentedKey)))
        {
            logger.LogWarning(
                "Request refused: the Function key is not one of tenant {TenantId}, the tenant of {Host}. CorrelationId: {CorrelationId}.",
                tenant.TenantId,
                environment.Host,
                environment.CorrelationId);

            throw new TenantAccessDeniedException(
                $"This Function key is not authorized for the environment '{environment.Host}'.");
        }
    }

    /// <summary>
    /// The key the host accepted: the x-functions-key header or the ?code= query value. When both are
    /// present and differ, which one the host used is not known here, so the request is not trusted.
    /// </summary>
    private static string? ReadPresentedKey(HttpRequestData request)
    {
        string? headerKey = request.Headers.TryGetValues(KeyHeaderName, out IEnumerable<string>? values)
            ? values.FirstOrDefault()?.Trim()
            : null;

        string? queryKey = QueryHelpers.ParseQuery(request.Url.Query).TryGetValue(KeyQueryName, out var queryValues)
            ? queryValues.FirstOrDefault()?.Trim()
            : null;

        if (string.IsNullOrEmpty(headerKey))
        {
            return string.IsNullOrEmpty(queryKey) ? null : queryKey;
        }

        if (string.IsNullOrEmpty(queryKey) || string.Equals(headerKey, queryKey, StringComparison.Ordinal))
        {
            return headerKey;
        }

        return null;
    }

    /// <summary>Tenant id (lowercase) → its keys. Read on every call so a changed setting needs no restart logic.</summary>
    private Dictionary<string, string[]> ReadTenantKeys()
    {
        Dictionary<string, string[]> result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (IConfigurationSection section in configuration.GetSection(SectionName).GetChildren())
        {
            if (!Guid.TryParse(section.Key, out Guid tenantId) || string.IsNullOrWhiteSpace(section.Value))
            {
                continue;
            }

            string[] keys = section.Value
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (keys.Length > 0)
            {
                result[tenantId.ToString("D")] = keys;
            }
        }

        return result;
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        byte[] actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(actual));

        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }
}

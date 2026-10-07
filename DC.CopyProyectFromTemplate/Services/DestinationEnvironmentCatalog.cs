using System.Text.Json;
using DC.CopyProyectFromTemplate.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DC.CopyProyectFromTemplate.Services;

/// <summary>An environment a project can be copied to.</summary>
public sealed record DestinationEnvironment(string Name, string Url);

/// <summary>The destinations on offer right now, where they came from, and whether anything else is refused.</summary>
public sealed record CatalogView(IReadOnlyList<DestinationEnvironment> Environments, bool IsEnforced, string Mode);

/// <summary>
/// The environments offered as copy destinations, and, when configured, the only ones accepted.
///
///   DestinationEnvironments   JSON array, e.g. [{"name":"UAT","url":"https://uat.crm.dynamics.com"}]
///
/// When the setting is present it is an ALLOW LIST: a copy to any other host is rejected with a
/// 400, so a caller cannot aim the Function's application users at an arbitrary environment.
/// When it is absent the list falls back to the hosts that have a block under DataverseEnvironments
/// (listing only; nothing is enforced, as environments served by the federated path need no block).
///
/// Whatever the source of the list, a request that names its environment only sees, and can only copy
/// to, the environments of that environment's own tenant (read from each environment's 401 challenge
/// and cached). One setting can therefore hold the environments of every customer.
/// </summary>
public sealed class DestinationEnvironmentCatalog
{
    public const string SettingName = "DestinationEnvironments";

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Lazy<Catalog> catalog;
    private readonly PowerPlatformEnvironmentDiscovery? discovery;
    private readonly DataverseTenantDiscovery? tenantDiscovery;
    private readonly ILogger<DestinationEnvironmentCatalog>? logger;

    /// <summary>
    /// Nothing is read or parsed here on purpose: this class is injected into the copy endpoint, so a
    /// malformed DestinationEnvironments setting must only fail the requests that USE the catalog
    /// (GET Environments, copy to another environment), never the existing same-environment copy.
    /// </summary>
    public DestinationEnvironmentCatalog(
        IConfiguration configuration,
        PowerPlatformEnvironmentDiscovery? discovery = null,
        DataverseTenantDiscovery? tenantDiscovery = null,
        ILogger<DestinationEnvironmentCatalog>? logger = null)
    {
        catalog = new Lazy<Catalog>(() => Load(configuration));
        this.discovery = discovery;
        this.tenantDiscovery = tenantDiscovery;
        this.logger = logger;
    }

    /// <summary>
    /// The destinations to offer for a request that comes from <paramref name="source"/>. With automatic
    /// discovery on (EnvironmentDiscovery = PowerPlatformApi) they are the environments of the tenant, read
    /// with the Function's own credentials and cached; the DestinationEnvironments setting is then not used.
    /// Otherwise they come from DestinationEnvironments or, without it, from the configured hosts.
    /// </summary>
    public async Task<CatalogView> GetAsync(ResolvedDataverseEnvironment? source, bool refresh, CancellationToken cancellationToken)
    {
        if (discovery != null && discovery.IsEnabled)
        {
            if (source == null)
            {
                throw new EnvironmentDiscoveryException(
                    "Automatic discovery needs to know which environment the request comes from: send " +
                    "environmentUrl, environmentApiUrl and cloud (the control sends them since version 1.11.0).");
            }

            IReadOnlyList<DestinationEnvironment> found = await discovery.DiscoverAsync(source, refresh, cancellationToken);

            return new CatalogView(found, true, "discovery");
        }

        IReadOnlyList<DestinationEnvironment> environments = Environments;

        if (source != null && tenantDiscovery != null)
        {
            environments = await KeepSameTenantAsync(source, environments, cancellationToken);
        }

        return new CatalogView(environments, IsEnforced, IsEnforced ? "setting" : "registry");
    }

    /// <summary>
    /// Same rule as <see cref="IsAllowed"/>, but with the list that applies to this request. A destination in
    /// another tenant than the source is refused even when no list is enforced.
    /// </summary>
    public async Task<bool> IsAllowedAsync(ResolvedDataverseEnvironment? source, string host, CancellationToken cancellationToken)
    {
        CatalogView view = await GetAsync(source, false, cancellationToken);

        bool listed = !view.IsEnforced || view.Environments.Any(environment =>
            string.Equals(HostOf(environment.Url), host, StringComparison.OrdinalIgnoreCase));

        // An enforced list is already reduced to the source's tenant (the discovered one is that tenant's own).
        if (!listed || view.IsEnforced || source == null || tenantDiscovery == null)
        {
            return listed;
        }

        string sourceTenant = await TenantOfSourceAsync(source, cancellationToken);
        string? destinationTenant = await TryTenantOfAsync($"https://{host}", source.CorrelationId, cancellationToken);

        return string.Equals(sourceTenant, destinationTenant, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The entries whose tenant is the source's. An entry whose tenant cannot be read is left out (fail
    /// closed); the source's own tenant must be readable, or the whole request fails.
    /// </summary>
    private async Task<IReadOnlyList<DestinationEnvironment>> KeepSameTenantAsync(
        ResolvedDataverseEnvironment source,
        IReadOnlyList<DestinationEnvironment> environments,
        CancellationToken cancellationToken)
    {
        string sourceTenant = await TenantOfSourceAsync(source, cancellationToken);

        (DestinationEnvironment Environment, string? TenantId)[] tenants = await Task.WhenAll(environments.Select(async environment =>
            (environment, await TryTenantOfAsync(environment.Url, source.CorrelationId, cancellationToken))));

        List<DestinationEnvironment> kept = tenants
            .Where(entry => string.Equals(entry.TenantId, sourceTenant, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Environment)
            .ToList();

        logger?.LogInformation(
            "Destination environments for {Host} (tenant {TenantId}): {Kept} of {Total} in the same tenant.",
            source.Host,
            sourceTenant,
            kept.Count,
            environments.Count);

        return kept;
    }

    private async Task<string> TenantOfSourceAsync(ResolvedDataverseEnvironment source, CancellationToken cancellationToken)
    {
        try
        {
            return (await tenantDiscovery!.DiscoverAsync(source, cancellationToken)).TenantId;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            throw new EnvironmentDiscoveryException(
                $"The tenant of the source environment '{source.Host}' could not be read, so no destination can be offered. {ex.Message}",
                ex);
        }
    }

    private async Task<string?> TryTenantOfAsync(string url, string correlationId, CancellationToken cancellationToken)
    {
        ResolvedDataverseEnvironment? environment = ToResolved(url, correlationId);

        if (environment == null)
        {
            logger?.LogWarning("Destination '{Url}' is not a Dataverse environment URL; it is not offered.", url);
            return null;
        }

        try
        {
            return (await tenantDiscovery!.DiscoverAsync(environment, cancellationToken)).TenantId;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            logger?.LogWarning(
                "The tenant of destination {Host} could not be read; it is not offered. Cause: {Cause}",
                environment.Host,
                ex.Message);

            return null;
        }
    }

    /// <summary>Builds the environment from its URL alone: the API host and the cloud follow from the host.</summary>
    private static ResolvedDataverseEnvironment? ToResolved(string url, string correlationId)
    {
        string? host = HostOf(url);

        if (host == null || !DataverseClouds.TryResolveFromHost(host, out DataverseCloud cloud, out _))
        {
            return null;
        }

        string[] labels = host.Split('.');
        string apiHost = labels.Length > 1 && labels[1] == "api"
            ? host
            : string.Join('.', new[] { labels[0], "api" }.Concat(labels.Skip(1)));
        string environmentHost = labels.Length > 1 && labels[1] == "api"
            ? string.Join('.', new[] { labels[0] }.Concat(labels.Skip(2)))
            : host;

        DataverseEnvironmentTarget target = new DataverseEnvironmentTarget
        {
            EnvironmentUrl = $"https://{environmentHost}",
            EnvironmentApiUrl = $"https://{apiHost}",
            Cloud = DataverseClouds.GetName(cloud),
            CorrelationId = correlationId
        };

        return DataverseEnvironmentResolver.TryResolve(target, out ResolvedDataverseEnvironment? resolved, out _)
            ? resolved
            : null;
    }

    public IReadOnlyList<DestinationEnvironment> Environments => catalog.Value.Environments;

    /// <summary>True when DestinationEnvironments is configured: only the listed hosts are accepted.</summary>
    public bool IsEnforced => catalog.Value.IsEnforced;

    public bool IsAllowed(string host)
    {
        return !IsEnforced || Environments.Any(environment =>
            string.Equals(HostOf(environment.Url), host, StringComparison.OrdinalIgnoreCase));
    }

    private static Catalog Load(IConfiguration configuration)
    {
        string? raw = configuration[SettingName]?.Trim();

        if (!string.IsNullOrWhiteSpace(raw))
        {
            return new Catalog(Parse(raw), true);
        }

        List<DestinationEnvironment> registered = configuration
            .GetSection(DataverseEnvironmentRegistry.SectionName)
            .GetChildren()
            .Where(section => !string.IsNullOrWhiteSpace(section.Key))
            .Select(section => new DestinationEnvironment(section.Key, $"https://{section.Key.Trim().ToLowerInvariant()}"))
            .OrderBy(environment => environment.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Catalog(registered, false);
    }

    private sealed record Catalog(IReadOnlyList<DestinationEnvironment> Environments, bool IsEnforced);

    private static List<DestinationEnvironment> Parse(string raw)
    {
        List<Entry>? entries;

        try
        {
            entries = JsonSerializer.Deserialize<List<Entry>>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Failing loudly: silently ignoring a malformed allow list would switch the check off.
            throw new InvalidOperationException(
                $"The application setting '{SettingName}' is not valid JSON. Expected " +
                "[{\"name\":\"UAT\",\"url\":\"https://uat.crm.dynamics.com\"}]. " + ex.Message,
                ex);
        }

        List<DestinationEnvironment> result = new List<DestinationEnvironment>();

        foreach (Entry entry in entries ?? new List<Entry>())
        {
            string? host = HostOf(entry.Url);

            if (host == null)
            {
                throw new InvalidOperationException(
                    $"The application setting '{SettingName}' has an entry without a valid https url: '{entry.Url}'.");
            }

            result.Add(new DestinationEnvironment(
                string.IsNullOrWhiteSpace(entry.Name) ? host : entry.Name.Trim(),
                $"https://{host}"));
        }

        return result.OrderBy(environment => environment.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? HostOf(string? url)
    {
        return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.Host.ToLowerInvariant()
            : null;
    }

    private sealed class Entry
    {
        public string? Name { get; set; }

        public string? Url { get; set; }
    }
}

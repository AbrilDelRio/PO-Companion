using System.Text.Json;
using Microsoft.Extensions.Configuration;

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

    /// <summary>
    /// Nothing is read or parsed here on purpose: this class is injected into the copy endpoint, so a
    /// malformed DestinationEnvironments setting must only fail the requests that USE the catalog
    /// (GET Environments, copy to another environment), never the existing same-environment copy.
    /// </summary>
    public DestinationEnvironmentCatalog(IConfiguration configuration, PowerPlatformEnvironmentDiscovery? discovery = null)
    {
        catalog = new Lazy<Catalog>(() => Load(configuration));
        this.discovery = discovery;
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

        return new CatalogView(Environments, IsEnforced, IsEnforced ? "setting" : "registry");
    }

    /// <summary>Same rule as <see cref="IsAllowed"/>, but with the list that applies to this request.</summary>
    public async Task<bool> IsAllowedAsync(ResolvedDataverseEnvironment? source, string host, CancellationToken cancellationToken)
    {
        CatalogView view = await GetAsync(source, false, cancellationToken);

        return !view.IsEnforced || view.Environments.Any(environment =>
            string.Equals(HostOf(environment.Url), host, StringComparison.OrdinalIgnoreCase));
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

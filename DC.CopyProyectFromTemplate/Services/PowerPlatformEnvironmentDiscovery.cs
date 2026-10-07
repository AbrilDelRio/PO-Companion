using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DC.CopyProyectFromTemplate.Services;

/// <summary>An access token for the discovery API and the tenant it was issued for.</summary>
public sealed record DiscoveryToken(string AccessToken, string TenantId);

/// <summary>
/// Gets a token for <paramref name="scope"/> with the credentials the Function already uses for the
/// SOURCE environment (its own secret, or its managed identity on the federated path).
/// </summary>
public delegate Task<DiscoveryToken> DiscoveryTokenFactory(
    ResolvedDataverseEnvironment source,
    string scope,
    CancellationToken cancellationToken);

/// <summary>The list of environments could not be obtained. The message is meant for the caller.</summary>
public sealed class EnvironmentDiscoveryException : Exception
{
    public EnvironmentDiscoveryException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Lists the Dataverse environments of the tenant that the Function signs into, with the Power
/// Platform API (GET environmentmanagement/environments). Nothing here is specific to a tenant: the same
/// code serves any tenant, once that tenant's administrator has given the Function's app registration the
/// built-in "Power Platform reader" role (read only) with Grant-EnvironmentReader.ps1.
///
/// Off by default. Settings:
///   EnvironmentDiscovery              PowerPlatformApi to turn it on (anything else = off)
///   EnvironmentDiscoveryTypes         environment types offered, default "Production,Sandbox"
///   EnvironmentDiscoveryApiUrl        default https://api.powerplatform.com (other clouds have their own host)
///   EnvironmentDiscoveryCacheMinutes  default 10; the Refresh button of the control bypasses the cache
///
/// The list is also the list of ALLOWED destinations: a copy to an environment that is not in it is rejected,
/// and when the list cannot be obtained the copy is refused rather than allowed.
/// </summary>
public sealed class PowerPlatformEnvironmentDiscovery
{
    public const string ModeSettingName = "EnvironmentDiscovery";
    public const string ModeValue = "PowerPlatformApi";
    public const string TypesSettingName = "EnvironmentDiscoveryTypes";
    public const string ApiUrlSettingName = "EnvironmentDiscoveryApiUrl";
    public const string CacheMinutesSettingName = "EnvironmentDiscoveryCacheMinutes";

    private const string DefaultApiUrl = "https://api.powerplatform.com";
    private const string DefaultTypes = "Production,Sandbox";
    private const int DefaultCacheMinutes = 10;
    private const int MaxPages = 50;

    private static readonly HttpClient SharedHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly IConfiguration configuration;
    private readonly DiscoveryTokenFactory tokenFactory;
    private readonly ILogger<PowerPlatformEnvironmentDiscovery> logger;
    private readonly HttpClient http;

    private readonly ConcurrentDictionary<string, CacheEntry> cache =
        new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

    public PowerPlatformEnvironmentDiscovery(
        IConfiguration configuration,
        DiscoveryTokenFactory tokenFactory,
        ILogger<PowerPlatformEnvironmentDiscovery> logger,
        HttpMessageHandler? handlerForTests = null)
    {
        this.configuration = configuration;
        this.tokenFactory = tokenFactory;
        this.logger = logger;
        http = handlerForTests == null
            ? SharedHttp
            : new HttpClient(handlerForTests) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public bool IsEnabled =>
        string.Equals(configuration[ModeSettingName]?.Trim(), ModeValue, StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<DestinationEnvironment>> DiscoverAsync(
        ResolvedDataverseEnvironment source,
        bool refresh,
        CancellationToken cancellationToken)
    {
        string apiUrl = ReadApiUrl();
        string[] types = ReadTypes();
        TimeSpan lifetime = TimeSpan.FromMinutes(ReadCacheMinutes());

        DiscoveryToken token;

        try
        {
            token = await tokenFactory(source, apiUrl + "/.default", cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            throw new EnvironmentDiscoveryException(
                $"The Function has no usable credentials for '{source.Host}': {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new EnvironmentDiscoveryException(
                $"A token for the Power Platform API could not be obtained ({ex.Message}). The app registration " +
                "needs the 'Power Platform API' service principal in the tenant (see Grant-EnvironmentReader.ps1).", ex);
        }

        string cacheKey = $"{token.TenantId}|{apiUrl}|{string.Join(",", types)}|{source.Host}";

        if (!refresh &&
            cache.TryGetValue(cacheKey, out CacheEntry? cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Environments;
        }

        List<DestinationEnvironment> environments = await FetchAsync(apiUrl, token, types, source.Host, cancellationToken);

        cache[cacheKey] = new CacheEntry(environments, DateTimeOffset.UtcNow.Add(lifetime));

        logger.LogInformation(
            "Environment discovery for tenant {TenantId}: {Count} destination environment(s) of type {Types}. " +
            "Source {Host} excluded. Refresh: {Refresh}.",
            token.TenantId,
            environments.Count,
            string.Join("/", types),
            source.Host,
            refresh);

        return environments;
    }

    private async Task<List<DestinationEnvironment>> FetchAsync(
        string apiUrl,
        DiscoveryToken token,
        string[] types,
        string sourceHost,
        CancellationToken cancellationToken)
    {
        string apiHost = new Uri(apiUrl).Host;
        string? next = apiUrl + "/environmentmanagement/environments?api-version=2024-10-01";
        Dictionary<string, DestinationEnvironment> byHost = new Dictionary<string, DestinationEnvironment>(StringComparer.OrdinalIgnoreCase);

        for (int page = 0; next != null; page++)
        {
            if (page >= MaxPages)
            {
                throw new EnvironmentDiscoveryException($"The environment list has more than {MaxPages} pages; stopped to avoid a loop.");
            }

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new EnvironmentDiscoveryException(DescribeFailure(response, body));
            }

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new EnvironmentDiscoveryException("The Power Platform API returned something that is not JSON.", ex);
            }

            using (document)
            {
                JsonElement root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object &&
                    TryGetIgnoreCase(root, "value", out JsonElement items) &&
                    items.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in items.EnumerateArray())
                    {
                        Accept(item, types, sourceHost, byHost);
                    }
                }

                next = ReadNextLink(root, apiHost);
            }
        }

        return byHost.Values
            .OrderBy(environment => environment.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Accept(
        JsonElement item,
        string[] types,
        string sourceHost,
        Dictionary<string, DestinationEnvironment> byHost)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? url = GetString(item, "url");

        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return; // an environment without Dataverse has no URL
        }

        if (!string.IsNullOrWhiteSpace(GetString(item, "deletedDateTime")))
        {
            return;
        }

        string? state = GetString(item, "state");

        if (!string.IsNullOrWhiteSpace(state) && !string.Equals(state, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? type = GetString(item, "type");

        if (types.Length > 0 && !types.Contains(type ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        string host = uri.Host.ToLowerInvariant();

        if (string.Equals(host, sourceHost, StringComparison.OrdinalIgnoreCase))
        {
            return; // never offer the current environment as its own destination
        }

        string name = GetString(item, "displayName") ?? host;
        byHost[host] = new DestinationEnvironment(name.Trim(), $"https://{host}");
    }

    /// <summary>
    /// The next page. It must stay on the API's own host: following another one would send the
    /// bearer token there.
    /// </summary>
    private static string? ReadNextLink(JsonElement root, string apiHost)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetIgnoreCase(root, "@odata.nextLink", out JsonElement link) ||
            link.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = link.GetString();

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, apiHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new EnvironmentDiscoveryException(
                "The Power Platform API pointed to a next page on another host; it was not followed.");
        }

        return value;
    }

    private static string DescribeFailure(HttpResponseMessage response, string body)
    {
        int status = (int)response.StatusCode;
        string detail = body.Length > 300 ? body.Substring(0, 300) : body;

        string hint = status switch
        {
            401 => " The token was refused: check the app registration and that the 'Power Platform API' service principal exists in the tenant.",
            403 => " The app registration has no role on Power Platform: an administrator must give it the 'Power Platform reader' role (Grant-EnvironmentReader.ps1).",
            404 => " The API address may be wrong for this cloud (EnvironmentDiscoveryApiUrl).",
            _ => string.Empty
        };

        return $"The Power Platform API answered {status} {response.ReasonPhrase}.{hint} {detail}".Trim();
    }

    private string ReadApiUrl()
    {
        string configured = configuration[ApiUrlSettingName]?.Trim() ?? string.Empty;
        string url = string.IsNullOrWhiteSpace(configured) ? DefaultApiUrl : configured.TrimEnd('/');

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new EnvironmentDiscoveryException($"The setting {ApiUrlSettingName} must be an https URL.");
        }

        return url;
    }

    private string[] ReadTypes()
    {
        string configured = configuration[TypesSettingName] ?? string.Empty;
        string raw = string.IsNullOrWhiteSpace(configured) ? DefaultTypes : configured;

        return raw
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(type => type.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private int ReadCacheMinutes()
    {
        return int.TryParse(configuration[CacheMinutesSettingName], out int minutes) && minutes >= 0
            ? minutes
            : DefaultCacheMinutes;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return TryGetIgnoreCase(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryGetIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed record CacheEntry(IReadOnlyList<DestinationEnvironment> Environments, DateTimeOffset ExpiresAt);
}

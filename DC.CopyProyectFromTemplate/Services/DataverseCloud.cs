namespace DC.CopyProyectFromTemplate.Services;

public enum DataverseCloud
{
    Commercial = 0,
    GCC = 1,
    GCCHigh = 2,
    DoD = 3,
    China = 4
}

/// <summary>
/// The Microsoft Entra instance a cloud signs into. Commercial and GCC share the global one,
/// GCC High and DoD share the US Government one. A managed identity only obtains tokens inside
/// its own instance, so the federated path never crosses this boundary (Microsoft Learn: another
/// tenant is supported, another cloud is not).
/// </summary>
public enum EntraCloud
{
    Global = 0,
    USGov = 1,
    China = 2
}

/// <summary>
/// Supported clouds: sign-in authority and Dataverse host suffix.
/// </summary>
public static class DataverseClouds
{
    // Order matters: ".crm9.dynamics.com" also ends in ".dynamics.com",
    // so GCC must be evaluated BEFORE Commercial.
    private static readonly (DataverseCloud Cloud, string Name, string HostSuffix, string Authority)[] Table =
    {
        (DataverseCloud.GCCHigh,    "GCCHigh",    ".crm.microsoftdynamics.us", "https://login.microsoftonline.us/"),
        (DataverseCloud.DoD,        "DoD",        ".crm.appsplatform.us",      "https://login.microsoftonline.us/"),
        (DataverseCloud.China,      "China",      ".dynamics.cn",              "https://login.chinacloudapi.cn/"),
        (DataverseCloud.GCC,        "GCC",        ".crm9.dynamics.com",        "https://login.microsoftonline.com/"),
        (DataverseCloud.Commercial, "Commercial", ".dynamics.com",             "https://login.microsoftonline.com/")
    };

    public static string AcceptedNames => "Commercial, GCC, GCCHigh, DoD, China";

    public static string SupportedHostSuffixes =>
        ".dynamics.com, .crm9.dynamics.com, .crm.microsoftdynamics.us, .crm.appsplatform.us, .dynamics.cn";

    public static bool TryParse(string? value, out DataverseCloud cloud)
    {
        cloud = DataverseCloud.Commercial;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value.Trim();

        foreach ((DataverseCloud Cloud, string Name, string HostSuffix, string Authority) entry in Table)
        {
            if (string.Equals(entry.Name, candidate, StringComparison.OrdinalIgnoreCase))
            {
                cloud = entry.Cloud;
                return true;
            }
        }

        return false;
    }

    public static string GetName(DataverseCloud cloud) => Find(cloud).Name;

    public static string GetAuthority(DataverseCloud cloud) => Find(cloud).Authority;

    public static string GetHostSuffix(DataverseCloud cloud) => Find(cloud).HostSuffix;

    /// <summary>The Entra instance the cloud signs into; it follows the authority of the table.</summary>
    public static EntraCloud GetEntraCloud(DataverseCloud cloud) => cloud switch
    {
        DataverseCloud.GCCHigh or DataverseCloud.DoD => EntraCloud.USGov,
        DataverseCloud.China => EntraCloud.China,
        _ => EntraCloud.Global
    };

    public static string GetEntraCloudName(EntraCloud cloud) => cloud switch
    {
        EntraCloud.USGov => "USGov",
        EntraCloud.China => "China",
        _ => "Global"
    };

    /// <summary>
    /// Audience of the managed identity token that a federated credential accepts, one per Entra
    /// instance. The values come from Microsoft Learn, "Configure an application to trust a
    /// managed identity"; the federated credential in the registration must carry the same one.
    /// </summary>
    public static string GetTokenExchangeAudience(EntraCloud cloud) => cloud switch
    {
        EntraCloud.USGov => "api://AzureADTokenExchangeUSGov",
        EntraCloud.China => "api://AzureADTokenExchangeChina",
        _ => "api://AzureADTokenExchange"
    };

    /// <summary>
    /// Infers the cloud from a Dataverse host. Walks the table in order, so GCC wins over Commercial.
    /// </summary>
    public static bool TryResolveFromHost(string? host, out DataverseCloud cloud, out string matchedSuffix)
    {
        cloud = DataverseCloud.Commercial;
        matchedSuffix = string.Empty;

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string candidate = host.Trim().TrimEnd('.').ToLowerInvariant();

        foreach ((DataverseCloud Cloud, string Name, string HostSuffix, string Authority) entry in Table)
        {
            if (candidate.EndsWith(entry.HostSuffix, StringComparison.Ordinal))
            {
                cloud = entry.Cloud;
                matchedSuffix = entry.HostSuffix;
                return true;
            }
        }

        return false;
    }

    private static (DataverseCloud Cloud, string Name, string HostSuffix, string Authority) Find(DataverseCloud cloud)
    {
        foreach ((DataverseCloud Cloud, string Name, string HostSuffix, string Authority) entry in Table)
        {
            if (entry.Cloud == cloud)
            {
                return entry;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(cloud), cloud, "Unsupported Dataverse cloud.");
    }
}

/// <summary>
/// Where this Function App runs, which decides the Entra instance of its managed identity and
/// therefore which environments the federated path can serve. Read from DataverseFederation:Cloud
/// when set (Global, USGov or China), otherwise from the WEBSITE_HOSTNAME that App Service
/// assigns: *.azurewebsites.us is Azure Government, *.chinacloudsites.cn is Azure China, anything
/// else is the global cloud. Pure, so the detection is unit-tested without Azure.
/// </summary>
public static class FunctionHostingCloud
{
    public const string SettingName = "Cloud";

    public const string AcceptedNames = "Global, USGov, China";

    public static bool TryParse(string? value, out EntraCloud cloud)
    {
        cloud = EntraCloud.Global;

        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "global":
            case "commercial":
            case "public":
                cloud = EntraCloud.Global;
                return true;
            case "usgov":
            case "usgovernment":
            case "azuregovernment":
            case "government":
                cloud = EntraCloud.USGov;
                return true;
            case "china":
            case "azurechina":
                cloud = EntraCloud.China;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The configured value wins; an invalid one is ignored and named in the source, so the
    /// mistake shows up in the log and in TestConnection instead of silently changing the cloud.
    /// </summary>
    public static EntraCloud Detect(string? configuredValue, string? websiteHostname, out string source)
    {
        string invalidNote = string.Empty;

        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            if (TryParse(configuredValue, out EntraCloud configured))
            {
                source = $"configured in {DataverseFederationSettings.SectionName}:{SettingName}";
                return configured;
            }

            invalidNote =
                $"the value '{configuredValue.Trim()}' of {DataverseFederationSettings.SectionName}:{SettingName} " +
                $"is not valid ({AcceptedNames}) and was ignored; ";
        }

        string host = (websiteHostname ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

        if (host.EndsWith(".azurewebsites.us", StringComparison.Ordinal))
        {
            source = $"{invalidNote}WEBSITE_HOSTNAME {host}";
            return EntraCloud.USGov;
        }

        if (host.EndsWith(".chinacloudsites.cn", StringComparison.Ordinal))
        {
            source = $"{invalidNote}WEBSITE_HOSTNAME {host}";
            return EntraCloud.China;
        }

        source = host.Length > 0
            ? $"{invalidNote}WEBSITE_HOSTNAME {host}"
            : $"{invalidNote}default, no WEBSITE_HOSTNAME";
        return EntraCloud.Global;
    }
}

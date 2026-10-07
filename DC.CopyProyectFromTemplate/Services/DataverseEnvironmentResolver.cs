using DC.CopyProyectFromTemplate.Models;

namespace DC.CopyProyectFromTemplate.Services;

/// <summary>
/// A validated and normalized environment. This is the only shape the lower layers consume.
/// </summary>
public sealed class ResolvedDataverseEnvironment
{
    public ResolvedDataverseEnvironment(
        string environmentUrl,
        string environmentApiUrl,
        string host,
        DataverseCloud cloud,
        string correlationId)
    {
        EnvironmentUrl = environmentUrl;
        EnvironmentApiUrl = environmentApiUrl;
        Host = host;
        Cloud = cloud;
        CorrelationId = correlationId;
    }

    /// <summary>Environment URL without a trailing slash, e.g. https://contoso.crm.dynamics.com</summary>
    public string EnvironmentUrl { get; }

    /// <summary>Web API URL without a trailing slash. Today it is only validated and logged.</summary>
    public string EnvironmentApiUrl { get; }

    /// <summary>Lowercase host of EnvironmentUrl. This is the per-environment credential key.</summary>
    public string Host { get; }

    public DataverseCloud Cloud { get; }

    public string CloudName => DataverseClouds.GetName(Cloud);

    /// <summary>Sign-in authority for the cloud, tenant excluded, e.g. https://login.microsoftonline.us/</summary>
    public string Authority => DataverseClouds.GetAuthority(Cloud);

    public string CorrelationId { get; }

    /// <summary>Client-credentials scope for Dataverse.</summary>
    public string Scope => $"{EnvironmentUrl}/.default";
}

public static class DataverseEnvironmentResolver
{
    /// <summary>
    /// Validates the routing fields sent by the caller. Returns false plus a plain-text message
    /// when something does not add up. When the caller sent none of the fields it returns true
    /// with resolved = null: that is the legacy path (fixed environment from configuration).
    /// </summary>
    public static bool TryResolve(
        DataverseEnvironmentTarget? target,
        out ResolvedDataverseEnvironment? resolved,
        out string? error)
    {
        resolved = null;
        error = null;

        if (target == null || target.IsEmpty)
        {
            return true;
        }

        List<string> missing = new List<string>(3);

        if (string.IsNullOrWhiteSpace(target.EnvironmentUrl))
        {
            missing.Add("environmentUrl");
        }

        if (string.IsNullOrWhiteSpace(target.EnvironmentApiUrl))
        {
            missing.Add("environmentApiUrl");
        }

        if (string.IsNullOrWhiteSpace(target.Cloud))
        {
            missing.Add("cloud");
        }

        if (missing.Count > 0)
        {
            error =
                $"The environment routing fields are incomplete: {string.Join(", ", missing)} missing. " +
                "Send environmentUrl, environmentApiUrl and cloud together, or none of the three.";
            return false;
        }

        if (!TryParseHttpsUrl(target.EnvironmentUrl, "environmentUrl", out Uri? environmentUri, out error))
        {
            return false;
        }

        if (!TryParseHttpsUrl(target.EnvironmentApiUrl, "environmentApiUrl", out Uri? environmentApiUri, out error))
        {
            return false;
        }

        if (!DataverseClouds.TryParse(target.Cloud, out DataverseCloud declaredCloud))
        {
            error =
                $"cloud '{target.Cloud}' is not a valid value. " +
                $"Accepted values: {DataverseClouds.AcceptedNames}.";
            return false;
        }

        string environmentHost = environmentUri!.Host.ToLowerInvariant();
        string environmentApiHost = environmentApiUri!.Host.ToLowerInvariant();

        if (!DataverseClouds.TryResolveFromHost(environmentHost, out DataverseCloud environmentCloud, out string environmentSuffix))
        {
            error =
                $"The environmentUrl host '{environmentHost}' does not match any known Dataverse cloud " +
                $"(supported suffixes: {DataverseClouds.SupportedHostSuffixes}).";
            return false;
        }

        if (environmentCloud != declaredCloud)
        {
            error = BuildMismatchMessage("environmentUrl", environmentHost, environmentSuffix, environmentCloud, declaredCloud);
            return false;
        }

        if (!DataverseClouds.TryResolveFromHost(environmentApiHost, out DataverseCloud environmentApiCloud, out string environmentApiSuffix))
        {
            error =
                $"The environmentApiUrl host '{environmentApiHost}' does not match any known Dataverse cloud " +
                $"(supported suffixes: {DataverseClouds.SupportedHostSuffixes}).";
            return false;
        }

        if (environmentApiCloud != declaredCloud)
        {
            error = BuildMismatchMessage("environmentApiUrl", environmentApiHost, environmentApiSuffix, environmentApiCloud, declaredCloud);
            return false;
        }

        string correlationId = string.IsNullOrWhiteSpace(target.CorrelationId)
            ? Guid.NewGuid().ToString("D")
            : target.CorrelationId.Trim();

        resolved = new ResolvedDataverseEnvironment(
            $"{environmentUri.Scheme}://{environmentUri.Authority}",
            $"{environmentApiUri.Scheme}://{environmentApiUri.Authority}",
            environmentHost,
            declaredCloud,
            correlationId);

        return true;
    }

    /// <summary>
    /// Rebuilds the DTO from the validated values, so the normalized form is what travels
    /// through Durable instead of the raw caller input.
    /// </summary>
    public static DataverseEnvironmentTarget ToTarget(ResolvedDataverseEnvironment resolved)
    {
        return new DataverseEnvironmentTarget
        {
            EnvironmentUrl = resolved.EnvironmentUrl,
            EnvironmentApiUrl = resolved.EnvironmentApiUrl,
            Cloud = resolved.CloudName,
            CorrelationId = resolved.CorrelationId
        };
    }

    private static string BuildMismatchMessage(
        string fieldName,
        string host,
        string matchedSuffix,
        DataverseCloud hostCloud,
        DataverseCloud declaredCloud)
    {
        return
            $"{fieldName} and cloud disagree. The {fieldName} host '{host}' ends in '{matchedSuffix}', " +
            $"which is the {DataverseClouds.GetName(hostCloud)} cloud; the cloud field declares " +
            $"{DataverseClouds.GetName(declaredCloud)}, whose suffix is '{DataverseClouds.GetHostSuffix(declaredCloud)}'. " +
            "Fix whichever one is wrong: neither is assumed.";
    }

    private static bool TryParseHttpsUrl(string value, string fieldName, out Uri? uri, out string? error)
    {
        error = null;

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri))
        {
            error = $"{fieldName} '{value}' is not an absolute URL.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"{fieldName} '{value}' must use https, not '{uri.Scheme}'.";
            uri = null;
            return false;
        }

        return true;
    }
}

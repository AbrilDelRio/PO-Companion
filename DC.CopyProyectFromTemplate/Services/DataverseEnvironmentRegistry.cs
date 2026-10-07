using Microsoft.Extensions.Configuration;

namespace DC.CopyProyectFromTemplate.Services;

public sealed class DataverseEnvironmentCredentials
{
    public DataverseEnvironmentCredentials(
        string tenantId,
        string clientId,
        string? clientSecret,
        string? certificateBase64,
        string? certificatePassword)
    {
        TenantId = tenantId;
        ClientId = clientId;
        ClientSecret = clientSecret;
        CertificateBase64 = certificateBase64;
        CertificatePassword = certificatePassword;
    }

    public string TenantId { get; }

    public string ClientId { get; }

    public string? ClientSecret { get; }

    public string? CertificateBase64 { get; }

    public string? CertificatePassword { get; }

    public bool UsesCertificate => !string.IsNullOrWhiteSpace(CertificateBase64);
}

/// <summary>
/// The per-environment settings exactly as found in configuration, before any decision is made.
/// Which credential path they lead to is decided by <see cref="DataverseCredentialResolver"/>.
/// </summary>
public sealed record DataverseEnvironmentSettings(
    string Host,
    string? TenantId,
    string? ClientId,
    string? ClientSecret,
    string? CertificateBase64,
    string? CertificatePassword)
{
    /// <summary>While either one exists, the secret path wins for this host.</summary>
    public bool HasSecretOrCertificate =>
        !string.IsNullOrWhiteSpace(ClientSecret) || !string.IsNullOrWhiteSpace(CertificateBase64);

    public bool HasIdentityValues =>
        !string.IsNullOrWhiteSpace(TenantId) || !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>
/// Global settings of the federated path. None of the values is a secret:
///   DataverseFederation:ClientId                 Application (client) ID of the registration
///   DataverseFederation:ManagedIdentityClientId  Client ID of the user-assigned managed identity
///   DataverseFederation:Cloud                    Optional: Global, USGov or China, the Entra instance
///                                                this Function runs in. Detected from WEBSITE_HOSTNAME
///                                                when empty (see FunctionHostingCloud).
/// </summary>
public sealed record DataverseFederationSettings(
    string? ClientId,
    string? ManagedIdentityClientId,
    EntraCloud HostingCloud = EntraCloud.Global,
    string HostingSource = "default")
{
    public const string SectionName = "DataverseFederation";

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ManagedIdentityClientId);

    public bool IsPartial =>
        !IsComplete && (!string.IsNullOrWhiteSpace(ClientId) || !string.IsNullOrWhiteSpace(ManagedIdentityClientId));

    public string HostingCloudName => DataverseClouds.GetEntraCloudName(HostingCloud);

    /// <summary>Audience the managed identity token must carry for this Function's Entra instance.</summary>
    public string TokenExchangeAudience => DataverseClouds.GetTokenExchangeAudience(HostingCloud);
}

/// <summary>
/// Reads credential settings from configuration. One block per Dataverse host:
///
///   DataverseEnvironments:{host}:TenantId
///   DataverseEnvironments:{host}:ClientId
///   DataverseEnvironments:{host}:ClientSecret            (or CertificateBase64)
///   DataverseEnvironments:{host}:CertificateBase64
///   DataverseEnvironments:{host}:CertificatePassword
///
/// plus the global DataverseFederation block of the federated path. On App Service Linux the
/// separator is "__" instead of ":", e.g. DataverseEnvironments__contoso.crm.dynamics.com__TenantId.
/// Secrets come from Key Vault references. Adding a tenant is app settings only: no code change.
/// </summary>
public sealed class DataverseEnvironmentRegistry
{
    public const string SectionName = "DataverseEnvironments";

    private readonly IConfiguration configuration;

    public DataverseEnvironmentRegistry(IConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public DataverseEnvironmentSettings ReadEnvironment(string host)
    {
        string prefix = $"{SectionName}:{host}";

        return new DataverseEnvironmentSettings(
            host,
            Read($"{prefix}:TenantId"),
            Read($"{prefix}:ClientId"),
            Read($"{prefix}:ClientSecret"),
            Read($"{prefix}:CertificateBase64"),
            Read($"{prefix}:CertificatePassword"));
    }

    public DataverseFederationSettings ReadFederation()
    {
        // WEBSITE_HOSTNAME is set by App Service on every plan and OS; locally it is absent.
        EntraCloud hosting = FunctionHostingCloud.Detect(
            Read($"{DataverseFederationSettings.SectionName}:{FunctionHostingCloud.SettingName}"),
            Read("WEBSITE_HOSTNAME"),
            out string hostingSource);

        return new DataverseFederationSettings(
            Read($"{DataverseFederationSettings.SectionName}:ClientId")?.Trim(),
            Read($"{DataverseFederationSettings.SectionName}:ManagedIdentityClientId")?.Trim(),
            hosting,
            hostingSource);
    }

    private string? Read(string key)
    {
        string? value = configuration[key];

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

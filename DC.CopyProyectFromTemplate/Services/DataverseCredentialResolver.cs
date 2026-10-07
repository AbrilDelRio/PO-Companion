namespace DC.CopyProyectFromTemplate.Services;

public enum DataverseCredentialPath
{
    None = 0,
    Secret = 1,
    Federated = 2
}

public sealed record DataverseCredentialResolution(
    DataverseCredentialPath Path,
    DataverseEnvironmentCredentials? SecretCredentials,
    DataverseFederationSettings? Federation,
    string? Error);

/// <summary>
/// Decides which credential path an environment uses. Pure on purpose, so the resolution order is
/// unit-tested without Azure. The order, from the option C guide:
///
///   1. The host has a ClientSecret or CertificateBase64: the secret path, exactly as before.
///      Sovereign-cloud customers depend on it, and it always wins while it exists.
///   2. Otherwise, DataverseFederation is configured and the environment lives in the same Entra
///      cloud as the Function's managed identity: the federated path.
///   3. Otherwise: the usual "no credentials configured" 400.
/// </summary>
public static class DataverseCredentialResolver
{
    /// <summary>
    /// Dataverse clouds the federated path can serve from a Function hosted in the given Entra
    /// instance. A managed identity only obtains tokens inside its own instance (Microsoft Learn:
    /// another tenant yes, another cloud no), so a Function in Azure Government serves GCC High
    /// and DoD, one in Azure China serves China, and one in the global cloud serves Commercial.
    /// GCC signs in at the global instance too, but stays out until verified against a real GCC
    /// environment.
    /// </summary>
    public static IReadOnlyList<DataverseCloud> FederatedCloudsFor(EntraCloud hosting) => hosting switch
    {
        EntraCloud.USGov => new[] { DataverseCloud.GCCHigh, DataverseCloud.DoD },
        EntraCloud.China => new[] { DataverseCloud.China },
        _ => new[] { DataverseCloud.Commercial }
    };

    public static DataverseCredentialResolution Resolve(
        DataverseEnvironmentSettings settings,
        DataverseFederationSettings federation,
        DataverseCloud cloud,
        bool linuxSettingNames)
    {
        SettingNames names = new SettingNames(settings.Host, linuxSettingNames);

        if (settings.HasSecretOrCertificate)
        {
            List<string> missing = new List<string>(2);

            if (string.IsNullOrWhiteSpace(settings.TenantId))
            {
                missing.Add(names.Environment("TenantId"));
            }

            if (string.IsNullOrWhiteSpace(settings.ClientId))
            {
                missing.Add(names.Environment("ClientId"));
            }

            // Once a secret exists the operator chose the secret path: an incomplete block is an
            // error, never a silent fall-through to the federated path.
            if (missing.Count > 0)
            {
                return Fail(
                    $"The credentials configured for the Dataverse environment '{settings.Host}' are incomplete: " +
                    $"{string.Join(" and ", missing)} missing.");
            }

            return new DataverseCredentialResolution(
                DataverseCredentialPath.Secret,
                new DataverseEnvironmentCredentials(
                    settings.TenantId!.Trim(),
                    settings.ClientId!.Trim(),
                    settings.ClientSecret?.Trim(),
                    settings.CertificateBase64?.Trim(),
                    settings.CertificatePassword),
                null,
                null);
        }

        if (TryUseFederation(federation, cloud, names, out string federationNote))
        {
            return new DataverseCredentialResolution(DataverseCredentialPath.Federated, null, federation, null);
        }

        string message = settings.HasIdentityValues
            ? $"The credentials configured for the Dataverse environment '{settings.Host}' are incomplete: " +
              $"{names.Environment("ClientSecret")} (or {names.Environment("CertificateBase64")}) is missing."
            : $"No credentials are configured for the Dataverse environment '{settings.Host}'. " +
              $"Add {names.Environment("TenantId")}, {names.Environment("ClientId")} and " +
              $"{names.Environment("ClientSecret")} (or {names.Environment("CertificateBase64")}) " +
              "to the Function app settings.";

        return Fail(string.IsNullOrEmpty(federationNote) ? message : $"{message} {federationNote}");
    }

    private static bool TryUseFederation(
        DataverseFederationSettings federation,
        DataverseCloud cloud,
        SettingNames names,
        out string note)
    {
        IReadOnlyList<DataverseCloud> allowed = FederatedCloudsFor(federation.HostingCloud);
        bool cloudAllowed = allowed.Contains(cloud);
        string cloudName = DataverseClouds.GetName(cloud);

        if (federation.IsComplete && cloudAllowed)
        {
            note = string.Empty;
            return true;
        }

        if (federation.IsComplete)
        {
            note =
                "The federated credentials are not used for this environment: this Function runs in the " +
                $"{federation.HostingCloudName} Entra cloud ({federation.HostingSource}), whose managed identity can " +
                $"only sign into environments of the same cloud ({string.Join(", ", allowed.Select(DataverseClouds.GetName))}), " +
                $"and this environment is {cloudName}. A managed identity cannot obtain tokens in another cloud.";
            return false;
        }

        if (federation.IsPartial)
        {
            string missing = string.IsNullOrWhiteSpace(federation.ClientId)
                ? names.Federation("ClientId")
                : names.Federation("ManagedIdentityClientId");

            note =
                $"The federated credentials are not used: {missing} is missing, and " +
                $"{names.Federation("ClientId")} and {names.Federation("ManagedIdentityClientId")} are both required.";
            return false;
        }

        note = cloudAllowed
            ? $"For the {cloudName} cloud you can use the managed identity instead: configure " +
              $"{names.Federation("ClientId")} and {names.Federation("ManagedIdentityClientId")}."
            : string.Empty;
        return false;
    }

    private static DataverseCredentialResolution Fail(string error) =>
        new DataverseCredentialResolution(DataverseCredentialPath.None, null, null, error);

    /// <summary>
    /// Setting names exactly as the user has to type them. On App Service Linux the separator is
    /// "__": people copy these names verbatim, and a ":" name never loads there.
    /// </summary>
    private readonly struct SettingNames
    {
        private readonly string host;
        private readonly string separator;

        public SettingNames(string host, bool linux)
        {
            this.host = host;
            separator = linux ? "__" : ":";
        }

        public string Environment(string name) =>
            $"{DataverseEnvironmentRegistry.SectionName}{separator}{host}{separator}{name}";

        public string Federation(string name) =>
            $"{DataverseFederationSettings.SectionName}{separator}{name}";
    }
}

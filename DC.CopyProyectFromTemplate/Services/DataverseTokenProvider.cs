using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace DC.CopyProyectFromTemplate.Services;

/// <summary>
/// What was decided for one environment: the credential path, and the tenant and authority it
/// signs into. Built by <see cref="DataverseTokenProvider.ResolveAsync"/>.
/// </summary>
public sealed record DataverseCredentialPlan(
    ResolvedDataverseEnvironment Environment,
    DataverseCredentialPath Path,
    string ClientId,
    string TenantId,
    string TenantSource,
    string AuthorityHost,
    DataverseEnvironmentCredentials? SecretCredentials,
    string? ManagedIdentityClientId,
    string? TokenExchangeAudience = null)
{
    public string Authority => $"{AuthorityHost}{TenantId}";

    public string PathName => Path == DataverseCredentialPath.Federated ? "federated" : "secret";
}

/// <summary>
/// Acquires Dataverse access tokens with an EXPLICIT authority per cloud, through one of two
/// credential paths per environment:
///   secret     the host's own ClientSecret or certificate, exactly as before;
///   federated  the registration, proven with the Function's managed identity (option C), for
///              environments of the same Entra cloud the Function runs in; the tenant is
///              discovered, not configured.
/// Every step that builds a client or requests a token runs inside a try and is logged in full,
/// with its AADSTS code: the last production failure took a day to surface because the
/// connection was built outside one.
/// </summary>
public sealed class DataverseTokenProvider
{
    private readonly DataverseEnvironmentRegistry registry;
    private readonly DataverseTenantDiscovery discovery;
    private readonly ILogger<DataverseTokenProvider> logger;

    private readonly ConcurrentDictionary<string, IConfidentialClientApplication> applications =
        new ConcurrentDictionary<string, IConfidentialClientApplication>(StringComparer.OrdinalIgnoreCase);

    private readonly object federatedLock = new object();
    private FederatedDataverseCredential? federatedCredential;

    public DataverseTokenProvider(
        DataverseEnvironmentRegistry registry,
        DataverseTenantDiscovery discovery,
        ILogger<DataverseTokenProvider> logger)
    {
        this.registry = registry;
        this.discovery = discovery;
        this.logger = logger;
    }

    /// <summary>
    /// Decides the credential path for an environment and, on the federated path, discovers its
    /// tenant. An InvalidDataException means the environment cannot be served as configured,
    /// which the HTTP layer turns into the usual plain-text 400.
    /// </summary>
    public async Task<DataverseCredentialPlan> ResolveAsync(
        ResolvedDataverseEnvironment environment,
        CancellationToken cancellationToken)
    {
        DataverseEnvironmentSettings settings = registry.ReadEnvironment(environment.Host);
        DataverseFederationSettings federation = registry.ReadFederation();

        DataverseCredentialResolution resolution = DataverseCredentialResolver.Resolve(
            settings,
            federation,
            environment.Cloud,
            OperatingSystem.IsLinux());

        switch (resolution.Path)
        {
            case DataverseCredentialPath.Secret:
            {
                DataverseEnvironmentCredentials credentials = resolution.SecretCredentials!;

                return new DataverseCredentialPlan(
                    environment,
                    DataverseCredentialPath.Secret,
                    credentials.ClientId,
                    credentials.TenantId,
                    "configured",
                    environment.Authority,
                    credentials,
                    null);
            }

            case DataverseCredentialPath.Federated:
            {
                DiscoveredTenant tenant = await discovery.DiscoverAsync(environment, cancellationToken);

                if (settings.HasIdentityValues)
                {
                    bool sameTenant = string.Equals(
                        settings.TenantId?.Trim(),
                        tenant.TenantId,
                        StringComparison.OrdinalIgnoreCase);

                    logger.LogInformation(
                        "Federated path for {Host}: its TenantId/ClientId settings are ignored because it has no " +
                        "secret. Configured TenantId: {ConfiguredTenantId}; discovered: {DiscoveredTenantId} " +
                        "({TenantComparison}).",
                        environment.Host,
                        settings.TenantId ?? "(none)",
                        tenant.TenantId,
                        sameTenant ? "same" : "DIFFERENT");
                }

                logger.LogInformation(
                    "Federated path for {Host} ({Cloud}): this Function runs in the {HostingCloud} Entra cloud " +
                    "({HostingSource}); token exchange audience {Audience}. CorrelationId: {CorrelationId}.",
                    environment.Host,
                    environment.CloudName,
                    federation.HostingCloudName,
                    federation.HostingSource,
                    federation.TokenExchangeAudience,
                    environment.CorrelationId);

                return new DataverseCredentialPlan(
                    environment,
                    DataverseCredentialPath.Federated,
                    resolution.Federation!.ClientId!,
                    tenant.TenantId,
                    "discovered",
                    tenant.AuthorityHost,
                    null,
                    resolution.Federation.ManagedIdentityClientId,
                    resolution.Federation.TokenExchangeAudience);
            }

            default:
                throw new InvalidDataException(
                    resolution.Error ?? $"No credentials are configured for the Dataverse environment '{environment.Host}'.");
        }
    }

    public async Task<string> GetAccessTokenAsync(DataverseCredentialPlan plan, CancellationToken cancellationToken = default)
    {
        ResolvedDataverseEnvironment environment = plan.Environment;
        IConfidentialClientApplication application = GetOrBuildApplication(plan);

        AuthenticationResult result;

        try
        {
            result = await application
                .AcquireTokenForClient(new[] { environment.Scope })
                .ExecuteAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            string aadsts = ActivityDiagnostics.FindAadstsCode(ex) ?? "(none)";
            string msalError = ex is MsalException msal ? msal.ErrorCode : ex.GetType().Name;

            logger.LogError(
                ex,
                "MSAL could not issue a Dataverse token. Path: {CredentialPath}. Host: {Host} ({Cloud}). " +
                "Tenant: {TenantId} ({TenantSource}). Authority: {Authority}. ClientId: {ClientId}. " +
                "AADSTS: {Aadsts}. MSAL error: {MsalError}. CorrelationId: {CorrelationId}. Cause: {Cause}",
                plan.PathName,
                environment.Host,
                environment.CloudName,
                plan.TenantId,
                plan.TenantSource,
                plan.Authority,
                plan.ClientId,
                aadsts,
                msalError,
                environment.CorrelationId,
                ActivityDiagnostics.Flatten(ex));

            // ServiceClient swallows whatever the token function throws into LastException, so the
            // AADSTS code travels in the message as well. Common ones: AADSTS7000215 wrong secret,
            // AADSTS7000222 expired secret, AADSTS700016 registration not in that tenant or not
            // consented there, AADSTS70021 no matching federated identity record (still
            // propagating, or the issuer, subject or audience of the federated credential is wrong).
            throw new InvalidOperationException(
                $"MSAL could not issue a Dataverse token for '{environment.Host}' ({environment.CloudName}) via the " +
                $"{plan.PathName} path, from authority '{plan.Authority}' with scope '{environment.Scope}'. " +
                $"AADSTS: {aadsts}. MSAL error code: {msalError}. {ex.Message}",
                ex);
        }

        logger.LogInformation(
            "Dataverse token acquired for {Host} ({Cloud}) via the {CredentialPath} path. Tenant: {TenantId}. " +
            "Authority: {Authority}. Source: {TokenSource}. CorrelationId: {CorrelationId}.",
            environment.Host,
            environment.CloudName,
            plan.PathName,
            plan.TenantId,
            plan.Authority,
            result.AuthenticationResultMetadata.TokenSource,
            environment.CorrelationId);

        return result.AccessToken;
    }

    /// <summary>
    /// A token for ANOTHER resource (the Power Platform API, for environment discovery) with the same
    /// credentials the plan uses for Dataverse: the host's own secret, or the managed identity on the
    /// federated path. <see cref="GetAccessTokenAsync"/> is not touched.
    /// </summary>
    public async Task<string> GetAccessTokenForScopeAsync(
        DataverseCredentialPlan plan,
        string scope,
        CancellationToken cancellationToken = default)
    {
        IConfidentialClientApplication application = GetOrBuildApplication(plan);

        try
        {
            AuthenticationResult result = await application
                .AcquireTokenForClient(new[] { scope })
                .ExecuteAsync(cancellationToken);

            return result.AccessToken;
        }
        catch (Exception ex)
        {
            string aadsts = ActivityDiagnostics.FindAadstsCode(ex) ?? "(none)";

            logger.LogError(
                ex,
                "MSAL could not issue a token for scope {Scope}. Path: {CredentialPath}. Host: {Host}. Tenant: {TenantId}. " +
                "ClientId: {ClientId}. AADSTS: {Aadsts}. Cause: {Cause}",
                scope,
                plan.PathName,
                plan.Environment.Host,
                plan.TenantId,
                plan.ClientId,
                aadsts,
                ActivityDiagnostics.Flatten(ex));

            throw new InvalidOperationException(
                $"No token for scope '{scope}' via the {plan.PathName} path for '{plan.Environment.Host}'. AADSTS: {aadsts}. {ex.Message}",
                ex);
        }
    }

    private IConfidentialClientApplication GetOrBuildApplication(DataverseCredentialPlan plan)
    {
        // Keyed by path as well as host: once a host's secret is deleted it moves to the federated
        // path, and must not keep using the secret-based client already cached for it.
        string key = $"{plan.PathName}|{plan.Environment.Host}|{plan.ClientId}|{plan.TenantId}";

        if (applications.TryGetValue(key, out IConfidentialClientApplication? cached))
        {
            return cached;
        }

        IConfidentialClientApplication application;

        try
        {
            application = plan.Path == DataverseCredentialPath.Federated
                ? BuildFederated(plan)
                : BuildSecret(plan);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "The {CredentialPath} confidential client for {Host} ({Cloud}) could not be built. ClientId: {ClientId}. " +
                "Authority: {Authority}. AADSTS: {Aadsts}. CorrelationId: {CorrelationId}. Cause: {Cause}",
                plan.PathName,
                plan.Environment.Host,
                plan.Environment.CloudName,
                plan.ClientId,
                plan.Authority,
                ActivityDiagnostics.FindAadstsCode(ex) ?? "(none)",
                plan.Environment.CorrelationId,
                ActivityDiagnostics.Flatten(ex));

            throw new InvalidOperationException(
                $"The {plan.PathName} confidential client for '{plan.Environment.Host}' could not be built. " +
                $"Cause: {ActivityDiagnostics.Flatten(ex)}",
                ex);
        }

        return applications.GetOrAdd(key, application);
    }

    private IConfidentialClientApplication BuildSecret(DataverseCredentialPlan plan)
    {
        DataverseEnvironmentCredentials credentials = plan.SecretCredentials!;

        ConfidentialClientApplicationBuilder builder = ConfidentialClientApplicationBuilder
            .Create(credentials.ClientId)
            .WithAuthority(plan.Authority, validateAuthority: true);

        if (credentials.UsesCertificate)
        {
            builder = builder.WithCertificate(LoadCertificate(credentials));
        }
        else
        {
            builder = builder.WithClientSecret(credentials.ClientSecret!);
        }

        logger.LogInformation(
            "Confidential client built for Dataverse environment {Host} ({Cloud}). " +
            "Authority: {Authority}. Credential: {CredentialKind}.",
            plan.Environment.Host,
            plan.Environment.CloudName,
            plan.Authority,
            credentials.UsesCertificate ? "certificate" : "client secret");

        return builder.Build();
    }

    private IConfidentialClientApplication BuildFederated(DataverseCredentialPlan plan)
    {
        FederatedDataverseCredential credential = GetFederatedCredential(
            plan.ClientId,
            plan.ManagedIdentityClientId!,
            plan.TokenExchangeAudience ?? DataverseClouds.GetTokenExchangeAudience(EntraCloud.Global));
        IConfidentialClientApplication application = credential.CreateClient(plan.AuthorityHost, plan.TenantId);

        logger.LogInformation(
            "Federated confidential client built for Dataverse environment {Host} ({Cloud}). ClientId: {ClientId}. " +
            "Managed identity: {ManagedIdentityClientId}. Authority: {Authority}. Token exchange audience: {Audience}.",
            plan.Environment.Host,
            plan.Environment.CloudName,
            plan.ClientId,
            plan.ManagedIdentityClientId,
            plan.Authority,
            credential.TokenExchangeAudience);

        return application;
    }

    /// <summary>
    /// Where this Function runs and what the federated path can serve from there, for the
    /// TestConnection report.
    /// </summary>
    public object DescribeHosting()
    {
        DataverseFederationSettings federation = registry.ReadFederation();

        return new
        {
            entraCloud = federation.HostingCloudName,
            source = federation.HostingSource,
            tokenExchangeAudience = federation.TokenExchangeAudience,
            federatedClouds = DataverseCredentialResolver.FederatedCloudsFor(federation.HostingCloud)
                .Select(DataverseClouds.GetName)
                .ToArray(),
            federationConfigured = federation.IsComplete
        };
    }

    /// <summary>
    /// The managed identity application is created once and shared, because MSAL caches the
    /// managed identity token inside it. It is only rebuilt if the configured IDs or the
    /// audience change.
    /// </summary>
    private FederatedDataverseCredential GetFederatedCredential(string clientId, string managedIdentityClientId, string tokenExchangeAudience)
    {
        lock (federatedLock)
        {
            if (federatedCredential == null ||
                !string.Equals(federatedCredential.ClientId, clientId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(federatedCredential.ManagedIdentityClientId, managedIdentityClientId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(federatedCredential.TokenExchangeAudience, tokenExchangeAudience.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                federatedCredential = new FederatedDataverseCredential(clientId, managedIdentityClientId, tokenExchangeAudience, logger);
            }

            return federatedCredential;
        }
    }

    private static X509Certificate2 LoadCertificate(DataverseEnvironmentCredentials credentials)
    {
        byte[] raw = Convert.FromBase64String(credentials.CertificateBase64!);

        return X509CertificateLoader.LoadPkcs12(raw, credentials.CertificatePassword);
    }
}

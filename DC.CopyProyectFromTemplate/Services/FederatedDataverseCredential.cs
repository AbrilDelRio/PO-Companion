using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.AppConfig;

namespace DC.CopyProyectFromTemplate.Services;

/// <summary>
/// Option C, federated path. The Function signs in as the "Sync Project Function" registration
/// and proves it with a token of its user-assigned managed identity instead of a client secret;
/// the registration trusts that identity through a federated credential. The registration and
/// the identity live in the same Entra instance as the Function (global, US Government or China),
/// and the token exchange audience is that instance's one. Built once and shared: MSAL caches the
/// managed identity token internally.
/// </summary>
public sealed class FederatedDataverseCredential
{
    private readonly IManagedIdentityApplication managedIdentity;
    private readonly ILogger logger;

    public FederatedDataverseCredential(string clientId, string managedIdentityClientId, string tokenExchangeAudience, ILogger logger)
    {
        ClientId = clientId;
        ManagedIdentityClientId = managedIdentityClientId;
        TokenExchangeAudience = tokenExchangeAudience.Trim().TrimEnd('/');
        // MSAL takes the audience as a scope: the resource plus "/.default".
        TokenExchangeScope = TokenExchangeAudience + "/.default";
        this.logger = logger;

        managedIdentity = ManagedIdentityApplicationBuilder
            .Create(ManagedIdentityId.WithUserAssignedClientId(managedIdentityClientId))
            .Build();
    }

    public string ClientId { get; }

    public string ManagedIdentityClientId { get; }

    /// <summary>api://AzureADTokenExchange, or the US Government / China variant.</summary>
    public string TokenExchangeAudience { get; }

    public string TokenExchangeScope { get; }

    /// <summary>One per customer tenant; the caller caches it per host, like the secret-based client.</summary>
    public IConfidentialClientApplication CreateClient(string authorityHost, string customerTenantId)
    {
        return ConfidentialClientApplicationBuilder
            .Create(ClientId)
            .WithAuthority(new Uri(authorityHost + customerTenantId), false)
            .WithClientAssertion(GetAssertionAsync)
            .Build();
    }

    private async Task<string> GetAssertionAsync(AssertionRequestOptions options)
    {
        try
        {
            AuthenticationResult result = await managedIdentity
                .AcquireTokenForManagedIdentity(TokenExchangeScope)
                .ExecuteAsync(options.CancellationToken)
                .ConfigureAwait(false);

            return result.AccessToken;
        }
        catch (Exception ex)
        {
            string aadsts = ActivityDiagnostics.FindAadstsCode(ex) ?? "(none)";

            // Without this the failure would surface only as the exchange's own error, hiding that
            // the managed identity step is the one that broke.
            logger.LogError(
                ex,
                "The managed identity {ManagedIdentityClientId} could not obtain a token for {Audience}, so the " +
                "federated sign-in of {ClientId} cannot proceed. Usually the identity is not assigned to the " +
                "Function App, the configured client ID is not its own, or the audience belongs to another " +
                "cloud than the one the Function runs in. AADSTS: {Aadsts}. Cause: {Cause}",
                ManagedIdentityClientId,
                TokenExchangeScope,
                ClientId,
                aadsts,
                ActivityDiagnostics.Flatten(ex));

            throw new InvalidOperationException(
                $"The managed identity '{ManagedIdentityClientId}' could not obtain a token for " +
                $"'{TokenExchangeScope}'. AADSTS: {aadsts}. Cause: {ActivityDiagnostics.Flatten(ex)}",
                ex);
        }
    }
}

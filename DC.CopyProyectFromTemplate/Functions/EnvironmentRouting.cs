using System.Text;
using DC.CopyProyectFromTemplate.Models;
using DC.CopyProyectFromTemplate.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace DC.CopyProyectFromTemplate.Functions;

internal static class EnvironmentRouting
{
    /// <summary>
    /// Validates the routing fields and resolves the credential path BEFORE any work is
    /// scheduled. When the caller named an environment that cannot be served, the request is
    /// rejected with InvalidDataException (a 400 in plain text): it never falls back to the
    /// default environment. Returns null only when the caller sent none of the fields, i.e. the
    /// legacy route.
    /// </summary>
    public static async Task<ResolvedDataverseEnvironment?> ResolveAsync(
        DataverseEnvironmentTarget target,
        DataverseTokenProvider tokenProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!DataverseEnvironmentResolver.TryResolve(target, out ResolvedDataverseEnvironment? environment, out string? error))
        {
            throw new InvalidDataException(error!);
        }

        if (environment == null)
        {
            logger.LogInformation(
                "The request did not declare environmentApiUrl / environmentUrl / cloud: taking the legacy " +
                "route against the configured environment.");

            return null;
        }

        DataverseCredentialPlan plan;

        try
        {
            plan = await tokenProvider.ResolveAsync(environment, cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(
                "Request for {Host} ({Cloud}) rejected: {Reason} CorrelationId: {CorrelationId}.",
                environment.Host,
                environment.CloudName,
                ex.Message,
                environment.CorrelationId);

            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Credential resolution for {Host} ({Cloud}) failed. CorrelationId: {CorrelationId}. Cause: {Cause}",
                environment.Host,
                environment.CloudName,
                environment.CorrelationId,
                ActivityDiagnostics.Flatten(ex));

            throw;
        }

        logger.LogInformation(
            "Credential path for {Host} ({Cloud}): {CredentialPath}. Tenant: {TenantId} ({TenantSource}). " +
            "Authority: {Authority}. ClientId: {ClientId}. CorrelationId: {CorrelationId}.",
            environment.Host,
            environment.CloudName,
            plan.PathName,
            plan.TenantId,
            plan.TenantSource,
            plan.Authority,
            plan.ClientId,
            environment.CorrelationId);

        return environment;
    }

    /// <summary>Reads a non-file multipart section as trimmed UTF-8 text.</summary>
    public static async Task<string> ReadFieldValueAsync(MultipartSection section, CancellationToken cancellationToken)
    {
        using StreamReader fieldReader = new StreamReader(
            section.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);

        return (await fieldReader.ReadToEndAsync(cancellationToken)).Trim();
    }
}

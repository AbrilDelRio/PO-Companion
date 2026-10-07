using System.Net;
using System.Text.Json.Nodes;
using DC.CopyProyectFromTemplate.Services;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace DC.CopyProyectFromTemplate.Functions;

/// <summary>
/// The 202 an MPP import, Excel import or project copy answers with:
/// { id, statusUri, accepted, environment, cloud, correlationId }, plus Location (the same
/// statusUri) and Retry-After.
///
/// statusUri is the address of the request with the instance id appended, answered by a GET on
/// the same function (ImportStatusResponder), so the function key that started the operation also
/// follows it. It replaces the Durable management URLs this body used
/// to carry (statusQueryGetUri, terminatePostUri, purgeHistoryDeleteUri and the rest): all of
/// them embed the Durable system key, so every caller holding a function key could terminate,
/// rewind or purge any orchestration. The request's query string, and with it a ?code= key,
/// is never copied into statusUri.
/// </summary>
internal static class AcceptedResponseBuilder
{
    public static async Task<HttpResponseData> CreateAsync(
        HttpRequestData request,
        string instanceId,
        ResolvedDataverseEnvironment? environment,
        string correlationId,
        ILogger logger,
        ResolvedDataverseEnvironment? destination = null)
    {
        string? statusUri = null;

        try
        {
            statusUri = ImportStatusBody.StatusUri(
                request.Url,
                instanceId,
                FirstHeader(request, "Forwarded"),
                FirstHeader(request, "X-Forwarded-Proto"),
                FirstHeader(request, "X-Forwarded-Host")).AbsoluteUri;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
        {
            // Never fail the request over the status link: the orchestration is already queued.
            logger.LogError(
                ex,
                "The status link could not be built for instance {InstanceId}; answering without it. " +
                "CorrelationId: {CorrelationId}.",
                instanceId,
                correlationId);
        }

        JsonObject body = new JsonObject
        {
            ["id"] = instanceId,
            ["statusUri"] = statusUri,
            ["accepted"] = true,
            ["environment"] = environment?.EnvironmentUrl,
            ["cloud"] = environment?.CloudName,
            ["correlationId"] = correlationId
        };

        if (destination != null)
        {
            // Cross-environment copy: "environment" is the source, this is where the copy goes.
            body["targetEnvironment"] = destination.EnvironmentUrl;
        }

        HttpResponseData response = request.CreateResponse(HttpStatusCode.Accepted);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");

        if (statusUri != null)
        {
            response.Headers.Add("Location", statusUri);
            response.Headers.Add("Retry-After", "10");
        }

        await response.WriteStringAsync(body.ToJsonString());

        return response;
    }

    private static string? FirstHeader(HttpRequestData request, string name)
    {
        return request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;
    }
}

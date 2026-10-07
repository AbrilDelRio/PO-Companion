using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace DC.CopyProyectFromTemplate.Functions;

/// <summary>
/// GET {route}/{instanceId} on the endpoint that started an MPP import, Excel import or project
/// copy: the state of that operation. The 202 of those operations links here through statusUri.
///
/// The status is answered by the same function that takes the POST, not by a function of its own,
/// because function keys are scoped to one function: the add-in holds the key of
/// ImportMppToProject and the PCF one key per endpoint, so a separate status function would answer
/// 401 to every caller. It replaces the Durable status URL the 202 used to hand out, whose
/// management URLs embed the Durable system key. This one only reads, and only the operations of
/// the endpoint's own orchestrator. It answers 202 while the operation runs and 200 once it
/// finished, the same convention as the Durable status URL.
/// </summary>
internal static class ImportStatusResponder
{
    /// <summary>
    /// The response for a status check, or for an address that carries an id on a POST; null when
    /// the request is a POST to the endpoint itself, which the caller then handles as a new operation.
    /// </summary>
    public static async Task<HttpResponseData?> TryHandleAsync(
        HttpRequestData request,
        string route,
        string orchestratorName,
        DurableTaskClient durableClient,
        ILogger logger)
    {
        // Read from the path rather than the binding data, so the answer does not depend on how
        // the host fills in an optional route value.
        string? instanceId = ImportStatusBody.InstanceIdFromPath(request.Url, route);
        bool isGet = string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase);

        if (!isGet)
        {
            return instanceId == null
                ? null
                : await CreateTextResponse(
                    request,
                    HttpStatusCode.NotFound,
                    "A POST starts a new operation and its address takes no import id.");
        }

        if (!ImportStatusBody.IsValidInstanceId(instanceId))
        {
            return await CreateTextResponse(request, HttpStatusCode.BadRequest, "The import id is missing or not valid.");
        }

        OrchestrationMetadata? metadata = await durableClient.GetInstanceAsync(
            instanceId,
            getInputsAndOutputs: true,
            request.FunctionContext.CancellationToken);

        // An operation started by another endpoint gets the same answer as an unknown id: each
        // key only reads what its own endpoint started.
        if (metadata == null || !ImportStatusBody.BelongsTo(metadata, orchestratorName))
        {
            logger.LogInformation(
                "Status requested for instance {InstanceId}, which {Orchestrator} did not start or whose history is gone.",
                instanceId,
                orchestratorName);

            return await CreateTextResponse(
                request,
                HttpStatusCode.NotFound,
                $"No import with id '{instanceId}' was found. Its history may have been purged.");
        }

        bool finished = ImportStatusBody.IsFinished(metadata.RuntimeStatus);
        JsonObject body = ImportStatusBody.Build(metadata);

        HttpResponseData response = request.CreateResponse(finished ? HttpStatusCode.OK : HttpStatusCode.Accepted);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");

        if (!finished)
        {
            response.Headers.Add("Retry-After", "10");
        }

        await response.WriteStringAsync(body.ToJsonString());

        return response;
    }

    private static async Task<HttpResponseData> CreateTextResponse(HttpRequestData request, HttpStatusCode statusCode, string message)
    {
        HttpResponseData response = request.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        await response.WriteStringAsync(message);
        return response;
    }
}

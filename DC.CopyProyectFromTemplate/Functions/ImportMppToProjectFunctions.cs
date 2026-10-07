using System.Net;
using DC.CopyProyectFromTemplate.Models;
using DC.CopyProyectFromTemplate.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace DC.CopyProyectFromTemplate.Functions;

public sealed class ImportMppToProjectFunctions
{
    private const string EndpointRoute = "ImportMppToProject";
    private const string OrchestratorFunctionName = "DC_ImportMppToProject_Orchestrator";
    private const string ActivityFunctionName = "DC_ImportMppToProject_Activity";

    private readonly MppBlobStorage blobStorage;
    private readonly ImportMppToProjectService importService;
    private readonly DataverseTokenProvider tokenProvider;
    private readonly TenantAccessGuard accessGuard;
    private readonly ILogger<ImportMppToProjectFunctions> logger;

    public ImportMppToProjectFunctions(MppBlobStorage blobStorage, ImportMppToProjectService importService, DataverseTokenProvider tokenProvider, TenantAccessGuard accessGuard, ILogger<ImportMppToProjectFunctions> logger)
    {
        this.blobStorage = blobStorage;
        this.importService = importService;
        this.tokenProvider = tokenProvider;
        this.accessGuard = accessGuard;
        this.logger = logger;
    }

    /// <summary>
    /// POST starts an MPP import; GET ImportMppToProject/{instanceId} answers the status of one,
    /// in this same function so the key that started the import can follow it.
    /// </summary>
    [Function("DC_ImportMppToProject_HttpStart")]
    public async Task<HttpResponseData> HttpStart([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = EndpointRoute + "/{instanceId?}")] HttpRequestData request, [DurableClient] DurableTaskClient durableClient)
    {
        HttpResponseData? statusResponse = await ImportStatusResponder.TryHandleAsync(request, EndpointRoute, OrchestratorFunctionName, durableClient, logger);

        if (statusResponse != null)
        {
            return statusResponse;
        }

        MppStoredFile? storedFile = null;

        try
        {
            MultipartInput input = await ReadMultipartInputAsync(request, request.FunctionContext.CancellationToken);

            storedFile = input.StoredFile;

            await accessGuard.EnsureAllowedAsync(request, input.Environment, request.FunctionContext.CancellationToken);

            string correlationId = input.Environment?.CorrelationId ?? Guid.NewGuid().ToString("D");

            ImportMppRequest orchestrationInput = new ImportMppRequest
            {
                ImportId = Guid.NewGuid(),
                TargetProjectId = input.TargetProjectId,
                ContainerName = storedFile.ContainerName,
                BlobName = storedFile.BlobName,
                OriginalFileName = storedFile.OriginalFileName,
                FileSizeBytes = storedFile.SizeBytes,
                Environment = input.Environment == null
                    ? null
                    : DataverseEnvironmentResolver.ToTarget(input.Environment),
                ResourceMap = input.ResourceMap
            };

            string instanceId = await durableClient.ScheduleNewOrchestrationInstanceAsync(OrchestratorFunctionName,orchestrationInput);

            if (input.ResourceMap != null)
            {
                logger.LogInformation(
                    "MPP import orchestration {InstanceId} carries a resourceMap with {DecisionCount} decisions. " +
                    "CorrelationId: {CorrelationId}.",
                    instanceId,
                    input.ResourceMap.Count,
                    correlationId);
            }

            logger.LogInformation(
                "Started MPP import orchestration {InstanceId} for project {TargetProjectId} on environment " +
                "{Environment} ({Cloud}). ImportId: {ImportId}. CorrelationId: {CorrelationId}.",
                instanceId,
                input.TargetProjectId,
                input.Environment?.EnvironmentUrl ?? "(legacy: configured environment)",
                input.Environment?.CloudName ?? "legacy",
                orchestrationInput.ImportId,
                correlationId);

            return await AcceptedResponseBuilder.CreateAsync(request, instanceId, input.Environment, correlationId, logger);
        }
        catch (TenantAccessDeniedException ex)
        {
            if (storedFile != null)
            {
                await blobStorage.DeleteIfExistsAsync(storedFile.ContainerName, storedFile.BlobName);
            }

            return await CreateErrorResponse(request, HttpStatusCode.Forbidden, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            if (storedFile != null)
            {
                await blobStorage.DeleteIfExistsAsync(storedFile.ContainerName, storedFile.BlobName);
            }

            return await CreateErrorResponse(request, HttpStatusCode.BadRequest, ex.Message);
        }
        catch (Exception)
        {
            if (storedFile != null)
            {
                await blobStorage.DeleteIfExistsAsync(storedFile.ContainerName, storedFile.BlobName);
            }

            throw;
        }
    }

    [Function(OrchestratorFunctionName)]
    public static async Task<ImportMppResult> RunOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ImportMppRequest input = context.GetInput<ImportMppRequest>() ?? throw new InvalidOperationException("The MPP import input is missing.");

        return await context.CallActivityAsync<ImportMppResult>(ActivityFunctionName, input);
    }

    [Function(ActivityFunctionName)]
    public async Task<ImportMppResult> RunActivity([ActivityTrigger] ImportMppRequest input, CancellationToken cancellationToken)
    {
        try
        {
            return await importService.ExecuteAsync(input, cancellationToken);
        }
        catch (Exception ex)
        {
            // The host only records its own FunctionInvocationException wrapper, so the real
            // exception is flattened into the message to guarantee it reaches the log stream.
            logger.LogError(
                ex,
                "MPP import activity failed. ImportId: {ImportId}. TargetProjectId: {TargetProjectId}. " +
                "Environment: {Environment} ({Cloud}). CorrelationId: {CorrelationId}. Cause: {Cause}",
                input.ImportId,
                input.TargetProjectId,
                input.Environment?.EnvironmentUrl ?? "(legacy: configured environment)",
                input.Environment?.Cloud ?? "legacy",
                input.Environment?.CorrelationId ?? "(none)",
                ActivityDiagnostics.Flatten(ex));

            throw;
        }
    }

    private async Task<MultipartInput> ReadMultipartInputAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        string? contentType = request.Headers.TryGetValues("Content-Type", out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

        if (!MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? mediaType) || !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Content-Type must be multipart/form-data with targetProjectId and file fields.");
        }

        string? boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new InvalidDataException("The multipart boundary is missing.");
        }

        Guid? targetProjectId = TryReadTargetProjectIdFromQuery(request.Url.Query);
        MppStoredFile? storedFile = null;

        DataverseEnvironmentTarget environmentTarget = new DataverseEnvironmentTarget();
        List<ResourceMapEntry>? resourceMap = null;

        MultipartReader reader = new MultipartReader(boundary, request.Body)
        {
            BodyLengthLimit = blobStorage.MaxUploadBytes,
            HeadersCountLimit = 32,
            HeadersLengthLimit = 32 * 1024
        };

        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(cancellationToken)) != null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition,out ContentDispositionHeaderValue? disposition) || !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? string.Empty;

            bool isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;

            // resourceMap is a plain field, but it is also read when a client sends it as a file
            // part: dropping it would silently fall back to name matching, which the contract forbids.
            if (isFile && string.Equals(fieldName, "resourceMap", StringComparison.OrdinalIgnoreCase))
            {
                isFile = false;
            }

            if (isFile)
            {
                if (!string.Equals(fieldName, "file", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (storedFile != null)
                {
                    throw new InvalidDataException("Only one MPP file can be uploaded per request.");
                }

                string originalFileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value ?? "project.mpp";

                originalFileName = Path.GetFileName(originalFileName);

                if (!string.Equals(Path.GetExtension(originalFileName), ".mpp", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The uploaded file must have the .mpp extension.");
                }

                storedFile = await blobStorage.UploadAsync(section.Body, originalFileName, cancellationToken);

                continue;
            }

            string value = await EnvironmentRouting.ReadFieldValueAsync(section, cancellationToken);

            switch (fieldName.ToLowerInvariant())
            {
                case "targetprojectid":
                    if (!Guid.TryParse(value.Trim('{', '}'), out Guid parsedProjectId) || parsedProjectId == Guid.Empty)
                    {
                        throw new InvalidDataException("targetProjectId must contain a valid project GUID.");
                    }

                    targetProjectId = parsedProjectId;
                    break;

                case "environmenturl":
                    environmentTarget.EnvironmentUrl = value;
                    break;

                case "environmentapiurl":
                    environmentTarget.EnvironmentApiUrl = value;
                    break;

                case "cloud":
                    environmentTarget.Cloud = value;
                    break;

                case "correlationid":
                    environmentTarget.CorrelationId = value;
                    break;

                case "resourcemap":
                    if (resourceMap != null)
                    {
                        throw new InvalidDataException("The resourceMap field can only be sent once per request.");
                    }

                    if (!ResourceMapParser.TryParse(value, out List<ResourceMapEntry>? parsedMap, out string? mapError))
                    {
                        throw new InvalidDataException(mapError!);
                    }

                    resourceMap = parsedMap;
                    break;
            }
        }

        if (!targetProjectId.HasValue || targetProjectId.Value == Guid.Empty)
        {
            throw new InvalidDataException("The targetProjectId form field is required.");
        }

        if (storedFile == null)
        {
            throw new InvalidDataException("The file form field containing an MPP file is required.");
        }

        ResolvedDataverseEnvironment? environment = await EnvironmentRouting.ResolveAsync(environmentTarget, tokenProvider, logger, cancellationToken);

        return new MultipartInput(targetProjectId.Value, storedFile, environment, resourceMap);
    }

    private static Guid? TryReadTargetProjectIdFromQuery(string query)
    {
        if (!QueryHelpers.ParseQuery(query).TryGetValue("targetProjectId", out Microsoft.Extensions.Primitives.StringValues value))
        {
            return null;
        }

        return Guid.TryParse(value.FirstOrDefault()?.Trim('{', '}'), out Guid projectId) && projectId != Guid.Empty ? projectId : null;
    }

    private static async Task<HttpResponseData> CreateErrorResponse(HttpRequestData request,HttpStatusCode statusCode, string message)
    {
        HttpResponseData response = request.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        await response.WriteStringAsync(message);
        return response;
    }

    private sealed record MultipartInput(
        Guid TargetProjectId,
        MppStoredFile StoredFile,
        ResolvedDataverseEnvironment? Environment,
        List<ResourceMapEntry>? ResourceMap);
}

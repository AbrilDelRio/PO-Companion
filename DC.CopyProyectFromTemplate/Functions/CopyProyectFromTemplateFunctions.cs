using System.Net;
using System.Text.Json;
using DC.CopyProyectFromTemplate.Models;
using DC.CopyProyectFromTemplate.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace DC.CopyProyectFromTemplate.Functions;

public sealed class CopyProyectFromTemplateFunctions
{
    private const string EndpointRoute = "CopyProyectFromTemplate";
    private const string OrchestratorFunctionName = "DC_CopyProyectFromTemplate_Orchestrator";
    private const string ActivityFunctionName = "DC_CopyProyectFromTemplate_Activity";

    private readonly CopyProyectFromTemplateService copyService;
    private readonly DataverseTokenProvider tokenProvider;
    private readonly DestinationEnvironmentCatalog destinationCatalog;
    private readonly ILogger<CopyProyectFromTemplateFunctions> logger;

    public CopyProyectFromTemplateFunctions(CopyProyectFromTemplateService copyService,DataverseTokenProvider tokenProvider,DestinationEnvironmentCatalog destinationCatalog,ILogger<CopyProyectFromTemplateFunctions> logger)
    {
        this.copyService = copyService;
        this.tokenProvider = tokenProvider;
        this.destinationCatalog = destinationCatalog;
        this.logger = logger;
    }

    /// <summary>
    /// POST starts a copy; GET CopyProyectFromTemplate/{instanceId} answers the status of one,
    /// in this same function so the key that started the copy can follow it.
    /// </summary>
    [Function("DC_CopyProjectFromTemplate_HttpStart")]
    public async Task<HttpResponseData> HttpStart([HttpTrigger(AuthorizationLevel.Function,"get","post",Route = EndpointRoute + "/{instanceId?}")]HttpRequestData request,[DurableClient] DurableTaskClient durableClient)
    {
        HttpResponseData? statusResponse = await ImportStatusResponder.TryHandleAsync(request, EndpointRoute, OrchestratorFunctionName, durableClient, logger);

        if (statusResponse != null)
        {
            return statusResponse;
        }

        CopyProjectRequest? input;

        try
        {
            input = await request.ReadFromJsonAsync<CopyProjectRequest>();
        }
        catch (JsonException)
        {
            return await CreateBadRequestResponse(request,"The request body must be valid JSON.");
        }

        if (input == null || input.SourceProjectId == Guid.Empty)
        {
            return await CreateBadRequestResponse(request,"The request body must contain a valid sourceProjectId GUID.");
        }

        if (!input.IsCrossEnvironment)
        {
            // Same-environment copy into an existing project (the original contract).
            if (input.TargetProjectId == Guid.Empty)
            {
                return await CreateBadRequestResponse(request,"The request body must contain valid sourceProjectId and targetProjectId GUIDs, or a targetEnvironment.");
            }

            if (input.SourceProjectId == input.TargetProjectId)
            {
                return await CreateBadRequestResponse(request,"sourceProjectId and targetProjectId must be different projects.");
            }
        }

        // One correlation id for the whole request: the source and the destination connection log it.
        string correlationId = string.IsNullOrWhiteSpace(input.CorrelationId)
            ? Guid.NewGuid().ToString("D")
            : input.CorrelationId.Trim();

        DataverseEnvironmentTarget environmentTarget = new DataverseEnvironmentTarget
        {
            EnvironmentUrl = input.EnvironmentUrl ?? string.Empty,
            EnvironmentApiUrl = input.EnvironmentApiUrl ?? string.Empty,
            Cloud = input.Cloud ?? string.Empty,
            CorrelationId = correlationId
        };

        ResolvedDataverseEnvironment? environment;

        try
        {
            environment = await EnvironmentRouting.ResolveAsync(environmentTarget, tokenProvider, logger, request.FunctionContext.CancellationToken);
        }
        catch (InvalidDataException ex)
        {
            return await CreateBadRequestResponse(request, ex.Message);
        }

        ResolvedDataverseEnvironment? destination = null;

        if (input.IsCrossEnvironment)
        {
            if (environment == null)
            {
                return await CreateBadRequestResponse(request,"A copy to another environment needs the source environment: send environmentUrl, environmentApiUrl and cloud.");
            }

            TargetEnvironmentRequest targetEnvironment = input.TargetEnvironment!;

            DataverseEnvironmentTarget destinationTarget = new DataverseEnvironmentTarget
            {
                EnvironmentUrl = targetEnvironment.EnvironmentUrl ?? string.Empty,
                EnvironmentApiUrl = targetEnvironment.EnvironmentApiUrl ?? string.Empty,
                Cloud = targetEnvironment.Cloud ?? string.Empty,
                CorrelationId = correlationId
            };

            if (destinationTarget.IsEmpty)
            {
                return await CreateBadRequestResponse(request,"targetEnvironment must contain environmentUrl, environmentApiUrl and cloud.");
            }

            try
            {
                // Resolving the credential path of the destination now means an environment this
                // Function cannot write to is a 400 here, not a failure minutes into the copy.
                destination = await EnvironmentRouting.ResolveAsync(destinationTarget, tokenProvider, logger, request.FunctionContext.CancellationToken);
            }
            catch (InvalidDataException ex)
            {
                return await CreateBadRequestResponse(request, ex.Message);
            }

            if (destination == null)
            {
                return await CreateBadRequestResponse(request,"targetEnvironment could not be resolved.");
            }

            if (string.Equals(environment.Host, destination.Host, StringComparison.OrdinalIgnoreCase))
            {
                return await CreateBadRequestResponse(request,"The destination environment must be different from the source environment.");
            }

            bool destinationAllowed;

            try
            {
                destinationAllowed = await destinationCatalog.IsAllowedAsync(environment, destination.Host, request.FunctionContext.CancellationToken);
            }
            catch (EnvironmentDiscoveryException ex)
            {
                // Fail closed: if the list of valid destinations cannot be read, nothing is copied.
                logger.LogWarning(ex, "Copy to {Host} refused: the allowed destinations could not be verified. CorrelationId: {CorrelationId}.", destination.Host, correlationId);

                return await CreateBadRequestResponse(request, $"The destination environment could not be verified: {ex.Message}");
            }

            if (!destinationAllowed)
            {
                logger.LogWarning(
                    "Copy to {Host} rejected: the host is not in the {Setting} allow list. CorrelationId: {CorrelationId}.",
                    destination.Host,
                    DestinationEnvironmentCatalog.SettingName,
                    correlationId);

                return await CreateBadRequestResponse(request,$"The environment '{destination.Host}' is not an allowed destination.");
            }

            input.DestinationEnvironment = DataverseEnvironmentResolver.ToTarget(destination);
        }

        input.Environment = environment == null ? null : DataverseEnvironmentResolver.ToTarget(environment);

        string instanceId = await durableClient.ScheduleNewOrchestrationInstanceAsync(OrchestratorFunctionName,input);

        logger.LogInformation(
            "Started CopyProyectFromTemplate orchestration {InstanceId} from source project " +
            "{SourceProjectId} to target project {TargetProjectId} on environment {Environment} ({Cloud}); " +
            "destination environment: {Destination}. CorrelationId: {CorrelationId}.",
            instanceId,
            input.SourceProjectId,
            input.IsCrossEnvironment ? "(new project)" : input.TargetProjectId.ToString(),
            environment?.EnvironmentUrl ?? "(legacy: configured environment)",
            environment?.CloudName ?? "legacy",
            destination?.EnvironmentUrl ?? "(same environment)",
            correlationId);

        return await AcceptedResponseBuilder.CreateAsync(request, instanceId, environment, correlationId, logger, destination);
    }

    [Function(OrchestratorFunctionName)]
    public static async Task<CopyProjectResult> RunOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        CopyProjectRequest input = context.GetInput<CopyProjectRequest>() ?? throw new InvalidOperationException("The orchestration input is missing.");

        return await context.CallActivityAsync<CopyProjectResult>(ActivityFunctionName, input);
    }

    [Function(ActivityFunctionName)]
    public CopyProjectResult RunActivity([ActivityTrigger] CopyProjectRequest input)
    {
        try
        {
            if (input.DestinationEnvironment != null)
            {
                return copyService.ExecuteToEnvironment(input.SourceProjectId, input.Environment, input.DestinationEnvironment);
            }

            return copyService.Execute(input.SourceProjectId, input.TargetProjectId, input.Environment);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Copy activity failed. SourceProjectId: {SourceProjectId}. TargetProjectId: {TargetProjectId}. " +
                "Destination: {Destination}. Environment: {Environment} ({Cloud}). CorrelationId: {CorrelationId}. Cause: {Cause}",
                input.SourceProjectId,
                input.TargetProjectId,
                input.DestinationEnvironment?.EnvironmentUrl ?? "(same environment)",
                input.Environment?.EnvironmentUrl ?? "(legacy: configured environment)",
                input.Environment?.Cloud ?? "legacy",
                input.Environment?.CorrelationId ?? "(none)",
                ActivityDiagnostics.Flatten(ex));

            throw;
        }
    }

    private static async Task<HttpResponseData> CreateBadRequestResponse(HttpRequestData request,string message)
    {
        HttpResponseData response = request.CreateResponse(HttpStatusCode.BadRequest);
        response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        await response.WriteStringAsync(message);
        return response;
    }
}

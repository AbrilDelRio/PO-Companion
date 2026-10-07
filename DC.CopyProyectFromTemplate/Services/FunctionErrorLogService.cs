using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;

namespace DC.CopyProyectFromTemplate.Services;

public sealed class FunctionErrorLogService
{
    private const string ErrorLogCreatedKey = "DC.FunctionErrorLogCreated";

    private readonly DataverseConnectionFactory connectionFactory;
    private readonly ILogger<FunctionErrorLogService> logger;

    public FunctionErrorLogService(
        DataverseConnectionFactory connectionFactory,
        ILogger<FunctionErrorLogService> logger)
    {
        this.connectionFactory = connectionFactory;
        this.logger = logger;
    }

    public void TryCreateLog(
        string functionName,
        string stage,
        Exception exception,
        Guid? targetProjectId = null,
        Guid? sourceProjectId = null)
    {
        if (HasErrorLogCreated(exception))
        {
            return;
        }

        Exception rootException = exception.GetBaseException();
        string description =
            $"Function: {functionName} | " +
            $"Stage: {stage} | " +
            $"UTC: {DateTimeOffset.UtcNow:O} | " +
            $"Error: {rootException.GetType().Name}: {rootException.Message}";

        if (description.Length > 3900)
        {
            description = description[..3900];
        }

        try
        {
            using ServiceClient serviceClient = connectionFactory.CreateClient();
            IOrganizationService service = serviceClient;
            Helper helper = new Helper(service, connectionFactory.DataverseUrl);
            helper.createLog(
                description,
                false,
                targetProjectId,
                sourceProjectId);

            exception.Data[ErrorLogCreatedKey] = true;
        }
        catch (Exception logException)
        {
            logger.LogError(
                logException,
                "The error log could not be created for function {FunctionName} at stage {Stage}.",
                functionName,
                stage);
        }
    }

    private static bool HasErrorLogCreated(Exception exception)
    {
        Exception? current = exception;
        while (current != null)
        {
            if (current.Data.Contains(ErrorLogCreatedKey))
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }
}

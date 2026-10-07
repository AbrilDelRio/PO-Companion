using DC.CopyProyectFromTemplate.Models;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;

namespace DC.CopyProyectFromTemplate.Services;

public sealed class ImportExcelToProjectService
{
    private readonly MppBlobStorage blobStorage;
    private readonly ExcelProjectReader excelReader;
    private readonly ExcelPlanDataverseImporter dataverseImporter;
    private readonly DataverseConnectionFactory connectionFactory;
    private readonly ILogger<ImportExcelToProjectService> logger;
    private Helper? helper;

    public ImportExcelToProjectService(MppBlobStorage blobStorage,ExcelProjectReader excelReader,ExcelPlanDataverseImporter dataverseImporter,DataverseConnectionFactory connectionFactory, ILogger<ImportExcelToProjectService> logger)
    {
        this.blobStorage = blobStorage;
        this.excelReader = excelReader;
        this.dataverseImporter = dataverseImporter;
        this.connectionFactory = connectionFactory;
        this.logger = logger;
    }

    public async Task<ImportExcelResult> ExecuteAsync(ImportExcelRequest input, CancellationToken cancellationToken)
    {
        ValidateInput(input);
        string? localFilePath = null;

        // Declared out here and disposed in the finally, never with a using inside the try: a using
        // declaration disposes the connection as the try block exits, before the catch runs, so
        // TryWriteFailureLog was writing through a closed client and no failure row ever appeared.
        ServiceClient? serviceClient = null;

        try
        {
            ResolvedDataverseEnvironment? environment = DataverseConnectionFactory.Resolve(input.Environment);

            logger.LogInformation(
                "Excel import {ImportId} connecting to {Environment} ({Cloud}) for project {TargetProjectId}. " +
                "CorrelationId: {CorrelationId}.",
                input.ImportId,
                environment?.EnvironmentUrl ?? "(legacy: configured environment)",
                environment?.CloudName ?? "legacy",
                input.TargetProjectId,
                environment?.CorrelationId ?? "(none)");

            serviceClient = connectionFactory.CreateClient(environment);
            IOrganizationService service = serviceClient;
            helper = new Helper(service, connectionFactory.ResolveUrl(environment));
            localFilePath = await blobStorage.DownloadExcelToTemporaryFileAsync(input.ContainerName, input.BlobName, cancellationToken);

            ExcelPlanReadResult readResult = excelReader.Read(localFilePath);
            ExcelPlanImportOutcome outcome = dataverseImporter.Import(readResult.Plan,input.ImportId,input.TargetProjectId,environment,cancellationToken);

            int inactiveTasks = readResult.Plan.Tasks.Count(task => !task.IsActive);
            int dependenciesSkipped = readResult.Plan.SkippedExternalDependencies;

            string warning = readResult.TruncatedPredecessorRows > 0
                ? $" {readResult.TruncatedPredecessorRows:N0} predecessor cells were already " +
                  "truncated with an ellipsis in the Excel source; every complete predecessor " +
                  "visible in those cells was imported."
                : string.Empty;

            return new ImportExcelResult
            {
                ImportId = input.ImportId,
                TargetProjectId = input.TargetProjectId,
                FileName = input.OriginalFileName,
                Completed = true,
                TasksCreated = outcome.TasksCreated,
                DependenciesCreated = outcome.DependenciesCreated,
                InactiveTasksImported = inactiveTasks,
                NamesTruncated = outcome.NamesTruncated,
                DependenciesSkipped = dependenciesSkipped,
                TruncatedPredecessorRows = readResult.TruncatedPredecessorRows,
                Message =
                    $"Imported {outcome.TasksCreated:N0} tasks and " +
                    $"{outcome.DependenciesCreated:N0} dependencies into project " +
                    $"'{outcome.ProjectName}'.{warning}"
            };
        }
        catch (Exception ex)
        {
            TryWriteFailureLog(ex);
            throw;
        }
        finally
        {
            serviceClient?.Dispose();
            MppBlobStorage.TryDeleteLocalFile(localFilePath);
            try
            {
                await blobStorage.DeleteIfExistsAsync(input.ContainerName, input.BlobName, CancellationToken.None);
            }
            catch (Exception)
            {
                // Swallowed on purpose: failing to delete the temporary blob must not overwrite
                // the real import exception.
            }
        }
    }

    /// <summary>
    /// Writes the failure row without ever masking the original exception.
    /// </summary>
    private void TryWriteFailureLog(Exception exception)
    {
        if (helper == null)
        {
            logger.LogWarning(
                "The failure could not be written to mfd_pocopyprojectlog: the Dataverse connection " +
                "was never established.");
            return;
        }

        try
        {
            helper.createLog($"{exception.Message}", false, null, null, true);
        }
        catch (Exception logException)
        {
            logger.LogWarning(
                logException,
                "The failure row could not be written to mfd_pocopyprojectlog. The original error is " +
                "the one reported by the activity.");
        }
    }

    private static void ValidateInput(ImportExcelRequest input)
    {
        if (input.ImportId == Guid.Empty)
        {
            throw new ArgumentException("Import ID cannot be empty.", nameof(input));
        }

        if (input.TargetProjectId == Guid.Empty)
        {
            throw new ArgumentException("Target project ID cannot be empty.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.ContainerName) ||
            string.IsNullOrWhiteSpace(input.BlobName))
        {
            throw new ArgumentException("The uploaded Excel file reference is missing.", nameof(input));
        }
    }
}

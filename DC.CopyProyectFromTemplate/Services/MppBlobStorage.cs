using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;

namespace DC.CopyProyectFromTemplate.Services;

public sealed class MppBlobStorage
{
    private const long DefaultMaxUploadBytes = 220_000_000;

    private readonly BlobServiceClient blobServiceClient;
    private readonly string containerName;

    public MppBlobStorage(IConfiguration configuration)
    {
        blobServiceClient = CreateBlobServiceClient(configuration);
        containerName = configuration["MppImportContainer"]?.Trim()
            ?? "mpp-imports";

        if (!long.TryParse(configuration["MppMaxUploadBytes"], out long configuredLimit) ||
            configuredLimit <= 0)
        {
            configuredLimit = DefaultMaxUploadBytes;
        }

        MaxUploadBytes = Math.Min(configuredLimit, DefaultMaxUploadBytes);
    }

    public long MaxUploadBytes { get; }

    public string ContainerName => containerName;

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        BlobContainerClient container = blobServiceClient.GetBlobContainerClient(containerName);

        await container.CreateIfNotExistsAsync(
            PublicAccessType.None,
            cancellationToken: cancellationToken);

        await container.GetPropertiesAsync(cancellationToken: cancellationToken);
    }

    public async Task<MppStoredFile> UploadAsync(
        Stream content,
        string originalFileName,
        CancellationToken cancellationToken)
    {
        return await UploadProjectFileAsync(
            content,
            originalFileName,
            ".mpp",
            "application/vnd.ms-project",
            "MPP",
            cancellationToken);
    }

    public async Task<MppStoredFile> UploadExcelAsync(
        Stream content,
        string originalFileName,
        CancellationToken cancellationToken)
    {
        return await UploadProjectFileAsync(
            content,
            originalFileName,
            ".xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "Excel",
            cancellationToken);
    }

    private async Task<MppStoredFile> UploadProjectFileAsync(
        Stream content,
        string originalFileName,
        string extension,
        string contentType,
        string fileType,
        CancellationToken cancellationToken)
    {
        BlobContainerClient container = blobServiceClient.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(
            PublicAccessType.None,
            cancellationToken: cancellationToken);

        string blobName = $"pending/{Guid.NewGuid():N}/source{extension}";
        BlobClient blob = container.GetBlobClient(blobName);

        try
        {
            await blob.UploadAsync(
                content,
                new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentType = contentType
                    },
                    Metadata = new Dictionary<string, string>
                    {
                        ["originalFileName"] = Uri.EscapeDataString(originalFileName)
                    }
                },
                cancellationToken);

            BlobProperties properties = (await blob.GetPropertiesAsync(
                cancellationToken: cancellationToken)).Value;

            if (properties.ContentLength <= 0)
            {
                throw new InvalidDataException($"The uploaded {fileType} file is empty.");
            }

            if (properties.ContentLength > MaxUploadBytes)
            {
                throw new InvalidDataException(
                    $"The uploaded {fileType} file is {properties.ContentLength:N0} bytes. " +
                    $"The configured maximum is {MaxUploadBytes:N0} bytes.");
            }

            return new MppStoredFile(
                containerName,
                blobName,
                originalFileName,
                properties.ContentLength);
        }
        catch (Exception)
        {
            await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            throw;
        }
    }

    public async Task<string> DownloadToTemporaryFileAsync(
        string requestedContainerName,
        string blobName,
        CancellationToken cancellationToken)
    {
        return await DownloadToTemporaryFileAsync(
            requestedContainerName,
            blobName,
            ".mpp",
            cancellationToken);
    }

    public async Task<string> DownloadExcelToTemporaryFileAsync(
        string requestedContainerName,
        string blobName,
        CancellationToken cancellationToken)
    {
        return await DownloadToTemporaryFileAsync(
            requestedContainerName,
            blobName,
            ".xlsx",
            cancellationToken);
    }

    private async Task<string> DownloadToTemporaryFileAsync(
        string requestedContainerName,
        string blobName,
        string extension,
        CancellationToken cancellationToken)
    {
        EnsureExpectedContainer(requestedContainerName);

        string tempPath = Path.Combine(
            Path.GetTempPath(),
            $"project-import-{Guid.NewGuid():N}{extension}");

        BlobClient blob = blobServiceClient
            .GetBlobContainerClient(containerName)
            .GetBlobClient(blobName);

        try
        {
            await using FileStream destination = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await blob.DownloadToAsync(destination, cancellationToken);
            return tempPath;
        }
        catch (Exception)
        {
            TryDeleteLocalFile(tempPath);
            throw;
        }
    }

    public async Task DeleteIfExistsAsync(
        string requestedContainerName,
        string blobName,
        CancellationToken cancellationToken = default)
    {
        EnsureExpectedContainer(requestedContainerName);

        await blobServiceClient
            .GetBlobContainerClient(containerName)
            .GetBlobClient(blobName)
            .DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    public static void TryDeleteLocalFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    private void EnsureExpectedContainer(string requestedContainerName)
    {
        if (!string.Equals(
                requestedContainerName,
                containerName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The MPP blob container is invalid.");
        }
    }

    private static BlobServiceClient CreateBlobServiceClient(IConfiguration configuration)
    {
        string? connectionString = configuration["MppStorageConnectionString"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = configuration["AzureWebJobsStorage"];
        }

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return new BlobServiceClient(connectionString);
        }

        string? blobServiceUri = GetFirstSetting(
            configuration,
            "MppStorage:blobServiceUri",
            "AzureWebJobsStorage:blobServiceUri",
            "MppStorage__blobServiceUri",
            "AzureWebJobsStorage__blobServiceUri");

        if (string.IsNullOrWhiteSpace(blobServiceUri))
        {
            string? accountName = GetFirstSetting(
                configuration,
                "MppStorage:accountName",
                "AzureWebJobsStorage:accountName",
                "MppStorage__accountName",
                "AzureWebJobsStorage__accountName");

            if (!string.IsNullOrWhiteSpace(accountName))
            {
                blobServiceUri = $"https://{accountName}.blob.core.windows.net";
            }
        }

        if (string.IsNullOrWhiteSpace(blobServiceUri))
        {
            throw new InvalidOperationException(
                "MPP storage is not configured. Configure MppStorageConnectionString, " +
                "AzureWebJobsStorage, or an identity-based blob service URI/account name.");
        }

        string? managedIdentityClientId = GetFirstSetting(
            configuration,
            "MppStorage:clientId",
            "AzureWebJobsStorage:clientId",
            "MppStorage__clientId",
            "AzureWebJobsStorage__clientId",
            "AZURE_CLIENT_ID");

        DefaultAzureCredentialOptions credentialOptions = new()
        {
            ExcludeInteractiveBrowserCredential = true
        };

        if (!string.IsNullOrWhiteSpace(managedIdentityClientId))
        {
            credentialOptions.ManagedIdentityClientId = managedIdentityClientId;
        }

        DefaultAzureCredential credential = new DefaultAzureCredential(credentialOptions);

        return new BlobServiceClient(new Uri(blobServiceUri), credential);
    }

    private static string? GetFirstSetting(
        IConfiguration configuration,
        params string[] names)
    {
        foreach (string name in names)
        {
            string? value = configuration[name];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}

public sealed record MppStoredFile(
    string ContainerName,
    string BlobName,
    string OriginalFileName,
    long SizeBytes);

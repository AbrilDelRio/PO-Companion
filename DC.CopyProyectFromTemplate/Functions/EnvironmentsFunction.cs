using DC.CopyProyectFromTemplate.Models;
using DC.CopyProyectFromTemplate.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace DC.CopyProyectFromTemplate.Functions;

/// <summary>
/// GET Environments: the destination environments the PCF offers in its "Destination environment"
/// dropdown, as { "mode", "environments": [ { "name", "url" } ] }.
///
/// Optional query: environmentUrl, environmentApiUrl and cloud (the environment the control runs in) and
/// refresh=1 (skip the cache). The control sends them; they are required when automatic discovery is on,
/// because that is how the Function knows which credentials to read the tenant's environments with.
/// </summary>
public sealed class EnvironmentsFunction
{
    private readonly DestinationEnvironmentCatalog catalog;
    private readonly DataverseTokenProvider tokenProvider;
    private readonly ILogger<EnvironmentsFunction> logger;

    public EnvironmentsFunction(
        DestinationEnvironmentCatalog catalog,
        DataverseTokenProvider tokenProvider,
        ILogger<EnvironmentsFunction> logger)
    {
        this.catalog = catalog;
        this.tokenProvider = tokenProvider;
        this.logger = logger;
    }

    [Function("DC_Environments")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "Environments")] HttpRequestData request)
    {
        CancellationToken cancellationToken = request.FunctionContext.CancellationToken;
        var query = QueryHelpers.ParseQuery(request.Url.Query);

        string environmentUrl = query.TryGetValue("environmentUrl", out var urlValues) ? urlValues.ToString() : string.Empty;
        string environmentApiUrl = query.TryGetValue("environmentApiUrl", out var apiValues) ? apiValues.ToString() : string.Empty;
        string cloud = query.TryGetValue("cloud", out var cloudValues) ? cloudValues.ToString() : string.Empty;
        bool refresh = query.TryGetValue("refresh", out var refreshValues) &&
                       (refreshValues.ToString() == "1" || string.Equals(refreshValues.ToString(), "true", StringComparison.OrdinalIgnoreCase));

        ResolvedDataverseEnvironment? source = null;

        try
        {
            DataverseEnvironmentTarget target = new DataverseEnvironmentTarget
            {
                EnvironmentUrl = environmentUrl,
                EnvironmentApiUrl = environmentApiUrl,
                Cloud = cloud
            };

            if (!target.IsEmpty)
            {
                source = await EnvironmentRouting.ResolveAsync(target, tokenProvider, logger, cancellationToken);
            }

            CatalogView view = await catalog.GetAsync(source, refresh, cancellationToken);

            HttpResponseData ok = request.CreateResponse(System.Net.HttpStatusCode.OK);

            await ok.WriteAsJsonAsync(new
            {
                mode = view.Mode,
                environments = view.Environments.Select(environment => new { name = environment.Name, url = environment.Url })
            });

            return ok;
        }
        catch (InvalidDataException ex)
        {
            return await Fail(request, System.Net.HttpStatusCode.BadRequest, ex.Message);
        }
        catch (EnvironmentDiscoveryException ex)
        {
            logger.LogWarning(ex, "The list of destination environments could not be obtained: {Reason}", ex.Message);

            return await Fail(request, System.Net.HttpStatusCode.BadGateway, ex.Message);
        }
    }

    private static async Task<HttpResponseData> Fail(HttpRequestData request, System.Net.HttpStatusCode status, string message)
    {
        HttpResponseData response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        await response.WriteStringAsync(message);
        return response;
    }
}

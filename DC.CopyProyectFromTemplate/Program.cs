using DC.CopyProyectFromTemplate.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

FunctionsApplicationBuilder builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddSingleton<DataverseEnvironmentRegistry>();
builder.Services.AddSingleton<DataverseTenantDiscovery>();
builder.Services.AddSingleton<DataverseTokenProvider>();
builder.Services.AddSingleton<DataverseConnectionFactory>();
builder.Services.AddSingleton<DiscoveryTokenFactory>(services =>
{
    DataverseTokenProvider tokenProvider = services.GetRequiredService<DataverseTokenProvider>();

    return async (source, scope, cancellationToken) =>
    {
        DataverseCredentialPlan plan = await tokenProvider.ResolveAsync(source, cancellationToken);
        string accessToken = await tokenProvider.GetAccessTokenForScopeAsync(plan, scope, cancellationToken);

        return new DiscoveryToken(accessToken, plan.TenantId);
    };
});
builder.Services.AddSingleton<PowerPlatformEnvironmentDiscovery>();
builder.Services.AddSingleton<DestinationEnvironmentCatalog>();
builder.Services.AddSingleton<MppBlobStorage>();
builder.Services.AddTransient<CopyProyectFromTemplateService>();
builder.Services.AddTransient<MppProjectReader>();
builder.Services.AddTransient<ImportMppToProjectService>();
builder.Services.AddTransient<ExcelProjectReader>();
builder.Services.AddTransient<ExcelPlanDataverseImporter>();
builder.Services.AddTransient<ImportExcelToProjectService>();

IHost host = builder.Build();
host.Run();

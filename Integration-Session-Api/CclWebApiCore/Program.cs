
using CenterForCreativeLeadership.Shared.HealthCheck;
using CenterForCreativeLeadership.Shared.Logging.Rapid7;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

//Add a custom appsetting.json file
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                     .AddJsonFile("secrets/appsettings.secrets.json", optional: true, reloadOnChange: true);

// Rapid7 InsightOps via the HTTP webhook sink. Replaces Serilog.Sinks.InsightOps, whose TCP token
// client silently dropped isolated low-frequency events — this service is plug-in triggered and very
// low traffic, exactly the profile that lost logs. Also drops the R7Insight -> log4net CVE-pin chore.
// Wires the sink, routes ILogger<T> through Serilog, applies the safe MinimumLevel baseline, and
// emits the standard startup line. Token still comes from Logentries:Token.
builder.AddCclRapid7Logging("integration-session-api");

builder.Services.AddSystemWebAdapters();
builder.Services.AddHttpForwarder();

//don't add app insights to local (Development) builds
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddApplicationInsightsTelemetry(m =>
        m.ConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"]);
    builder.Services.AddApplicationInsightsKubernetesEnricher();
}

builder.Services.AddControllers().AddNewtonsoftJson();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CCL health checks — exposes /_liveness (process up, used by Helm probes) and /_health (full).
// This service has no SQL/Redis; its backing store is Dynamics CRM reached per-request via
// ServiceClient, so no dependency probe is registered here (a CRM auth ping would false-fail
// readiness on transient token blips). /_liveness reports process health only.
builder.Services.AddCclHealthChecks(m => m);

var app = builder.Build();

// Local dev only: serve under the APIM route prefix so URLs match the deployed shape.
// Runtime check (NOT #if DEBUG, which misbehaves in containers). Deployed envs are unaffected —
// the nginx ingress strips the prefix, so nothing here runs outside Development.
if (app.Environment.IsDevelopment())
{
    app.UsePathBase("/integration-session");
}

app.UseCclHealthChecks();

app.UseSwagger();
app.UseSwaggerUI();

//app.UseHttpsRedirection();

//app.UseAuthorization();
app.UseSystemWebAdapters();

app.MapControllers();

//Configure the proxy if necessary
//app.MapForwarder("/{**catch-all}", app.Configuration["ProxyTo"]).Add(static builder => ((RouteEndpointBuilder)builder).Order = int.MaxValue);

try
{
    app.Run();
}
finally
{
    // Flush the webhook sink's background batching worker so in-flight events aren't lost on shutdown.
    Log.CloseAndFlush();
}

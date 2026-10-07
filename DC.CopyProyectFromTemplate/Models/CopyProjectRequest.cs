using System.Text.Json.Serialization;

namespace DC.CopyProyectFromTemplate.Models;

public sealed class CopyProjectRequest
{
    [JsonPropertyName("sourceProjectId")]
    public Guid SourceProjectId { get; init; }

    /// <summary>
    /// Legacy same-environment copy: the project that receives the copy. Not used (and not
    /// required) when <see cref="TargetEnvironment"/> is sent: the project is then created in the
    /// destination environment with a new id.
    /// </summary>
    [JsonPropertyName("targetProjectId")]
    public Guid TargetProjectId { get; init; }

    /// <summary>Source environment: the one the PCF runs in.</summary>
    [JsonPropertyName("environmentUrl")]
    public string? EnvironmentUrl { get; init; }

    [JsonPropertyName("environmentApiUrl")]
    public string? EnvironmentApiUrl { get; init; }

    [JsonPropertyName("cloud")]
    public string? Cloud { get; init; }

    /// <summary>Correlation id chosen by the caller; echoed back and used in every log line.</summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    /// <summary>Destination environment of a cross-environment copy (as sent by the PCF).</summary>
    [JsonPropertyName("targetEnvironment")]
    public TargetEnvironmentRequest? TargetEnvironment { get; init; }

    /// <summary>Set by the HTTP entry point once the three source fields above are validated.</summary>
    [JsonPropertyName("environment")]
    public DataverseEnvironmentTarget? Environment { get; set; }

    /// <summary>Set by the HTTP entry point once <see cref="TargetEnvironment"/> is validated.</summary>
    [JsonPropertyName("destinationEnvironment")]
    public DataverseEnvironmentTarget? DestinationEnvironment { get; set; }

    [JsonIgnore]
    public bool IsCrossEnvironment => TargetEnvironment != null;
}

/// <summary>The destination environment exactly as the caller declared it.</summary>
public sealed class TargetEnvironmentRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("environmentUrl")]
    public string? EnvironmentUrl { get; init; }

    [JsonPropertyName("environmentApiUrl")]
    public string? EnvironmentApiUrl { get; init; }

    [JsonPropertyName("cloud")]
    public string? Cloud { get; init; }
}

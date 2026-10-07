using System.Text.Json.Serialization;

namespace DC.CopyProyectFromTemplate.Models;

/// <summary>
/// The Dataverse environment to write to, as declared by the caller (the VSTO add-in) through
/// the environmentApiUrl / environmentUrl / cloud multipart fields. It travels inside the
/// Durable orchestration input, so it must stay serializable.
/// </summary>
public sealed class DataverseEnvironmentTarget
{
    [JsonPropertyName("environmentUrl")]
    public string EnvironmentUrl { get; set; } = string.Empty;

    [JsonPropertyName("environmentApiUrl")]
    public string EnvironmentApiUrl { get; set; } = string.Empty;

    [JsonPropertyName("cloud")]
    public string Cloud { get; set; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(EnvironmentUrl) &&
        string.IsNullOrWhiteSpace(EnvironmentApiUrl) &&
        string.IsNullOrWhiteSpace(Cloud);
}

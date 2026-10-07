using System.Text.Json.Serialization;

namespace DC.CopyProyectFromTemplate.Models;

public sealed class CopyProjectResult
{
    [JsonPropertyName("sourceProjectId")]
    public Guid SourceProjectId { get; init; }

    /// <summary>The project that received the copy. In a cross-environment copy: the NEW project.</summary>
    [JsonPropertyName("targetProjectId")]
    public Guid TargetProjectId { get; init; }

    [JsonPropertyName("targetEnvironment")]
    public string? TargetEnvironment { get; init; }

    [JsonPropertyName("completed")]
    public bool Completed { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>References that could not be mapped to the destination and were left out.</summary>
    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; init; } = new List<string>();
}

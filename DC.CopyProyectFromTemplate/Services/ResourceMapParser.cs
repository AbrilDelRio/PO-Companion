using System.Text.Json;
using System.Text.Json.Serialization;
using DC.CopyProyectFromTemplate.Models;

namespace DC.CopyProyectFromTemplate.Services;

/// <summary>
/// Parses and validates the resourceMap multipart field. A malformed map is rejected with a 400
/// (approved 2026-09-10): ignoring it would be a silent failure, and falling back to name
/// matching would contradict the decisions the user took in the add-in.
/// </summary>
public static class ResourceMapParser
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static bool TryParse(string? json, out List<ResourceMapEntry>? map, out string? error)
    {
        map = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The resourceMap field is empty. Send a JSON array of decisions, or omit the field.";
            return false;
        }

        List<ResourceMapEntry?>? entries;

        try
        {
            entries = JsonSerializer.Deserialize<List<ResourceMapEntry?>>(json, Options);
        }
        catch (JsonException ex)
        {
            error = $"The resourceMap field is not a valid JSON array of decisions: {ex.Message}";
            return false;
        }

        if (entries == null)
        {
            error = "The resourceMap field must be a JSON array, not null.";
            return false;
        }

        Dictionary<int, ResourceMapEntry> decisions = new Dictionary<int, ResourceMapEntry>();

        for (int index = 0; index < entries.Count; index++)
        {
            ResourceMapEntry? entry = entries[index];

            if (entry == null)
            {
                error = $"resourceMap entry {index} is null.";
                return false;
            }

            string label = string.IsNullOrWhiteSpace(entry.ProjectResourceName)
                ? $"entry {index}"
                : $"entry {index} ('{entry.ProjectResourceName}')";

            if (entry.ProjectResourceUniqueId <= 0)
            {
                error = $"resourceMap {label} has no valid projectResourceUniqueId.";
                return false;
            }

            if (entry.BookableResourceId == Guid.Empty)
            {
                error = $"resourceMap {label} has no valid bookableResourceId.";
                return false;
            }

            if (entry.BookableResourceCategoryId == Guid.Empty)
            {
                error = $"resourceMap {label} has an empty bookableResourceCategoryId; send a role ID or omit the property.";
                return false;
            }

            if (decisions.TryGetValue(entry.ProjectResourceUniqueId, out ResourceMapEntry? previous))
            {
                if (previous.BookableResourceId != entry.BookableResourceId ||
                    previous.BookableResourceCategoryId != entry.BookableResourceCategoryId)
                {
                    error =
                        $"resourceMap lists projectResourceUniqueId {entry.ProjectResourceUniqueId} twice with different " +
                        $"decisions ({previous.BookableResourceId} and {entry.BookableResourceId}). Send one decision per resource.";
                    return false;
                }

                // The same decision twice is harmless: it collapses into one.
                continue;
            }

            decisions[entry.ProjectResourceUniqueId] = entry;
        }

        map = decisions.Values.ToList();
        return true;
    }
}

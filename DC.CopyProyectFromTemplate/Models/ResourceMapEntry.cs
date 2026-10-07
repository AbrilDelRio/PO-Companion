using System.Text.Json.Serialization;

namespace DC.CopyProyectFromTemplate.Models;

/// <summary>
/// One decision the user took in the add-in "Check resources": which bookable resource a
/// Project resource is. Arrives in the multipart field resourceMap and travels through Durable.
/// </summary>
public sealed class ResourceMapEntry
{
    /// <summary>The resource UniqueID in the .mpp; assignments in the file reference it. The key.</summary>
    [JsonPropertyName("projectResourceUniqueId")]
    public int ProjectResourceUniqueId { get; set; }

    /// <summary>For logs and messages only. Never matched on.</summary>
    [JsonPropertyName("projectResourceName")]
    public string? ProjectResourceName { get; set; }

    /// <summary>An active bookableresource of the target environment, chosen or created by the add-in.</summary>
    [JsonPropertyName("bookableResourceId")]
    public Guid BookableResourceId { get; set; }

    /// <summary>
    /// The role (bookableresourcecategory) of the project team member, decided in the add-in.
    /// Only used when the team member has to be created. Optional for older add-ins: without it
    /// the bookable resource own category is used, if it has one.
    /// </summary>
    [JsonPropertyName("bookableResourceCategoryId")]
    public Guid? BookableResourceCategoryId { get; set; }
}

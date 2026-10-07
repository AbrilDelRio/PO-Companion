using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace DC.CopyProyectFromTemplate;

internal sealed class DataverseEntitySchema
{
    private readonly Dictionary<string, AttributeMetadata> attributes;

    public DataverseEntitySchema(IOrganizationService service, string logicalName)
    {
        RetrieveEntityResponse response = (RetrieveEntityResponse)service.Execute(
            new RetrieveEntityRequest
            {
                LogicalName = logicalName,
                EntityFilters = EntityFilters.Attributes,
                RetrieveAsIfPublished = true
            });

        attributes = response.EntityMetadata.Attributes
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.LogicalName))
            .ToDictionary(
                attribute => attribute.LogicalName,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the table has the column, for reads. Asking Dataverse for a column the table lacks
    /// fails the whole request, and not every environment has the same schedule columns.
    /// </summary>
    public bool Has(string attributeName)
    {
        return attributes.TryGetValue(attributeName, out AttributeMetadata? metadata) &&
               metadata.IsValidForRead != false;
    }

    public bool CanCreate(string attributeName)
    {
        return attributes.TryGetValue(attributeName, out AttributeMetadata? metadata) &&
               metadata.IsValidForCreate == true &&
               metadata.AttributeOf == null;
    }

    public bool SetString(
        Entity entity,
        string attributeName,
        string? value,
        out bool truncated)
    {
        truncated = false;

        if (string.IsNullOrWhiteSpace(value) ||
            !TryGetCreatable(attributeName, out AttributeMetadata? metadata))
        {
            return false;
        }

        if (metadata is not StringAttributeMetadata &&
            metadata is not MemoAttributeMetadata)
        {
            return false;
        }

        int? maxLength = metadata switch
        {
            StringAttributeMetadata text => text.MaxLength,
            MemoAttributeMetadata memo => memo.MaxLength,
            _ => null
        };

        string normalized = value.Trim();
        if (maxLength.HasValue && maxLength.Value > 0 && normalized.Length > maxLength.Value)
        {
            normalized = normalized[..maxLength.Value];
            truncated = true;
        }

        entity[metadata.LogicalName] = normalized;
        return true;
    }

    public bool SetValue(Entity entity, string attributeName, object? value)
    {
        if (value == null || !TryGetCreatable(attributeName, out AttributeMetadata? metadata))
        {
            return false;
        }

        AttributeMetadata creatableMetadata = metadata!;
        object converted = ConvertValue(creatableMetadata, value);
        entity[creatableMetadata.LogicalName] = converted;
        return true;
    }

    private bool TryGetCreatable(
        string attributeName,
        out AttributeMetadata? metadata)
    {
        if (!attributes.TryGetValue(attributeName, out metadata) ||
            metadata.IsValidForCreate != true ||
            metadata.AttributeOf != null)
        {
            metadata = null;
            return false;
        }

        return true;
    }

    private static object ConvertValue(AttributeMetadata metadata, object value)
    {
        return metadata.AttributeType switch
        {
            AttributeTypeCode.Integer => Convert.ToInt32(value),
            AttributeTypeCode.BigInt => Convert.ToInt64(value),
            AttributeTypeCode.Decimal => Convert.ToDecimal(value),
            AttributeTypeCode.Double => Convert.ToDouble(value),
            AttributeTypeCode.Money => value is Money
                ? value
                : new Money(Convert.ToDecimal(value)),
            AttributeTypeCode.Boolean => Convert.ToBoolean(value),
            AttributeTypeCode.Picklist or
            AttributeTypeCode.State or
            AttributeTypeCode.Status => value is OptionSetValue
                ? value
                : new OptionSetValue(Convert.ToInt32(value)),
            AttributeTypeCode.DateTime => value is DateTime dateTime
                ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                : value,
            AttributeTypeCode.String or
            AttributeTypeCode.Memo => Convert.ToString(
                value,
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value
        };
    }
}

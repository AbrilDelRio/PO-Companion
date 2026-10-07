using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Metadata.Query;

namespace DC.CopyProyectTemplateV4Plugin
{
    internal static class MetadataCache
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<string, CacheEntry> CreatableFields = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        public static string[] GetCreatableFields(IOrganizationService service, string logicalName)
        {
            CacheEntry cached;
            if (CreatableFields.TryGetValue(logicalName, out cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
            {
                return cached.Fields;
            }

            string[] fields = Load(service, logicalName);
            CreatableFields[logicalName] = new CacheEntry(fields, DateTime.UtcNow.Add(Ttl));
            return fields;
        }

        private sealed class CacheEntry
        {
            public CacheEntry(string[] fields, DateTime expiresAtUtc)
            {
                Fields = fields;
                ExpiresAtUtc = expiresAtUtc;
            }

            public string[] Fields { get; }
            public DateTime ExpiresAtUtc { get; }
        }

        private static string[] Load(IOrganizationService service, string logicalName)
        {
            try
            {
                return LoadViaMetadataChanges(service, logicalName);
            }
            catch (Exception)
            {
                return LoadViaRetrieveEntity(service, logicalName);
            }
        }

        private static string[] LoadViaMetadataChanges(IOrganizationService service, string logicalName)
        {
            EntityQueryExpression entityQuery = new EntityQueryExpression
            {
                Criteria =
                {
                    Conditions =
                    {
                        new MetadataConditionExpression(
                            "LogicalName", MetadataConditionOperator.Equals, logicalName)
                    }
                },
                Properties = new MetadataPropertiesExpression("Attributes") { AllProperties = false },
                AttributeQuery = new AttributeQueryExpression
                {
                    Properties = new MetadataPropertiesExpression(
                        "LogicalName", "IsValidForCreate", "AttributeOf", "AttributeType")
                    {
                        AllProperties = false
                    }
                }
            };

            RetrieveMetadataChangesResponse response = (RetrieveMetadataChangesResponse)service.Execute(
                new RetrieveMetadataChangesRequest { Query = entityQuery });

            if (response.EntityMetadata == null || response.EntityMetadata.Count == 0)
            {
                throw new InvalidPluginExecutionException($"Metadata for table '{logicalName}' was not found.");
            }

            return Filter(response.EntityMetadata[0].Attributes);
        }

        private static string[] LoadViaRetrieveEntity(IOrganizationService service, string logicalName)
        {
            RetrieveEntityRequest request = new RetrieveEntityRequest
            {
                LogicalName = logicalName,
                EntityFilters = EntityFilters.Attributes
            };

            RetrieveEntityResponse response = (RetrieveEntityResponse)service.Execute(request);

            return Filter(response.EntityMetadata.Attributes);
        }

        private static string[] Filter(IEnumerable<AttributeMetadata> attributes)
        {
            List<string> fields = new List<string>();

            foreach (AttributeMetadata attribute in attributes)
            {
                if (attribute.IsValidForCreate == true
                    && attribute.AttributeOf == null
                    && attribute.AttributeType != AttributeTypeCode.Uniqueidentifier
                    && !string.IsNullOrWhiteSpace(attribute.LogicalName))
                {
                    fields.Add(attribute.LogicalName);
                }
            }

            return fields.ToArray();
        }
    }
}

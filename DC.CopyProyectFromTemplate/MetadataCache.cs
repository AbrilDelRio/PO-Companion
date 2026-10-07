using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Metadata.Query;

namespace DC.CopyProyectFromTemplate
{
    internal static class MetadataCache
    {
        private static readonly ConcurrentDictionary<string, string[]> CreatableFields =
            new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        /// <param name="environmentKey">
        /// Identifies the environment the service is connected to. The columns of a table can differ
        /// between environments, so the cache is per environment when the caller works with several.
        /// </param>
        public static string[] GetCreatableFields(IOrganizationService service, string logicalName, string environmentKey = "")
        {
            string cacheKey = environmentKey + "|" + logicalName;

            string[] cached;
            if (CreatableFields.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }

            string[] fields = Load(service, logicalName);
            CreatableFields[cacheKey] = fields;
            return fields;
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

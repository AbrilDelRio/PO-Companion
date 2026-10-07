using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;

namespace DC.CopyProyectTemplateV4Plugin
{
    internal sealed class BatchWriter
    {
        private const int MaxBatchSize = 25;
        private readonly IOrganizationService service;
        private readonly List<OrganizationRequest> buffer = new List<OrganizationRequest>(MaxBatchSize);
        private int batchNumber;

        public BatchWriter(IOrganizationService service)
        {
            this.service = service;
        }

        public int PendingCount
        {
            get { return buffer.Count; }
        }

        public void Create(Entity record)
        {
            buffer.Add(new CreateRequest { Target = record });

            if (buffer.Count >= MaxBatchSize)
            {
                Flush();
            }
        }

        public void Delete(string logicalName, Guid id)
        {
            buffer.Add(new DeleteRequest { Target = new EntityReference(logicalName, id) });

            if (buffer.Count >= MaxBatchSize)
            {
                Flush();
            }
        }

        public void Flush()
        {
            if (buffer.Count == 0)
            {
                return;
            }

            ExecuteMultipleRequest request = new ExecuteMultipleRequest
            {
                Settings = new ExecuteMultipleSettings
                {
                    // Matches the previous fail fast behaviour of the per record Create calls.
                    ContinueOnError = false,

                    // Ids are generated client side, so the response payload is never read.
                    ReturnResponses = false
                },
                Requests = new OrganizationRequestCollection()
            };

            for (int i = 0; i < buffer.Count; i++)
            {
                request.Requests.Add(buffer[i]);
            }

            buffer.Clear();

            int currentBatch = ++batchNumber;
            string operations = string.Join(", ", request.Requests
                .GroupBy(DescribeRequest)
                .Select(group => $"{group.Key}: {group.Count()}"));
            Stopwatch stopwatch = Stopwatch.StartNew();
            ExecuteMultipleResponse response;

            try
            {
                response = (ExecuteMultipleResponse)service.Execute(request);
            }
            catch (Exception ex)
            {
                throw new InvalidPluginExecutionException(
                    $"ExecuteMultiple batch {currentBatch} ({request.Requests.Count} operations; {operations}) " +
                    $"failed after {stopwatch.Elapsed.TotalSeconds:F1} seconds. " +
                    $"Some operations may already have been committed; reconcile the target before retrying. {ex.Message}", ex);
            }

            if (response.IsFaulted)
            {
                foreach (ExecuteMultipleResponseItem item in response.Responses)
                {
                    if (item.Fault != null)
                    {
                        throw new InvalidPluginExecutionException(
                            $"ExecuteMultiple batch {currentBatch}, request {item.RequestIndex} " +
                            $"({DescribeRequest(request.Requests[item.RequestIndex])}) failed after " +
                            $"{stopwatch.Elapsed.TotalSeconds:F1} seconds: {item.Fault.Message}");
                    }
                }

                throw new InvalidPluginExecutionException("A batch request failed without returning a fault.");
            }
        }

        public void DiscardPending()
        {
            buffer.Clear();
        }

        private static string DescribeRequest(OrganizationRequest request)
        {
            if (request is CreateRequest create)
            {
                return $"Create {create.Target.LogicalName}";
            }

            if (request is DeleteRequest delete)
            {
                return $"Delete {delete.Target.LogicalName}";
            }

            return request.RequestName;
        }
    }
}

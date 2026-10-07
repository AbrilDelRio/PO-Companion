using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;

namespace DC.CopyProyectFromTemplate
{
    internal sealed class BatchWriter
    {
        private const int DefaultMaxBatchSize = 50;

        private readonly IOrganizationService service;
        private readonly int maxBatchSize;
        private readonly List<OrganizationRequest> buffer;

        public BatchWriter(IOrganizationService service, int maxBatchSize = DefaultMaxBatchSize)
        {
            if (maxBatchSize <= 0 || maxBatchSize > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBatchSize), "Batch size must be between 1 and 1000.");
            }

            this.service = service;
            this.maxBatchSize = maxBatchSize;
            buffer = new List<OrganizationRequest>(maxBatchSize);
        }

        public int PendingCount
        {
            get { return buffer.Count; }
        }

        public void Create(Entity record)
        {
            buffer.Add(new CreateRequest { Target = record });

            if (buffer.Count >= maxBatchSize)
            {
                Flush();
            }
        }

        public void Update(Entity record)
        {
            buffer.Add(new UpdateRequest { Target = record });

            if (buffer.Count >= maxBatchSize)
            {
                Flush();
            }
        }

        public void Delete(string logicalName, Guid id)
        {
            buffer.Add(new DeleteRequest { Target = new EntityReference(logicalName, id) });

            if (buffer.Count >= maxBatchSize)
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
                    ContinueOnError = false,

                    ReturnResponses = false
                },
                Requests = new OrganizationRequestCollection()
            };

            for (int i = 0; i < buffer.Count; i++)
            {
                request.Requests.Add(buffer[i]);
            }

            buffer.Clear();

            ExecuteMultipleResponse response = (ExecuteMultipleResponse)service.Execute(request);

            if (response.IsFaulted)
            {
                foreach (ExecuteMultipleResponseItem item in response.Responses)
                {
                    if (item.Fault != null)
                    {
                        throw new InvalidPluginExecutionException(
                            $"Batch request {item.RequestIndex} failed: {item.Fault.Message}");
                    }
                }

                throw new InvalidPluginExecutionException("A batch request failed without returning a fault.");
            }
        }

        public void TryFlush()
        {
            try
            {
                Flush();
            }
            catch (Exception)
            {
                buffer.Clear();
            }
        }
    }
}

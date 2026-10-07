using CclCrmProxyCore.Helpers;
using CclWebApi.Models;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Web;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace CclWebApi.Helpers
{
    public abstract class MagentoUpdate
    {
        private CclServiceContext serviceContext;
        public abstract bool Update(UploadResult _result);
        public SessionTrack SessionTrack { get; set; }

        public virtual bool Validate()
        {
            return true;
        }

        protected CclServiceContext ServiceContext
        {
            get
            {
                if(serviceContext == null)
                {
                    serviceContext = new CclServiceContext(this.Service);
                }

                return serviceContext;
            }
        }
        protected IOrganizationService Service { get; set; }

        protected List<Annotation> getNotesForObject(Guid _objectId)
        {
            using (CclServiceContext localContext = new CclServiceContext(this.Service))
            {
                return localContext.AnnotationSet.Where(x => x.ObjectId.Id == _objectId).ToList();
            }
        }

        public static MagentoUpdate construct(IOrganizationService _service, SessionTrack _sessionTrack)
        {
            MagentoUpdate localMagentoUpdate = null;

            if (_sessionTrack != null && _sessionTrack.RecordSource == RecordSource.Magento)
            {
                switch (_sessionTrack.SessionRegistration.TransactionType)
                {
                    case MagentoTransactionType.Transfer:
                        localMagentoUpdate = new MagentoUpdate_Transfer();
                        break;
                    case MagentoTransactionType.Pending:
                        localMagentoUpdate = new MagentoUpdate_Pending();
                        break;
                    case MagentoTransactionType.Cancel:
                        localMagentoUpdate = new MagentoUpdate_Cancel();
                        break;
                }
            }

            if(localMagentoUpdate !=null)
            {
                localMagentoUpdate.Service = _service;
                localMagentoUpdate.SessionTrack = _sessionTrack;
            }

            return localMagentoUpdate;
        }
    }
}
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectTemplateV4Plugin
{
    internal static class QueryHelper
    {
        private const int PageSize = 5000;

        public static List<Entity> RetrieveAll(IOrganizationService service, QueryExpression query)
        {
            query.PageInfo = new PagingInfo
            {
                Count = PageSize,
                PageNumber = 1,
                PagingCookie = null,
                ReturnTotalRecordCount = false
            };

            List<Entity> results = null;

            while (true)
            {
                EntityCollection page = service.RetrieveMultiple(query);

                if (results == null)
                {
                    results = new List<Entity>(page.MoreRecords ? PageSize * 2 : page.Entities.Count);
                }

                results.AddRange(page.Entities);

                if (!page.MoreRecords)
                {
                    return results;
                }

                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }
        }
    }
}

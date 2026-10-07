using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CclCrmProxyCore.Helpers
{
    //bump
    public static class CrmExtensions
    {
        /*public static string GetName(this List<new_axcostcentre> list, EntityReference er)
        {
            string ret = string.Empty;

            if (er == null)
                return ret;

            var cc = list.Where(x => x.Id == er.Id).FirstOrDefault();
            if (cc != null)
                return cc.new_name; 

            return ret;
        }*/

        /// <summary>
        /// Returns string attribute value for specified entity ID
        /// </summary>
        /// <typeparam name="T">Any CRM type that inherits from Entity class</typeparam>
        /// <param name="list">Input list</param>
        /// <param name="er">Guid of entity to search by</param>
        /// <param name="fieldName">Attribute name - e.g. 'new_name'</param>
        /// <returns></returns>
        public static string SagGetAttrValue<T>(this List<T> list, EntityReference er, string fieldName)
        {
            string ret = string.Empty;

            IEnumerable<Entity> crmList = list.Cast<Entity>();

            if (er == null)
                return ret;

            var cc = crmList.Where(x => x.Id == er.Id).FirstOrDefault();
            if (cc != null)
                ret = cc.GetAttributeValue<string>(fieldName); //"new_name"

            return ret;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectTemplateV4Plugin
{
    public class PoConfiguration
    {
        private readonly IOrganizationService service = null;

        public PoConfiguration(IOrganizationService service)
        {
            this.service = service;
        }

        //RETRIEVE THE MAIN CONFIGURATION FROM THE MAIN TABLE
        public Entity getPOConfiguration(string poCodeConfiguration)
        {
            QueryExpression query = new QueryExpression("mfd_pocopyprojectconfiguration")
            {
                ColumnSet = new ColumnSet("mfd_copyresources", "mfd_generaterequirements", "mfd_copytask", "mfd_copyteammembers", "mfd_copydatesmethod", "mfd_templatefield", "mfd_generateprojectmanager"),
                NoLock = true,
                TopCount = 1
            };
            query.Criteria.AddCondition("mfd_code", ConditionOperator.Equal, poCodeConfiguration);
            Entity entityPoConfig = service.RetrieveMultiple(query).Entities.FirstOrDefault();
            return entityPoConfig;
        }

        public string getConfigurationCode(Entity target)
        {
            //Entity poConfiguration = target.GetAttributeValue<Entity>("mfd_poconfiguration");
            string poCodeConfiguration;
            //if (poConfiguration != null)
            //{
                //poCodeConfiguration = poConfiguration.GetAttributeValue<string>("mfd_code");
            //}
            //else
            //{
                poCodeConfiguration = "1000";
            //}

            return poCodeConfiguration;
        }

        //GET THE SELECTED FIELDS TO COPY FROM THE NEW PROJECT TO THE TEMPLATE
        public List<Entity> getProjectFieldsToCopy(Entity entityPoConfig, IOrganizationService service)
        {
            QueryExpression query = new QueryExpression("mfd_pocopyprojectfields")
            {
                ColumnSet = new ColumnSet("mfd_code", "mfd_field"),
                NoLock = true
            };
            query.Criteria.AddCondition("mfd_copyprojectconfiguration", ConditionOperator.Equal, entityPoConfig.Id);
            List<Entity> entityPo = service.RetrieveMultiple(query).Entities.ToList();

            return entityPo;
        }

        //GET THE FIELDS OF THE TASKS
        public string[] getProjectTaskFieldsToCopy(Entity entityPoConfig)
        {
            QueryExpression query = new QueryExpression("mfd_pocopyprojecttaskfield")
            {
                ColumnSet = new ColumnSet("mfd_code", "mfd_field"),
                NoLock = true
            };

            query.Criteria.AddCondition("mfd_copyprojectconfiguration", ConditionOperator.Equal, entityPoConfig.Id);

            List<Entity> entityPoTask = service.RetrieveMultiple(query).Entities.ToList();

            string[] tasksColumns = entityPoTask.Select(x => x.GetAttributeValue<string>("mfd_field")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();

            return tasksColumns;
        }
    }
}

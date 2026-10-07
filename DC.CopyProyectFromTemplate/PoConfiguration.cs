using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DC.CopyProyectFromTemplate
{
    public enum BusinessApplication
    {
        Psa = 1,
        ProjectOperations = 2
    }

    public class PoConfiguration
    {
        private readonly IOrganizationService service;

        public PoConfiguration(IOrganizationService service)
        {
            this.service = service;
        }

        public Entity? getPOConfiguration(string poCodeConfiguration)
        {
            QueryExpression query = new QueryExpression("mfd_pocopyprojectconfiguration")
            {
                ColumnSet = new ColumnSet("mfd_copyresources", "mfd_generaterequirements", "mfd_copytask", "mfd_copyteammembers", "mfd_copydatesmethod", "mfd_templatefield", "mfd_bizapp"),
                NoLock = true,
                TopCount = 1
            };
            query.Criteria.AddCondition("mfd_code", ConditionOperator.Equal, poCodeConfiguration);
            Entity? entityPoConfig = service.RetrieveMultiple(query).Entities.FirstOrDefault();
            return entityPoConfig;
        }

        public BusinessApplication getBusinessApplication(Entity target)
        {
            string poCodeConfiguration = getConfigurationCode(target);
            Entity entityPoConfig = getPOConfiguration(poCodeConfiguration)
                ?? throw new InvalidPluginExecutionException(
                    $"No PO copy configuration was found for code '{poCodeConfiguration}'.");

            OptionSetValue bizApp = entityPoConfig.GetAttributeValue<OptionSetValue>("mfd_bizapp")
                ?? throw new InvalidPluginExecutionException(
                    $"The PO copy configuration '{poCodeConfiguration}' does not contain mfd_bizapp.");

            if (!Enum.IsDefined(typeof(BusinessApplication), bizApp.Value))
            {
                throw new InvalidPluginExecutionException(
                    $"The mfd_bizapp value '{bizApp.Value}' is not supported.");
            }

            return (BusinessApplication)bizApp.Value;
        }

        public string getConfigurationCode(Entity target)
        {
            Entity poConfiguration = target.GetAttributeValue<Entity>("mfd_poconfiguration");
            string poCodeConfiguration;
            if (poConfiguration != null)
            {
                poCodeConfiguration = poConfiguration.GetAttributeValue<string>("mfd_code");
            }
            else
            {
                poCodeConfiguration = "1000";
            }

            return poCodeConfiguration;
        }

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

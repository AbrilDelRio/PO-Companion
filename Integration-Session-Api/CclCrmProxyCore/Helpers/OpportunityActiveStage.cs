using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CclCrmProxyCore.Helpers
{
    public class OpportunityActiveStage
    {
        public OpportunityActiveStage(Guid stageId, int stageStep, string stageName, Guid processId, string processLogicalName)
        {
            StageId = stageId;
            StageStep = stageStep;
            StageName = stageName;
            ProcessId = processId;
            ProcessLogicalName = processLogicalName;
        }
        public Guid StageId { get; set; }
        public int StageStep { get; set; }
        public string StageName { get; set; }
        public Guid ProcessId { get; set; }
        public string ProcessLogicalName { get; set; }

    }
}

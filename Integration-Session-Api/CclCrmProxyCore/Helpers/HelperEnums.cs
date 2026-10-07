using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace CclCrmProxyCore.Helpers
{
    public enum ActionType
    {
        Create = 1,
        Update = 2,
        Delete = 3
    }

    public enum SOType
    {
        WorkBased = 192350001,
        ItemBased = 192350000,
        ServiceMaintenanceBased = 690970002
    }

    public enum Vertical
    {
        Financial = 100000000,
        Health = 100000001,
        HighTech = 100000002,
        Insurance = 100000003,
        NonProfit = 100000004,
        RetailConsumerGoods = 100000005,
        TravelMediaEntertainment = 100000006
    }
}
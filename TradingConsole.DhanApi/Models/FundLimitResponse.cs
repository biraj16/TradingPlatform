// In TradingConsole.DhanApi/Models/FundLimitResponse.cs
using Newtonsoft.Json;

namespace TradingConsole.DhanApi.Models
{
    public class FundLimitResponse
    {
        // Note: The property name matches the typo in the official Dhan API documentation
        [JsonProperty("availabelBalance")]
        public decimal AvailableBalance { get; set; }

        [JsonProperty("utilizedAmount")]
        public decimal UtilizedAmount { get; set; }

        [JsonProperty("collateralAmount")]
        public decimal CollateralAmount { get; set; }

        [JsonProperty("withdrawableBalance")]
        public decimal WithdrawableBalance { get; set; }
    }
}
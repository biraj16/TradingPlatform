using Newtonsoft.Json;

namespace TradingConsole.DhanApi.Models
{
    public class PositionResponse
    {
        // FIX: Properties are now nullable to prevent compiler warnings (CS8618).
        // The JSON deserializer may not always provide a value for every property.
        [JsonProperty("securityId")]
        public string? SecurityId { get; set; }

        [JsonProperty("tradingSymbol")]
        public string? TradingSymbol { get; set; }

        [JsonProperty("exchange")]
        public string? Exchange { get; set; }

        [JsonProperty("productType")]
        public string? ProductType { get; set; }

        [JsonProperty("positionType")]
        public string? PositionType { get; set; }

        [JsonProperty("netQty")]
        public int NetQuantity { get; set; }

        [JsonProperty("buyAvg")]
        public decimal BuyAverage { get; set; }

        [JsonProperty("sellAvg")]
        public decimal SellAverage { get; set; }

        [JsonProperty("buyQty")]
        public int BuyQuantity { get; set; }

        [JsonProperty("sellQty")]
        public int SellQuantity { get; set; }

        [JsonProperty("costPrice")]
        public decimal CostPrice { get; set; }

        [JsonProperty("ltp")]
        public decimal LastTradedPrice { get; set; }

        [JsonProperty("unrealizedProfit")]
        public decimal UnrealizedProfit { get; set; }

        [JsonProperty("realizedProfit")]
        public decimal RealizedProfit { get; set; }
    }
}

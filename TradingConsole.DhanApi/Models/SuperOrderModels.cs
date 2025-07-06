using Newtonsoft.Json;

namespace TradingConsole.DhanApi.Models
{
    // CORRECTED: This model now matches the /v2/super/orders endpoint
    public class SuperOrderRequest
    {
        [JsonProperty("dhanClientId")]
        public string DhanClientId { get; set; }

        [JsonProperty("correlationId", NullValueHandling = NullValueHandling.Ignore)]
        public string? CorrelationId { get; set; }

        [JsonProperty("transactionType")]
        public string TransactionType { get; set; }

        [JsonProperty("exchangeSegment")]
        public string ExchangeSegment { get; set; }

        [JsonProperty("productType")]
        public string ProductType { get; set; }

        [JsonProperty("orderType")]
        public string OrderType { get; set; }

        [JsonProperty("validity")]
        public string Validity { get; set; } = "DAY";

        [JsonProperty("securityId")]
        public string SecurityId { get; set; }

        [JsonProperty("quantity")]
        public int Quantity { get; set; }

        [JsonProperty("price", NullValueHandling = NullValueHandling.Ignore)]
        public decimal? Price { get; set; }

        // --- CORRECTED Field Names ---
        [JsonProperty("targetPrice")]
        public decimal TargetPrice { get; set; }

        [JsonProperty("stopLossPrice")]
        public decimal StopLossPrice { get; set; }

        [JsonProperty("trailingJump", NullValueHandling = NullValueHandling.Ignore)]
        public decimal? TrailingJump { get; set; }
    }

    // CORRECTED: This model now matches the /v2/super/orders/{id} endpoint
    public class ModifySuperOrderRequest
    {
        [JsonProperty("dhanClientId")]
        public string DhanClientId { get; set; }

        [JsonProperty("orderId")]
        public string OrderId { get; set; }

        [JsonProperty("quantity")]
        public int Quantity { get; set; }

        [JsonProperty("price")]
        public decimal Price { get; set; }

        // --- CORRECTED Field Names ---
        [JsonProperty("targetPrice")]
        public decimal TargetPrice { get; set; }

        [JsonProperty("stopLossPrice")]
        public decimal StopLossPrice { get; set; }

        [JsonProperty("trailingJump", NullValueHandling = NullValueHandling.Ignore)]
        public decimal? TrailingJump { get; set; }
    }
}
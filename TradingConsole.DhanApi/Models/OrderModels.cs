using Newtonsoft.Json;

namespace TradingConsole.DhanApi.Models
{
    // This model is for placing a NEW order
    public class OrderRequest
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

        [JsonProperty("triggerPrice", NullValueHandling = NullValueHandling.Ignore)]
        public decimal? TriggerPrice { get; set; }
    }

    // --- ADDED: This model is for MODIFYING an existing order ---
    public class ModifyOrderRequest
    {
        [JsonProperty("dhanClientId")]
        public string DhanClientId { get; set; }

        [JsonProperty("orderId")]
        public string OrderId { get; set; }

        [JsonProperty("orderType")]
        public string OrderType { get; set; }

        [JsonProperty("quantity")]
        public int Quantity { get; set; }

        [JsonProperty("price")]
        public decimal Price { get; set; }

        [JsonProperty("triggerPrice")]
        public decimal TriggerPrice { get; set; }

        [JsonProperty("validity")]
        public string Validity { get; set; } = "DAY";
    }


    public class OrderResponse
    {
        [JsonProperty("orderId")]
        public string? OrderId { get; set; }

        [JsonProperty("orderStatus")]
        public string? OrderStatus { get; set; }
    }

    public class OrderBookEntry
    {
        [JsonProperty("dhanClientId")]
        public string DhanClientId { get; set; }

        [JsonProperty("orderId")]
        public string OrderId { get; set; }

        [JsonProperty("exchangeSegment")]
        public string ExchangeSegment { get; set; }

        [JsonProperty("productType")]
        public string ProductType { get; set; }

        [JsonProperty("orderType")]
        public string OrderType { get; set; }

        [JsonProperty("orderStatus")]
        public string OrderStatus { get; set; }

        [JsonProperty("transactionType")]
        public string TransactionType { get; set; }

        [JsonProperty("securityId")]
        public string SecurityId { get; set; }

        [JsonProperty("tradingSymbol")]
        public string TradingSymbol { get; set; }

        [JsonProperty("quantity")]
        public int Quantity { get; set; }

        [JsonProperty("filledQty")]
        public int FilledQuantity { get; set; }

        [JsonProperty("price")]
        public decimal Price { get; set; }

        [JsonProperty("triggerPrice")]
        public decimal TriggerPrice { get; set; }

        [JsonProperty("averageTradedPrice")]
        public decimal AverageTradedPrice { get; set; }

        [JsonProperty("createTime")]
        public string CreateTime { get; set; }

        [JsonProperty("updateTime")]
        public string UpdateTime { get; set; }

        // --- ADDED: UI helper property to determine if Modify/Cancel buttons should be enabled ---
        [JsonIgnore]
        public bool IsPending => OrderStatus == "PENDING" || OrderStatus == "TRIGGER_PENDING" || OrderStatus == "AMO_RECEIVED";
    }
}

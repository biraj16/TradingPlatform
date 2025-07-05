using Newtonsoft.Json;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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

    // ADDED: New model for placing a SLICE order
    public class SliceOrderRequest
    {
        [JsonProperty("dhanClientId")]
        public string DhanClientId { get; set; }

        [JsonProperty("transactionType")]
        public string TransactionType { get; set; }

        [JsonProperty("exchangeSegment")]
        public string ExchangeSegment { get; set; }

        [JsonProperty("productType")]
        public string ProductType { get; set; }

        [JsonProperty("orderType")]
        public string OrderType { get; set; } // MARKET or LIMIT

        [JsonProperty("securityId")]
        public string SecurityId { get; set; }

        [JsonProperty("totalQuantity")]
        public int TotalQuantity { get; set; }

        [JsonProperty("sliceQuantity")]
        public int SliceQuantity { get; set; }

        [JsonProperty("interval")]
        public int Interval { get; set; }

        [JsonProperty("price", NullValueHandling = NullValueHandling.Ignore)]
        public decimal? Price { get; set; }
    }

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

    public class OrderBookEntry : INotifyPropertyChanged
    {
        private string _orderStatus = string.Empty;
        private int _filledQuantity;

        [JsonProperty("dhanClientId")]
        public string DhanClientId { get; set; } = string.Empty;

        [JsonProperty("orderId")]
        public string OrderId { get; set; } = string.Empty;

        [JsonProperty("exchangeSegment")]
        public string ExchangeSegment { get; set; } = string.Empty;

        [JsonProperty("productType")]
        public string ProductType { get; set; } = string.Empty;

        [JsonProperty("orderType")]
        public string OrderType { get; set; } = string.Empty;

        [JsonProperty("orderStatus")]
        public string OrderStatus
        {
            get => _orderStatus;
            set { _orderStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsPending)); }
        }

        [JsonProperty("transactionType")]
        public string TransactionType { get; set; } = string.Empty;

        [JsonProperty("securityId")]
        public string SecurityId { get; set; } = string.Empty;

        [JsonProperty("tradingSymbol")]
        public string TradingSymbol { get; set; } = string.Empty;

        [JsonProperty("quantity")]
        public int Quantity { get; set; }

        [JsonProperty("filledQty")]
        public int FilledQuantity
        {
            get => _filledQuantity;
            set { _filledQuantity = value; OnPropertyChanged(); }
        }

        [JsonProperty("price")]
        public decimal Price { get; set; }

        [JsonProperty("triggerPrice")]
        public decimal TriggerPrice { get; set; }

        [JsonProperty("averageTradedPrice")]
        public decimal AverageTradedPrice { get; set; }

        [JsonProperty("createTime")]
        public string CreateTime { get; set; } = string.Empty;

        [JsonProperty("updateTime")]
        public string UpdateTime { get; set; } = string.Empty;

        [JsonIgnore]
        public bool IsPending => OrderStatus == "PENDING" || OrderStatus == "TRIGGER_PENDING" || OrderStatus == "AMO_RECEIVED";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

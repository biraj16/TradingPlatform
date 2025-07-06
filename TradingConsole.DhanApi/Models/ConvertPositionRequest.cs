using Newtonsoft.Json;

namespace TradingConsole.DhanApi.Models
{
    public class ConvertPositionRequest
    {
        [JsonProperty("dhanClientId")]
        public string DhanClientId { get; set; }

        [JsonProperty("securityId")]
        public string SecurityId { get; set; }

        [JsonProperty("productType")]
        public string ProductType { get; set; }

        [JsonProperty("convertTo")]
        public string ConvertTo { get; set; }

        [JsonProperty("quantity")]
        public int Quantity { get; set; }
    }
}

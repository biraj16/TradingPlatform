using Newtonsoft.Json;

namespace TradingConsole.DhanApi.Models
{
    public class QuoteResponse
    {
        [JsonProperty("securityId")]
        public string SecurityId { get; set; }

        [JsonProperty("ltp")]
        public decimal Ltp { get; set; }

        [JsonProperty("prev_close")]
        public decimal PreviousClose { get; set; }
    }
}

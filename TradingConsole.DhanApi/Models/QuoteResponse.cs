using Newtonsoft.Json;

namespace TradingConsole.DhanApi.Models
{
    public class QuoteResponse
    {
        // FIX: Property is now nullable to prevent compiler warnings (CS8618).
        [JsonProperty("securityId")]
        public string? SecurityId { get; set; }

        [JsonProperty("ltp")]
        public decimal Ltp { get; set; }

        [JsonProperty("prev_close")]
        public decimal PreviousClose { get; set; }
    }
}

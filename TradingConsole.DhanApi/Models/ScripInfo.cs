using System;

namespace TradingConsole.DhanApi.Models
{
    public class ScripInfo
    {
        public string Segment { get; set; } = string.Empty;
        public string SecurityId { get; set; } = string.Empty;
        public string UnderlyingSecurityId { get; set; } = string.Empty;
        public string TradingSymbol { get; set; } = string.Empty;
        public DateTime? ExpiryDate { get; set; }
        public decimal StrikePrice { get; set; }
        public string OptionType { get; set; } = string.Empty;
        public string InstrumentType { get; set; } = string.Empty;
        public int LotSize { get; set; }

        // --- NEW PROPERTY TO HOLD THE CORRECT SEARCHABLE NAME ---
        public string SemInstrumentName { get; set; } = string.Empty;
    }
}

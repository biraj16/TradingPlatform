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
        public string SemInstrumentName { get; set; } = string.Empty;

        // --- FIX: Added the missing property for the underlying symbol's name ---
        public string UnderlyingSymbol { get; set; } = string.Empty;
    }
}


namespace TradingConsole.DhanApi.Models.WebSocket
{
    // Represents the common 8-byte header for all incoming WebSocket packets
    public class MarketFeedHeader
    {
        public byte FeedCode { get; set; }
        public int MessageLength { get; set; }
        public int Timestamp { get; set; }
        // FIX: Property is now nullable to prevent compiler warnings (CS8618).
        public string? SecurityId { get; set; }
        public byte ExchangeSegment { get; set; }
    }

    // Represents a live Ticker packet (LTP)
    public class TickerPacket
    {
        // FIX: Property is now nullable to prevent compiler warnings (CS8618).
        public string? SecurityId { get; set; }
        public decimal LastPrice { get; set; }
        public int LastTradeTime { get; set; }
    }

    public class QuotePacket
    {
        // FIX: Property is now nullable to prevent compiler warnings (CS8618).
        public string? SecurityId { get; set; }
        public decimal LastPrice { get; set; }
        public int LastTradeQuantity { get; set; }
        public int LastTradeTime { get; set; }
        public decimal AvgTradePrice { get; set; }
        public long Volume { get; set; }
        public long TotalSellQuantity { get; set; }
        public long TotalBuyQuantity { get; set; }
        public decimal Open { get; set; }
        public decimal Close { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
    }

    public class PreviousClosePacket
    {
        // FIX: Property is now nullable to prevent compiler warnings (CS8618).
        public string? SecurityId { get; set; }
        public decimal PreviousClose { get; set; }
    }
}

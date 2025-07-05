namespace TradingConsole.DhanApi.Models.WebSocket
{
    public class OiPacket
    {
        // FIX: Property is now nullable to prevent compiler warnings (CS8618).
        public string? SecurityId { get; set; }
        public int OpenInterest { get; set; }
    }
}

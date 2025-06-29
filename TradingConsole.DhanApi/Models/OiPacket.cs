namespace TradingConsole.DhanApi.Models.WebSocket
{
    public class OiPacket
    {
        public string SecurityId { get; set; } = string.Empty;
        public int OpenInterest { get; set; }
    }
}

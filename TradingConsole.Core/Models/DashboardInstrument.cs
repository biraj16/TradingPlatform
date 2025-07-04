// In TradingConsole.Core/Models/DashboardInstrument.cs
namespace TradingConsole.Core.Models
{
    public class DashboardInstrument : LiveInstrumentData
    {
        public int SegmentId { get; set; }
        public string ExchId { get; set; } = string.Empty;
        public bool IsFuture { get; set; }
        public string UnderlyingSymbol { get; set; } = string.Empty;

        // ADDED: A property to hold the full instrument name for display
        public string DisplayName { get; set; } = string.Empty;

        private long _openInterest;
        public long OpenInterest
        {
            get => _openInterest;
            set { if (_openInterest != value) { _openInterest = value; OnPropertyChanged(); } }
        }

        private string? _tradingSignal;
        public string? TradingSignal
        {
            get => _tradingSignal;
            set { if (_tradingSignal != value) { _tradingSignal = value; OnPropertyChanged(); } }
        }
    }
}

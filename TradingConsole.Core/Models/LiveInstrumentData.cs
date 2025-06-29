using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TradingConsole.Core.Models
{
    public class LiveInstrumentData : INotifyPropertyChanged
    {
        private decimal _ltp;
        private decimal _open;
        private decimal _high;
        private decimal _low;
        private decimal _close;
        private long _volume;
        private decimal _change;
        private decimal _changePercent;

        public string SecurityId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string FeedType { get; set; } = "Ticker"; // Ticker or Quote

        public decimal LTP { get => _ltp; set { if (_ltp != value) { _ltp = value; OnPropertyChanged(); UpdateChange(); } } }
        public decimal Open { get => _open; set { if (_open != value) { _open = value; OnPropertyChanged(); } } }
        public decimal High { get => _high; set { if (_high != value) { _high = value; OnPropertyChanged(); } } }
        public decimal Low { get => _low; set { if (_low != value) { _low = value; OnPropertyChanged(); } } }
        public decimal Close { get => _close; set { if (_close != value) { _close = value; OnPropertyChanged(); UpdateChange(); } } }
        public long Volume { get => _volume; set { if (_volume != value) { _volume = value; OnPropertyChanged(); } } }

        // --- FIX: Changed setters from 'private set' to public 'set' to resolve the binding exception ---
        public decimal Change { get => _change; set { if (_change != value) { _change = value; OnPropertyChanged(); } } }
        public decimal ChangePercent { get => _changePercent; set { if (_changePercent != value) { _changePercent = value; OnPropertyChanged(); } } }

        private void UpdateChange()
        {
            if (Close > 0)
            {
                // The calculation logic remains here. The public setter is to satisfy the WPF binding engine.
                Change = LTP - Close;
                ChangePercent = (Change / Close);
            }
            else
            {
                Change = 0;
                ChangePercent = 0;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

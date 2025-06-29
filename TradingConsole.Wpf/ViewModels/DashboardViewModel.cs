using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using TradingConsole.DhanApi.Models.WebSocket;

namespace TradingConsole.Wpf.ViewModels
{
    // The LiveInstrumentData and DashboardInstrument classes remain the same
    public class LiveInstrumentData : INotifyPropertyChanged
    {
        private decimal _ltp;
        public decimal LTP { get => _ltp; set { if (_ltp != value) { _ltp = value; OnPropertyChanged(); OnPropertyChanged(nameof(Change)); OnPropertyChanged(nameof(ChangePercent)); } } }

        private decimal _open;
        public decimal Open { get => _open; set { if (_open != value) { _open = value; OnPropertyChanged(); } } }

        private decimal _high;
        public decimal High { get => _high; set { if (_high != value) { _high = value; OnPropertyChanged(); } } }

        private decimal _low;
        public decimal Low { get => _low; set { if (_low != value) { _low = value; OnPropertyChanged(); } } }

        private decimal _close;
        public decimal Close { get => _close; set { if (_close != value) { _close = value; OnPropertyChanged(); OnPropertyChanged(nameof(Change)); OnPropertyChanged(nameof(ChangePercent)); } } }

        private long _volume;
        public long Volume { get => _volume; set { if (_volume != value) { _volume = value; OnPropertyChanged(); } } }

        private int _lastTradedQuantity;
        public int LastTradedQuantity { get => _lastTradedQuantity; set { if (_lastTradedQuantity != value) { _lastTradedQuantity = value; OnPropertyChanged(); } } }

        private int _lastTradeTime;
        public int LastTradeTime { get => _lastTradeTime; set { if (_lastTradeTime != value) { _lastTradeTime = value; OnPropertyChanged(); } } }

        private decimal _avgTradePrice;
        public decimal AvgTradePrice { get => _avgTradePrice; set { if (_avgTradePrice != value) { _avgTradePrice = value; OnPropertyChanged(); } } }


        public string SecurityId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string FeedType { get; set; } = string.Empty;

        public decimal Change => (LTP == 0 || Close == 0) ? 0 : LTP - Close;
        public decimal ChangePercent => Close == 0 ? 0 : (Change / Close);
        public bool IsChangePositive => Change > 0;
        public bool IsChangeNegative => Change < 0;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class DashboardInstrument : LiveInstrumentData
    {
        public int SegmentId { get; set; }
        public bool IsFuture { get; set; }
        public string UnderlyingSymbol { get; set; } = string.Empty;

        private long _openInterest;
        public long OpenInterest
        {
            get => _openInterest;
            set { if (_openInterest != value) { _openInterest = value; OnPropertyChanged(); } }
        }

        // --- ADDED: This property will hold the result of your live analysis ---
        private string? _tradingSignal;
        public string? TradingSignal
        {
            get => _tradingSignal;
            set { if (_tradingSignal != value) { _tradingSignal = value; OnPropertyChanged(); } }
        }
    }

    public class DashboardViewModel
    {
        public ObservableCollection<DashboardInstrument> MonitoredInstruments { get; } = new ObservableCollection<DashboardInstrument>();

        public DashboardViewModel()
        {
            // The list is populated by the MainViewModel.
        }

        public void UpdateLtp(TickerPacket packet)
        {
            var instrument = MonitoredInstruments.FirstOrDefault(i => i.SecurityId == packet.SecurityId);
            if (instrument != null)
            {
                instrument.LTP = packet.LastPrice;
            }
        }

        public void UpdateQuote(QuotePacket packet)
        {
            var instrument = MonitoredInstruments.FirstOrDefault(i => i.SecurityId == packet.SecurityId);
            if (instrument != null)
            {
                instrument.LTP = packet.LastPrice;
                instrument.Open = packet.Open;
                instrument.High = packet.High;
                instrument.Low = packet.Low;
                instrument.Close = packet.Close;
                instrument.Volume = packet.Volume;
                instrument.LastTradedQuantity = packet.LastTradeQuantity;
                instrument.LastTradeTime = packet.LastTradeTime;
                instrument.AvgTradePrice = packet.AvgTradePrice;
            }
        }

        public void UpdateOi(OiPacket packet)
        {
            var instrument = MonitoredInstruments.FirstOrDefault(i => i.SecurityId == packet.SecurityId);
            if (instrument != null)
            {
                instrument.OpenInterest = packet.OpenInterest;
            }
        }

        // --- ADDED: Method to store the previous day's close price for each instrument ---
        public void UpdatePreviousClose(PreviousClosePacket packet)
        {
            var instrument = MonitoredInstruments.FirstOrDefault(i => i.SecurityId == packet.SecurityId);
            if (instrument != null)
            {
                instrument.Close = packet.PreviousClose;
            }
        }
    }
}

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TradingConsole.Core.Models;
using TradingConsole.DhanApi;
using TradingConsole.DhanApi.Models;
using TradingConsole.DhanApi.Models.WebSocket;
using TradingConsole.Wpf.Services;

namespace TradingConsole.Wpf.ViewModels
{
    public class DashboardViewModel : INotifyPropertyChanged
    {
        private readonly ScripMasterService _scripMasterService;

        public ObservableCollection<DashboardInstrument> MonitoredInstruments { get; } = new ObservableCollection<DashboardInstrument>();

        // NEW: Properties for the search functionality
        private string _searchTerm = string.Empty;
        public string SearchTerm
        {
            get => _searchTerm;
            set
            {
                _searchTerm = value;
                OnPropertyChanged();
                // Trigger search automatically as the user types
                ExecuteSearch(null);
            }
        }

        public ObservableCollection<ScripInfo> SearchResults { get; }
        public ICommand AddInstrumentCommand { get; }
        public event Action<ScripInfo>? InstrumentSelectedForAddition;

        public DashboardViewModel(ScripMasterService scripMasterService)
        {
            _scripMasterService = scripMasterService;
            SearchResults = new ObservableCollection<ScripInfo>();
            AddInstrumentCommand = new RelayCommand(ExecuteAddInstrument);
        }

        private void ExecuteSearch(object? parameter)
        {
            var results = _scripMasterService.SearchInstruments(SearchTerm);
            SearchResults.Clear();
            foreach (var result in results)
            {
                SearchResults.Add(result);
            }
        }

        private void ExecuteAddInstrument(object? parameter)
        {
            if (parameter is ScripInfo scripInfo)
            {
                // Raise an event to notify the MainViewModel to handle the addition and subscription
                InstrumentSelectedForAddition?.Invoke(scripInfo);

                // Clear search results and term after adding
                SearchResults.Clear();
                _searchTerm = string.Empty;
                OnPropertyChanged(nameof(SearchTerm));
            }
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

        public void UpdatePreviousClose(PreviousClosePacket packet)
        {
            var instrument = MonitoredInstruments.FirstOrDefault(i => i.SecurityId == packet.SecurityId);
            if (instrument != null)
            {
                instrument.Close = packet.PreviousClose;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

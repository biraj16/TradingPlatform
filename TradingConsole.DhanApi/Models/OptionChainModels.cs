using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;

namespace TradingConsole.DhanApi.Models
{
    // A base class for INotifyPropertyChanged implementation
    public class ObservableModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class Index
    {
        public string Name { get; set; } = string.Empty;
        public string ScripId { get; set; } = string.Empty;
        public string Segment { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string ExchId { get; set; } = string.Empty;
        public override string ToString() => Name;
    }

    public class OptionChainResponse
    {
        [JsonProperty("data")]
        public OptionStrikeData? Data { get; set; }
    }

    public class OptionStrikeData
    {
        [JsonProperty("last_price")]
        public decimal UnderlyingLastPrice { get; set; }

        [JsonProperty("oc")]
        public Dictionary<string, OptionStrike>? OptionChain { get; set; }
    }

    public class OptionStrike
    {
        [JsonProperty("strikePrice")]
        public decimal StrikePrice { get; set; }

        [JsonProperty("ce")]
        public OptionData? CallOption { get; set; }

        [JsonProperty("pe")]
        public OptionData? PutOption { get; set; }
    }

    public class OptionData : ObservableModel
    {
        private decimal _lastPrice;
        private int _openInterest;
        private long _volume;
        private Greeks? _greeks;
        private string _securityId = string.Empty;

        // --- CRITICAL FIX: Added JsonProperty attribute to ensure SecurityId is deserialized ---
        [JsonProperty("securityId")]
        public string SecurityId { get => _securityId; set { _securityId = value; OnPropertyChanged(nameof(SecurityId)); } }

        [JsonProperty("last_price")]
        public decimal LastPrice
        {
            get => _lastPrice;
            set
            {
                if (_lastPrice != value)
                {
                    _lastPrice = value;
                    OnPropertyChanged(nameof(LastPrice));
                    OnPropertyChanged(nameof(LtpChange));
                    OnPropertyChanged(nameof(LtpChangePercent));
                }
            }
        }

        [JsonProperty("previous_close_price")]
        public decimal PreviousClose { get; set; }

        [JsonProperty("oi")]
        public int OpenInterest
        {
            get => _openInterest;
            set
            {
                if (_openInterest != value)
                {
                    _openInterest = value;
                    OnPropertyChanged(nameof(OpenInterest));
                    OnPropertyChanged(nameof(OiChange));
                    OnPropertyChanged(nameof(OiChangePercent));
                }
            }
        }

        [JsonProperty("previous_oi")]
        public int PreviousOpenInterest { get; set; }

        [JsonProperty("volume")]
        public long Volume
        {
            get => _volume;
            set
            {
                if (_volume != value)
                {
                    _volume = value;
                    OnPropertyChanged(nameof(Volume));
                }
            }
        }

        [JsonProperty("implied_volatility")]
        public decimal ImpliedVolatility { get; set; }

        [JsonProperty("greeks")]
        public Greeks? Greeks
        {
            get => _greeks;
            set
            {
                if (_greeks != value)
                {
                    _greeks = value;
                    OnPropertyChanged(nameof(Greeks));
                }
            }
        }

        public decimal LtpChange => LastPrice - PreviousClose;
        public decimal LtpChangePercent => PreviousClose == 0 ? 0 : (LtpChange / PreviousClose);
        public int OiChange => OpenInterest - PreviousOpenInterest;
        public decimal OiChangePercent => PreviousOpenInterest == 0 ? 0 : ((decimal)OiChange / PreviousOpenInterest);
    }

    public class Greeks : ObservableModel
    {
        private decimal _delta;

        [JsonProperty("delta")]
        public decimal Delta
        {
            get => _delta;
            set
            {
                if (_delta != value)
                {
                    _delta = value;
                    OnPropertyChanged(nameof(Delta));
                }
            }
        }
    }

    public class ExpiryListResponse
    {
        [JsonProperty("data")]
        public List<string>? ExpiryDates { get; set; }
    }
}

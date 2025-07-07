using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using TradingConsole.DhanApi.Models.WebSocket;
using TradingConsole.Core.Models; // Reference the new ObservableModel

namespace TradingConsole.Wpf.Services
{
    /// <summary>
    /// A data model to hold the state and calculated values for a single instrument being analyzed.
    /// This class now inherits from ObservableModel to enable UI updates when its properties change.
    /// </summary>
    public class AnalysisResult : ObservableModel // Inherit from ObservableModel
    {
        private string _securityId = string.Empty;
        private string _symbol = string.Empty; // Added Symbol for display
        private decimal _vwap;
        private decimal _shortEma; // Renamed from _ema
        private decimal _longEma;  // NEW: Long EMA property
        private string _tradingSignal = string.Empty;

        // Option-specific analysis properties for Implied Volatility (IV) and Volume
        private decimal _currentIv;
        private decimal _avgIv;
        private string _ivSignal = "Neutral";
        private long _currentVolume;
        private long _avgVolume;
        private string _volumeSignal = "Neutral";

        // Properties for grouping
        private string _instrumentGroup = string.Empty;
        private string _underlyingGroup = string.Empty; // For Nifty, Banknifty, Sensex in options/futures


        public string SecurityId { get => _securityId; set { _securityId = value; OnPropertyChanged(); } }
        public string Symbol { get => _symbol; set { _symbol = value; OnPropertyChanged(); } } // Make sure Symbol is set
        public decimal Vwap { get => _vwap; set { if (_vwap != value) { _vwap = value; OnPropertyChanged(); } } }

        // NEW: Short and Long EMA properties
        public decimal ShortEma { get => _shortEma; set { if (_shortEma != value) { _shortEma = value; OnPropertyChanged(); } } }
        public decimal LongEma { get => _longEma; set { if (_longEma != value) { _longEma = value; OnPropertyChanged(); } } }

        public string TradingSignal { get => _tradingSignal; set { if (_tradingSignal != value) { _tradingSignal = value; OnPropertyChanged(); } } }

        // Properties for IV and Volume analysis
        public decimal CurrentIv { get => _currentIv; set { if (_currentIv != value) { _currentIv = value; OnPropertyChanged(); } } }
        public decimal AvgIv { get => _avgIv; set { if (_avgIv != value) { _avgIv = value; OnPropertyChanged(); } } }
        public string IvSignal { get => _ivSignal; set { if (_ivSignal != value) { _ivSignal = value; OnPropertyChanged(); } } }
        public long CurrentVolume { get => _currentVolume; set { if (_currentVolume != value) { _currentVolume = value; OnPropertyChanged(); } } }
        public long AvgVolume { get => _avgVolume; set { if (_avgVolume != value) { _avgVolume = value; OnPropertyChanged(); } } }
        public string VolumeSignal { get => _volumeSignal; set { if (_volumeSignal != value) { _volumeSignal = value; OnPropertyChanged(); } } }

        // Grouping properties
        public string InstrumentGroup
        {
            get => _instrumentGroup;
            set { if (_instrumentGroup != value) { _instrumentGroup = value; OnPropertyChanged(); } }
        }

        public string UnderlyingGroup
        {
            get => _underlyingGroup;
            set { if (_underlyingGroup != value) { _underlyingGroup = value; OnPropertyChanged(); } }
        }

        // Helper property for combined display/grouping logic
        public string FullGroupIdentifier
        {
            get
            {
                if (InstrumentGroup == "Options")
                {
                    if (UnderlyingGroup.ToUpper().Contains("NIFTY") && !UnderlyingGroup.ToUpper().Contains("BANK")) return "Nifty Options";
                    if (UnderlyingGroup.ToUpper().Contains("BANKNIFTY")) return "Banknifty Options";
                    if (UnderlyingGroup.ToUpper().Contains("SENSEX")) return "Sensex Options";
                    return "Other Stock Options"; // Fallback for other stock options if any
                }
                if (InstrumentGroup == "Futures")
                {
                    if (UnderlyingGroup.ToUpper().Contains("NIFTY") || UnderlyingGroup.ToUpper().Contains("BANKNIFTY") || UnderlyingGroup.ToUpper().Contains("SENSEX"))
                        return "Index Futures";
                    return "Stock Futures"; // Assuming other futures are stocks
                }
                // For "Indices" and "Stocks" it's just the InstrumentGroup itself
                return InstrumentGroup;
            }
        }
    }

    /// <summary>
    /// The core engine for performing live, intraday analysis on instrument data.
    /// This service stores historical data and calculates indicators for the current session.
    /// </summary>
    public class AnalysisService : INotifyPropertyChanged
    {
        // --- CONTROL PANEL PARAMETER: EMA Lengths ---
        private int _shortEmaLength = 9; // Renamed from _emaLength
        public int ShortEmaLength
        {
            get => _shortEmaLength;
            set
            {
                if (_shortEmaLength != value)
                {
                    _shortEmaLength = value;
                    OnPropertyChanged(); // Notify the UI of the change
                }
            }
        }

        private int _longEmaLength = 21; // NEW: Long EMA Length
        public int LongEmaLength
        {
            get => _longEmaLength;
            set
            {
                if (_longEmaLength != value)
                {
                    _longEmaLength = value;
                    OnPropertyChanged(); // Notify the UI of the change
                }
            }
        }

        // Parameters for IV and Volume analysis thresholds
        private int _ivHistoryLength = 15;
        private decimal _ivSpikeThreshold = 0.01m;
        private int _volumeHistoryLength = 12;
        private double _volumeBurstMultiplier = 2.0;

        // Minimum number of valid IV history points required to calculate an IV signal.
        private const int MinIvHistoryForSignal = 2;

        // Stores a limited history of DashboardInstrument objects for each instrument.
        private readonly Dictionary<string, List<DashboardInstrument>> _instrumentHistory = new();

        // Stores the calculated analysis state (VWAP, EMA history, IV history, Volume history) for each instrument.
        // Updated to store two EMA values.
        private readonly Dictionary<string, (decimal cumulativePriceVolume, long cumulativeVolume, decimal currentShortEma, decimal currentLongEma, List<decimal> ivHistory, List<long> volumeHistory)> _analysisState = new();

        public event Action<AnalysisResult>? OnAnalysisUpdated;

        /// <summary>
        /// This is the entry point for all live instrument data for analysis.
        /// It accepts a DashboardInstrument object which contains comprehensive data.
        /// </summary>
        /// <param name="instrument">The DashboardInstrument object with the latest data.</param>
        public void OnInstrumentDataReceived(DashboardInstrument instrument)
        {
            // Initialize history and state for a new instrument if not already present.
            if (!_analysisState.ContainsKey(instrument.SecurityId))
            {
                var initialIvHistory = new List<decimal>();
                if (instrument.ImpliedVolatility > 0)
                {
                    initialIvHistory.Add(instrument.ImpliedVolatility);
                }
                // Initialize both EMAs to 0 initially
                _analysisState[instrument.SecurityId] = (0, 0, 0, 0, initialIvHistory, new List<long>());
                _instrumentHistory[instrument.SecurityId] = new List<DashboardInstrument>();
            }

            // Add the current instrument data to its history.
            // Keep history limited to prevent excessive memory usage.
            _instrumentHistory[instrument.SecurityId].Add(instrument);
            if (_instrumentHistory[instrument.SecurityId].Count > Math.Max(_ivHistoryLength, _volumeHistoryLength) * 2)
            {
                _instrumentHistory[instrument.SecurityId].RemoveAt(0); // Remove oldest entry
            }

            // Run the complex analysis calculations.
            RunComplexAnalysis(instrument);
        }

        /// <summary>
        /// This is the "brain" of your trading logic. It calculates all indicators and signals.
        /// </summary>
        /// <param name="instrument">The DashboardInstrument object with the latest data.</param>
        private void RunComplexAnalysis(DashboardInstrument instrument)
        {
            // Retrieve the current analysis state for this instrument.
            if (!_analysisState.TryGetValue(instrument.SecurityId, out var state))
            {
                // Safeguard: Should be initialized by OnInstrumentDataReceived
                state = (0, 0, 0, 0, new List<decimal>(), new List<long>());
                _analysisState[instrument.SecurityId] = state;
            }

            // --- Calculation 1: VWAP (Volume Weighted Average Price) ---
            state.cumulativePriceVolume += instrument.AvgTradePrice * instrument.LastTradedQuantity;
            state.cumulativeVolume += instrument.LastTradedQuantity;
            decimal vwap = (state.cumulativeVolume > 0) ? state.cumulativePriceVolume / state.cumulativeVolume : 0;

            // --- Calculation 2: EMA (Exponential Moving Average) - Short EMA ---
            decimal shortMultiplier = 2.0m / (ShortEmaLength + 1);
            if (state.currentShortEma == 0) // For the first calculation, EMA starts with LTP
            {
                state.currentShortEma = instrument.LTP;
            }
            else
            {
                state.currentShortEma = ((instrument.LTP - state.currentShortEma) * shortMultiplier) + state.currentShortEma;
            }

            // --- Calculation 3: EMA (Exponential Moving Average) - Long EMA ---
            decimal longMultiplier = 2.0m / (LongEmaLength + 1);
            if (state.currentLongEma == 0) // For the first calculation, EMA starts with LTP
            {
                state.currentLongEma = instrument.LTP;
            }
            else
            {
                state.currentLongEma = ((instrument.LTP - state.currentLongEma) * longMultiplier) + state.currentLongEma;
            }


            // --- Calculation 4: Implied Volatility (IV) Analysis ---
            if (instrument.ImpliedVolatility > 0)
            {
                state.ivHistory.Add(instrument.ImpliedVolatility);
            }
            if (state.ivHistory.Count > _ivHistoryLength) state.ivHistory.RemoveAt(0);

            decimal currentIv = instrument.ImpliedVolatility;
            decimal avgIv = 0;
            string ivSignal = "Neutral";

            var validIvHistory = state.ivHistory.Where(iv => iv > 0).ToList();
            if (validIvHistory.Any() && validIvHistory.Count >= MinIvHistoryForSignal)
            {
                avgIv = validIvHistory.Average();
                if (currentIv > (avgIv + _ivSpikeThreshold))
                {
                    ivSignal = "IV Spike Up";
                }
                else if (currentIv < (avgIv - _ivSpikeThreshold))
                {
                    ivSignal = "IV Drop Down";
                }
            }
            else if (currentIv > 0)
            {
                ivSignal = "Building History...";
            }


            // --- Calculation 5: Volume Burst Analysis ---
            state.volumeHistory.Add(instrument.Volume);
            if (state.volumeHistory.Count > _volumeHistoryLength) state.volumeHistory.RemoveAt(0);

            long currentVolume = instrument.Volume;
            double avgVolume = state.volumeHistory.Any() ? state.volumeHistory.Average(v => (double)v) : 0;
            string volumeSignal = "Neutral";

            if (avgVolume > 0 && currentVolume > (avgVolume * _volumeBurstMultiplier))
            {
                volumeSignal = "Volume Burst";
            }

            // Save the updated analysis state back to the dictionary.
            _analysisState[instrument.SecurityId] = state;

            // --- Generate Trading Signals/Conclusions (Overall Signal) ---
            string overallSignal = "Neutral";
            // Basic price-based signals (can be refined)
            if (state.currentShortEma > 0 && state.currentLongEma > 0) // Ensure both EMAs are calculated
            {
                if (instrument.LTP > state.currentShortEma && instrument.LTP > state.currentLongEma && instrument.LTP > vwap)
                {
                    overallSignal = "Strong Bullish";
                }
                else if (instrument.LTP > state.currentShortEma && instrument.LTP > state.currentLongEma)
                {
                    overallSignal = "Bullish: Above Both EMAs";
                }
                else if (instrument.LTP < state.currentShortEma && instrument.LTP < state.currentLongEma)
                {
                    overallSignal = "Bearish: Below Both EMAs";
                }
                else if (state.currentShortEma > state.currentLongEma && instrument.LTP > state.currentShortEma)
                {
                    overallSignal = "Bullish Crossover (Short > Long)";
                }
                else if (state.currentShortEma < state.currentLongEma && instrument.LTP < state.currentShortEma)
                {
                    overallSignal = "Bearish Crossover (Short < Long)";
                }
                else if (instrument.LTP > state.currentShortEma)
                {
                    overallSignal = "Bullish: Above Short EMA";
                }
                else if (instrument.LTP < state.currentShortEma)
                {
                    overallSignal = "Bearish: Below Short EMA";
                }
            }
            else if (state.currentShortEma > 0) // Fallback if long EMA not yet calculated
            {
                if (instrument.LTP > state.currentShortEma && instrument.LTP > vwap)
                {
                    overallSignal = "Strong Bullish (Short EMA)";
                }
                else if (instrument.LTP > state.currentShortEma)
                {
                    overallSignal = "Bullish: Above Short EMA";
                }
                else if (instrument.LTP < state.currentShortEma)
                {
                    overallSignal = "Bearish: Below Short EMA";
                }
            }


            // Combine signals for a more comprehensive "spike" detection for options.
            if (ivSignal == "IV Spike Up" && volumeSignal == "Volume Burst" && overallSignal.Contains("Bullish"))
            {
                overallSignal = "Strong Buy Signal (Spike)";
            }
            else if (ivSignal == "IV Spike Up" && volumeSignal == "Volume Burst")
            {
                overallSignal = "Potential Spike (IV/Vol)";
            }

            // Determine InstrumentGroup and UnderlyingGroup for AnalysisResult
            string instrumentGroup;
            string underlyingGroup = string.Empty;

            if (instrument.SegmentId == 0) // Typically represents Indices (e.g., IDX_I)
            {
                instrumentGroup = "Indices";
                underlyingGroup = instrument.Symbol; // Use Symbol for Indices like "Nifty 50", "Nifty Bank"
            }
            else if (instrument.IsFuture)
            {
                instrumentGroup = "Futures";
                underlyingGroup = instrument.UnderlyingSymbol; // e.g., "NIFTY", "BANKNIFTY", "RELIANCE"
            }
            // Heuristic for options: check if DisplayName contains CALL/PUT and not a Future
            else if (instrument.DisplayName.ToUpper().Contains("CALL") || instrument.DisplayName.ToUpper().Contains("PUT"))
            {
                instrumentGroup = "Options";
                underlyingGroup = instrument.UnderlyingSymbol; // e.g., "NIFTY", "BANKNIFTY", "HDFCBANK"
            }
            else // Default to Stocks if not explicitly identified as Index, Future, or Option
            {
                instrumentGroup = "Stocks";
                underlyingGroup = instrument.Symbol; // Use Symbol for Equity stocks
            }


            // Fire Event with All Results
            OnAnalysisUpdated?.Invoke(new AnalysisResult
            {
                SecurityId = instrument.SecurityId,
                Symbol = instrument.DisplayName, // Use DisplayName for better readability in the UI
                Vwap = vwap,
                ShortEma = state.currentShortEma, // Pass short EMA
                LongEma = state.currentLongEma,   // Pass long EMA
                TradingSignal = overallSignal, // This is the combined price-based and spike signal
                CurrentIv = currentIv,
                AvgIv = avgIv,
                IvSignal = ivSignal,
                CurrentVolume = currentVolume,
                AvgVolume = (long)avgVolume, // Cast back to long for display
                VolumeSignal = volumeSignal,
                InstrumentGroup = instrumentGroup,
                UnderlyingGroup = underlyingGroup
            });
        }

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion
    }
}

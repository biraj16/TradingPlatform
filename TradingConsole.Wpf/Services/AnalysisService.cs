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
        private decimal _ema;
        private string _tradingSignal = string.Empty;

        // NEW: Option-specific analysis properties for Implied Volatility (IV) and Volume
        private decimal _currentIv;
        private decimal _avgIv;
        private string _ivSignal = "Neutral";
        private long _currentVolume;
        private long _avgVolume;
        private string _volumeSignal = "Neutral";

        public string SecurityId { get => _securityId; set { _securityId = value; OnPropertyChanged(); } }
        public string Symbol { get => _symbol; set { _symbol = value; OnPropertyChanged(); } } // Make sure Symbol is set
        public decimal Vwap { get => _vwap; set { if (_vwap != value) { _vwap = value; OnPropertyChanged(); } } }
        public decimal Ema { get => _ema; set { if (_ema != value) { _ema = value; OnPropertyChanged(); } } }
        public string TradingSignal { get => _tradingSignal; set { if (_tradingSignal != value) { _tradingSignal = value; OnPropertyChanged(); } } }

        // NEW: Properties for IV and Volume analysis
        public decimal CurrentIv { get => _currentIv; set { if (_currentIv != value) { _currentIv = value; OnPropertyChanged(); } } }
        public decimal AvgIv { get => _avgIv; set { if (_avgIv != value) { _avgIv = value; OnPropertyChanged(); } } }
        public string IvSignal { get => _ivSignal; set { if (_ivSignal != value) { _ivSignal = value; OnPropertyChanged(); } } }
        public long CurrentVolume { get => _currentVolume; set { if (_currentVolume != value) { _currentVolume = value; OnPropertyChanged(); } } }
        public long AvgVolume { get => _avgVolume; set { if (_avgVolume != value) { _avgVolume = value; OnPropertyChanged(); } } }
        public string VolumeSignal { get => _volumeSignal; set { if (_volumeSignal != value) { _volumeSignal = value; OnPropertyChanged(); } } }
    }

    /// <summary>
    /// The core engine for performing live, intraday analysis on instrument data.
    /// This service stores historical data and calculates indicators for the current session.
    /// </summary>
    public class AnalysisService : INotifyPropertyChanged
    {
        // --- CONTROL PANEL PARAMETER: EMA Length ---
        private int _emaLength = 9;
        public int EmaLength
        {
            get => _emaLength;
            set
            {
                if (_emaLength != value)
                {
                    _emaLength = value;
                    OnPropertyChanged(); // Notify the UI of the change
                }
            }
        }

        // NEW: Parameters for IV and Volume analysis thresholds
        private int _ivHistoryLength = 15; // Number of IV data points to consider for average (e.g., last 10 updates)
        private decimal _ivSpikeThreshold = 0.01m; // E.g., 0.5% absolute increase from average for a "spike" (0.005 means 0.5%)
        private int _volumeHistoryLength = 12; // Number of volume data points to consider for average
        private double _volumeBurstMultiplier = 2.0; // E.g., current volume is 2x average volume for a "burst"

        // Minimum number of valid IV history points required to calculate an IV signal.
        // This prevents false positives when history is still building or only contains zeros.
        private const int MinIvHistoryForSignal = 2;

        // Stores a limited history of DashboardInstrument objects for each instrument.
        private readonly Dictionary<string, List<DashboardInstrument>> _instrumentHistory = new();

        // Stores the calculated analysis state (VWAP, EMA, IV history, Volume history) for each instrument.
        private readonly Dictionary<string, (decimal cumulativePriceVolume, long cumulativeVolume, decimal currentEma, List<decimal> ivHistory, List<long> volumeHistory)> _analysisState = new();

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
                // Initialize ivHistory with the current IV if it's meaningful.
                var initialIvHistory = new List<decimal>();
                if (instrument.ImpliedVolatility > 0)
                {
                    initialIvHistory.Add(instrument.ImpliedVolatility);
                }
                _analysisState[instrument.SecurityId] = (0, 0, 0, initialIvHistory, new List<long>());
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
            // Ensure the state exists before accessing it.
            if (!_analysisState.TryGetValue(instrument.SecurityId, out var state))
            {
                // This should ideally not happen if OnInstrumentDataReceived is always called first,
                // but as a safeguard, initialize if missing.
                state = (0, 0, 0, new List<decimal>(), new List<long>());
                _analysisState[instrument.SecurityId] = state;
            }


            // --- Calculation 1: VWAP (Volume Weighted Average Price) ---
            // VWAP uses AvgTradePrice and LastTradedQuantity for calculation.
            state.cumulativePriceVolume += instrument.AvgTradePrice * instrument.LastTradedQuantity;
            state.cumulativeVolume += instrument.LastTradedQuantity;
            decimal vwap = (state.cumulativeVolume > 0) ? state.cumulativePriceVolume / state.cumulativeVolume : 0;

            // --- Calculation 2: EMA (Exponential Moving Average) ---
            // EMA uses the Last Traded Price (LTP) and a smoothing multiplier.
            decimal multiplier = 2.0m / (EmaLength + 1);
            if (state.currentEma == 0) // For the first calculation, EMA starts with LTP
            {
                state.currentEma = instrument.LTP;
            }
            else
            {
                state.currentEma = ((instrument.LTP - state.currentEma) * multiplier) + state.currentEma;
            }

            // --- Calculation 3: Implied Volatility (IV) Analysis ---
            // Only add non-zero IV to history to avoid skewing averages with initial zero values.
            if (instrument.ImpliedVolatility > 0)
            {
                state.ivHistory.Add(instrument.ImpliedVolatility); // Add current valid IV
            }
            if (state.ivHistory.Count > _ivHistoryLength) state.ivHistory.RemoveAt(0);

            decimal currentIv = instrument.ImpliedVolatility;
            // Calculate average IV only from non-zero values in history.
            // Require a minimum number of history points before calculating average for signal.
            decimal avgIv = 0;
            string ivSignal = "Neutral";

            var validIvHistory = state.ivHistory.Where(iv => iv > 0).ToList();
            if (validIvHistory.Count >= MinIvHistoryForSignal)
            {
                avgIv = validIvHistory.Average();
                // Check if current IV is significantly different from average
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
                // If not enough history for a signal, but IV is present, just show current IV.
                // This helps differentiate between 'no data' and 'neutral'.
                ivSignal = "Building History...";
            }


            // --- Calculation 4: Volume Burst Analysis ---
            // Add current total volume to history and maintain history length.
            state.volumeHistory.Add(instrument.Volume);
            if (state.volumeHistory.Count > _volumeHistoryLength) state.volumeHistory.RemoveAt(0);

            long currentVolume = instrument.Volume;
            // Calculate average volume from history.
            double avgVolume = state.volumeHistory.Any() ? state.volumeHistory.Average(v => (double)v) : 0;
            string volumeSignal = "Neutral";

            // Detect volume bursts: if current volume is much higher than its recent average.
            if (avgVolume > 0 && currentVolume > (avgVolume * _volumeBurstMultiplier))
            {
                volumeSignal = "Volume Burst";
            }

            // Save the updated analysis state back to the dictionary.
            _analysisState[instrument.SecurityId] = state;

            // --- Generate Trading Signals/Conclusions (Overall Signal) ---
            string overallSignal = "Neutral";
            // Basic price-based signals (can be refined)
            if (state.currentEma > 0)
            {
                if (instrument.LTP > state.currentEma && instrument.LTP > vwap)
                {
                    overallSignal = "Strong Bullish";
                }
                else if (instrument.LTP > state.currentEma)
                {
                    overallSignal = "Bullish: Above EMA";
                }
                else if (instrument.LTP < state.currentEma)
                {
                    overallSignal = "Bearish: Below EMA";
                }
            }

            // Combine signals for a more comprehensive "spike" detection for options.
            // This is a simplified example; real trading signals would be more complex.
            if (ivSignal == "IV Spike Up" && volumeSignal == "Volume Burst" && overallSignal.Contains("Bullish"))
            {
                overallSignal = "Strong Buy Signal (Spike)";
            }
            else if (ivSignal == "IV Spike Up" && volumeSignal == "Volume Burst")
            {
                overallSignal = "Potential Spike (IV/Vol)";
            }


            // --- Fire Event with All Results ---
            // Invoke the event to notify subscribers (e.g., AnalysisTabViewModel) with the latest analysis.
            OnAnalysisUpdated?.Invoke(new AnalysisResult
            {
                SecurityId = instrument.SecurityId,
                Symbol = instrument.DisplayName, // Use DisplayName for better readability in the UI
                Vwap = vwap,
                Ema = state.currentEma,
                TradingSignal = overallSignal, // This is the combined price-based and spike signal
                CurrentIv = currentIv,
                AvgIv = avgIv,
                IvSignal = ivSignal,
                CurrentVolume = currentVolume,
                AvgVolume = (long)avgVolume, // Cast back to long for display
                VolumeSignal = volumeSignal
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

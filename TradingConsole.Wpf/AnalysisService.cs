using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using TradingConsole.DhanApi.Models.WebSocket;

namespace TradingConsole.Wpf.Services
{
    /// <summary>
    /// A data model to hold the state and calculated values for a single instrument being analyzed.
    /// </summary>
    public class AnalysisResult
    {
        public string SecurityId { get; set; }
        public string Symbol { get; set; }
        public decimal Vwap { get; set; }
        public decimal Ema { get; set; } // Added EMA to our results
        public string TradingSignal { get; set; }
    }

    /// <summary>
    /// The core engine for performing live, intraday analysis on quote data.
    /// This service stores quote data and calculated indicators for the current session.
    /// </summary>
    public class AnalysisService : INotifyPropertyChanged
    {
        // --- CONTROL PANEL PARAMETER: EMA Length ---
        // This property will be controlled by the UI.
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

        // Stores the list of incoming quotes for each instrument (SecurityId -> List of Quotes).
        private readonly Dictionary<string, List<QuotePacket>> _quoteHistory = new();

        // Stores the calculated analysis values (like VWAP and EMA) for each instrument.
        private readonly Dictionary<string, (decimal cumulativePriceVolume, long cumulativeVolume, decimal currentEma)> _analysisState = new();

        public event Action<AnalysisResult>? OnAnalysisUpdated;

        /// <summary>
        /// This is the entry point for all live quote data.
        /// </summary>
        public void OnQuoteReceived(QuotePacket packet)
        {
            if (!_quoteHistory.ContainsKey(packet.SecurityId))
            {
                _quoteHistory[packet.SecurityId] = new List<QuotePacket>();
                _analysisState[packet.SecurityId] = (0, 0, 0); // Initialize state
            }
            _quoteHistory[packet.SecurityId].Add(packet);

            RunComplexAnalysis(packet);
        }

        /// <summary>
        /// This is the "brain" of your trading logic. It calculates all indicators.
        /// </summary>
        private void RunComplexAnalysis(QuotePacket packet)
        {
            var state = _analysisState[packet.SecurityId];

            // --- Calculation 1: VWAP (Volume Weighted Average Price) ---
            state.cumulativePriceVolume += packet.AvgTradePrice * packet.Volume;
            state.cumulativeVolume += packet.Volume;
            decimal vwap = (state.cumulativeVolume > 0) ? state.cumulativePriceVolume / state.cumulativeVolume : 0;

            // --- Calculation 2: EMA (Exponential Moving Average) ---
            decimal multiplier = 2.0m / (EmaLength + 1);
            if (state.currentEma == 0) // First calculation, use LTP as starting point
            {
                state.currentEma = packet.LastPrice;
            }
            else
            {
                state.currentEma = ((packet.LastPrice - state.currentEma) * multiplier) + state.currentEma;
            }

            _analysisState[packet.SecurityId] = state; // Save the updated state

            // --- Generate Trading Signals/Conclusions ---
            string signal = "Neutral";
            if (state.currentEma > 0)
            {
                if (packet.LastPrice > state.currentEma && packet.LastPrice > vwap)
                {
                    signal = "Strong Bullish";
                }
                else if (packet.LastPrice > state.currentEma)
                {
                    signal = "Bullish: Above EMA";
                }
                else if (packet.LastPrice < state.currentEma)
                {
                    signal = "Bearish: Below EMA";
                }
            }

            // --- Fire Event with All Results ---
            OnAnalysisUpdated?.Invoke(new AnalysisResult
            {
                SecurityId = packet.SecurityId,
                Vwap = vwap,
                Ema = state.currentEma,
                TradingSignal = signal
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

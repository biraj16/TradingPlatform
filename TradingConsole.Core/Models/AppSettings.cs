// In TradingConsole.Core/Models/AppSettings.cs
using System.Collections.Generic;

namespace TradingConsole.Core.Models
{
    /// <summary>
    /// Represents the application's user-configurable settings that will be saved to a file.
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// Stores the freeze quantity limits for different index options.
        /// The key is the index name (e.g., "NIFTY") and the value is the quantity.
        /// </summary>
        public Dictionary<string, int> FreezeQuantities { get; set; }

        /// <summary>
        /// Stores the user's customized list of instruments for the dashboard.
        /// Format: "TYPE:SYMBOL", e.g., "EQ:RELIANCE", "FUT:NIFTY", "IDX:Nifty 50"
        /// </summary>
        public List<string> MonitoredSymbols { get; set; }

        // NEW: EMA Lengths for Analysis Service
        public int ShortEmaLength { get; set; }
        public int LongEmaLength { get; set; }

        public AppSettings()
        {
            // Initialize with default values.
            FreezeQuantities = new Dictionary<string, int>
            {
                { "NIFTY", 1800 },
                { "BANKNIFTY", 900 },
                { "FINNIFTY", 1800 },
                { "SENSEX", 1000 }
            };

            // This default list is used only if a settings file doesn't exist.
            // The user's configuration will be saved and loaded subsequently.
            MonitoredSymbols = new List<string>
            {
                "IDX:Nifty 50",
                "IDX:Nifty Bank",
                "IDX:Sensex",
                "EQ:HDFCBANK",
                "EQ:ICICIBANK",
                "EQ:RELIANCE INDUSTRIES",
                "EQ:INFOSYS",
                "EQ:ITC",
                "EQ:TATA CONSULTANCY",
                "FUT:NIFTY",
                "FUT:BANKNIFTY",
                "FUT:HDFCBANK",
                "FUT:ICICIBANK",
                "FUT:RELIANCE",
                "FUT:INFY",
                "FUT:TCS"
            };

            // NEW: Default EMA lengths
            ShortEmaLength = 9;  // Default for short EMA
            LongEmaLength = 21;  // Default for long EMA
        }
    }
}

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
        }
    }
}

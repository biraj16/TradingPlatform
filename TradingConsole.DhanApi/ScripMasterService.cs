using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using TradingConsole.DhanApi.Models;

namespace TradingConsole.DhanApi
{
    public class ScripMasterService
    {
        private List<ScripInfo> _scripMaster = new List<ScripInfo>();
        private readonly HttpClient _httpClient;
        private const string ScripMasterUrl = "https://images.dhan.co/api-data/api-scrip-master-detailed.csv";

        public ScripMasterService()
        {
            _httpClient = new HttpClient();
        }

        public async Task LoadScripMasterAsync()
        {
            try
            {
                var csvData = await _httpClient.GetStringAsync(ScripMasterUrl);
                var tempMaster = new List<ScripInfo>();

                var allowedTypes = new HashSet<string>
                {
                    "EQUITY",
                    "FUTIDX",
                    "FUTSTK",
                    "INDEX",
                    "OPTIDX"
                };

                using (var reader = new StringReader(csvData))
                {
                    await reader.ReadLineAsync(); // Skip header
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        var values = ParseCsvLine(line);
                        if (values.Length < 15) continue;

                        var instrumentType = values[4] ?? string.Empty;

                        if (allowedTypes.Contains(instrumentType))
                        {
                            var scrip = new ScripInfo
                            {
                                Segment = values[1] ?? string.Empty,
                                SecurityId = values[2] ?? string.Empty,
                                SemInstrumentName = values[6] ?? string.Empty,
                                UnderlyingSecurityId = values[5] ?? string.Empty,
                                TradingSymbol = values[8] ?? string.Empty,
                                ExpiryDate = FormatDate(values[12]),
                                StrikePrice = decimal.TryParse(values[13], NumberStyles.Any, CultureInfo.InvariantCulture, out var sp) ? sp : 0,
                                OptionType = values[14] ?? string.Empty,
                                InstrumentType = instrumentType,
                                LotSize = (int)(decimal.TryParse(values[11], NumberStyles.Any, CultureInfo.InvariantCulture, out var ds) ? ds : 0),
                            };

                            bool isDerivative = instrumentType.Contains("FUT") || instrumentType.Contains("OPT");
                            if (!string.IsNullOrEmpty(scrip.SecurityId) && (!isDerivative || scrip.ExpiryDate.HasValue))
                            {
                                tempMaster.Add(scrip);
                            }
                        }
                    }
                }
                _scripMaster = tempMaster;
                Debug.WriteLine($"Scrip Master loaded with {_scripMaster.Count} relevant instruments.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load scrip master: {ex.Message}");
            }
        }

        private string[] ParseCsvLine(string line)
        {
            var values = new List<string>();
            bool inQuotes = false;
            var currentValue = "";

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    values.Add(currentValue.Trim().Trim('"'));
                    currentValue = "";
                }
                else
                {
                    currentValue += c;
                }
            }

            values.Add(currentValue.Trim().Trim('"'));
            return values.ToArray();
        }

        // --- NEW UNIFIED SEARCH STRATEGY ---
        private IEnumerable<ScripInfo> FindInstruments(string segment, string instrumentType, string symbol, bool useSemInstrumentName = false)
        {
            var searchTerm = symbol.Trim();
            return _scripMaster.Where(s =>
                s.Segment == segment &&
                s.InstrumentType == instrumentType &&
                (useSemInstrumentName
                    ? s.SemInstrumentName.Trim().Equals(searchTerm, StringComparison.OrdinalIgnoreCase)
                    : s.TradingSymbol.Trim().Equals(searchTerm, StringComparison.OrdinalIgnoreCase))
            );
        }

        public string? FindNearMonthFutureSecurityId(string baseSymbol)
        {
            // Find both Index and Stock futures in the Futures segment ('F')
            var indexFutures = FindInstruments("F", "FUTIDX", baseSymbol, useSemInstrumentName: false);
            var stockFutures = FindInstruments("F", "FUTSTK", baseSymbol, useSemInstrumentName: false);

            var allFutures = indexFutures.Concat(stockFutures)
                .Where(s => s.ExpiryDate >= DateTime.Today)
                .OrderBy(s => s.ExpiryDate);

            return allFutures.FirstOrDefault()?.SecurityId;
        }

        public string? FindEquitySecurityId(string tradingSymbol)
        {
            // Find an Equity in the Equity segment ('E')
            return FindInstruments("E", "EQUITY", tradingSymbol).FirstOrDefault()?.SecurityId;
        }

        public string? FindIndexSecurityId(string tradingSymbol)
        {
            // Find an Index in the Index segment ('I'), searching by the SEM name
            return FindInstruments("I", "INDEX", tradingSymbol, useSemInstrumentName: true).FirstOrDefault()?.SecurityId;
        }

        public string? FindSecurityId(string underlyingSymbol, string expiry, decimal strike, string optionType)
        {
            if (!DateTime.TryParse(expiry, out var targetDate))
            {
                Debug.WriteLine($"Invalid expiry date format: {expiry}");
                return null;
            }

            // Find both Index and Stock options in the Options segment ('O')
            var indexOptions = FindInstruments("O", "OPTIDX", underlyingSymbol, useSemInstrumentName: false);
            var stockOptions = FindInstruments("O", "OPTSTK", underlyingSymbol, useSemInstrumentName: false);

            var result = indexOptions.Concat(stockOptions)
                .FirstOrDefault(s =>
                    s.ExpiryDate.HasValue &&
                    s.ExpiryDate.Value.Date == targetDate.Date &&
                    s.StrikePrice == strike &&
                    s.OptionType.Equals(optionType, StringComparison.OrdinalIgnoreCase));

            if (result == null)
            {
                Debug.WriteLine($"No option found for: {underlyingSymbol}, {expiry}, {strike}, {optionType}");
            }
            return result?.SecurityId;
        }

        private DateTime? FormatDate(string dateStr)
        {
            if (string.IsNullOrWhiteSpace(dateStr)) return null;
            string[] formats = { "dd-MM-yyyy", "dd-MMM-yyyy", "yyyy-MM-dd", "MM/dd/yyyy", "dd/MM/yyyy" };
            if (DateTime.TryParseExact(dateStr.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }
            if (DateTime.TryParse(dateStr.Trim(), out date))
            {
                return date;
            }
            Debug.WriteLine($"Could not parse date: {dateStr}");
            return null;
        }

        public int GetLotSizeForSecurity(string securityId)
        {
            var match = _scripMaster.FirstOrDefault(s => s.SecurityId == securityId);
            return match?.LotSize ?? 0;
        }
    }
}

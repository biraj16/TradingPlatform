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

                // --- OPTIMIZATION: Only load the instrument types we actually need ---
                var allowedTypes = new HashSet<string>
                {
                    "EQUITY",
                    "FUTIDX",
                    "FUTSTK",
                    "INDEX",
                    "OPTIDX" 
                    // "OPTSTK" has been removed as per your requirement.
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

                        // --- UPDATED LOGIC: Check if the instrument type is in our allowed list ---
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

        public string? FindNearMonthFutureSecurityId(string baseSymbol)
        {
            string normalizedSymbol = NormalizeSymbolForSearch(baseSymbol);
            var allFutures = _scripMaster.Where(s =>
                (s.InstrumentType == "FUTIDX" || s.InstrumentType == "FUTSTK") &&
                s.ExpiryDate >= DateTime.Today)
                .OrderBy(s => s.ExpiryDate)
                .ToList();

            var exactMatch = allFutures.FirstOrDefault(s =>
                s.TradingSymbol.Equals(baseSymbol, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null) return exactMatch.SecurityId;

            if (baseSymbol.ToUpper() == "NIFTY" || baseSymbol.ToUpper() == "BANKNIFTY" || baseSymbol.ToUpper() == "FINNIFTY")
            {
                var indexMatch = allFutures.FirstOrDefault(s =>
                    s.InstrumentType == "FUTIDX" &&
                    s.SemInstrumentName.Equals(normalizedSymbol, StringComparison.OrdinalIgnoreCase));
                if (indexMatch != null) return indexMatch.SecurityId;
            }

            var prefixMatches = allFutures.Where(s =>
                s.TradingSymbol.StartsWith(baseSymbol, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (prefixMatches.Any()) return prefixMatches.First().SecurityId;

            var candidates = allFutures.Where(s =>
                (s.SemInstrumentName.Contains(baseSymbol, StringComparison.OrdinalIgnoreCase) ||
                 s.TradingSymbol.Contains(baseSymbol, StringComparison.OrdinalIgnoreCase)) &&
                !IsSymbolMismatch(baseSymbol, s.TradingSymbol))
                .ToList();
            return candidates.FirstOrDefault()?.SecurityId;
        }

        private bool IsSymbolMismatch(string searchSymbol, string tradingSymbol)
        {
            if (searchSymbol.ToUpper() == "NIFTY")
            {
                var upper = tradingSymbol.ToUpper();
                return upper.Contains("BANK") || upper.Contains("FIN") || upper.Contains("MID") || upper.Contains("SMALL");
            }
            return false;
        }

        private string NormalizeSymbolForSearch(string symbol)
        {
            return symbol.ToUpperInvariant() switch
            {
                "NIFTY" => "NIFTY 50",
                "BANKNIFTY" => "NIFTY BANK",
                "FINNIFTY" => "NIFTY FIN SERVICE",
                "MIDCPNIFTY" => "NIFTY MID SELECT",
                _ => symbol.ToUpperInvariant()
            };
        }

        public string? FindEquitySecurityId(string tradingSymbol)
        {
            return _scripMaster.FirstOrDefault(s => s.InstrumentType == "EQUITY" &&
                s.TradingSymbol.Equals(tradingSymbol, StringComparison.OrdinalIgnoreCase))?.SecurityId;
        }

        public string? FindIndexSecurityId(string tradingSymbol)
        {
            string searchSymbol = NormalizeSymbolForSearch(tradingSymbol);
            return _scripMaster.FirstOrDefault(s => s.InstrumentType == "INDEX" &&
                s.SemInstrumentName.Equals(searchSymbol, StringComparison.OrdinalIgnoreCase))?.SecurityId;
        }

        public string? FindSecurityId(string underlyingSymbol, string expiry, decimal strike, string optionType)
        {
            if (!DateTime.TryParse(expiry, out var targetDate))
            {
                Debug.WriteLine($"Invalid expiry date format: {expiry}");
                return null;
            }
            var result = _scripMaster.FirstOrDefault(s =>
                s.TradingSymbol.Equals(underlyingSymbol, StringComparison.OrdinalIgnoreCase) &&
                s.ExpiryDate.HasValue && s.ExpiryDate.Value.Date == targetDate.Date &&
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

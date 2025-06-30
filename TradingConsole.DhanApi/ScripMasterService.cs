using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using TradingConsole.DhanApi.Models;

namespace TradingConsole.DhanApi
{
    public class ScripMasterService
    {
        // --- The base ScripInfo class is now sufficient, no internal class needed ---
        public class ScripInfo
        {
            public string Segment { get; set; } = string.Empty;
            public string SecurityId { get; set; } = string.Empty;
            public string SemInstrumentName { get; set; } = string.Empty;
            public string TradingSymbol { get; set; } = string.Empty;
            public DateTime? ExpiryDate { get; set; }
            public decimal StrikePrice { get; set; }
            public string OptionType { get; set; } = string.Empty;
            public string InstrumentType { get; set; } = string.Empty;
            public int LotSize { get; set; }
            public string UnderlyingSymbol { get; set; } = string.Empty;
        }

        private List<ScripInfo> _scripMaster = new List<ScripInfo>();
        private readonly HttpClient _httpClient;
        // --- STRATEGY FIX: Use the simpler, more reliable compact scrip master file ---
        private const string ScripMasterUrl = "https://images.dhan.co/api-data/api-scrip-master.csv";

        public ScripMasterService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        public async Task LoadScripMasterAsync()
        {
            try
            {
                Debug.WriteLine("Starting to download compact scrip master CSV...");
                var csvData = await _httpClient.GetStringAsync(ScripMasterUrl);
                Debug.WriteLine($"Downloaded compact CSV data: {csvData.Length} characters");

                var tempMaster = new List<ScripInfo>();
                var allowedTypes = new HashSet<string> { "EQUITY", "FUTIDX", "FUTSTK", "INDEX", "OPTIDX" };

                using (var reader = new StringReader(csvData))
                {
                    var headerLine = await reader.ReadLineAsync();
                    if (headerLine == null) return;

                    var headers = ParseCsvLine(headerLine);
                    var headerMap = new Dictionary<string, int>();
                    for (int i = 0; i < headers.Length; i++)
                    {
                        headerMap[headers[i].Trim()] = i;
                    }

                    // --- STRATEGY FIX: Validate against the known columns of the compact file ---
                    var requiredColumns = new[] {
                        "INSTRUMENT_TYPE", "SEGMENT", "SECURITY_ID", "UNDERLYING_SYMBOL",
                        "SYMBOL_NAME", "TRADING_SYMBOL", "LOT_SIZE", "EXPIRY_DATE", "STRIKE_PRICE", "OPTION_TYPE"
                    };

                    foreach (var col in requiredColumns)
                    {
                        if (!headerMap.ContainsKey(col))
                        {
                            Debug.WriteLine($"CRITICAL ERROR: Required column '{col}' not found in compact file headers.");
                            return;
                        }
                    }

                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        var values = ParseCsvLine(line);
                        if (values.Length <= headerMap["INSTRUMENT_TYPE"]) continue;

                        var instrumentType = GetSafeValue(values, headerMap, "INSTRUMENT_TYPE");

                        // As you correctly instructed: only filter by instrument type.
                        if (allowedTypes.Contains(instrumentType))
                        {
                            var scrip = new ScripInfo
                            {
                                Segment = GetSafeValue(values, headerMap, "SEGMENT"),
                                SecurityId = GetSafeValue(values, headerMap, "SECURITY_ID"),
                                InstrumentType = instrumentType,
                                SemInstrumentName = GetSafeValue(values, headerMap, "SYMBOL_NAME"),
                                TradingSymbol = GetSafeValue(values, headerMap, "TRADING_SYMBOL"),
                                UnderlyingSymbol = GetSafeValue(values, headerMap, "UNDERLYING_SYMBOL"),
                                LotSize = ParseIntSafe(GetSafeValue(values, headerMap, "LOT_SIZE")),
                                ExpiryDate = FormatDate(GetSafeValue(values, headerMap, "EXPIRY_DATE")),
                                StrikePrice = ParseDecimalSafe(GetSafeValue(values, headerMap, "STRIKE_PRICE")),
                                OptionType = GetSafeValue(values, headerMap, "OPTION_TYPE"),
                            };
                            tempMaster.Add(scrip);
                        }
                    }
                }
                _scripMaster = tempMaster;
                Debug.WriteLine($"Scrip Master loaded with {_scripMaster.Count} relevant instruments.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load scrip master: {ex.GetType().Name} - {ex.Message}");
            }
        }

        // --- DEFINITIVE FIX: Bulletproof Date Parser ---
        private DateTime? FormatDate(string dateStr)
        {
            if (string.IsNullOrWhiteSpace(dateStr)) return null;
            string[] formats = { "dd-MMM-yy", "d-MMM-yy", "dd-MM-yyyy", "d-MM-yyyy", "dd-MMM-yyyy", "d-MMM-yyyy", "yyyy-MM-dd", "MM/dd/yyyy" };
            foreach (var format in formats)
            {
                if (DateTime.TryParseExact(dateStr.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return date;
                }
            }
            if (DateTime.TryParse(dateStr.Trim(), out var fallbackDate)) return fallbackDate;
            return null;
        }

        // --- DEFINITIVE FIX: Intelligent Search Logic ---
        public string? FindNearMonthFutureSecurityId(string baseSymbol)
        {
            var term = baseSymbol.Trim();
            var result = _scripMaster
                .Where(s => s.Segment == "F" &&
                            (s.InstrumentType == "FUTIDX" || s.InstrumentType == "FUTSTK") &&
                            s.UnderlyingSymbol.Equals(term, StringComparison.OrdinalIgnoreCase) &&
                            s.ExpiryDate.HasValue && s.ExpiryDate.Value >= DateTime.Today)
                .OrderBy(s => s.ExpiryDate)
                .FirstOrDefault();

            if (result == null) Debug.WriteLine($"[RESOLVER_FAIL] No future found for: {baseSymbol}");
            else Debug.WriteLine($"[RESOLVER_SUCCESS] Found future: {result.TradingSymbol} | {result.SecurityId}");
            return result?.SecurityId;
        }

        public string? FindEquitySecurityId(string tradingSymbol)
        {
            var term = tradingSymbol.Trim();
            var result = _scripMaster.FirstOrDefault(s => s.Segment == "E" && s.InstrumentType == "EQUITY" && s.TradingSymbol.Equals(term, StringComparison.OrdinalIgnoreCase));
            if (result == null) Debug.WriteLine($"[RESOLVER_FAIL] No equity found for: {tradingSymbol}");
            else Debug.WriteLine($"[RESOLVER_SUCCESS] Found equity: {result.TradingSymbol} | {result.SecurityId}");
            return result?.SecurityId;
        }

        public string? FindIndexSecurityId(string tradingSymbol)
        {
            var term = tradingSymbol.Trim();
            var result = _scripMaster.FirstOrDefault(s => s.Segment == "I" && s.InstrumentType == "INDEX" &&
                                               (s.TradingSymbol.Equals(term, StringComparison.OrdinalIgnoreCase) ||
                                                s.SemInstrumentName.Contains(term, StringComparison.OrdinalIgnoreCase)));
            if (result == null) Debug.WriteLine($"[RESOLVER_FAIL] No index found for: {tradingSymbol}");
            else Debug.WriteLine($"[RESOLVER_SUCCESS] Found index: {result.SemInstrumentName} | {result.SecurityId}");
            return result?.SecurityId;
        }

        public string? FindSecurityId(string underlyingSymbol, string expiry, decimal strike, string optionType)
        {
            if (!DateTime.TryParse(expiry, out var targetDate)) return null;
            var term = underlyingSymbol.Trim();
            var result = _scripMaster
                .FirstOrDefault(s => s.Segment == "O" && s.InstrumentType == "OPTIDX" &&
                                      s.UnderlyingSymbol.Equals(term, StringComparison.OrdinalIgnoreCase) &&
                                      s.ExpiryDate.HasValue && s.ExpiryDate.Value.Date == targetDate.Date &&
                                      s.StrikePrice == strike && s.OptionType.Equals(optionType, StringComparison.OrdinalIgnoreCase));

            if (result == null) Debug.WriteLine($"No index option found for: {underlyingSymbol}, {expiry}, {strike}, {optionType}");
            return result?.SecurityId;
        }

        // Helper methods for safe parsing and CSV reading
        private string GetSafeValue(string[] values, Dictionary<string, int> headerMap, string columnName) => headerMap.TryGetValue(columnName, out int index) && index < values.Length ? values[index]?.Trim() ?? string.Empty : string.Empty;
        private int ParseIntSafe(string value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? (int)result : 0;
        private decimal ParseDecimalSafe(string value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0;
        private string[] ParseCsvLine(string line)
        {
            var values = new List<string>();
            var current_value = new StringBuilder();
            bool in_quotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (in_quotes && i < line.Length - 1 && line[i + 1] == '"')
                    {
                        current_value.Append('"');
                        i++;
                    }
                    else
                    {
                        in_quotes = !in_quotes;
                    }
                }
                else if (c == ',' && !in_quotes) { values.Add(current_value.ToString()); current_value.Clear(); }
                else { current_value.Append(c); }
            }
            values.Add(current_value.ToString());
            return values.ToArray();
        }
        public int GetLotSizeForSecurity(string securityId) => _scripMaster.FirstOrDefault(s => s.SecurityId == securityId)?.LotSize ?? 0;
    }
}

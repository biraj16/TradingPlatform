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
        private List<ScripInfo> _scripMaster = new List<ScripInfo>();
        private readonly HttpClient _httpClient;
        private const string ScripMasterUrl = "https://images.dhan.co/api-data/api-scrip-master-detailed.csv";

        public ScripMasterService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        public async Task LoadScripMasterAsync()
        {
            try
            {
                Debug.WriteLine("[ScripMaster] Starting to download detailed scrip master CSV...");
                var csvData = await _httpClient.GetStringAsync(ScripMasterUrl);
                Debug.WriteLine($"[ScripMaster] Downloaded detailed CSV data: {csvData.Length} characters");

                var tempMaster = new List<ScripInfo>();
                var allowedTypes = new HashSet<string> { "EQUITY", "FUTIDX", "FUTSTK", "INDEX", "OPTIDX", "OPTSTK" };

                using (var reader = new StringReader(csvData))
                {
                    var headerLine = await reader.ReadLineAsync();
                    if (headerLine == null)
                    {
                        Debug.WriteLine("[ScripMaster] CSV header line is null. Aborting load.");
                        return;
                    }

                    var headers = ParseCsvLine(headerLine);
                    var headerMap = new Dictionary<string, int>();
                    for (int i = 0; i < headers.Length; i++)
                    {
                        headerMap[headers[i].Trim().ToUpperInvariant()] = i;
                    }

                    const string Col_InstrumentType = "INSTRUMENT_TYPE";
                    const string Col_Segment = "SEGMENT";
                    const string Col_SecurityId = "SECURITY_ID";
                    const string Col_UnderlyingSymbol = "UNDERLYING_SYMBOL";
                    const string Col_TradingSymbol = "SYMBOL_NAME";
                    const string Col_LotSize = "LOT_SIZE";
                    const string Col_ExpiryDate = "SM_EXPIRY_DATE";
                    const string Col_StrikePrice = "STRIKE_PRICE";
                    const string Col_OptionType = "OPTION_TYPE";
                    const string Col_CustomSymbol = "DISPLAY_NAME";

                    var requiredColumns = new[] {
                        Col_InstrumentType, Col_Segment, Col_SecurityId, Col_UnderlyingSymbol,
                        Col_TradingSymbol, Col_LotSize, Col_ExpiryDate, Col_StrikePrice, Col_OptionType, Col_CustomSymbol
                    };

                    foreach (var col in requiredColumns)
                    {
                        if (!headerMap.ContainsKey(col.ToUpperInvariant()))
                        {
                            Debug.WriteLine($"[ScripMaster] CRITICAL ERROR: Required column '{col}' not found in detailed file headers. Headers found: {string.Join(", ", headers)}");
                            return;
                        }
                    }

                    string? line;
                    int parsedRelevantCount = 0;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        var values = ParseCsvLine(line);
                        if (values.Length <= requiredColumns.Max(c => headerMap[c.ToUpperInvariant()]))
                        {
                            Debug.WriteLine($"[ScripMaster] Skipping malformed line (too few columns): {line}");
                            continue;
                        }

                        var instrumentType = GetSafeValue(values, headerMap, Col_InstrumentType);

                        if (allowedTypes.Contains(instrumentType))
                        {
                            var scrip = new ScripInfo
                            {
                                Segment = GetSafeValue(values, headerMap, Col_Segment),
                                SecurityId = GetSafeValue(values, headerMap, Col_SecurityId),
                                InstrumentType = instrumentType,
                                SemInstrumentName = GetSafeValue(values, headerMap, Col_CustomSymbol),
                                TradingSymbol = GetSafeValue(values, headerMap, Col_TradingSymbol),
                                UnderlyingSymbol = GetSafeValue(values, headerMap, Col_UnderlyingSymbol),
                                LotSize = ParseIntSafe(GetSafeValue(values, headerMap, Col_LotSize)),
                                ExpiryDate = FormatDate(GetSafeValue(values, headerMap, Col_ExpiryDate)),
                                StrikePrice = ParseDecimalSafe(GetSafeValue(values, headerMap, Col_StrikePrice)),
                                OptionType = GetSafeValue(values, headerMap, Col_OptionType),
                            };
                            tempMaster.Add(scrip);

                            if (parsedRelevantCount < 20)
                            {
                                Debug.WriteLine($"[ScripMaster] Parsed {scrip.InstrumentType}: " +
                                                $"ID={scrip.SecurityId}, " +
                                                $"TradingSymbol='{scrip.TradingSymbol}', " +
                                                $"UnderlyingSymbol='{scrip.UnderlyingSymbol}', " +
                                                $"SemInstrumentName='{scrip.SemInstrumentName}', " +
                                                $"Expiry={scrip.ExpiryDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}, " +
                                                $"Strike={scrip.StrikePrice}, " +
                                                $"OptionType='{scrip.OptionType}'");
                                parsedRelevantCount++;
                            }
                        }
                    }
                }
                _scripMaster = tempMaster;
                Debug.WriteLine($"[ScripMaster] Scrip Master loaded with {_scripMaster.Count} relevant instruments.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScripMaster] FAILED to load scrip master: {ex.GetType().Name} - {ex.Message}");
                Debug.WriteLine(ex.StackTrace);
            }
        }

        private DateTime? FormatDate(string dateStr)
        {
            if (string.IsNullOrWhiteSpace(dateStr) || dateStr == "0") return null;

            string[] formats = new[]
            {
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd",
                "yyyyMMdd"
            };

            foreach (string format in formats)
            {
                if (DateTime.TryParseExact(dateStr.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return date;
                }
            }

            Debug.WriteLine($"[ScripMaster_FormatDate] Could not parse date string with any known format: '{dateStr}'");
            return null;
        }

        public string? FindEquitySecurityId(string tradingSymbol)
        {
            var term = tradingSymbol.Trim().ToUpperInvariant();
            Debug.WriteLine($"[ScripMaster_FindEquity] Searching for EQUITY: '{term}'");

            // Prioritize exact match on TradingSymbol (SYMBOL_NAME)
            var result = _scripMaster.FirstOrDefault(s => s.InstrumentType == "EQUITY" &&
                                                         s.TradingSymbol.ToUpperInvariant().Equals(term));
            if (result != null)
            {
                Debug.WriteLine($"[ScripMaster_FindEquity] SUCCESS - Found equity via TradingSymbol: {result.TradingSymbol} | ID: {result.SecurityId}");
                return result.SecurityId;
            }

            // Fallback: Exact match on SemInstrumentName (DISPLAY_NAME)
            result = _scripMaster.FirstOrDefault(s => s.InstrumentType == "EQUITY" &&
                                                     s.SemInstrumentName.ToUpperInvariant().Equals(term));
            if (result != null)
            {
                Debug.WriteLine($"[ScripMaster_FindEquity] SUCCESS - Found equity via SemInstrumentName: {result.SemInstrumentName} | ID: {result.SecurityId}");
                return result.SecurityId;
            }

            // New: Last resort - Contains match on TradingSymbol (SYMBOL_NAME) for partial matches
            // This can help with cases like "HDFCBANK" vs "HDFCBANK-EQ" if the full name isn't used in the app
            result = _scripMaster.FirstOrDefault(s => s.InstrumentType == "EQUITY" &&
                                                     s.TradingSymbol.ToUpperInvariant().Contains(term));
            if (result != null)
            {
                Debug.WriteLine($"[ScripMaster_FindEquity] SUCCESS - Found equity via TradingSymbol (contains): {result.TradingSymbol} | ID: {result.SecurityId}");
                return result.SecurityId;
            }

            // Additional fallback: Contains match on SemInstrumentName (DISPLAY_NAME)
            result = _scripMaster.FirstOrDefault(s => s.InstrumentType == "EQUITY" &&
                                                     s.SemInstrumentName.ToUpperInvariant().Contains(term));
            if (result != null)
            {
                Debug.WriteLine($"[ScripMaster_FindEquity] SUCCESS - Found equity via SemInstrumentName (contains): {result.SemInstrumentName} | ID: {result.SecurityId}");
                return result.SecurityId;
            }


            Debug.WriteLine($"[ScripMaster_FindEquity] FAIL - No equity found for: '{tradingSymbol}'");
            return null;
        }

        public string? FindIndexSecurityId(string tradingSymbol)
        {
            var term = tradingSymbol.Trim().ToUpperInvariant();
            Debug.WriteLine($"[ScripMaster_FindIndex] Searching for SPOT INDEX using UNDERLYING_SYMBOL: '{term}'");
            var result = _scripMaster.FirstOrDefault(s => s.InstrumentType == "INDEX" && s.UnderlyingSymbol.ToUpperInvariant().Equals(term));

            if (result == null) Debug.WriteLine($"[ScripMaster_FindIndex] FAIL - No spot index found for: '{tradingSymbol}'");
            else Debug.WriteLine($"[ScripMaster_FindIndex] SUCCESS - Found spot index: {result.UnderlyingSymbol} | ID: {result.SecurityId}");

            return result?.SecurityId;
        }

        /// <summary>
        /// Finds a derivative (Future or Stock Future) security ID based on its underlying symbol.
        /// Prioritizes near-month expiry.
        /// </summary>
        /// <param name="underlyingSymbol">The underlying symbol (e.g., "NIFTY", "RELIANCE").</param>
        /// <returns>The SecurityId of the nearest month future, or null if not found.</returns>
        public string? FindNearMonthFutureSecurityId(string underlyingSymbol)
        {
            var term = underlyingSymbol.Trim().ToUpperInvariant();
            Debug.WriteLine($"[ScripMaster_FindFuture] Searching for NEAR MONTH FUTURE for underlying: '{term}'");

            // Filter for Futures (Index or Stock) and active expiry dates
            var futures = _scripMaster
                .Where(s => (s.InstrumentType == "FUTIDX" || s.InstrumentType == "FUTSTK") &&
                            s.ExpiryDate.HasValue && s.ExpiryDate.Value.Date >= DateTime.Today)
                .ToList();

            // Try to match using UnderlyingSymbol first (most direct)
            var directMatch = futures
                .Where(s => s.UnderlyingSymbol.ToUpperInvariant().Equals(term))
                .OrderBy(s => s.ExpiryDate)
                .FirstOrDefault();

            if (directMatch != null)
            {
                Debug.WriteLine($"[ScripMaster_FindFuture] SUCCESS - Found future via direct UnderlyingSymbol match: {directMatch.TradingSymbol} | ID: {directMatch.SecurityId}");
                return directMatch.SecurityId;
            }

            // Fallback: Try to match using TradingSymbol (SYMBOL_NAME) if it contains the underlying symbol
            // This is especially relevant for index futures where SYMBOL_NAME might be like "NIFTY24JULFUT"
            var tradingSymbolMatch = futures
                .Where(s => s.TradingSymbol.ToUpperInvariant().Contains(term))
                .OrderBy(s => s.ExpiryDate)
                .FirstOrDefault();

            if (tradingSymbolMatch != null)
            {
                Debug.WriteLine($"[ScripMaster_FindFuture] SUCCESS - Found future via TradingSymbol (contains) match: {tradingSymbolMatch.TradingSymbol} | ID: {tradingSymbolMatch.SecurityId}");
                return tradingSymbolMatch.SecurityId;
            }

            // Last resort: Try to match via SemInstrumentName (DISPLAY_NAME) if it contains the underlying symbol
            var semInstrumentNameMatch = futures
                .Where(s => s.SemInstrumentName.ToUpperInvariant().Contains(term))
                .OrderBy(s => s.ExpiryDate)
                .FirstOrDefault();

            if (semInstrumentNameMatch != null)
            {
                Debug.WriteLine($"[ScripMaster_FindFuture] SUCCESS - Found future via SemInstrumentName (contains) match: {semInstrumentNameMatch.TradingSymbol} | ID: {semInstrumentNameMatch.SecurityId}");
                return semInstrumentNameMatch.SecurityId;
            }

            // --- New Debugging: Log details of futures that were considered but not matched ---
            if (futures.Any())
            {
                Debug.WriteLine($"[ScripMaster_FindFuture] DEBUG: No future found for '{term}' through standard methods. Examining first 5 relevant FUTIDX entries:");
                // Filter for FUTIDX that *might* be related to the term, even if not a direct match
                var relevantFuturesForDebug = _scripMaster
                    .Where(s => s.InstrumentType == "FUTIDX" &&
                                (s.UnderlyingSymbol.ToUpperInvariant().Contains(term) ||
                                 s.TradingSymbol.ToUpperInvariant().Contains(term) ||
                                 s.SemInstrumentName.ToUpperInvariant().Contains(term)))
                    .OrderBy(s => s.ExpiryDate)
                    .Take(5)
                    .ToList();

                if (relevantFuturesForDebug.Any())
                {
                    foreach (var f in relevantFuturesForDebug)
                    {
                        Debug.WriteLine($"  - TradingSymbol: '{f.TradingSymbol}', UnderlyingSymbol: '{f.UnderlyingSymbol}', SemInstrumentName: '{f.SemInstrumentName}', Expiry: {f.ExpiryDate?.ToString("yyyy-MM-dd")}, SecurityId: {f.SecurityId}");
                    }
                }
                else
                {
                    Debug.WriteLine($"[ScripMaster_FindFuture] DEBUG: No relevant FUTIDX entries found for '{term}' in the master list.");
                }
            }


            Debug.WriteLine($"[ScripMaster_FindFuture] FAIL - No future contract found for underlying: '{underlyingSymbol}'");
            return null;
        }

        public string? FindDerivativeSecurityId(string baseSymbol)
        {
            Debug.WriteLine($"[ScripMaster_FindDerivative] Request for DERIVATIVE ID for: '{baseSymbol}' (delegating to FindNearMonthFutureSecurityId)");
            return FindNearMonthFutureSecurityId(baseSymbol);
        }


        public string? FindOptionSecurityId(string underlyingSymbol, DateTime expiryDate, decimal strikePrice, string optionType)
        {
            var termUnderlying = underlyingSymbol.Trim().ToUpperInvariant();
            var termOptionType = optionType.Trim().ToUpperInvariant();
            Debug.WriteLine($"[ScripMaster_FindOption] Searching for OPTION: Underlying='{termUnderlying}', Expiry='{expiryDate:yyyy-MM-dd}', Strike={strikePrice}, Type='{termOptionType}'");

            var options = _scripMaster
                .Where(s => (s.InstrumentType == "OPTIDX" || s.InstrumentType == "OPTSTK") &&
                            s.ExpiryDate.HasValue && s.ExpiryDate.Value.Date == expiryDate.Date &&
                            s.StrikePrice == strikePrice &&
                            s.OptionType.ToUpperInvariant().Equals(termOptionType))
                .ToList();

            // Try to match using UnderlyingSymbol first (most direct)
            var directMatch = options
                .FirstOrDefault(s => s.UnderlyingSymbol.ToUpperInvariant().Equals(termUnderlying));

            if (directMatch != null)
            {
                Debug.WriteLine($"[ScripMaster_FindOption] SUCCESS - Found option via direct UnderlyingSymbol match: {directMatch.TradingSymbol} | ID: {directMatch.SecurityId}");
                return directMatch.SecurityId;
            }

            // Fallback: Try to match using TradingSymbol (SYMBOL_NAME) if it contains the underlying symbol
            var tradingSymbolMatch = options
                .FirstOrDefault(s => s.TradingSymbol.ToUpperInvariant().Contains(termUnderlying));

            if (tradingSymbolMatch != null)
            {
                Debug.WriteLine($"[ScripMaster_FindOption] SUCCESS - Found option via TradingSymbol (contains) match: {tradingSymbolMatch.TradingSymbol} | ID: {tradingSymbolMatch.SecurityId}");
                return tradingSymbolMatch.SecurityId;
            }

            // Last resort: Try to match via SemInstrumentName (DISPLAY_NAME) if it contains the full components
            var semInstrumentNameMatch = options
                .FirstOrDefault(s => s.SemInstrumentName.ToUpperInvariant().Contains(termUnderlying) &&
                                     s.SemInstrumentName.ToUpperInvariant().Contains(expiryDate.ToString("ddMMMyy").ToUpperInvariant()) &&
                                     s.SemInstrumentName.ToUpperInvariant().Contains(strikePrice.ToString(CultureInfo.InvariantCulture)) &&
                                     s.SemInstrumentName.ToUpperInvariant().Contains(termOptionType));

            if (semInstrumentNameMatch != null)
            {
                Debug.WriteLine($"[ScripMaster_FindOption] SUCCESS - Found option via SemInstrumentName (full match) match: {semInstrumentNameMatch.TradingSymbol} | ID: {semInstrumentNameMatch.SecurityId}");
                return semInstrumentNameMatch.SecurityId;
            }

            Debug.WriteLine($"[ScripMaster_FindOption] FAIL - No option found for the given parameters.");
            return null;
        }


        private string GetSafeValue(string[] values, Dictionary<string, int> headerMap, string columnName)
        {
            var upperColumnName = columnName.ToUpperInvariant();
            if (headerMap.TryGetValue(upperColumnName, out int index) && index < values.Length)
            {
                return values[index]?.Trim() ?? string.Empty;
            }
            Debug.WriteLine($"[ScripMaster_GetSafeValue] Column '{columnName}' not found or index out of bounds.");
            return string.Empty;
        }

        private int ParseIntSafe(string value)
        {
            if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            {
                return (int)result;
            }
            Debug.WriteLine($"[ScripMaster_ParseIntSafe] Could not parse int from value: '{value}'");
            return 0;
        }

        private decimal ParseDecimalSafe(string value)
        {
            if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }
            Debug.WriteLine($"[ScripMaster_ParseDecimalSafe] Could not parse decimal from value: '{value}'");
            return 0;
        }

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
                        i++; // Skip the escaped quote
                    }
                    else
                    {
                        in_quotes = !in_quotes;
                    }
                }
                else if (c == ',' && !in_quotes)
                {
                    values.Add(current_value.ToString().Trim()); // Trim values during parsing
                    current_value.Clear();
                }
                else
                {
                    current_value.Append(c);
                }
            }
            values.Add(current_value.ToString().Trim()); // Add the last value and trim
            return values.ToArray();
        }

        public int GetLotSizeForSecurity(string securityId)
        {
            var lotSize = _scripMaster.FirstOrDefault(s => s.SecurityId == securityId)?.LotSize ?? 0;
            if (lotSize == 0)
            {
                Debug.WriteLine($"[ScripMaster_LotSize] Could not find lot size for SecurityId: {securityId}");
            }
            return lotSize;
        }
    }
}

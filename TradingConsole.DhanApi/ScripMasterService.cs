using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions; // Added for regex
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
                // These allowed types are the values expected in the "INSTRUMENT" column (5th column)
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

                    // --- CORRECTED COLUMN NAMES BASED ON USER'S PROVIDED ORDER ---
                    const string Col_ExchId = "EXCH_ID";                // User's 1st column
                    const string Col_Segment = "SEGMENT";               // User's 2nd column
                    const string Col_SecurityId = "SECURITY_ID";        // User's 3rd column
                    const string Col_Instrument = "INSTRUMENT";         // User's 5th column - This is the actual instrument type column
                    const string Col_UnderlyingSymbol = "UNDERLYING_SYMBOL"; // User's 7th column
                    const string Col_TradingSymbol = "SYMBOL_NAME";     // User's 8th column
                    const string Col_CustomSymbol = "DISPLAY_NAME";     // User's 9th column
                    const string Col_LotSize = "LOT_SIZE";              // User's 12th column
                    const string Col_ExpiryDate = "SM_EXPIRY_DATE";     // User's 13th column
                    const string Col_StrikePrice = "STRIKE_PRICE";      // User's 14th column
                    const string Col_OptionType = "OPTION_TYPE";        // User's 15th column


                    var requiredColumns = new[] {
                        Col_ExchId, Col_Instrument, Col_Segment, Col_SecurityId, Col_UnderlyingSymbol,
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

                        // Get instrument type from the "INSTRUMENT" column (5th column)
                        var instrumentType = GetSafeValue(values, headerMap, Col_Instrument);
                        var exchId = GetSafeValue(values, headerMap, Col_ExchId);
                        var segment = GetSafeValue(values, headerMap, Col_Segment);


                        if (allowedTypes.Contains(instrumentType))
                        {
                            var scrip = new ScripInfo
                            {
                                ExchId = exchId, // Populate ExchId
                                Segment = MapCsvSegmentToApiSegment(exchId, segment, instrumentType), // Map to API-compatible segment
                                SecurityId = GetSafeValue(values, headerMap, Col_SecurityId),
                                InstrumentType = instrumentType, // Populated from "INSTRUMENT" column
                                SemInstrumentName = GetSafeValue(values, headerMap, Col_CustomSymbol), // DISPLAY_NAME (9th col)
                                TradingSymbol = GetSafeValue(values, headerMap, Col_TradingSymbol), // SYMBOL_NAME (8th col)
                                UnderlyingSymbol = GetSafeValue(values, headerMap, Col_UnderlyingSymbol), // UNDERLYING_SYMBOL (7th col)
                                LotSize = ParseIntSafe(GetSafeValue(values, headerMap, Col_LotSize)),
                                ExpiryDate = FormatDate(GetSafeValue(values, headerMap, Col_ExpiryDate)),
                                StrikePrice = ParseDecimalSafe(GetSafeValue(values, headerMap, Col_StrikePrice)),
                                OptionType = GetSafeValue(values, headerMap, Col_OptionType),
                            };
                            tempMaster.Add(scrip);

                            if (parsedRelevantCount < 20) // Log first few relevant entries
                            {
                                Debug.WriteLine($"[ScripMaster] Parsed {scrip.InstrumentType}: " +
                                                $"ExchId='{scrip.ExchId}', Segment='{scrip.Segment}', ID={scrip.SecurityId}, " +
                                                $"TradingSymbol='{scrip.TradingSymbol}', UnderlyingSymbol='{scrip.UnderlyingSymbol}', " +
                                                $"SemInstrumentName='{scrip.SemInstrumentName}', Expiry={scrip.ExpiryDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}, " +
                                                $"Strike={scrip.StrikePrice}, OptionType='{scrip.OptionType}'");
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

        // Helper to map CSV segment to API-compatible segment
        private string MapCsvSegmentToApiSegment(string exchId, string csvSegment, string instrumentType)
        {
            // For Equity, use NSE_EQ or BSE_EQ based on ExchId
            if (instrumentType == "EQUITY")
            {
                return exchId.ToUpperInvariant() == "NSE" ? "NSE_EQ" : "BSE_EQ";
            }
            // For Futures and Options, use NSE_FNO or BSE_FNO based on ExchId
            else if (instrumentType == "FUTIDX" || instrumentType == "FUTSTK" || instrumentType == "OPTIDX" || instrumentType == "OPTSTK")
            {
                return exchId.ToUpperInvariant() == "NSE" ? "NSE_FNO" : "BSE_FNO";
            }
            // For spot indices, use IDX_I (as per Dhan API docs, even if CSV says something else)
            else if (instrumentType == "INDEX")
            {
                return "IDX_I";
            }
            // Default or unknown segments
            return csvSegment; // Fallback to original CSV segment if no specific mapping
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

        /// <summary>
        /// Helper method to remove all whitespace from a string.
        /// </summary>
        private string RemoveWhitespace(string input)
        {
            return new string(input.Where(c => !char.IsWhiteSpace(c)).ToArray());
        }

        /// <summary>
        /// Helper method to check if a string contains any digits.
        /// </summary>
        private bool ContainsDigit(string input)
        {
            return input.Any(char.IsDigit);
        }


        public string? FindEquitySecurityId(string tradingSymbol)
        {
            var term = RemoveWhitespace(tradingSymbol).ToUpperInvariant(); // Remove whitespace from search term
            Debug.WriteLine($"[ScripMaster_FindEquity] Searching for EQUITY: '{tradingSymbol}' (normalized: '{term}') (contains match on DISPLAY_NAME)");

            // As per user: "NSE for the 1st column, select equity in the 5th column then in the 9th column we can search for the security"
            var result = _scripMaster.FirstOrDefault(s => s.ExchId.ToUpperInvariant().Equals("NSE") && // Filter by EXCH_ID (1st column)
                                                         s.InstrumentType == "EQUITY" &&             // Filter by INSTRUMENT (5th column)
                                                         RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains(term)); // Contains match on DISPLAY_NAME (9th column), after removing whitespace
            if (result != null)
            {
                Debug.WriteLine($"[ScripMaster_FindEquity] SUCCESS - Found equity: '{result.SemInstrumentName}' | ID: {result.SecurityId}");
                return result.SecurityId;
            }

            // Fallback for INFY and similar cases: try searching SYMBOL_NAME (8th column) with contains
            // This is a pragmatic fallback for cases where DISPLAY_NAME doesn't contain the short symbol.
            result = _scripMaster.FirstOrDefault(s => s.ExchId.ToUpperInvariant().Equals("NSE") &&
                                                     s.InstrumentType == "EQUITY" &&
                                                     RemoveWhitespace(s.TradingSymbol).ToUpperInvariant().Contains(term));
            if (result != null)
            {
                Debug.WriteLine($"[ScripMaster_FindEquity] SUCCESS - Found equity via TradingSymbol (contains): '{result.TradingSymbol}' | ID: {result.SecurityId}");
                return result.SecurityId;
            }


            Debug.WriteLine($"[ScripMaster_FindEquity] FAIL - No equity found for: '{tradingSymbol}'");
            return null;
        }

        public ScripInfo? FindIndexScripInfo(string indexName)
        {
            var term = RemoveWhitespace(indexName).ToUpperInvariant();
            Debug.WriteLine($"[ScripMaster_FindIndexScripInfo] Searching for SPOT INDEX: '{indexName}' (normalized: '{term}') (exact match on DISPLAY_NAME)");

            var result = _scripMaster.FirstOrDefault(s => s.InstrumentType == "INDEX" &&
                                                         RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Equals(term));

            if (result == null) Debug.WriteLine($"[ScripMaster_FindIndexScripInfo] FAIL - No spot index found for: '{indexName}'");
            else Debug.WriteLine($"[ScripMaster_FindIndexScripInfo] SUCCESS - Found spot index: '{result.SemInstrumentName}' | ID: {result.SecurityId}, ExchId: {result.ExchId}");

            return result;
        }

        public string? FindIndexSecurityId(string indexName)
        {
            return FindIndexScripInfo(indexName)?.SecurityId;
        }


        /// <summary>
        /// Finds a derivative (Future or Stock Future) ScripInfo based on its underlying symbol.
        /// Prioritizes near-month expiry.
        /// </summary>
        /// <param name="underlyingSymbol">The underlying symbol (e.g., "NIFTY", "RELIANCE", "Nifty 50", "Nifty Bank").</param>
        /// <returns>The ScripInfo of the nearest month future, or null if not found.</returns>
        public ScripInfo? FindNearMonthFutureSecurityId(string underlyingSymbol)
        {
            var normalizedUnderlyingSymbol = RemoveWhitespace(underlyingSymbol).ToUpperInvariant();
            Debug.WriteLine($"[ScripMaster_FindFuture] Searching for NEAR MONTH FUTURE for underlying: '{underlyingSymbol}' (normalized: '{normalizedUnderlyingSymbol}')");

            var futures = _scripMaster
                .Where(s => (s.InstrumentType == "FUTIDX" || s.InstrumentType == "FUTSTK") &&
                            s.ExpiryDate.HasValue && s.ExpiryDate.Value.Date >= DateTime.Today)
                .ToList();

            // --- Stock Futures (FUTSTK) ---
            // Prioritize exact match on UnderlyingSymbol for stock futures
            var matchingStockFutures = futures
                .Where(s => s.ExchId.ToUpperInvariant().Equals("NSE") && s.InstrumentType == "FUTSTK" &&
                            s.UnderlyingSymbol.ToUpperInvariant().Equals(normalizedUnderlyingSymbol))
                .OrderBy(s => s.ExpiryDate)
                .ToList();

            if (matchingStockFutures.Any())
            {
                var result = matchingStockFutures.FirstOrDefault();
                Debug.WriteLine($"[ScripMaster_FindFuture] SUCCESS - Found stock future: '{result?.SemInstrumentName}' | ID: {result?.SecurityId}");
                return result;
            }

            // --- Index Futures (FUTIDX) ---
            // Specific logic for Nifty and Bank Nifty as per user's instructions
            if (normalizedUnderlyingSymbol == "NIFTY50") // Input from MainViewModel for Nifty 50
            {
                // Search for NIFTY futures, explicitly excluding BANKNIFTY and FINNIFTY, and ensuring no day numbers
                var niftyFutures = futures
                    .Where(s => s.InstrumentType == "FUTIDX" &&
                                RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("NIFTY") &&
                                !RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("BANKNIFTY") &&
                                !RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("FINNIFTY") && // Exclude FINNIFTY
                                                                                                                  // Ensure no digits are present between 'NIFTY' and 'FUT' (e.g., "NIFTY 08 JUL FUT")
                                !Regex.IsMatch(RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant(), @"NIFTY\s*\d+\s*[A-Z]{3}\s*FUT"))
                    .OrderBy(s => s.ExpiryDate)
                    .ToList();
                if (niftyFutures.Any())
                {
                    var result = niftyFutures.FirstOrDefault();
                    Debug.WriteLine($"[ScripMaster_FindFuture] SUCCESS - Found Nifty future: '{result?.SemInstrumentName}' | ID: {result?.SecurityId}");
                    return result;
                }
            }
            else if (normalizedUnderlyingSymbol == "NIFTYBANK") // Input from MainViewModel for Nifty Bank
            {
                // Search for BANKNIFTY futures
                var bankNiftyFutures = futures
                    .Where(s => s.InstrumentType == "FUTIDX" &&
                                RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("BANKNIFTY"))
                    .OrderBy(s => s.ExpiryDate)
                    .ToList();
                if (bankNiftyFutures.Any())
                {
                    var result = bankNiftyFutures.FirstOrDefault();
                    Debug.WriteLine($"[ScripMaster_FindFuture] SUCCESS - Found BankNifty future: '{result?.SemInstrumentName}' | ID: {result?.SecurityId}");
                    return result;
                }
            }
            else if (normalizedUnderlyingSymbol == "SENSEX")
            {
                // Sensex logic: Should contain "SENSEX" and "FUT" but NOT have a number between them.
                var sensexFutures = futures
                    .Where(s => s.InstrumentType == "FUTIDX" &&
                                RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("SENSEX") &&
                                RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("FUT") &&
                                // Regex to ensure no digits between SENSEX and FUT
                                !Regex.IsMatch(RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant(), @"SENSEX\s*\d+\s*[A-Z]{3}\s*FUT")) // Exclude "SENSEX 08 JUL FUT"
                    .OrderBy(s => s.ExpiryDate)
                    .ToList();
                if (sensexFutures.Any())
                {
                    var result = sensexFutures.FirstOrDefault();
                    Debug.WriteLine($"[ScripMaster_FindFuture] SUCCESS - Found Sensex future: '{result?.SemInstrumentName}' | ID: {result?.SecurityId}");
                    return result;
                }
            }
            else
            {
                // Generic fallback for other index futures if any (e.g., if a new index is added)
                var genericIndexFutures = futures
                    .Where(s => s.InstrumentType == "FUTIDX" &&
                                s.UnderlyingSymbol.ToUpperInvariant().Equals(normalizedUnderlyingSymbol))
                    .OrderBy(s => s.ExpiryDate)
                    .ToList();
                if (genericIndexFutures.Any())
                {
                    var result = genericIndexFutures.FirstOrDefault();
                    Debug.WriteLine($"[ScripMaster_FindFuture] SUCCESS - Found generic index future: '{result?.SemInstrumentName}' | ID: {result?.SecurityId}");
                    return result;
                }
            }

            // --- Debugging: Log details of all relevant futures that were considered but not matched ---
            if (futures.Any())
            {
                Debug.WriteLine($"[ScripMaster_FindFuture] DEBUG: No future found for '{underlyingSymbol}' (normalized: '{normalizedUnderlyingSymbol}') through DISPLAY_NAME contains. Examining first 5 relevant FUTIDX/FUTSTK entries:");
                // Take from the already filtered 'futures' list for relevant entries
                var relevantFuturesForDebug = futures.Take(5).ToList();

                if (relevantFuturesForDebug.Any())
                {
                    foreach (var f in relevantFuturesForDebug)
                    {
                        Debug.WriteLine($"  - ExchId: '{f.ExchId}', Segment: '{f.Segment}', TradingSymbol: '{f.TradingSymbol}', UnderlyingSymbol: '{f.UnderlyingSymbol}', SemInstrumentName: '{f.SemInstrumentName}', Expiry: {f.ExpiryDate?.ToString("yyyy-MM-dd")}, SecurityId: {f.SecurityId}");
                    }
                }
                else
                {
                    Debug.WriteLine($"[ScripMaster_FindFuture] DEBUG: No relevant FUTIDX/FUTSTK entries found for '{underlyingSymbol}' (normalized: '{normalizedUnderlyingSymbol}') in the master list after initial filtering.");
                }
            }

            Debug.WriteLine($"[ScripMaster_FindFuture] FAIL - No future contract found for underlying: '{underlyingSymbol}'");
            return null;
        }

        public string? FindDerivativeSecurityId(string baseSymbol)
        {
            Debug.WriteLine($"[ScripMaster_FindDerivative] Request for DERIVATIVE ID for: '{baseSymbol}' (delegating to FindNearMonthFutureSecurityId)");
            return FindNearMonthFutureSecurityId(baseSymbol)?.SecurityId; // Return only SecurityId for this wrapper
        }

        // In TradingConsole.DhanApi/ScripMasterService.cs

        public string? FindOptionSecurityId(string underlyingSymbol, DateTime expiryDate, decimal strikePrice, string optionType)
        {
            var termUnderlying = RemoveWhitespace(underlyingSymbol).ToUpperInvariant();
            var termOptionType = optionType.Trim().ToUpperInvariant();
            Debug.WriteLine($"[ScripMaster_FindOption] Searching for OPTION: Underlying='{underlyingSymbol}' (normalized: '{termUnderlying}'), Expiry='{expiryDate:yyyy-MM-dd}', Strike={strikePrice}, Type='{termOptionType}'");

            // Pre-filter by the exact properties we know for sure. This is efficient.
            var potentialOptions = _scripMaster
                .Where(s => (s.InstrumentType == "OPTIDX" || s.InstrumentType == "OPTSTK") &&
                            s.ExpiryDate.HasValue && s.ExpiryDate.Value.Date == expiryDate.Date &&
                            s.StrikePrice == strikePrice &&
                            s.OptionType.ToUpperInvariant().Equals(termOptionType))
                .ToList();

            if (!potentialOptions.Any())
            {
                Debug.WriteLine($"[ScripMaster_FindOption] FAIL - No options found after initial filtering by expiry, strike, and type.");
                return null;
            }

            // Adjust the search term for common index names
            string adjustedUnderlyingForSearch = termUnderlying;
            if (termUnderlying == "NIFTY50")
            {
                adjustedUnderlyingForSearch = "NIFTY";
            }
            else if (termUnderlying == "NIFTYBANK")
            {
                adjustedUnderlyingForSearch = "BANKNIFTY";
            }

            // Now, from the potential candidates, find the one with the matching display name.
            // This is more reliable than trying to construct a perfect string.
            var matchingOption = potentialOptions.FirstOrDefault(s => {
                var displayName = RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant();

                // Check if the display name contains the correct underlying symbol.
                // This handles cases like "NIFTY" vs "BANKNIFTY".
                return displayName.Contains(adjustedUnderlyingForSearch);
            });

            if (matchingOption != null)
            {
                Debug.WriteLine($"[ScripMaster_FindOption] SUCCESS - Found option: '{matchingOption.SemInstrumentName}' | ID: {matchingOption.SecurityId}");
                return matchingOption.SecurityId;
            }

            Debug.WriteLine($"[ScripMaster_FindOption] FAIL - Could not find a matching display name among potential candidates for underlying: '{adjustedUnderlyingForSearch}'");
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
            // Suppress logging for empty strings, as they are a common occurrence for optional fields
            if (!string.IsNullOrEmpty(value))
            {
                Debug.WriteLine($"[ScripMaster_ParseIntSafe] Could not parse int from value: '{value}'");
            }
            return 0;
        }

        private decimal ParseDecimalSafe(string value)
        {
            if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }
            // Suppress logging for empty strings, as they are a common occurrence for optional fields
            if (!string.IsNullOrEmpty(value))
            {
                Debug.WriteLine($"[ScripMaster_ParseDecimalSafe] Could not parse decimal from value: '{value}'");
            }
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

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

                        if (allowedTypes.Contains(instrumentType))
                        {
                            var scrip = new ScripInfo
                            {
                                ExchId = GetSafeValue(values, headerMap, Col_ExchId), // Populate ExchId
                                Segment = GetSafeValue(values, headerMap, Col_Segment),
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

        public string? FindIndexSecurityId(string indexName)
        {
            var term = RemoveWhitespace(indexName).ToUpperInvariant(); // Remove whitespace from search term
            Debug.WriteLine($"[ScripMaster_FindIndex] Searching for SPOT INDEX: '{indexName}' (normalized: '{term}') (exact match on DISPLAY_NAME)");
            // As per user: "in the 5th column we select INDEX. in the 9th column we should search for exact match "Nifty 50", "Nifty Bank", "Sensex"."
            // No EXCH_ID filter here as per instruction
            var result = _scripMaster.FirstOrDefault(s => s.InstrumentType == "INDEX" && // Filter by INSTRUMENT (5th column)
                                                         RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Equals(term)); // Exact match on DISPLAY_NAME (9th column), after removing whitespace

            if (result == null) Debug.WriteLine($"[ScripMaster_FindIndex] FAIL - No spot index found for: '{indexName}'");
            else Debug.WriteLine($"[ScripMaster_FindIndex] SUCCESS - Found spot index: '{result.SemInstrumentName}' | ID: {result.SecurityId}");

            return result?.SecurityId;
        }

        /// <summary>
        /// Finds a derivative (Future or Stock Future) ScripInfo based on its underlying symbol.
        /// Prioritizes near-month expiry.
        /// </summary>
        /// <param name="underlyingSymbol">The underlying symbol (e.g., "NIFTY", "RELIANCE").</param>
        /// <returns>The ScripInfo of the nearest month future, or null if not found.</returns>
        public ScripInfo? FindNearMonthFutureSecurityId(string underlyingSymbol) // Changed return type to ScripInfo?
        {
            var term = RemoveWhitespace(underlyingSymbol).ToUpperInvariant(); // Remove whitespace from search term
            Debug.WriteLine($"[ScripMaster_FindFuture] Searching for NEAR MONTH FUTURE for underlying: '{underlyingSymbol}' (normalized: '{term}')");

            // Filter for Futures (Index or Stock) and active expiry dates
            var futures = _scripMaster
                .Where(s => (s.InstrumentType == "FUTIDX" || s.InstrumentType == "FUTSTK") && // Filter by INSTRUMENT (5th column)
                            s.ExpiryDate.HasValue && s.ExpiryDate.Value.Date >= DateTime.Today)
                .ToList();

            // --- Stock Futures (FUTSTK) - Use EXCH_ID filter ---
            // As per user: "NSE 1st column, 5th column FUTSTK then name of the underlying in 9th column"
            var stockFutures = futures
                .Where(s => s.ExchId.ToUpperInvariant().Equals("NSE") && s.InstrumentType == "FUTSTK")
                .ToList();

            var matchingStockFutures = stockFutures
                .Where(s => RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains(term)) // Contains match on DISPLAY_NAME (9th column), after removing whitespace
                .OrderBy(s => s.ExpiryDate)
                .ToList();

            if (matchingStockFutures.Any())
            {
                var result = matchingStockFutures.FirstOrDefault();
                Debug.WriteLine($"[ScripMaster_FindFuture] SUCCESS - Found stock future: '{result?.SemInstrumentName}' | ID: {result?.SecurityId}");
                return result; // Return ScripInfo
            }

            // --- Index Futures (FUTIDX) - DO NOT use EXCH_ID filter ---
            // As per user: "for index futures, NSE 1st column, 5th column FUTIDX then name of the underlying in 9th column"
            // Correction: "for index futures, ... we dont need to use EXCH_ID filter else sensex will fail."
            var indexFutures = futures
                .Where(s => s.InstrumentType == "FUTIDX") // Filter by INSTRUMENT (5th column), no EXCH_ID filter
                .ToList();

            // --- Refined search for Index Futures to avoid NIFTY/BANKNIFTY confusion and handle SENSEX format ---
            var matchingIndexFutures = new List<ScripInfo>();

            if (term == "NIFTY")
            {
                // NIFTY: Should contain "NIFTY" but NOT "BANKNIFTY". Also, ensure it doesn't contain digits (e.g. "NIFTY 2025 JUL FUT")
                matchingIndexFutures = indexFutures
                    .Where(s => RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("NIFTY") &&
                                !RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("BANKNIFTY") && // Exclude Banknifty
                                !ContainsDigit(RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant())) // Exclude if it contains any digit
                    .OrderBy(s => s.ExpiryDate)
                    .ToList();
            }
            else if (term == "BANKNIFTY")
            {
                // BANKNIFTY: Should contain "BANKNIFTY". Also, ensure it doesn't contain digits.
                matchingIndexFutures = indexFutures
                    .Where(s => RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("BANKNIFTY") &&
                                !ContainsDigit(RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant())) // Exclude if it contains any digit
                    .OrderBy(s => s.ExpiryDate)
                    .ToList();
            }
            else if (term == "SENSEX")
            {
                // SENSEX: Should contain "SENSEX" and match the "SENSEX JUL FUT" pattern (no day number).
                // This means the DISPLAY_NAME should contain "SENSEX" and "FUT" but NOT have a number between them.
                // A more robust check for "SENSEX JUL FUT" pattern:
                matchingIndexFutures = indexFutures
                    .Where(s => RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("SENSEX") &&
                                RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("FUT") &&
                                !ContainsDigit(RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Replace("SENSEX", "").Replace("FUT", ""))) // Check for digits excluding SENSEX and FUT
                    .OrderBy(s => s.ExpiryDate)
                    .ToList();
            }
            else
            {
                // Generic fallback for other index futures if any
                matchingIndexFutures = indexFutures
                    .Where(s => RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains(term))
                    .OrderBy(s => s.ExpiryDate)
                    .ToList();
            }


            if (matchingIndexFutures.Any())
            {
                var result = matchingIndexFutures.FirstOrDefault();
                Debug.WriteLine($"[ScripMaster_FindFuture] SUCCESS - Found index future: '{result?.SemInstrumentName}' | ID: {result?.SecurityId}");
                return result; // Return ScripInfo
            }


            // --- Debugging: Log details of all relevant futures that were considered but not matched ---
            if (futures.Any())
            {
                Debug.WriteLine($"[ScripMaster_FindFuture] DEBUG: No future found for '{underlyingSymbol}' (normalized: '{term}') through DISPLAY_NAME contains. Examining first 5 relevant FUTIDX/FUTSTK entries:");
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
                    Debug.WriteLine($"[ScripMaster_FindFuture] DEBUG: No relevant FUTIDX/FUTSTK entries found for '{underlyingSymbol}' (normalized: '{term}') in the master list after initial filtering.");
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


        public string? FindOptionSecurityId(string underlyingSymbol, DateTime expiryDate, decimal strikePrice, string optionType)
        {
            var termUnderlying = RemoveWhitespace(underlyingSymbol).ToUpperInvariant(); // Remove whitespace from underlying symbol term
            var termOptionType = optionType.Trim().ToUpperInvariant();
            Debug.WriteLine($"[ScripMaster_FindOption] Searching for OPTION: Underlying='{underlyingSymbol}' (normalized: '{termUnderlying}'), Expiry='{expiryDate:yyyy-MM-dd}', Strike={strikePrice}, Type='{termOptionType}' (exact match on DISPLAY_NAME)");

            // As per user: "5th column OPTIDX, then in the 5th column we have to look for the required option."
            // Removed segment filter as per user instruction: "FindOptionSecurityId also do not need to use exchange filter, else sensex options wont be found."
            var options = _scripMaster
                .Where(s => s.InstrumentType == "OPTIDX" && // Filter by INSTRUMENT (5th column)
                            s.ExpiryDate.HasValue && s.ExpiryDate.Value.Date == expiryDate.Date &&
                            s.StrikePrice == strikePrice &&
                            s.OptionType.ToUpperInvariant().Equals(termOptionType))
                .ToList();

            // Construct the expected DISPLAY_NAME format: "index name" "expiry date (dd)" "month name 1st 3 letters" "strike price" "CALL/PUT"
            // Example: "SENSEX 26 MAR 78000 PUT"
            // Note: `underlyingSymbol` will be "Nifty 50", "Nifty Bank", "Sensex" from MainViewModel
            string expectedExpiryPart = expiryDate.ToString("dd MMM", CultureInfo.InvariantCulture).ToUpperInvariant(); // e.g., "26 MAR"
            // Remove .00 if present for strike price to match common display format
            string formattedStrike = strikePrice.ToString(CultureInfo.InvariantCulture).Replace(".00", "");
            // Construct the search term, removing whitespace from the underlying symbol part
            string expectedDisplayNamePart = $"{RemoveWhitespace(underlyingSymbol).ToUpperInvariant()} {expectedExpiryPart} {formattedStrike} {termOptionType}";

            Debug.WriteLine($"[ScripMaster_FindOption] Expected DISPLAY_NAME for search: '{expectedDisplayNamePart}'");


            // Search by DISPLAY_NAME (SemInstrumentName) for exact match of the constructed string, after removing whitespace from CSV value
            var matchingOption = options
                .FirstOrDefault(s => RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Equals(expectedDisplayNamePart));


            if (matchingOption != null)
            {
                Debug.WriteLine($"[ScripMaster_FindOption] SUCCESS - Found option: '{matchingOption.SemInstrumentName}' | ID: {matchingOption.SecurityId}");
                return matchingOption.SecurityId;
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

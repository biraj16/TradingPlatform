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
        // NOTE: This service now uses the central ScripInfo class from TradingConsole.DhanApi.Models

        private List<ScripInfo> _scripMaster = new List<ScripInfo>();
        private readonly HttpClient _httpClient;
        private const string ScripMasterUrl = "https://images.dhan.co/api-data/api-scrip-master.csv";

        public ScripMasterService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        public async Task LoadScripMasterAsync()
        {
            try
            {
                Debug.WriteLine("[ScripMaster] Starting to download compact scrip master CSV...");
                var csvData = await _httpClient.GetStringAsync(ScripMasterUrl);
                Debug.WriteLine($"[ScripMaster] Downloaded compact CSV data: {csvData.Length} characters");

                var tempMaster = new List<ScripInfo>();
                var allowedTypes = new HashSet<string> { "EQUITY", "FUTIDX", "FUTSTK", "INDEX", "OPTIDX", "OPTSTK" };

                using (var reader = new StringReader(csvData))
                {
                    var headerLine = await reader.ReadLineAsync();
                    if (headerLine == null) return;

                    var headers = ParseCsvLine(headerLine);
                    var headerMap = new Dictionary<string, int>();
                    for (int i = 0; i < headers.Length; i++)
                    {
                        headerMap[headers[i].Trim().ToUpperInvariant()] = i;
                    }

                    const string Col_InstrumentType = "SEM_INSTRUMENT_NAME";
                    const string Col_Segment = "SEM_SEGMENT";
                    const string Col_SecurityId = "SEM_SMST_SECURITY_ID";
                    const string Col_UnderlyingSymbol = "SM_SYMBOL_NAME";
                    const string Col_TradingSymbol = "SEM_TRADING_SYMBOL";
                    const string Col_LotSize = "SEM_LOT_UNITS";
                    const string Col_ExpiryDate = "SEM_EXPIRY_DATE";
                    const string Col_StrikePrice = "SEM_STRIKE_PRICE";
                    const string Col_OptionType = "SEM_OPTION_TYPE";
                    const string Col_CustomSymbol = "SEM_CUSTOM_SYMBOL";

                    var requiredColumns = new[] {
                        Col_InstrumentType, Col_Segment, Col_SecurityId, Col_UnderlyingSymbol,
                        Col_TradingSymbol, Col_LotSize, Col_ExpiryDate, Col_StrikePrice, Col_OptionType, Col_CustomSymbol
                    };

                    foreach (var col in requiredColumns)
                    {
                        if (!headerMap.ContainsKey(col.ToUpperInvariant()))
                        {
                            Debug.WriteLine($"[ScripMaster] CRITICAL ERROR: Required column '{col}' not found in compact file headers.");
                            return;
                        }
                    }

                    string? line;
                    int parsedCount = 0;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        var values = ParseCsvLine(line);
                        if (values.Length <= requiredColumns.Max(c => headerMap[c.ToUpperInvariant()])) continue;

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

                            if (parsedCount < 10 && scrip.InstrumentType == "FUTIDX")
                            {
                                Debug.WriteLine($"[ScripMaster] Parsed FUTIDX: ID={scrip.SecurityId}, Name(Custom)='{scrip.SemInstrumentName}', Underlying(SM_Symbol)='{scrip.UnderlyingSymbol}'");
                                parsedCount++;
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
            if (DateTime.TryParseExact(dateStr.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }
            return null;
        }

        public string? FindEquitySecurityId(string tradingSymbol)
        {
            Debug.WriteLine($"[ScripMaster_Find] Searching for EQUITY: '{tradingSymbol}'");
            var term = tradingSymbol.Trim();
            var result = _scripMaster.FirstOrDefault(s => s.InstrumentType == "EQUITY" && s.TradingSymbol.Equals(term, StringComparison.OrdinalIgnoreCase));

            if (result == null) Debug.WriteLine($"[ScripMaster_Find] FAIL - No equity found for: '{tradingSymbol}'");
            else Debug.WriteLine($"[ScripMaster_Find] SUCCESS - Found equity: {result.TradingSymbol} | ID: {result.SecurityId}");

            return result?.SecurityId;
        }

        public string? FindIndexSecurityId(string tradingSymbol)
        {
            Debug.WriteLine($"[ScripMaster_Find] Searching for SPOT INDEX using SM_SYMBOL_NAME: '{tradingSymbol}'");
            var term = tradingSymbol.Trim();
            // Spot indices use the SM_SYMBOL_NAME column, which is mapped to our UnderlyingSymbol property.
            var result = _scripMaster.FirstOrDefault(s => s.InstrumentType == "INDEX" && s.UnderlyingSymbol.Equals(term, StringComparison.OrdinalIgnoreCase));

            if (result == null) Debug.WriteLine($"[ScripMaster_Find] FAIL - No spot index found for: '{tradingSymbol}'");
            else Debug.WriteLine($"[ScripMaster_Find] SUCCESS - Found spot index: {result.UnderlyingSymbol} | ID: {result.SecurityId}");

            return result?.SecurityId;
        }

        // This is a private helper method for finding derivatives.
        private ScripInfo? FindDerivative(string baseSymbol)
        {
            var term = baseSymbol.Trim();
            // Derivatives use the SEM_CUSTOM_SYMBOL column, which is mapped to our SemInstrumentName property.
            Debug.WriteLine($"[ScripMaster_Find] Searching for DERIVATIVE using SEM_CUSTOM_SYMBOL containing: '{term}'");
            return _scripMaster
                .Where(s => (s.InstrumentType == "FUTIDX" || s.InstrumentType == "FUTSTK") &&
                            s.SemInstrumentName.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                            s.ExpiryDate.HasValue && s.ExpiryDate.Value.Date >= DateTime.Today)
                .OrderBy(s => s.ExpiryDate)
                .FirstOrDefault();
        }

        public string? FindDerivativeSecurityId(string baseSymbol)
        {
            Debug.WriteLine($"[ScripMaster_Find] Request for DERIVATIVE ID for: '{baseSymbol}'");
            var future = FindDerivative(baseSymbol);

            if (future == null) Debug.WriteLine($"[ScripMaster_Find] FAIL - No derivative contract found for: '{baseSymbol}'");
            else Debug.WriteLine($"[ScripMaster_Find] SUCCESS - Found derivative ID via future {future.TradingSymbol}: {future.SecurityId}");

            return future?.SecurityId;
        }

        public string? FindNearMonthFutureSecurityId(string baseSymbol)
        {
            Debug.WriteLine($"[ScripMaster_Find] Request for NEAR MONTH FUTURE for: '{baseSymbol}'");
            var result = FindDerivative(baseSymbol);

            if (result == null) Debug.WriteLine($"[ScripMaster_Find] FAIL - No future found for: '{baseSymbol}'");
            else Debug.WriteLine($"[ScripMaster_Find] SUCCESS - Found future: {result.TradingSymbol} | ID: {result.SecurityId}");

            return result?.SecurityId;
        }

        public string? FindOptionSecurityId(string underlyingSymbol, DateTime expiryDate, decimal strikePrice, string optionType)
        {
            Debug.WriteLine($"[ScripMaster_Find] Searching for OPTION using SEM_CUSTOM_SYMBOL: {underlyingSymbol} {expiryDate:ddMMMyy} {strikePrice} {optionType}");
            var term = underlyingSymbol.Trim();
            var type = optionType.Trim().ToUpperInvariant();

            // Options also use the SEM_CUSTOM_SYMBOL column for their descriptive name.
            var result = _scripMaster
                .FirstOrDefault(s => (s.InstrumentType == "OPTIDX" || s.InstrumentType == "OPTSTK") &&
                                      s.SemInstrumentName.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                                      s.ExpiryDate.HasValue && s.ExpiryDate.Value.Date == expiryDate.Date &&
                                      s.StrikePrice == strikePrice &&
                                      s.OptionType.Equals(type, StringComparison.OrdinalIgnoreCase));

            if (result == null) Debug.WriteLine($"[ScripMaster_Find] FAIL - No option found for the given parameters.");
            else Debug.WriteLine($"[ScripMaster_Find] SUCCESS - Found option: {result.TradingSymbol} | ID: {result.SecurityId}");

            return result?.SecurityId;
        }


        private string GetSafeValue(string[] values, Dictionary<string, int> headerMap, string columnName)
        {
            var upperColumnName = columnName.ToUpperInvariant();
            return headerMap.TryGetValue(upperColumnName, out int index) && index < values.Length ? values[index]?.Trim() ?? string.Empty : string.Empty;
        }
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

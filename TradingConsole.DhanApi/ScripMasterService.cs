using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TradingConsole.Core.Models;
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
                var tempMaster = new List<ScripInfo>();
                var allowedTypes = new HashSet<string> { "EQUITY", "FUTIDX", "FUTSTK", "INDEX", "OPTIDX", "OPTSTK" };

                using (var reader = new StringReader(csvData))
                {
                    var headerLine = await reader.ReadLineAsync();
                    if (headerLine == null) return;

                    var headers = ParseCsvLine(headerLine);
                    var headerMap = new Dictionary<string, int>();
                    for (int i = 0; i < headers.Length; i++) headerMap[headers[i].Trim().ToUpperInvariant()] = i;

                    const string Col_ExchId = "EXCH_ID", Col_Segment = "SEGMENT", Col_SecurityId = "SECURITY_ID", Col_Instrument = "INSTRUMENT", Col_UnderlyingSymbol = "UNDERLYING_SYMBOL", Col_TradingSymbol = "SYMBOL_NAME", Col_CustomSymbol = "DISPLAY_NAME", Col_LotSize = "LOT_SIZE", Col_ExpiryDate = "SM_EXPIRY_DATE", Col_StrikePrice = "STRIKE_PRICE", Col_OptionType = "OPTION_TYPE";

                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        var values = ParseCsvLine(line);
                        var instrumentType = GetSafeValue(values, headerMap, Col_Instrument);
                        if (allowedTypes.Contains(instrumentType))
                        {
                            var exchId = GetSafeValue(values, headerMap, Col_ExchId);
                            var segment = GetSafeValue(values, headerMap, Col_Segment);
                            tempMaster.Add(new ScripInfo
                            {
                                ExchId = exchId,
                                Segment = MapCsvSegmentToApiSegment(exchId, segment, instrumentType),
                                SecurityId = GetSafeValue(values, headerMap, Col_SecurityId),
                                InstrumentType = instrumentType,
                                SemInstrumentName = GetSafeValue(values, headerMap, Col_CustomSymbol),
                                TradingSymbol = GetSafeValue(values, headerMap, Col_TradingSymbol),
                                UnderlyingSymbol = GetSafeValue(values, headerMap, Col_UnderlyingSymbol),
                                LotSize = ParseIntSafe(GetSafeValue(values, headerMap, Col_LotSize)),
                                ExpiryDate = FormatDate(GetSafeValue(values, headerMap, Col_ExpiryDate)),
                                StrikePrice = ParseDecimalSafe(GetSafeValue(values, headerMap, Col_StrikePrice)),
                                OptionType = GetSafeValue(values, headerMap, Col_OptionType),
                            });
                        }
                    }
                }
                _scripMaster = tempMaster;
                Debug.WriteLine($"[ScripMaster] Scrip Master loaded with {_scripMaster.Count} relevant instruments.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScripMaster] FAILED to load scrip master: {ex.Message}");
            }
        }

        public void ResolveInstrumentDetails(List<DashboardInstrument> instruments)
        {
            foreach (var inst in instruments)
            {
                ScripInfo? scripInfo = null;
                if (inst.IsFuture)
                {
                    scripInfo = FindNearMonthFutureSecurityId(inst.UnderlyingSymbol);
                }
                else if (inst.FeedType == "Quote" && inst.SegmentId == 1)
                {
                    scripInfo = FindEquityScripInfo(inst.Symbol);
                }
                else if (inst.FeedType == "Ticker")
                {
                    scripInfo = FindIndexScripInfo(inst.Symbol);
                }

                if (scripInfo != null)
                {
                    inst.SecurityId = scripInfo.SecurityId;
                    inst.SegmentId = GetSegmentIdFromName(scripInfo.Segment);
                    inst.Symbol = scripInfo.SemInstrumentName;
                    inst.DisplayName = scripInfo.SemInstrumentName;
                }
            }
        }

        // RESTORED: Your original, correct logic for finding equities.
        public ScripInfo? FindEquityScripInfo(string tradingSymbol)
        {
            var term = RemoveWhitespace(tradingSymbol).ToUpperInvariant();
            var result = _scripMaster.FirstOrDefault(s => s.ExchId.ToUpperInvariant().Equals("NSE") && s.InstrumentType == "EQUITY" && RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains(term));
            return result ?? _scripMaster.FirstOrDefault(s => s.ExchId.ToUpperInvariant().Equals("NSE") && s.InstrumentType == "EQUITY" && RemoveWhitespace(s.TradingSymbol).ToUpperInvariant().Contains(term));
        }

        // RESTORED: Your original, correct logic for finding indices.
        public ScripInfo? FindIndexScripInfo(string indexName)
        {
            var term = RemoveWhitespace(indexName).ToUpperInvariant();
            return _scripMaster.FirstOrDefault(s => s.InstrumentType == "INDEX" && RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Equals(term));
        }

        // RESTORED: Your original, more robust logic for finding futures.
        public ScripInfo? FindNearMonthFutureSecurityId(string underlyingSymbol)
        {
            var normalizedUnderlyingSymbol = RemoveWhitespace(underlyingSymbol).ToUpperInvariant();
            Debug.WriteLine($"[ScripMaster_FindFuture] Searching for NEAR MONTH FUTURE for underlying: '{underlyingSymbol}' (normalized: '{normalizedUnderlyingSymbol}')");

            var futures = _scripMaster
                .Where(s => (s.InstrumentType == "FUTIDX" || s.InstrumentType == "FUTSTK") &&
                            s.ExpiryDate.HasValue && s.ExpiryDate.Value.Date >= DateTime.Today)
                .ToList();

            var matchingStockFutures = futures
                .Where(s => s.ExchId.ToUpperInvariant().Equals("NSE") && s.InstrumentType == "FUTSTK" &&
                            s.UnderlyingSymbol.ToUpperInvariant().Equals(normalizedUnderlyingSymbol))
                .OrderBy(s => s.ExpiryDate)
                .ToList();

            if (matchingStockFutures.Any())
            {
                return matchingStockFutures.FirstOrDefault();
            }

            if (normalizedUnderlyingSymbol == "NIFTY50")
            {
                var niftyFutures = futures
                    .Where(s => s.InstrumentType == "FUTIDX" &&
                                RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("NIFTY") &&
                                !RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("BANKNIFTY") &&
                                !RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("FINNIFTY") &&
                                !Regex.IsMatch(RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant(), @"NIFTY\s*\d+\s*[A-Z]{3}\s*FUT"))
                    .OrderBy(s => s.ExpiryDate);
                if (niftyFutures.Any()) return niftyFutures.FirstOrDefault();
            }
            else if (normalizedUnderlyingSymbol == "NIFTYBANK")
            {
                var bankNiftyFutures = futures
                    .Where(s => s.InstrumentType == "FUTIDX" &&
                                RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("BANKNIFTY"))
                    .OrderBy(s => s.ExpiryDate);
                if (bankNiftyFutures.Any()) return bankNiftyFutures.FirstOrDefault();
            }
            else if (normalizedUnderlyingSymbol == "SENSEX")
            {
                var sensexFutures = futures
                    .Where(s => s.InstrumentType == "FUTIDX" &&
                                RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("SENSEX") &&
                                RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant().Contains("FUT") &&
                                !Regex.IsMatch(RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant(), @"SENSEX\s*\d+\s*[A-Z]{3}\s*FUT"))
                    .OrderBy(s => s.ExpiryDate);
                if (sensexFutures.Any()) return sensexFutures.FirstOrDefault();
            }
            else
            {
                var genericIndexFutures = futures
                    .Where(s => s.InstrumentType == "FUTIDX" &&
                                s.UnderlyingSymbol.ToUpperInvariant().Equals(normalizedUnderlyingSymbol))
                    .OrderBy(s => s.ExpiryDate);
                if (genericIndexFutures.Any()) return genericIndexFutures.FirstOrDefault();
            }

            Debug.WriteLine($"[ScripMaster_FindFuture] FAIL - No future contract found for underlying: '{underlyingSymbol}'");
            return null;
        }

        // RESTORED: Your original logic for finding options, adapted to return the full ScripInfo object.
        public ScripInfo? FindOptionScripInfo(string underlyingSymbol, DateTime expiryDate, decimal strikePrice, string optionType)
        {
            var termUnderlying = RemoveWhitespace(underlyingSymbol).ToUpperInvariant();
            var termOptionType = optionType.Trim().ToUpperInvariant();

            string adjustedUnderlyingForSearch = termUnderlying;
            if (termUnderlying == "NIFTY 50") adjustedUnderlyingForSearch = "NIFTY";
            if (termUnderlying == "NIFTY BANK") adjustedUnderlyingForSearch = "BANKNIFTY";

            var potentialOptions = _scripMaster
                .Where(s => (s.InstrumentType == "OPTIDX" || s.InstrumentType == "OPTSTK") &&
                            s.ExpiryDate.HasValue && s.ExpiryDate.Value.Date == expiryDate.Date &&
                            s.StrikePrice == strikePrice &&
                            s.OptionType.ToUpperInvariant().Equals(termOptionType))
                .ToList();

            if (!potentialOptions.Any())
            {
                return null;
            }

            var matchingOption = potentialOptions.FirstOrDefault(s => {
                var displayName = RemoveWhitespace(s.SemInstrumentName).ToUpperInvariant();
                return displayName.Contains(adjustedUnderlyingForSearch);
            });

            return matchingOption;
        }

        public int GetLotSizeForSecurity(string securityId) => _scripMaster.FirstOrDefault(s => s.SecurityId == securityId)?.LotSize ?? 0;

        public int GetSegmentIdFromName(string segmentName) => segmentName switch { "NSE_EQ" => 1, "NSE_FNO" => 2, "BSE_EQ" => 3, "BSE_FNO" => 8, "IDX_I" => 0, "I" => 0, _ => -1 };
        private string MapCsvSegmentToApiSegment(string exchId, string csvSegment, string instrumentType) { if (instrumentType == "EQUITY") return exchId.ToUpperInvariant() == "NSE" ? "NSE_EQ" : "BSE_EQ"; if (instrumentType == "FUTIDX" || instrumentType == "FUTSTK" || instrumentType == "OPTIDX" || instrumentType == "OPTSTK") return exchId.ToUpperInvariant() == "NSE" ? "NSE_FNO" : "BSE_FNO"; if (instrumentType == "INDEX") return "IDX_I"; return csvSegment; }
        private DateTime? FormatDate(string dateStr) { if (string.IsNullOrWhiteSpace(dateStr) || dateStr == "0") return null; string[] formats = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "yyyyMMdd" }; foreach (string format in formats) { if (DateTime.TryParseExact(dateStr.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date; } return null; }
        private string RemoveWhitespace(string input) => new string(input.Where(c => !char.IsWhiteSpace(c)).ToArray());
        private int ParseIntSafe(string value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? (int)result : 0;
        private decimal ParseDecimalSafe(string value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0;
        private string[] ParseCsvLine(string line) { var values = new List<string>(); var current_value = new StringBuilder(); bool in_quotes = false; for (int i = 0; i < line.Length; i++) { char c = line[i]; if (c == '"') { if (in_quotes && i < line.Length - 1 && line[i + 1] == '"') { current_value.Append('"'); i++; } else { in_quotes = !in_quotes; } } else if (c == ',' && !in_quotes) { values.Add(current_value.ToString().Trim()); current_value.Clear(); } else { current_value.Append(c); } } values.Add(current_value.ToString().Trim()); return values.ToArray(); }
        private string GetSafeValue(string[] values, Dictionary<string, int> headerMap, string columnName) { if (headerMap.TryGetValue(columnName.ToUpperInvariant(), out int index) && index < values.Length) { return values[index]?.Trim() ?? string.Empty; } return string.Empty; }
    }
}

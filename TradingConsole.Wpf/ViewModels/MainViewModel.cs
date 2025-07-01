// TradingConsole.Wpf/ViewModels/MainViewModel.cs

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using TradingConsole.Core.Models;
using TradingConsole.DhanApi;
using TradingConsole.DhanApi.Models;
using TradingConsole.DhanApi.Models.WebSocket;
using TradingConsole.Wpf.Services;
using TickerIndex = TradingConsole.DhanApi.Models.Index;


namespace TradingConsole.Wpf.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly DhanApiClient _apiClient;
        private readonly DhanWebSocketClient _webSocketClient;
        private readonly ScripMasterService _scripMasterService;
        private readonly AnalysisService _analysisService;
        private readonly string _dhanClientId;
        private Timer? _optionChainRefreshTimer;
        private readonly Dictionary<string, OptionDetails> _optionScripMap = new();

        private bool _isDataLoading = false;


        public DashboardViewModel Dashboard { get; }
        public AnalysisService AnalysisService => _analysisService;

        public decimal OpenPnl => OpenPositions.Sum(p => p.UnrealizedPnl);
        public decimal BookedPnl => ClosedPositions.Sum(p => p.RealizedPnl);
        public decimal NetPnl => OpenPnl + BookedPnl;

        private bool? _selectAllOpenPositions = false;
        public bool? SelectAllOpenPositions
        {
            get => _selectAllOpenPositions;
            set
            {
                if (_selectAllOpenPositions != value)
                {
                    _selectAllOpenPositions = value;
                    foreach (var pos in OpenPositions)
                    {
                        pos.IsSelected = _selectAllOpenPositions ?? false;
                    }
                    OnPropertyChanged(nameof(SelectAllOpenPositions));
                }
            }
        }

        public ObservableCollection<Position> OpenPositions { get; }
        public ObservableCollection<Position> ClosedPositions { get; }
        public FundDetails FundDetails { get; }
        public ObservableCollection<OptionChainRow> OptionChainRows { get; }
        public ObservableCollection<TickerIndex> Indices { get; }
        public ObservableCollection<string> ExpiryDates { get; }
        public ObservableCollection<OrderBookEntry> Orders { get; }

        private TickerIndex? _selectedIndex;
        public TickerIndex? SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (_selectedIndex != value)
                {
                    _selectedIndex = value;
                    OnPropertyChanged(nameof(SelectedIndex));
                    // --- FIX: This now triggers the entire reliable refresh sequence ---
                    Task.Run(() => LoadExpiryAndOptionChainAsync());
                }
            }
        }
        private string? _selectedExpiry;
        public string? SelectedExpiry
        {
            get => _selectedExpiry;
            set
            {
                if (_selectedExpiry != value)
                {
                    _selectedExpiry = value;
                    OnPropertyChanged(nameof(SelectedExpiry));
                    // This is for manual changes by the user
                    Task.Run(() => LoadOptionChainOnlyAsync(_lastApiSecurityIdUsed, _lastApiSegmentUsed)); // Pass last used API parameters
                }
            }
        }
        public string StatusMessage { get; private set; } = string.Empty;

        private decimal _underlyingPrice;
        public decimal UnderlyingPrice { get => _underlyingPrice; set { if (_underlyingPrice != value) { _underlyingPrice = value; OnPropertyChanged(nameof(UnderlyingPrice)); OnPropertyChanged(nameof(UnderlyingPriceChange)); OnPropertyChanged(nameof(UnderlyingPriceChangePercent)); } } }
        private decimal _underlyingPreviousClose;
        public decimal UnderlyingPreviousClose { get => _underlyingPreviousClose; set { if (_underlyingPreviousClose != value) { _underlyingPreviousClose = value; OnPropertyChanged(nameof(UnderlyingPriceChange)); OnPropertyChanged(nameof(UnderlyingPriceChangePercent)); } } }
        public decimal UnderlyingPriceChange => UnderlyingPrice - UnderlyingPreviousClose;
        public decimal UnderlyingPriceChangePercent => UnderlyingPreviousClose == 0 ? 0 : (UnderlyingPriceChange / UnderlyingPreviousClose);

        private long _totalCallOi;
        public long TotalCallOi { get => _totalCallOi; set { _totalCallOi = value; OnPropertyChanged(nameof(TotalCallOi)); } }
        private long _totalPutOi;
        public long TotalPutOi { get => _totalPutOi; set { _totalPutOi = value; OnPropertyChanged(nameof(TotalPutOi)); } }
        private long _totalCallVolume;
        public long TotalCallVolume { get => _totalCallVolume; set { _totalCallVolume = value; OnPropertyChanged(nameof(TotalCallVolume)); } }
        private long _totalPutVolume;
        public long TotalPutVolume { get => _totalPutVolume; set { _totalPutVolume = value; OnPropertyChanged(nameof(TotalPutVolume)); } }
        private decimal _pcrOi;
        public decimal PcrOi { get => _pcrOi; set { _pcrOi = value; OnPropertyChanged(nameof(PcrOi)); } }

        private long _maxOi;
        public long MaxOi { get => _maxOi; set { _maxOi = value; OnPropertyChanged(nameof(MaxOi)); } }
        private decimal _maxOiChange;
        public decimal MaxOiChange { get => _maxOiChange; set { _maxOiChange = value; OnPropertyChanged(nameof(MaxOiChange)); } }

        // Store the last successfully used API parameters for option chain calls
        private string _lastApiSecurityIdUsed = string.Empty;
        private string _lastApiSegmentUsed = string.Empty;


        public ICommand BuyCallCommand { get; }
        public ICommand SellCallCommand { get; }
        public ICommand BuyPutCommand { get; }
        public ICommand SellPutCommand { get; }
        public ICommand RefreshOrdersCommand { get; }
        public ICommand RefreshPortfolioCommand { get; }
        public ICommand AddPositionCommand { get; }
        public ICommand ExitPositionCommand { get; }
        public ICommand CloseSelectedPositionsCommand { get; }
        public ICommand ConvertPositionCommand { get; }
        public ICommand ModifyOrderCommand { get; }
        public ICommand CancelOrderCommand { get; }


        public MainViewModel(string clientId, string accessToken)
        {
            _dhanClientId = clientId;
            _apiClient = new DhanApiClient(clientId, accessToken);
            _webSocketClient = new DhanWebSocketClient(clientId, accessToken);
            _scripMasterService = new ScripMasterService();

            _analysisService = new AnalysisService();
            _analysisService.OnAnalysisUpdated += OnAnalysisResultUpdated;

            Dashboard = new DashboardViewModel();

            _webSocketClient.OnConnected += OnWebSocketConnected;
            _webSocketClient.OnLtpUpdate += OnLtpUpdateReceived;
            _webSocketClient.OnPreviousCloseUpdate += OnPreviousCloseUpdateReceived;
            _webSocketClient.OnQuoteUpdate += OnQuoteUpdateReceived;
            _webSocketClient.OnOiUpdate += OnOiUpdateReceived;

            OpenPositions = new ObservableCollection<Position>();
            OpenPositions.CollectionChanged += (s, e) => { OnPropertyChanged(nameof(OpenPnl)); OnPropertyChanged(nameof(NetPnl)); };
            ClosedPositions = new ObservableCollection<Position>();
            ClosedPositions.CollectionChanged += (s, e) => { OnPropertyChanged(nameof(BookedPnl)); OnPropertyChanged(nameof(NetPnl)); };

            FundDetails = new FundDetails();
            OptionChainRows = new ObservableCollection<OptionChainRow>();
            ExpiryDates = new ObservableCollection<string>();
            Orders = new ObservableCollection<OrderBookEntry>();

            Indices = new ObservableCollection<TickerIndex>();

            BuyCallCommand = new RelayCommand(ExecuteBuyCall);
            SellCallCommand = new RelayCommand(ExecuteSellCall);
            BuyPutCommand = new RelayCommand(ExecuteBuyPut);
            SellPutCommand = new RelayCommand(ExecuteSellPut);
            RefreshOrdersCommand = new RelayCommand(async (p) => await LoadOrdersAsync());
            RefreshPortfolioCommand = new RelayCommand(async (p) => await LoadPortfolioAsync());
            AddPositionCommand = new RelayCommand(ExecuteAddPosition);
            ExitPositionCommand = new RelayCommand(ExecuteExitPosition);
            CloseSelectedPositionsCommand = new RelayCommand(async (p) => await ExecuteCloseSelectedPositionsAsync());
            ConvertPositionCommand = new RelayCommand(async (p) => await ExecuteConvertPositionAsync(p));
            ModifyOrderCommand = new RelayCommand(ExecuteModifyOrder);
            CancelOrderCommand = new RelayCommand(async (p) => await ExecuteCancelOrderAsync(p));

            Task.Run(() => LoadDataOnStartupAsync());
        }

        private void OnAnalysisResultUpdated(AnalysisResult result)
        {
            App.Current.Dispatcher.InvokeAsync(() =>
            {
                var instrumentToUpdate = Dashboard.MonitoredInstruments.FirstOrDefault(i => i.SecurityId == result.SecurityId);
                if (instrumentToUpdate != null)
                {
                    instrumentToUpdate.TradingSignal = result.TradingSignal;
                }
            });
        }

        #region Command Methods
        private void ExecuteBuyCall(object? p) => OpenOrderWindowForOption(p as OptionChainRow, true, true);
        private void ExecuteSellCall(object? p) => OpenOrderWindowForOption(p as OptionChainRow, false, true);
        private void ExecuteBuyPut(object? p) => OpenOrderWindowForOption(p as OptionChainRow, true, false);
        private void ExecuteSellPut(object? p) => OpenOrderWindowForOption(p as OptionChainRow, false, false);
        private void ExecuteAddPosition(object? p) { if (p is Position pos) OpenOrderWindowForPosition(pos, pos.Quantity > 0); }
        private void ExecuteExitPosition(object? p) { if (p is Position pos) OpenOrderWindowForPosition(pos, pos.Quantity < 0); }

        private async Task ExecuteCloseSelectedPositionsAsync()
        {
            var selectedPositions = OpenPositions.Where(pos => pos.IsSelected).ToList();
            if (!selectedPositions.Any()) { MessageBox.Show("No positions selected.", "Information", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (MessageBox.Show($"Are you sure you want to close {selectedPositions.Count} selected position(s) at market price?", "Confirm Close Positions", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No) return;
            await UpdateStatusAsync($"Closing {selectedPositions.Count} position(s)...");
            foreach (var pos in selectedPositions)
            {
                var orderRequest = new OrderRequest { DhanClientId = _dhanClientId, TransactionType = pos.Quantity > 0 ? "SELL" : "BUY", ExchangeSegment = "NSE_FNO", ProductType = pos.ProductType, OrderType = "MARKET", SecurityId = pos.SecurityId, Quantity = Math.Abs(pos.Quantity), Validity = "DAY" };
                await _apiClient.PlaceOrderAsync(orderRequest);
            }
            await Task.Delay(1500);
            await LoadPortfolioAsync();
        }

        private async Task ExecuteConvertPositionAsync(object? parameter)
        {
            if (parameter is Position position)
            {
                var newProductType = position.ProductType == "INTRADAY" ? "MARGIN" : "INTRADAY";
                var convertRequest = new ConvertPositionRequest
                {
                    DhanClientId = _dhanClientId,
                    SecurityId = position.SecurityId,
                    ProductType = position.ProductType,
                    ConvertTo = newProductType,
                    Quantity = Math.Abs(position.Quantity)
                };
                var success = await _apiClient.ConvertPositionAsync(convertRequest);
                if (success) { await UpdateStatusAsync("Position conversion successful."); await LoadPortfolioAsync(); }
                else { await UpdateStatusAsync("Position conversion failed."); }
            }
        }

        private void ExecuteModifyOrder(object? parameter)
        {
            if (parameter is OrderBookEntry order)
            {
                var orderViewModel = new OrderEntryViewModel(order, _apiClient, _dhanClientId, _scripMasterService);
                var orderWindow = new OrderEntryWindow { DataContext = orderViewModel, Title = "Modify Order" };
                orderWindow.Owner = Application.Current.MainWindow;
                orderWindow.ShowDialog();
                Task.Run(LoadOrdersAsync);
            }
        }

        private async Task ExecuteCancelOrderAsync(object? parameter)
        {
            if (parameter is OrderBookEntry order)
            {
                if (MessageBox.Show($"Are you sure you want to cancel order {order.OrderId}?", "Confirm Cancellation", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    await UpdateStatusAsync($"Cancelling order {order.OrderId}...");
                    var response = await _apiClient.CancelOrderAsync(order.OrderId);
                    if (response != null)
                    {
                        await UpdateStatusAsync($"Order {order.OrderId} cancellation processed.");
                    }
                    else
                    {
                        await UpdateStatusAsync($"Failed to cancel order {order.OrderId}.");
                    }
                    await LoadOrdersAsync();
                }
            }
        }


        private void OpenOrderWindowForOption(OptionChainRow? row, bool isBuy, bool isCall)
        {
            if (row == null) return;
            var option = isCall ? row.CallOption : row.PutOption;
            if (option == null || string.IsNullOrEmpty(option.SecurityId)) { MessageBox.Show("Cannot place order for an invalid instrument.", "Error", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            string instrumentName = ConstructInstrumentName(row.StrikePrice, isCall);
            var orderViewModel = new OrderEntryViewModel(option, isBuy, instrumentName, _apiClient, _dhanClientId, _scripMasterService, "NSE_FNO");
            var orderWindow = new OrderEntryWindow { DataContext = orderViewModel, Owner = Application.Current.MainWindow };
            orderWindow.ShowDialog();
            Task.Run(LoadPortfolioAsync);
        }

        private void OpenOrderWindowForPosition(Position position, bool isBuy)
        {
            var orderViewModel = new OrderEntryViewModel(position, isBuy, _apiClient, _dhanClientId, _scripMasterService, "NSE_FNO");
            var orderWindow = new OrderEntryWindow { DataContext = orderViewModel, Owner = Application.Current.MainWindow };
            orderWindow.ShowDialog();
            Task.Run(LoadPortfolioAsync);
        }

        private string ConstructInstrumentName(decimal strike, bool isCall) { return $"{SelectedIndex?.Symbol} {SelectedExpiry?.ToUpper()} {strike} {(isCall ? "CALL" : "PUT")}"; }
        #endregion

        #region Data Loading and WebSocket Handling
        private async Task LoadDataOnStartupAsync()
        {
            try
            {
                await UpdateStatusAsync("Downloading Instrument Master...");
                await _scripMasterService.LoadScripMasterAsync();

                PopulateIndices();

                await _webSocketClient.ConnectAsync();
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"Fatal error during startup: {ex.Message}");
            }
        }

        private void PopulateIndices()
        {
            var symbolsToLoad = new Dictionary<string, string>
            {
                { "Nifty 50", "NIFTY" },
                { "Nifty Bank", "BANKNIFTY" },
                { "Sensex", "SENSEX" }
            };

            foreach (var pair in symbolsToLoad)
            {
                // For indices, we use the spot index SecurityId for display and initial subscription
                var securityId = _scripMasterService.FindIndexSecurityId(pair.Value);
                if (!string.IsNullOrEmpty(securityId))
                {
                    App.Current.Dispatcher.Invoke(() =>
                    {
                        Indices.Add(new TickerIndex
                        {
                            Name = pair.Key,
                            Symbol = pair.Value,
                            ScripId = securityId,
                            Segment = "I" // Segment for spot indices
                        });
                    });
                }
                else
                {
                    Debug.WriteLine($"WARNING: Could not find SecurityId for index: {pair.Key} (Symbol: {pair.Value})");
                }
            }
        }


        private async void OnWebSocketConnected()
        {
            await UpdateStatusAsync("Initializing Dashboard and Subscribing...");

            await InitializeDashboardAsync();
            await UpdateSubscriptionsAsync();

            await LoadPortfolioAsync();
            await LoadOrdersAsync();

            _optionChainRefreshTimer = new Timer(async _ => await RefreshOptionChainDataAsync(), null, Timeout.Infinite, Timeout.Infinite);

            var initialIndex = Indices.FirstOrDefault();
            if (initialIndex != null) await App.Current.Dispatcher.InvokeAsync(() => { SelectedIndex = initialIndex; });
        }

        private async Task InitializeDashboardAsync()
        {
            await UpdateStatusAsync("Configuring Dashboard...");
            await App.Current.Dispatcher.InvokeAsync(() => Dashboard.MonitoredInstruments.Clear());

            var instrumentsToMonitor = new List<DashboardInstrument>();
            // Add indices (spot) for ticker updates
            foreach (var index in Indices)
            {
                instrumentsToMonitor.Add(new() { Symbol = index.Symbol, SecurityId = index.ScripId, FeedType = "Ticker", SegmentId = 0 }); // SegmentId 0 for IDX_I
            }

            // Add futures and equities
            instrumentsToMonitor.AddRange(new List<DashboardInstrument>
            {
                new() { Symbol = "NIFTY-FUT", IsFuture = true, UnderlyingSymbol = "NIFTY", FeedType = "Quote", SegmentId = 2 }, // SegmentId 2 for NSE_FNO
                new() { Symbol = "BANKNIFTY-FUT", IsFuture = true, UnderlyingSymbol = "BANKNIFTY", FeedType = "Quote", SegmentId = 2 },
                new() { Symbol = "HDFCBANK", FeedType = "Quote", SegmentId = 1 }, // SegmentId 1 for NSE_EQ
                new() { Symbol = "HDFCBANK-FUT", IsFuture = true, UnderlyingSymbol = "HDFCBANK", FeedType = "Quote", SegmentId = 2 },
                new() { Symbol = "ICICIBANK", FeedType = "Quote", SegmentId = 1 },
                new() { Symbol = "ICICIBANK-FUT", IsFuture = true, UnderlyingSymbol = "ICICIBANK", FeedType = "Quote", SegmentId = 2 },
                new() { Symbol = "RELIANCE", FeedType = "Quote", SegmentId = 1 },
                new() { Symbol = "RELIANCE-FUT", IsFuture = true, UnderlyingSymbol = "RELIANCE", FeedType = "Quote", SegmentId = 2 },
                new() { Symbol = "INFY", FeedType = "Quote", SegmentId = 1 },
                new() { Symbol = "INFY-FUT", IsFuture = true, UnderlyingSymbol = "INFY", FeedType = "Quote", SegmentId = 2 }
            });

            await ResolveInstrumentIdsAsync(instrumentsToMonitor);

            // Add resolved instruments to Dashboard.MonitoredInstruments
            foreach (var inst in instrumentsToMonitor)
            {
                if (!string.IsNullOrEmpty(inst.SecurityId))
                {
                    await App.Current.Dispatcher.InvokeAsync(() => Dashboard.MonitoredInstruments.Add(inst));
                }
                else
                {
                    Debug.WriteLine($"[DashboardInit] Skipping instrument with unresolved SecurityId: {inst.Symbol}");
                }
            }

            // Add options for each index. This will now happen after futures are resolved.
            foreach (var indexInfo in Indices)
            {
                await AddIndexOptionsToDashboardAsync(indexInfo);
                await Task.Delay(1500); // Small delay to avoid overwhelming API/WebSocket
            }
        }

        private async Task ResolveInstrumentIdsAsync(IEnumerable<DashboardInstrument> instruments)
        {
            await UpdateStatusAsync("Resolving dynamic instruments...");
            foreach (var inst in instruments)
            {
                if (string.IsNullOrEmpty(inst.SecurityId)) // Only try to resolve if not already set (e.g., for spot indices)
                {
                    Debug.WriteLine($"[RESOLVER] Attempting to resolve: {inst.Symbol}");
                    if (inst.IsFuture)
                    {
                        inst.SecurityId = _scripMasterService.FindNearMonthFutureSecurityId(inst.UnderlyingSymbol) ?? string.Empty;
                        Debug.WriteLine($"[RESOLVER] Future Found For '{inst.UnderlyingSymbol}': ID = {inst.SecurityId}");
                    }
                    else if (inst.FeedType == "Quote" && inst.SegmentId == 1) // Assuming SegmentId 1 is NSE_EQ
                    {
                        inst.SecurityId = _scripMasterService.FindEquitySecurityId(inst.Symbol) ?? string.Empty;
                        Debug.WriteLine($"[RESOLVER] Equity Found For '{inst.Symbol}': ID: {inst.SecurityId}");
                    }
                    // Indices (FeedType "Ticker", SegmentId 0) are resolved in PopulateIndices and should have SecurityId set
                }
            }
        }

        private async Task AddIndexOptionsToDashboardAsync(TickerIndex indexInfo)
        {
            try
            {
                string apiSecurityId = string.Empty;
                string apiSegment = string.Empty;
                ExpiryListResponse? expiryListResponse = null;

                // --- CRITICAL FIX ATTEMPT 1: Try using the SPOT INDEX SecurityId and IDX_I segment for Option Chain API calls ---
                Debug.WriteLine($"[AddIndexOptions] Attempt 1: Fetching expiry dates for {indexInfo.Name} using SPOT INDEX SecurityId: {indexInfo.ScripId} and Segment: {indexInfo.Segment}");
                expiryListResponse = await _apiClient.GetExpiryListAsync(indexInfo.ScripId, indexInfo.Segment);

                if (expiryListResponse?.ExpiryDates != null && expiryListResponse.ExpiryDates.Any())
                {
                    apiSecurityId = indexInfo.ScripId;
                    apiSegment = indexInfo.Segment;
                    Debug.WriteLine($"[AddIndexOptions] Attempt 1 (Spot Index) successful for expiry list.");
                }
                else
                {
                    Debug.WriteLine($"[AddIndexOptions] Attempt 1 (Spot Index) failed for expiry list. Trying Attempt 2 (Future ID).");

                    // --- CRITICAL FIX ATTEMPT 2: If spot index fails, try using the FUTURE's SecurityId and NSE_FNO segment ---
                    string? futureSecurityId = _scripMasterService.FindNearMonthFutureSecurityId(indexInfo.Symbol);
                    if (!string.IsNullOrEmpty(futureSecurityId))
                    {
                        apiSecurityId = futureSecurityId;
                        apiSegment = "NSE_FNO"; // Futures and options are in NSE_FNO
                        Debug.WriteLine($"[AddIndexOptions] Attempt 2: Fetching expiry dates for {indexInfo.Name} using FUTURE SecurityId: {apiSecurityId} and Segment: {apiSegment}");
                        expiryListResponse = await _apiClient.GetExpiryListAsync(apiSecurityId, apiSegment);

                        if (expiryListResponse?.ExpiryDates != null && expiryListResponse.ExpiryDates.Any())
                        {
                            Debug.WriteLine($"[AddIndexOptions] Attempt 2 (Future ID) successful for expiry list.");
                        }
                        else
                        {
                            await UpdateStatusAsync($"Could not get expiry dates for {indexInfo.Name} after both attempts.");
                            Debug.WriteLine($"[AddIndexOptions] No expiry dates found for {indexInfo.Name} after both attempts. Aborting option dashboard load.");
                            return; // Both attempts failed
                        }
                    }
                    else
                    {
                        await UpdateStatusAsync($"Could not find near-month future for {indexInfo.Name}. Cannot load options.");
                        Debug.WriteLine($"[AddIndexOptions] Failed to find future SecurityId for {indexInfo.Name} ({indexInfo.Symbol}). Aborting option dashboard load.");
                        return; // No future found to try
                    }
                }

                var nearestExpiry = expiryListResponse.ExpiryDates.FirstOrDefault();
                if (string.IsNullOrEmpty(nearestExpiry))
                {
                    Debug.WriteLine($"[AddIndexOptions] Nearest expiry is null or empty for {indexInfo.Name}. Aborting option dashboard load.");
                    return;
                }

                await Task.Delay(1100); // Respect API rate limits

                Debug.WriteLine($"[AddIndexOptions] Fetching option chain for {indexInfo.Name} (API SecId: {apiSecurityId}, Segment: {apiSegment}, Expiry: {nearestExpiry}).");
                await UpdateStatusAsync($"Fetching option chain for {indexInfo.Name}...");

                // Use the determined apiSecurityId and apiSegment for GetOptionChainAsync
                var optionChainResponse = await _apiClient.GetOptionChainAsync(apiSecurityId, apiSegment, nearestExpiry);
                if (optionChainResponse?.Data?.OptionChain == null)
                {
                    await UpdateStatusAsync($"Failed to load option chain for {indexInfo.Name}.");
                    Debug.WriteLine($"[AddIndexOptions] Option chain data is null for {indexInfo.Name} (API SecId: {apiSecurityId}, Segment: {apiSegment}, Expiry: {nearestExpiry})");
                    return;
                }

                var underlyingPrice = optionChainResponse.Data.UnderlyingLastPrice;
                var allStrikes = optionChainResponse.Data.OptionChain
                    .Select(kvp => decimal.TryParse(kvp.Key, out var p) ? new { Price = p, Data = kvp.Value } : null)
                    .Where(s => s != null)
                    .OrderBy(s => s!.Price)
                    .ToList();

                if (!allStrikes.Any())
                {
                    Debug.WriteLine($"[AddIndexOptions] No strikes found in option chain for {indexInfo.Name}. Aborting option dashboard load.");
                    return;
                }

                var atmStrikeData = allStrikes.OrderBy(s => Math.Abs(s!.Price - underlyingPrice)).First();
                var atmIndex = allStrikes.IndexOf(atmStrikeData!);

                int startIndex = Math.Max(0, atmIndex - 4);
                int endIndex = Math.Min(allStrikes.Count - 1, atmIndex + 4);

                for (int i = startIndex; i <= endIndex; i++)
                {
                    var strikeInfo = allStrikes[i]!;
                    if (strikeInfo.Data == null) continue;

                    string formattedStrike = strikeInfo.Price.ToString("G29");

                    if (strikeInfo.Data.CallOption != null)
                    {
                        await App.Current.Dispatcher.InvokeAsync(() => {
                            Dashboard.MonitoredInstruments.Add(new DashboardInstrument
                            {
                                Symbol = $"{indexInfo.Symbol} {formattedStrike} CE",
                                SecurityId = strikeInfo.Data.CallOption.SecurityId,
                                FeedType = "Quote",
                                SegmentId = 2, // NSE_FNO for options
                                UnderlyingSymbol = indexInfo.Symbol
                            });
                        });
                    }

                    if (strikeInfo.Data.PutOption != null)
                    {
                        await App.Current.Dispatcher.InvokeAsync(() => {
                            Dashboard.MonitoredInstruments.Add(new DashboardInstrument
                            {
                                Symbol = $"{indexInfo.Symbol} {formattedStrike} PE",
                                SecurityId = strikeInfo.Data.PutOption.SecurityId,
                                FeedType = "Quote",
                                SegmentId = 2, // NSE_FNO for options
                                UnderlyingSymbol = indexInfo.Symbol
                            });
                        });
                    }
                }
                await UpdateStatusAsync($"Successfully added options for {indexInfo.Name} to dashboard.");
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"Error adding options for {indexInfo.Name}: {ex.Message}");
                Debug.WriteLine($"[AddIndexOptions] Error fetching options for {indexInfo.Name}: {ex}");
            }
        }

        private async Task UpdateSubscriptionsAsync()
        {
            var allInstruments = Dashboard.MonitoredInstruments
                .Where(i => !string.IsNullOrEmpty(i.SecurityId))
                .ToList();

            var quoteInstruments = allInstruments
                .Where(i => i.FeedType == "Quote")
                .ToDictionary(i => i.SecurityId, i => i.SegmentId);

            var tickerInstruments = allInstruments
                .Where(i => i.FeedType == "Ticker")
                .ToDictionary(i => i.SecurityId, i => i.SegmentId);

            if (quoteInstruments.Any())
            {
                Debug.WriteLine($"[WebSocket] Subscribing to {quoteInstruments.Count} Quote instruments.");
                await _webSocketClient.SubscribeToInstrumentsAsync(quoteInstruments, 17); // Quote
            }

            if (tickerInstruments.Any())
            {
                Debug.WriteLine($"[WebSocket] Subscribing to {tickerInstruments.Count} Ticker instruments.");
                await _webSocketClient.SubscribeToInstrumentsAsync(tickerInstruments, 15); // Ticker
            }
        }

        private void OnLtpUpdateReceived(TickerPacket packet)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                if (SelectedIndex != null && packet.SecurityId == SelectedIndex.ScripId)
                {
                    UnderlyingPrice = packet.LastPrice;
                }
                Dashboard.UpdateLtp(packet);
            });
        }

        private void OnPreviousCloseUpdateReceived(PreviousClosePacket packet)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                Dashboard.UpdatePreviousClose(packet);
                if (SelectedIndex?.ScripId == packet.SecurityId)
                {
                    UnderlyingPreviousClose = packet.PreviousClose;
                }
            });
        }

        private void OnQuoteUpdateReceived(QuotePacket packet)
        {
            _analysisService.OnQuoteReceived(packet);

            App.Current.Dispatcher.Invoke(() =>
            {
                // Update underlying price for the selected index if it's a quote packet for that index
                if (SelectedIndex != null && packet.SecurityId == SelectedIndex.ScripId)
                {
                    UnderlyingPrice = packet.LastPrice;
                }

                // Update option chain LTP and Volume
                if (_optionScripMap.TryGetValue(packet.SecurityId, out var optionDetails))
                {
                    optionDetails.LTP = packet.LastPrice;
                    optionDetails.Volume = packet.Volume;
                }

                Dashboard.UpdateQuote(packet);
            });
        }

        private void OnOiUpdateReceived(OiPacket packet)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                if (_optionScripMap.TryGetValue(packet.SecurityId, out var optionDetails))
                {
                    optionDetails.OI = packet.OpenInterest;
                }

                Dashboard.UpdateOi(packet);
            });
        }


        private void UpdateUnderlyingData()
        {
            // This method might become less critical if underlying price is updated via WebSocket for the selected index
            // But it can still be used for initial load or if WebSocket data is temporarily unavailable.
            if (SelectedIndex == null) return;

            var selectedInstrument = Dashboard.MonitoredInstruments.FirstOrDefault(i => i.SecurityId == SelectedIndex.ScripId);
            if (selectedInstrument != null)
            {
                UnderlyingPrice = selectedInstrument.LTP;
                UnderlyingPreviousClose = selectedInstrument.Close;
            }
        }

        public async Task LoadPortfolioAsync()
        {
            await UpdateStatusAsync("Fetching portfolio...");
            try
            {
                var positionsFromApi = await _apiClient.GetPositionsAsync();
                var fundLimitFromApi = await _apiClient.GetFundLimitAsync();

                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    var selectedIds = new HashSet<string>(OpenPositions.Where(p => p.IsSelected).Select(p => p.SecurityId));
                    OpenPositions.Clear();
                    ClosedPositions.Clear();
                    if (positionsFromApi != null) { foreach (var posData in positionsFromApi) { var uiPosition = new Position { IsSelected = selectedIds.Contains(posData.SecurityId), SecurityId = posData.SecurityId, Ticker = posData.TradingSymbol, Quantity = posData.NetQuantity, AveragePrice = posData.BuyAverage, LastTradedPrice = posData.LastTradedPrice, RealizedPnl = posData.RealizedProfit, ProductType = posData.ProductType, SellAverage = posData.SellAverage, BuyQuantity = posData.BuyQuantity, SellQuantity = posData.SellQuantity }; if (uiPosition.Quantity != 0) OpenPositions.Add(uiPosition); else ClosedPositions.Add(uiPosition); } }
                    if (fundLimitFromApi != null) { FundDetails.AvailableBalance = fundLimitFromApi.AvailableBalance; FundDetails.UtilizedMargin = fundLimitFromApi.UtilizedAmount; FundDetails.Collateral = fundLimitFromApi.CollateralAmount; FundDetails.WithdrawableBalance = fundLimitFromApi.WithdrawableBalance; }
                    OnPropertyChanged(nameof(BookedPnl));
                    OnPropertyChanged(nameof(OpenPnl));
                    OnPropertyChanged(nameof(NetPnl));
                });
                await UpdateStatusAsync("Portfolio updated.");
            }
            catch (DhanApiException ex)
            {
                await UpdateStatusAsync(ex.Message);
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"An unexpected error occurred: {ex.Message}");
            }
        }

        public async Task LoadOrdersAsync()
        {
            await UpdateStatusAsync("Fetching order book...");
            try
            {
                var orders = await _apiClient.GetOrderBookAsync();
                await App.Current.Dispatcher.InvokeAsync(() => { Orders.Clear(); if (orders != null) foreach (var order in orders.OrderByDescending(o => DateTime.Parse(o.CreateTime))) Orders.Add(order); });
                await UpdateStatusAsync("Order book updated.");
            }
            catch (DhanApiException ex)
            {
                await UpdateStatusAsync(ex.Message);
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"An unexpected error occurred: {ex.Message}");
            }
        }

        private async Task LoadExpiryAndOptionChainAsync()
        {
            if (_isDataLoading || SelectedIndex == null) return;

            try
            {
                _isDataLoading = true;

                string currentApiSecurityId = string.Empty;
                string currentApiSegment = string.Empty;
                ExpiryListResponse? expiryListResponse = null;

                // --- CRITICAL FIX ATTEMPT 1: Try using the SPOT INDEX SecurityId and IDX_I segment for Option Chain API calls ---
                Debug.WriteLine($"[OptionChain] Attempt 1: Fetching expiry dates for {SelectedIndex.Name} using SPOT INDEX SecurityId: {SelectedIndex.ScripId} and Segment: {SelectedIndex.Segment}");
                expiryListResponse = await _apiClient.GetExpiryListAsync(SelectedIndex.ScripId, SelectedIndex.Segment);

                if (expiryListResponse?.ExpiryDates != null && expiryListResponse.ExpiryDates.Any())
                {
                    currentApiSecurityId = SelectedIndex.ScripId;
                    currentApiSegment = SelectedIndex.Segment;
                    Debug.WriteLine($"[OptionChain] Attempt 1 (Spot Index) successful for expiry list.");
                }
                else
                {
                    Debug.WriteLine($"[OptionChain] Attempt 1 (Spot Index) failed for expiry list. Trying Attempt 2 (Future ID).");

                    // --- CRITICAL FIX ATTEMPT 2: If spot index fails, try using the FUTURE's SecurityId and NSE_FNO segment ---
                    string? futureSecurityId = _scripMasterService.FindNearMonthFutureSecurityId(SelectedIndex.Symbol);
                    if (!string.IsNullOrEmpty(futureSecurityId))
                    {
                        currentApiSecurityId = futureSecurityId;
                        currentApiSegment = "NSE_FNO"; // Futures and options are in NSE_FNO
                        Debug.WriteLine($"[OptionChain] Attempt 2: Fetching expiry dates for {SelectedIndex.Name} using FUTURE SecurityId: {currentApiSecurityId} and Segment: {currentApiSegment}");
                        expiryListResponse = await _apiClient.GetExpiryListAsync(currentApiSecurityId, currentApiSegment);

                        if (expiryListResponse?.ExpiryDates != null && expiryListResponse.ExpiryDates.Any())
                        {
                            Debug.WriteLine($"[OptionChain] Attempt 2 (Future ID) successful for expiry list.");
                        }
                        else
                        {
                            await UpdateStatusAsync($"Could not get expiry dates for {SelectedIndex.Name} after both attempts.");
                            Debug.WriteLine($"[OptionChain] No expiry dates found for {SelectedIndex.Name} after both attempts. Aborting option chain load.");
                            App.Current.Dispatcher.Invoke(ExpiryDates.Clear);
                            App.Current.Dispatcher.Invoke(OptionChainRows.Clear);
                            return; // Both attempts failed
                        }
                    }
                    else
                    {
                        await UpdateStatusAsync($"Could not find near-month future for {SelectedIndex.Name}. Cannot load options.");
                        Debug.WriteLine($"[OptionChain] Failed to find future SecurityId for {SelectedIndex.Name} ({SelectedIndex.Symbol}). Aborting option chain load.");
                        App.Current.Dispatcher.Invoke(ExpiryDates.Clear);
                        App.Current.Dispatcher.Invoke(OptionChainRows.Clear);
                        return; // No future found to try
                    }
                }

                string? firstExpiry = expiryListResponse.ExpiryDates.FirstOrDefault();
                if (string.IsNullOrEmpty(firstExpiry))
                {
                    await UpdateStatusAsync("No valid expiry dates found.");
                    App.Current.Dispatcher.Invoke(OptionChainRows.Clear);
                    Debug.WriteLine("[OptionChain] First expiry is null or empty. Aborting option chain load.");
                    return;
                }

                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    ExpiryDates.Clear();
                    expiryListResponse.ExpiryDates.ForEach(ExpiryDates.Add);
                    _selectedExpiry = firstExpiry;
                    OnPropertyChanged(nameof(SelectedExpiry));
                });
                await UpdateStatusAsync($"Expiry dates loaded for {SelectedIndex.Name}.");

                // Store the successfully used API parameters for subsequent calls (e.g., manual expiry change, refresh)
                _lastApiSecurityIdUsed = currentApiSecurityId;
                _lastApiSegmentUsed = currentApiSegment;

                // Now load the option chain using the successfully determined API parameters
                await LoadOptionChainOnlyAsync(_lastApiSecurityIdUsed, _lastApiSegmentUsed);
            }
            catch (DhanApiException ex)
            {
                await UpdateStatusAsync($"API Error loading expiry/chain: {ex.Message}");
                Debug.WriteLine($"[OptionChain] DhanApiException: {ex.Message}");
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"An unexpected error occurred during expiry/chain load: {ex.Message}");
                Debug.WriteLine($"[OptionChain] General Exception: {ex.Message}");
            }
            finally
            {
                _isDataLoading = false;
            }
        }

        // Modified to accept apiSecurityId and apiSegment
        private async Task LoadOptionChainOnlyAsync(string apiSecurityId, string apiSegment)
        {
            if (_isDataLoading) return;

            try
            {
                _isDataLoading = true;
                if (SelectedIndex == null || string.IsNullOrWhiteSpace(SelectedExpiry))
                {
                    Debug.WriteLine("[OptionChain] SelectedIndex or SelectedExpiry is null/empty. Cannot load option chain.");
                    return;
                }

                _optionChainRefreshTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                Debug.WriteLine($"[OptionChain] Fetching option chain for {SelectedIndex.Name} (API SecId: {apiSecurityId}, Segment: {apiSegment}, Expiry: {SelectedExpiry}).");
                await UpdateStatusAsync($"Fetching option chain for {SelectedExpiry}...");

                // --- RELIABILITY FIX: Get latest underlying price for the spot index before fetching chain ---
                // This is for the UnderlyingPrice display, not for the option chain API call itself.
                var quoteResponse = await _apiClient.GetQuoteAsync(SelectedIndex.ScripId); // Use spot index ID for quote
                if (quoteResponse != null)
                {
                    UnderlyingPrice = quoteResponse.Ltp;
                    UnderlyingPreviousClose = quoteResponse.PreviousClose;
                    Debug.WriteLine($"[OptionChain] Underlying Price for {SelectedIndex.Symbol} (ID: {SelectedIndex.ScripId}): {UnderlyingPrice}");
                }
                else
                {
                    Debug.WriteLine($"[OptionChain] Failed to get underlying quote for {SelectedIndex.Symbol} (ID: {SelectedIndex.ScripId})");
                }


                // Use the determined apiSecurityId and apiSegment for GetOptionChainAsync
                var optionChainResponse = await _apiClient.GetOptionChainAsync(apiSecurityId, apiSegment, SelectedExpiry);
                if (optionChainResponse?.Data?.OptionChain != null)
                {
                    await App.Current.Dispatcher.InvokeAsync(() =>
                    {
                        _optionScripMap.Clear();

                        var currentUnderlyingPrice = UnderlyingPrice;

                        var allStrikes = optionChainResponse.Data.OptionChain
                            .Select(kvp => decimal.TryParse(kvp.Key, out var p) ? new { Price = p, Data = kvp.Value } : null)
                            .Where(s => s != null)
                            .OrderBy(s => s.Price)
                            .ToList();

                        if (!allStrikes.Any())
                        {
                            Debug.WriteLine("[OptionChain] No strikes found in option chain response.");
                            return;
                        }
                        var atmStrike = allStrikes.OrderBy(s => Math.Abs(s.Price - currentUnderlyingPrice)).FirstOrDefault();
                        if (atmStrike == null)
                        {
                            Debug.WriteLine("[OptionChain] Could not determine ATM strike.");
                            return;
                        }

                        int atmIndex = allStrikes.IndexOf(atmStrike);
                        const int strikesToShow = 15;
                        int startIndex = Math.Max(0, atmIndex - (strikesToShow / 2));
                        int endIndex = Math.Min(allStrikes.Count - 1, atmIndex + (strikesToShow / 2));

                        OptionChainRows.Clear();
                        for (int i = startIndex; i <= endIndex; i++)
                        {
                            var strikeData = allStrikes[i].Data;
                            if (strikeData == null) continue;
                            strikeData.StrikePrice = allStrikes[i].Price;

                            var newRow = new OptionChainRow
                            {
                                StrikePrice = strikeData.StrikePrice,
                                IsAtm = strikeData.StrikePrice == atmStrike.Price,
                                CallState = strikeData.StrikePrice < currentUnderlyingPrice ? OptionState.ITM :
                                            (strikeData.StrikePrice == currentUnderlyingPrice ? OptionState.ATM : OptionState.OTM),
                                PutState = strikeData.StrikePrice > currentUnderlyingPrice ? OptionState.ITM :
                                           (strikeData.StrikePrice == currentUnderlyingPrice ? OptionState.ATM : OptionState.OTM),
                                CallOption = MapToOptionDetails(strikeData.CallOption),
                                PutOption = MapToOptionDetails(strikeData.PutOption)
                            };
                            OptionChainRows.Add(newRow);

                            if (newRow.CallOption != null && !string.IsNullOrEmpty(newRow.CallOption.SecurityId))
                            {
                                _optionScripMap[newRow.CallOption.SecurityId] = newRow.CallOption;
                                Debug.WriteLine($"[OptionChain] Mapped CallOption: {newRow.CallOption.SecurityId}");
                            }
                            if (newRow.PutOption != null && !string.IsNullOrEmpty(newRow.PutOption.SecurityId))
                            {
                                _optionScripMap[newRow.PutOption.SecurityId] = newRow.PutOption;
                                Debug.WriteLine($"[OptionChain] Mapped PutOption: {newRow.PutOption.SecurityId}");
                            }
                        }

                        CalculateOptionChainAggregates();
                        ManageOptionChainRefreshTimer();
                    });

                    var instrumentsToSubscribe = _optionScripMap.Keys.ToDictionary(id => id, id => 2);
                    if (instrumentsToSubscribe.Any())
                    {
                        Debug.WriteLine($"[WebSocket] Subscribing to {instrumentsToSubscribe.Count} Option instruments for Quote/OI updates.");
                        await _webSocketClient.SubscribeToInstrumentsAsync(instrumentsToSubscribe, 17);
                        await _webSocketClient.SubscribeToInstrumentsAsync(instrumentsToSubscribe, 5);
                    }
                    await UpdateStatusAsync("Option chain loaded and subscribed to live updates.");
                }
                else
                {
                    await UpdateStatusAsync("Failed to load option chain data.");
                    Debug.WriteLine("[OptionChain] Option chain response data is null.");
                    App.Current.Dispatcher.Invoke(OptionChainRows.Clear);
                }
            }
            catch (DhanApiException ex)
            {
                await UpdateStatusAsync($"API Error loading option chain: {ex.Message}");
                Debug.WriteLine($"[OptionChain] DhanApiException: {ex.Message}");
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"An unexpected error occurred during option chain load: {ex.Message}");
                Debug.WriteLine($"[OptionChain] General Exception: {ex.Message}");
            }
            finally
            {
                _isDataLoading = false;
            }
        }

        private async Task RefreshOptionChainDataAsync()
        {
            if (SelectedIndex == null || string.IsNullOrWhiteSpace(SelectedExpiry)) return;

            try
            {
                // Use the last successfully determined API parameters for refresh
                string apiSecurityId = _lastApiSecurityIdUsed;
                string apiSegment = _lastApiSegmentUsed;

                if (string.IsNullOrEmpty(apiSecurityId) || string.IsNullOrEmpty(apiSegment))
                {
                    Debug.WriteLine($"[OptionChainRefresh] API parameters not set. Skipping refresh for {SelectedIndex.Name}.");
                    return;
                }

                Debug.WriteLine($"[OptionChainRefresh] Refreshing option chain for {SelectedIndex.Name} (API SecId: {apiSecurityId}, Segment: {apiSegment}, Expiry: {SelectedExpiry}).");

                var optionChainResponse = await _apiClient.GetOptionChainAsync(apiSecurityId, apiSegment, SelectedExpiry);
                if (optionChainResponse?.Data?.OptionChain != null)
                {
                    await App.Current.Dispatcher.InvokeAsync(() =>
                    {
                        // Update underlying price for the selected index if it's a quote packet for that index
                        var spotIndexInstrument = Dashboard.MonitoredInstruments.FirstOrDefault(i => i.SecurityId == SelectedIndex.ScripId);
                        if (spotIndexInstrument != null)
                        {
                            UnderlyingPrice = spotIndexInstrument.LTP;
                            UnderlyingPreviousClose = spotIndexInstrument.Close;
                        }

                        var currentUnderlyingPrice = UnderlyingPrice;
                        var newChainData = optionChainResponse.Data.OptionChain;

                        // Re-determine ATM strike based on latest underlying price
                        var allStrikes = newChainData
                            .Select(kvp => decimal.TryParse(kvp.Key, out var p) ? new { Price = p, Data = kvp.Value } : null)
                            .Where(s => s != null)
                            .OrderBy(s => s!.Price)
                            .ToList();

                        var atmStrike = allStrikes.OrderBy(s => Math.Abs(s!.Price - currentUnderlyingPrice)).FirstOrDefault();

                        foreach (var rowToUpdate in OptionChainRows)
                        {
                            if (newChainData.TryGetValue(rowToUpdate.StrikePrice.ToString("F2"), out var strikeData) ||
                                newChainData.TryGetValue(rowToUpdate.StrikePrice.ToString("F0"), out strikeData) ||
                                newChainData.TryGetValue(rowToUpdate.StrikePrice.ToString(CultureInfo.InvariantCulture), out strikeData))
                            {
                                if (rowToUpdate.CallOption != null && strikeData.CallOption != null)
                                {
                                    rowToUpdate.CallOption.OI = strikeData.CallOption.OpenInterest;
                                    rowToUpdate.CallOption.OiChange = strikeData.CallOption.OiChange;
                                    rowToUpdate.CallOption.Volume = strikeData.CallOption.Volume;
                                    rowToUpdate.CallOption.IV = strikeData.CallOption.ImpliedVolatility;
                                    rowToUpdate.CallOption.Delta = strikeData.CallOption.Greeks?.Delta ?? 0;
                                    rowToUpdate.CallOption.PreviousClose = strikeData.CallOption.PreviousClose;
                                }

                                if (rowToUpdate.PutOption != null && strikeData.PutOption != null)
                                {
                                    rowToUpdate.PutOption.OI = strikeData.PutOption.OpenInterest;
                                    rowToUpdate.PutOption.OiChange = strikeData.PutOption.OiChange;
                                    rowToUpdate.PutOption.Volume = strikeData.PutOption.Volume;
                                    rowToUpdate.PutOption.IV = strikeData.PutOption.ImpliedVolatility;
                                    rowToUpdate.PutOption.Delta = strikeData.PutOption.Greeks?.Delta ?? 0;
                                    rowToUpdate.PutOption.PreviousClose = strikeData.PutOption.PreviousClose;
                                }

                                if (atmStrike != null)
                                {
                                    rowToUpdate.IsAtm = rowToUpdate.StrikePrice == atmStrike.Price;
                                    rowToUpdate.CallState = rowToUpdate.StrikePrice < currentUnderlyingPrice ? OptionState.ITM :
                                                            (rowToUpdate.StrikePrice == currentUnderlyingPrice ? OptionState.ATM : OptionState.OTM);
                                    rowToUpdate.PutState = rowToUpdate.StrikePrice > currentUnderlyingPrice ? OptionState.ITM :
                                                           (rowToUpdate.StrikePrice == currentUnderlyingPrice ? OptionState.ATM : OptionState.OTM);
                                }
                            }
                        }

                        CalculateOptionChainAggregates();
                    });
                }
                else
                {
                    Debug.WriteLine("[OptionChainRefresh] Option chain data is null during refresh. Skipping update.");
                }
            }
            catch (DhanApiException ex)
            {
                Debug.WriteLine($"[OptionChainRefresh] API Error during periodic refresh: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OptionChainRefresh] An unexpected error occurred during periodic refresh: {ex.Message}");
            }
        }


        private OptionDetails MapToOptionDetails(DhanApi.Models.OptionData? d)
        {
            if (d == null) return new OptionDetails();
            return new OptionDetails { SecurityId = d.SecurityId, LTP = d.LastPrice, PreviousClose = d.PreviousClose, IV = d.ImpliedVolatility, OI = d.OpenInterest, OiChange = d.OiChange, OiChangePercent = d.OiChangePercent, Volume = d.Volume, Delta = d.Greeks?.Delta ?? 0 };
        }

        private void CalculateOptionChainAggregates()
        {
            if (!OptionChainRows.Any()) return;

            long runningCallOi = 0;
            long runningPutOi = 0;
            long runningCallVolume = 0;
            long runningPutVolume = 0;

            foreach (var row in OptionChainRows)
            {
                if (row.CallOption != null)
                {
                    runningCallOi += (long)row.CallOption.OI;
                    runningCallVolume += row.CallOption.Volume;
                }
                if (row.PutOption != null)
                {
                    runningPutOi += (long)row.PutOption.OI;
                    runningPutVolume += row.PutOption.Volume;
                }
            }

            TotalCallOi = runningCallOi;
            TotalPutOi = runningPutOi;
            TotalCallVolume = runningCallVolume;
            TotalPutVolume = runningPutVolume;

            PcrOi = (TotalCallOi > 0) ? (decimal)TotalPutOi / TotalCallOi : 0;

            long maxCallOi = OptionChainRows.Any(r => r.CallOption != null) ? OptionChainRows.Max(r => (long)(r.CallOption?.OI ?? 0)) : 0;
            long maxPutOi = OptionChainRows.Any(r => r.PutOption != null) ? OptionChainRows.Max(r => (long)(r.PutOption?.OI ?? 0)) : 0;
            MaxOi = Math.Max(maxCallOi, maxPutOi);

            decimal maxCallOiChange = OptionChainRows.Any(r => r.CallOption != null) ? OptionChainRows.Max(r => Math.Abs(r.CallOption?.OiChange ?? 0)) : 0;
            decimal maxPutOiChange = OptionChainRows.Any(r => r.PutOption != null) ? OptionChainRows.Max(r => Math.Abs(r.PutOption?.OiChange ?? 0)) : 0;
            MaxOiChange = Math.Max(maxCallOiChange, maxPutOiChange);
        }

        private void ManageOptionChainRefreshTimer()
        {
            if (_optionChainRefreshTimer == null) return;

            var now = DateTime.Now;
            var marketOpen = DateTime.Today.Add(new TimeSpan(9, 15, 0));
            var marketClose = DateTime.Today.Add(new TimeSpan(15, 30, 0));
            var refreshInterval = TimeSpan.FromSeconds(5);

            if (now >= marketOpen && now < marketClose)
            {
                _optionChainRefreshTimer.Change(refreshInterval, refreshInterval);
                UpdateStatusAsync("Live option chain refresh started.");
            }
            else if (now < marketOpen)
            {
                var delay = marketOpen - now;
                _optionChainRefreshTimer.Change(delay, refreshInterval);
                UpdateStatusAsync($"Live refresh scheduled to start at {marketOpen:HH:mm:ss}.");
            }
            else
            {
                _optionChainRefreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
                UpdateStatusAsync("Market is closed. Live refresh stopped.");
            }
        }

        private Task UpdateStatusAsync(string m) => App.Current.Dispatcher.InvokeAsync(() => { StatusMessage = m; OnPropertyChanged(nameof(StatusMessage)); }).Task;

        #endregion

        #region Boilerplate
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        public void Dispose()
        {
            _webSocketClient?.Dispose();
            _optionChainRefreshTimer?.Dispose();
        }
        #endregion
    }
}

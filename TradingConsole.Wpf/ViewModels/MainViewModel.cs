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
using TradingConsole.Wpf.Services;
using TradingConsole.DhanApi;
using TradingConsole.DhanApi.Models;
using TradingConsole.DhanApi.Models.WebSocket;
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
                    // This now triggers the entire reliable refresh sequence
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
                    // CRITICAL FIX: Only trigger LoadOptionChainOnlyAsync if the change
                    // is NOT due to LoadExpiryAndOptionChainAsync setting the initial expiry.
                    // This prevents redundant calls and potential rate limit issues.
                    // We check if _isDataLoading is false, which indicates a user-initiated change.
                    if (!_isDataLoading && !string.IsNullOrEmpty(_selectedExpiry))
                    {
                        Task.Run(() => LoadOptionChainOnlyAsync(_lastApiSecurityIdUsed, _lastApiSegmentUsed));
                    }
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

        // CRITICAL FIX: Added missing OpenOrderWindowForPosition method
        private void OpenOrderWindowForPosition(Position position, bool isBuy)
        {
            // You'll need to decide what information is relevant for opening an order window from a position.
            // For now, I'll assume it's similar to opening from an option, but with position details.
            // You might need to adjust the OrderEntryViewModel constructor or create a new one for positions.
            string instrumentName = position.Ticker; // Use the position's ticker as the instrument name
            var orderViewModel = new OrderEntryViewModel(position, isBuy, _apiClient, _dhanClientId, _scripMasterService, position.ProductType); // Pass relevant position details
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
                // Updated to use full index names for FindIndexSecurityId
                { "Nifty 50", "Nifty 50" },
                { "Nifty Bank", "Nifty Bank" },
                { "Sensex", "Sensex" }
            };

            foreach (var pair in symbolsToLoad)
            {
                // FindIndexSecurityId now expects the full index name
                // CRITICAL FIX: Get the full ScripInfo for the index to retrieve ExchId
                ScripInfo? indexScripInfo = _scripMasterService.FindIndexScripInfo(pair.Value);

                if (indexScripInfo != null && !string.IsNullOrEmpty(indexScripInfo.SecurityId))
                {
                    App.Current.Dispatcher.Invoke(() =>
                    {
                        Indices.Add(new TickerIndex
                        {
                            Name = pair.Key, // Display name
                            Symbol = pair.Key, // Use full name as symbol for consistency with how it's passed to FindIndexSecurityId
                            ScripId = indexScripInfo.SecurityId,
                            Segment = indexScripInfo.Segment, // Segment for spot indices (IDX_I in Dhan API)
                            ExchId = indexScripInfo.ExchId // CRITICAL FIX: Populate ExchId
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

            // Start the option chain refresh timer after initial data load
            // CRITICAL FIX: Changed from TimeSpan.FromSeconds(5) to TimeSpan.FromSeconds(10) for refresh interval
            // to reduce API calls and mitigate "TooManyRequests" errors.
            _optionChainRefreshTimer = new Timer(async _ => await RefreshOptionChainDataAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));


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
                // Corrected input for INFY equity
                new() { Symbol = "NIFTY-FUT", IsFuture = true, UnderlyingSymbol = "NIFTY", FeedType = "Quote", SegmentId = 2 }, // SegmentId 2 for NSE_FNO
                new() { Symbol = "BANKNIFTY-FUT", IsFuture = true, UnderlyingSymbol = "BANKNIFTY", FeedType = "Quote", SegmentId = 2 },
                new() { Symbol = "HDFCBANK", FeedType = "Quote", SegmentId = 1 }, // SegmentId 1 for NSE_EQ
                new() { Symbol = "HDFCBANK-FUT", IsFuture = true, UnderlyingSymbol = "HDFCBANK", FeedType = "Quote", SegmentId = 2 },
                new() { Symbol = "ICICIBANK", FeedType = "Quote", SegmentId = 1 },
                new() { Symbol = "ICICIBANK-FUT", IsFuture = true, UnderlyingSymbol = "ICICIBANK", FeedType = "Quote", SegmentId = 2 },
                // CRITICAL FIX: Changed "RELIANCE" to "RELIANCE INDUSTRIES" for equity search
                new() { Symbol = "RELIANCE INDUSTRIES", FeedType = "Quote", SegmentId = 1 },
                new() { Symbol = "RELIANCE-FUT", IsFuture = true, UnderlyingSymbol = "RELIANCE", FeedType = "Quote", SegmentId = 2 },
                new() { Symbol = "INFOSYS", FeedType = "Quote", SegmentId = 1 }, // Changed from "INFY" to "INFOSYS"
                new() { Symbol = "INFY-FUT", IsFuture = true, UnderlyingSymbol = "INFY", FeedType = "Quote", SegmentId = 2 }
            });

            await ResolveInstrumentIdsAsync(instrumentsToMonitor);

            // Add resolved instruments to Dashboard.MonitoredInstruments
            foreach (var inst in instrumentsToMonitor)
            {
                if (!string.IsNullOrEmpty(inst.SecurityId))
                {
                    // CRITICAL FIX: Ensure no duplicate SecurityIds are added to MonitoredInstruments
                    // This prevents the "An item with the same key has already been added" error
                    if (!Dashboard.MonitoredInstruments.Any(x => x.SecurityId == inst.SecurityId))
                    {
                        await App.Current.Dispatcher.InvokeAsync(() => Dashboard.MonitoredInstruments.Add(inst));
                    }
                    else
                    {
                        Debug.WriteLine($"[DashboardInit] Skipping duplicate instrument (SecurityId: {inst.SecurityId}) for dashboard: {inst.Symbol}");
                    }
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
                // CRITICAL FIX: Increased delay between adding options for each index
                // to give the API more time to reset rate limits.
                // This delay accounts for the two API calls (expiry list + option chain) for each index.
                await Task.Delay(2 * DhanApiClient.ApiCallDelayMs + 100); // 2 API calls + small buffer
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
                        // FindNearMonthFutureSecurityId now returns ScripInfo
                        ScripInfo? futureScrip = _scripMasterService.FindNearMonthFutureSecurityId(inst.UnderlyingSymbol);
                        if (futureScrip != null)
                        {
                            inst.SecurityId = futureScrip.SecurityId;
                            inst.SegmentId = GetSegmentIdFromName(futureScrip.Segment); // Ensure segment is correctly mapped
                            Debug.WriteLine($"[RESOLVER] Future Found For '{inst.UnderlyingSymbol}': ID = {inst.SecurityId}, Segment = {futureScrip.Segment}");
                        }
                        else
                        {
                            Debug.WriteLine($"[RESOLVER] FAIL - No future found for '{inst.UnderlyingSymbol}'");
                        }
                    }
                    else if (inst.FeedType == "Quote" && inst.SegmentId == 1) // Assuming SegmentId 1 is NSE_EQ
                    {
                        // Pass the exact symbol as input for equity search
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
                // CRITICAL FIX: Use the spot index's SecurityId and Segment for option chain API calls
                string apiSecurityId = indexInfo.ScripId;
                string apiSegment = MapSegmentForOptionChainApi(indexInfo.Segment); // Ensure correct API segment for indices

                ExpiryListResponse? expiryListResponse = null;

                Debug.WriteLine($"[OptionChain] Fetching expiry dates for {indexInfo.Name} using SPOT INDEX SecurityId: {apiSecurityId} and Segment: {apiSegment}");
                expiryListResponse = await _apiClient.GetExpiryListAsync(apiSecurityId, apiSegment);

                if (expiryListResponse?.ExpiryDates == null || !expiryListResponse.ExpiryDates.Any())
                {
                    await UpdateStatusAsync($"Could not get expiry dates for {indexInfo.Name}. Aborting option chain load.");
                    Debug.WriteLine($"[OptionChain] No expiry dates found for {indexInfo.Name} (Spot ID: {apiSecurityId}). Aborting option chain load.");
                    App.Current.Dispatcher.Invoke(ExpiryDates.Clear);
                    App.Current.Dispatcher.Invoke(OptionChainRows.Clear);
                    return;
                }

                string? nearestExpiry = expiryListResponse.ExpiryDates.FirstOrDefault();
                if (string.IsNullOrEmpty(nearestExpiry))
                {
                    Debug.WriteLine($"[AddIndexOptions] Nearest expiry is null or empty for {indexInfo.Name}. Aborting option dashboard load.");
                    return;
                }

                // Removed Task.Delay(1100) here, as it's now handled by DhanApiClient's semaphore.
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

                // Determine the correct F&O segment ID for options based on the index's exchange
                int optionSegmentIdForDashboard;
                if (indexInfo.ExchId == "NSE") // Nifty 50 and Nifty Bank are NSE indices
                {
                    optionSegmentIdForDashboard = GetSegmentIdFromName("NSE_FNO");
                }
                else if (indexInfo.ExchId == "BSE") // Sensex is a BSE index
                {
                    optionSegmentIdForDashboard = GetSegmentIdFromName("BSE_FNO");
                }
                else
                {
                    optionSegmentIdForDashboard = GetSegmentIdFromName("NSE_FNO"); // Default to NSE_FNO if exchange is unknown
                }


                for (int i = startIndex; i <= endIndex; i++)
                {
                    var strikeInfo = allStrikes[i]!;
                    if (strikeInfo.Data == null) continue;

                    string formattedStrike = strikeInfo.Price.ToString("G29");

                    if (strikeInfo.Data.CallOption != null)
                    {
                        await App.Current.Dispatcher.InvokeAsync(() => {
                            // CRITICAL FIX: Ensure no duplicate SecurityIds are added to MonitoredInstruments here either
                            if (!Dashboard.MonitoredInstruments.Any(x => x.SecurityId == strikeInfo.Data.CallOption.SecurityId))
                            {
                                Dashboard.MonitoredInstruments.Add(new DashboardInstrument
                                {
                                    Symbol = $"{indexInfo.Symbol} {formattedStrike} CE",
                                    SecurityId = strikeInfo.Data.CallOption.SecurityId,
                                    FeedType = "Quote",
                                    SegmentId = optionSegmentIdForDashboard, // Use the determined F&O segment ID
                                    UnderlyingSymbol = indexInfo.Symbol
                                });
                            }
                            else
                            {
                                // Log if SecurityId is empty, which is the core issue
                                if (string.IsNullOrEmpty(strikeInfo.Data.CallOption.SecurityId))
                                {
                                    Debug.WriteLine($"[AddIndexOptions] Skipping duplicate Call Option (SecurityId: EMPTY) for dashboard: {indexInfo.Symbol} {formattedStrike} CE. Check API response mapping.");
                                }
                                else
                                {
                                    Debug.WriteLine($"[AddIndexOptions] Skipping duplicate Call Option (SecurityId: {strikeInfo.Data.CallOption.SecurityId}) for dashboard: {indexInfo.Symbol} {formattedStrike} CE");
                                }
                            }
                        });
                        // Add a small delay between adding each option to the dashboard to prevent rapid UI updates
                        // and potentially reduce API call bursts if this triggers other chained events.
                        await Task.Delay(50);
                    }

                    if (strikeInfo.Data.PutOption != null)
                    {
                        await App.Current.Dispatcher.InvokeAsync(() => {
                            // CRITICAL FIX: Ensure no duplicate SecurityIds are added to MonitoredInstruments here either
                            if (!Dashboard.MonitoredInstruments.Any(x => x.SecurityId == strikeInfo.Data.PutOption.SecurityId))
                            {
                                Dashboard.MonitoredInstruments.Add(new DashboardInstrument
                                {
                                    Symbol = $"{indexInfo.Symbol} {formattedStrike} PE",
                                    SecurityId = strikeInfo.Data.PutOption.SecurityId,
                                    FeedType = "Quote",
                                    SegmentId = optionSegmentIdForDashboard, // Use the determined F&O segment ID
                                    UnderlyingSymbol = indexInfo.Symbol
                                });
                            }
                            else
                            {
                                // Log if SecurityId is empty, which is the core issue
                                if (string.IsNullOrEmpty(strikeInfo.Data.PutOption.SecurityId))
                                {
                                    Debug.WriteLine($"[AddIndexOptions] Skipping duplicate Put Option (SecurityId: EMPTY) for dashboard: {indexInfo.Symbol} {formattedStrike} PE. Check API response mapping.");
                                }
                                else
                                {
                                    Debug.WriteLine($"[AddIndexOptions] Skipping duplicate Put Option (SecurityId: {strikeInfo.Data.PutOption.SecurityId}) for dashboard: {indexInfo.Symbol} {formattedStrike} PE");
                                }
                            }
                        });
                        // Add a small delay between adding each option to the dashboard
                        await Task.Delay(50);
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
                // Apply DistinctBy here as a final safeguard, though ideally duplicates are prevented at Add stage
                .DistinctBy(i => i.SecurityId)
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

        // Helper to convert segment name string to int ID for WebSocket
        private int GetSegmentIdFromName(string segmentName)
        {
            return segmentName switch
            {
                "NSE_EQ" => 1,
                "NSE_FNO" => 2,
                "BSE_EQ" => 3, // Assuming BSE_EQ exists and has an ID
                "BSE_FNO" => 8, // As per user's hint for Sensex options
                "IDX_I" => 0,
                "I" => 0, // Map internal "I" for Index to WebSocket's 0
                _ => -1 // Unknown segment
            };
        }

        // Helper to map segment name for Option Chain API calls (which expects "IDX_I" for indices)
        private string MapSegmentForOptionChainApi(string segmentName)
        {
            return segmentName switch
            {
                "I" => "IDX_I", // Map internal "I" for Index to API's "IDX_I"
                _ => segmentName // For other segments, use as is (e.g., NSE_FNO, BSE_FNO)
            };
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
            _analysisService.OnQuoteReceived(packet); // Changed from OnAnalysisReceived to OnQuoteReceived

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
                    if (positionsFromApi != null) { foreach (var posData in positions.Where(p => p.NetQuantity != 0)) { var uiPosition = new Position { IsSelected = selectedIds.Contains(posData.SecurityId), SecurityId = posData.SecurityId, Ticker = posData.TradingSymbol, Quantity = posData.NetQuantity, AveragePrice = posData.BuyAverage, LastTradedPrice = posData.LastTradedPrice, RealizedPnl = posData.RealizedProfit, ProductType = posData.ProductType, SellAverage = posData.SellAverage, BuyQuantity = posData.BuyQuantity, SellQuantity = posData.SellQuantity }; OpenPositions.Add(uiPosition); } foreach (var posData in positions.Where(p => p.NetQuantity == 0)) { var uiPosition = new Position { IsSelected = selectedIds.Contains(posData.SecurityId), SecurityId = posData.SecurityId, Ticker = posData.TradingSymbol, Quantity = posData.NetQuantity, AveragePrice = posData.BuyAverage, LastTradedPrice = posData.LastTradedPrice, RealizedPnl = posData.RealizedProfit, ProductType = posData.ProductType, SellAverage = posData.SellAverage, BuyQuantity = posData.BuyQuantity, SellQuantity = posData.SellQuantity }; ClosedPositions.Add(uiPosition); } }
                    if (fundLimitFromApi != null) { FundDetails.AvailableBalance = fundLimitFromApi.AvailableBalance; FundDetails.UtilizedMargin = fundLimitFromApi.UtilizedAmount; FundDetails.Collateral = fundLimitFromApi.CollateralAmount; FundDetails.WithdrawableBalance = fundLimitFromApi.WithdrawableBalance; }
                    OnPropertyChanged(nameof(BookedPnl));
                    OnPropertyChanged(nameof(OpenPnl));
                    OnPropertyChanged(nameof(NetPnl));
                });
                await UpdateStatusAsync("Portfolio updated.");
            }
            catch (DhanApiException ex)
            {
                // Log the exception details for debugging
                Debug.WriteLine($"[LoadPortfolioAsync] Dhan API Exception: {ex.Message}");
                await UpdateStatusAsync($"API Error: {ex.Message}");
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"An unexpected error occurred: {ex.Message}");
                Debug.WriteLine($"[LoadPortfolioAsync] Unexpected Error: {ex.Message}");
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
                // Log the exception details for debugging
                Debug.WriteLine($"[LoadOrdersAsync] Dhan API Exception: {ex.Message}");
                await UpdateStatusAsync($"API Error: {ex.Message}");
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"An unexpected error occurred: {ex.Message}");
                Debug.WriteLine($"[LoadOrdersAsync] Unexpected Error: {ex.Message}");
            }
        }

        private async Task LoadExpiryAndOptionChainAsync()
        {
            if (_isDataLoading || SelectedIndex == null) return;

            try
            {
                _isDataLoading = true; // Set flag to prevent redundant calls from SelectedExpiry setter

                // CRITICAL FIX: Use the spot index's SecurityId and Segment for option chain API calls
                string apiSecurityId = SelectedIndex.ScripId;
                string apiSegment = MapSegmentForOptionChainApi(SelectedIndex.Segment); // Map to API's expected segment for indices

                ExpiryListResponse? expiryListResponse = null;

                Debug.WriteLine($"[OptionChain] Fetching expiry dates for {SelectedIndex.Name} using SPOT INDEX SecurityId: {apiSecurityId} and Segment: {apiSegment}");
                expiryListResponse = await _apiClient.GetExpiryListAsync(apiSecurityId, apiSegment);

                if (expiryListResponse?.ExpiryDates == null || !expiryListResponse.ExpiryDates.Any())
                {
                    await UpdateStatusAsync($"Could not get expiry dates for {SelectedIndex.Name}. Aborting option chain load.");
                    Debug.WriteLine($"[OptionChain] No expiry dates found for {SelectedIndex.Name} (Spot ID: {apiSecurityId}). Aborting option chain load.");
                    App.Current.Dispatcher.Invoke(ExpiryDates.Clear);
                    App.Current.Dispatcher.Invoke(OptionChainRows.Clear);
                    return;
                }

                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    ExpiryDates.Clear();
                    foreach (var expiry in expiryListResponse.ExpiryDates.OrderBy(d => DateTime.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                    {
                        ExpiryDates.Add(expiry);
                    }
                    // Temporarily set _selectedExpiry directly to avoid triggering the setter's Task.Run
                    _selectedExpiry = ExpiryDates.FirstOrDefault();
                    OnPropertyChanged(nameof(SelectedExpiry)); // Manually notify property changed
                });

                // Store these for subsequent refreshes
                _lastApiSecurityIdUsed = apiSecurityId;
                _lastApiSegmentUsed = apiSegment;

                // Now explicitly call LoadOptionChainOnlyAsync once
                if (!string.IsNullOrEmpty(_selectedExpiry))
                {
                    await LoadOptionChainOnlyAsync(apiSecurityId, apiSegment);
                }
            }
            catch (DhanApiException ex)
            {
                Debug.WriteLine($"[OptionChain] Dhan API Exception: {ex.Message}");
                await UpdateStatusAsync($"API Error: {ex.Message}");
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"An unexpected error occurred: {ex.Message}");
                Debug.WriteLine($"[OptionChain] Unexpected Error: {ex.Message}");
            }
            finally
            {
                _isDataLoading = false; // Reset flag
            }
        }

        private async Task LoadOptionChainOnlyAsync(string apiSecurityId, string apiSegment)
        {
            if (SelectedIndex == null || string.IsNullOrWhiteSpace(SelectedExpiry) || string.IsNullOrEmpty(apiSecurityId) || string.IsNullOrEmpty(apiSegment)) return;

            try
            {
                await UpdateStatusAsync($"Fetching option chain for {SelectedIndex.Name} - {SelectedExpiry}...");
                Debug.WriteLine($"[OptionChain] Fetching option chain for {SelectedIndex.Name} (API SecId: {apiSecurityId}, Segment: {apiSegment}, Expiry: {SelectedExpiry}).");

                var optionChainResponse = await _apiClient.GetOptionChainAsync(apiSecurityId, apiSegment, SelectedExpiry);

                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    OptionChainRows.Clear();
                    _optionScripMap.Clear();
                    TotalCallOi = 0;
                    TotalPutOi = 0;
                    TotalCallVolume = 0;
                    TotalPutVolume = 0;
                    PcrOi = 0;
                    MaxOi = 0;
                    MaxOiChange = 0;
                });

                if (optionChainResponse?.Data?.OptionChain != null)
                {
                    await App.Current.Dispatcher.InvokeAsync(() =>
                    {
                        UnderlyingPrice = optionChainResponse.Data.UnderlyingLastPrice;

                        var allStrikes = optionChainResponse.Data.OptionChain
                            .Select(kvp => decimal.TryParse(kvp.Key, out var p) ? new { Price = p, Data = kvp.Value } : null)
                            .Where(s => s != null)
                            .OrderBy(s => s!.Price)
                            .ToList();

                        var currentUnderlyingPrice = UnderlyingPrice;
                        var atmStrikeData = allStrikes.OrderBy(s => Math.Abs(s!.Price - currentUnderlyingPrice)).First();

                        foreach (var strikeInfo in allStrikes)
                        {
                            if (strikeInfo?.Data == null) continue;

                            var callOptionDetails = MapToOptionDetails(strikeInfo.Data.CallOption);
                            var putOptionDetails = MapToOptionDetails(strikeInfo.Data.PutOption);

                            // Populate _optionScripMap for WebSocket updates
                            if (!string.IsNullOrEmpty(callOptionDetails.SecurityId))
                            {
                                _optionScripMap[callOptionDetails.SecurityId] = callOptionDetails;
                            }
                            if (!string.IsNullOrEmpty(putOptionDetails.SecurityId))
                            {
                                _optionScripMap[putOptionDetails.SecurityId] = putOptionDetails;
                            }

                            OptionChainRows.Add(new OptionChainRow
                            {
                                StrikePrice = strikeInfo.Price,
                                CallOption = callOptionDetails,
                                PutOption = putOptionDetails,
                                IsAtm = strikeInfo.Price == atmStrikeData.Price,
                                CallState = strikeInfo.Price < currentUnderlyingPrice ? OptionState.ITM :
                                            (currentUnderlyingPrice == strikeInfo.Price ? OptionState.ATM : OptionState.OTM),
                                PutState = strikeInfo.Price > currentUnderlyingPrice ? OptionState.ITM :
                                           (currentUnderlyingPrice == strikeInfo.Price ? OptionState.ATM : OptionState.OTM)
                            });
                        }
                        CalculateOptionChainAggregates();
                    });
                    await UpdateStatusAsync($"Option chain for {SelectedIndex.Name} - {SelectedExpiry} loaded.");
                }
                else
                {
                    await UpdateStatusAsync($"Failed to load option chain for {SelectedIndex.Name} - {SelectedExpiry}. Data is null.");
                    Debug.WriteLine($"[OptionChain] Option chain data is null for {SelectedIndex.Name} (API SecId: {apiSecurityId}, Segment: {apiSegment}, Expiry: {SelectedExpiry})");
                }
            }
            catch (DhanApiException ex)
            {
                Debug.WriteLine($"[OptionChain] Dhan API Exception: {ex.Message}");
                await UpdateStatusAsync($"API Error: {ex.Message}");
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"An unexpected error occurred: {ex.Message}");
                Debug.WriteLine($"[OptionChain] Unexpected Error: {ex.Message}");
            }
        }


        private async Task RefreshOptionChainDataAsync()
        {
            if (SelectedIndex == null || string.IsNullOrWhiteSpace(SelectedExpiry)) return;

            try
            {
                // CRITICAL FIX: Use properties of the currently SelectedIndex for refresh
                string apiSecurityId = SelectedIndex.ScripId;
                string apiSegment = MapSegmentForOptionChainApi(SelectedIndex.Segment);

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
                                                            (currentUnderlyingPrice == rowToUpdate.StrikePrice ? OptionState.ATM : OptionState.OTM);
                                    rowToUpdate.PutState = rowToUpdate.StrikePrice > currentUnderlyingPrice ? OptionState.ITM :
                                                           (currentUnderlyingPrice == rowToUpdate.StrikePrice ? OptionState.ATM : OptionState.OTM);
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
            // CRITICAL FIX: Increased refresh interval to 10 seconds to reduce API calls
            var refreshInterval = TimeSpan.FromSeconds(10);

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

﻿// TradingConsole.Wpf/ViewModels/MainViewModel.cs

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
        #region Constants
        private const string ProductTypeIntraday = "INTRADAY";
        private const string ProductTypeMargin = "MARGIN";
        private const string OrderTypeMarket = "MARKET";
        private const string TransactionTypeBuy = "BUY";
        private const string TransactionTypeSell = "SELL";
        private const string ValidityDay = "DAY";
        private const string FeedTypeQuote = "Quote";
        private const string FeedTypeTicker = "Ticker";
        #endregion

        #region Private Fields
        private readonly DhanApiClient _apiClient;
        private readonly DhanWebSocketClient _webSocketClient;
        private readonly ScripMasterService _scripMasterService;
        private readonly AnalysisService _analysisService;
        private readonly string _dhanClientId;
        private Timer? _optionChainRefreshTimer;
        private readonly Dictionary<string, OptionDetails> _optionScripMap = new();
        private readonly HashSet<string> _dashboardOptionsLoadedFor = new();
        private readonly Dictionary<string, string> _nearestExpiryDates = new();
        private bool _isDataLoading = false;
        #endregion

        #region Public Properties
        public DashboardViewModel Dashboard { get; }
        public AnalysisService AnalysisService => _analysisService;
        public SettingsViewModel Settings { get; }

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
        #endregion

        public MainViewModel(string clientId, string accessToken)
        {
            _dhanClientId = clientId;
            _apiClient = new DhanApiClient(clientId, accessToken);
            _webSocketClient = new DhanWebSocketClient(clientId, accessToken);
            _scripMasterService = new ScripMasterService();

            var settingsService = new SettingsService();
            Settings = new SettingsViewModel(settingsService);

            _analysisService = new AnalysisService();
            _analysisService.OnAnalysisUpdated += OnAnalysisResultUpdated;

            // MODIFIED: Pass the scrip master service to the DashboardViewModel
            Dashboard = new DashboardViewModel(_scripMasterService);
            // NEW: Subscribe to the event that fires when a user adds an instrument from search results
            Dashboard.InstrumentSelectedForAddition += OnInstrumentSelectedForAddition;

            _webSocketClient.OnConnected += OnWebSocketConnected;
            _webSocketClient.OnLtpUpdate += OnLtpUpdateReceived;
            _webSocketClient.OnPreviousCloseUpdate += OnPreviousCloseUpdateReceived;
            _webSocketClient.OnQuoteUpdate += OnQuoteUpdateReceived;
            _webSocketClient.OnOiUpdate += OnOiUpdateReceived;
            _webSocketClient.OnOrderUpdate += OnOrderUpdateReceived;

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

        /// <summary>
        /// NEW: Event handler that adds a searched instrument to the dashboard and subscribes to its feed.
        /// </summary>
        private async void OnInstrumentSelectedForAddition(ScripInfo scripInfo)
        {
            if (Dashboard.MonitoredInstruments.Any(i => i.SecurityId == scripInfo.SecurityId))
            {
                await UpdateStatusAsync($"{scripInfo.SemInstrumentName} is already in the dashboard.");
                return;
            }

            var newInstrument = new DashboardInstrument
            {
                Symbol = scripInfo.TradingSymbol,
                DisplayName = scripInfo.SemInstrumentName,
                SecurityId = scripInfo.SecurityId,
                SegmentId = _scripMasterService.GetSegmentIdFromName(scripInfo.Segment),
                ExchId = scripInfo.ExchId,
                FeedType = FeedTypeQuote, // Always subscribe to quote for full data
                UnderlyingSymbol = scripInfo.UnderlyingSymbol,
                IsFuture = scripInfo.InstrumentType.StartsWith("FUT")
            };

            Application.Current.Dispatcher.Invoke(() =>
            {
                Dashboard.MonitoredInstruments.Add(newInstrument);
            });

            var subscription = new Dictionary<string, int> { { newInstrument.SecurityId, newInstrument.SegmentId } };
            await _webSocketClient.SubscribeToInstrumentsAsync(subscription, 17); // 17 for Quote

            await UpdateStatusAsync($"{newInstrument.DisplayName} added to dashboard and subscribed.");
        }

        private void OnAnalysisResultUpdated(AnalysisResult result)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
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
            try
            {
                foreach (var pos in selectedPositions)
                {
                    var orderRequest = new OrderRequest
                    {
                        DhanClientId = _dhanClientId,
                        TransactionType = pos.Quantity > 0 ? TransactionTypeSell : TransactionTypeBuy,
                        ExchangeSegment = "NSE_FNO",
                        ProductType = pos.ProductType,
                        OrderType = OrderTypeMarket,
                        SecurityId = pos.SecurityId,
                        Quantity = Math.Abs(pos.Quantity),
                        Validity = ValidityDay
                    };
                    await _apiClient.PlaceOrderAsync(orderRequest);
                }
                await Task.Delay(1500);
                await LoadPortfolioAsync();
            }
            catch (DhanApiException ex)
            {
                await UpdateStatusAsync($"Failed to close positions: {ex.Message}");
            }
        }

        private async Task ExecuteConvertPositionAsync(object? parameter)
        {
            if (parameter is Position position)
            {
                var newProductType = position.ProductType == ProductTypeIntraday ? ProductTypeMargin : ProductTypeIntraday;
                var convertRequest = new ConvertPositionRequest
                {
                    DhanClientId = _dhanClientId,
                    SecurityId = position.SecurityId,
                    ProductType = position.ProductType,
                    ConvertTo = newProductType,
                    Quantity = Math.Abs(position.Quantity)
                };
                try
                {
                    var success = await _apiClient.ConvertPositionAsync(convertRequest);
                    if (success)
                    {
                        await UpdateStatusAsync("Position conversion successful.");
                        await LoadPortfolioAsync();
                    }
                    else
                    {
                        await UpdateStatusAsync("Position conversion failed for an unknown reason.");
                    }
                }
                catch (DhanApiException ex)
                {
                    await UpdateStatusAsync($"Position conversion failed: {ex.Message}");
                }
            }
        }

        private void ExecuteModifyOrder(object? parameter)
        {
            if (parameter is not OrderBookEntry order) return;

            var dashboardInstrument = Dashboard.MonitoredInstruments.FirstOrDefault(i => i.SecurityId == order.SecurityId);
            var previousClose = dashboardInstrument?.Close ?? order.Price;
            var freezeLimit = GetFreezeLimitForInstrument(order.TradingSymbol);

            var orderViewModel = new OrderEntryViewModel(
                order.SecurityId,
                order.TradingSymbol,
                order.ExchangeSegment,
                order.TransactionType == TransactionTypeBuy,
                order.Price,
                previousClose,
                _scripMasterService.GetLotSizeForSecurity(order.SecurityId),
                order.ProductType,
                _apiClient,
                _webSocketClient,
                _dhanClientId,
                _scripMasterService,
                freezeLimit,
                existingOrder: order
            );

            var orderWindow = new OrderEntryWindow { DataContext = orderViewModel, Title = "Modify Order" };
            orderWindow.Owner = Application.Current.MainWindow;
            orderWindow.ShowDialog();
            Task.Run(LoadOrdersAsync);
        }

        private async Task ExecuteCancelOrderAsync(object? parameter)
        {
            if (parameter is OrderBookEntry order)
            {
                if (MessageBox.Show($"Are you sure you want to cancel order {order.OrderId}?", "Confirm Cancellation", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    await UpdateStatusAsync($"Cancelling order {order.OrderId}...");
                    try
                    {
                        var response = await _apiClient.CancelOrderAsync(order.OrderId);
                        if (response != null)
                        {
                            await UpdateStatusAsync($"Order {order.OrderId} cancellation processed.");
                        }
                    }
                    catch (DhanApiException ex)
                    {
                        await UpdateStatusAsync($"Failed to cancel order {order.OrderId}: {ex.Message}");
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
            string exchangeSegment = SelectedIndex?.ExchId == "BSE" ? "BSE_FNO" : "NSE_FNO";
            var freezeLimit = GetFreezeLimitForInstrument(SelectedIndex?.Symbol ?? "");

            var orderViewModel = new OrderEntryViewModel(
                option.SecurityId,
                instrumentName,
                exchangeSegment,
                isBuy,
                option.LTP,
                option.PreviousClose,
                _scripMasterService.GetLotSizeForSecurity(option.SecurityId),
                ProductTypeIntraday,
                _apiClient,
                _webSocketClient,
                _dhanClientId,
                _scripMasterService,
                freezeLimit
            );

            var orderWindow = new OrderEntryWindow { DataContext = orderViewModel, Owner = Application.Current.MainWindow };
            orderWindow.ShowDialog();
            Task.Run(LoadPortfolioAsync);
        }

        private void OpenOrderWindowForPosition(Position position, bool isBuy)
        {
            var dashboardInstrument = Dashboard.MonitoredInstruments.FirstOrDefault(i => i.SecurityId == position.SecurityId);
            var previousClose = dashboardInstrument?.Close ?? position.LastTradedPrice;
            var exchangeSegment = dashboardInstrument?.SegmentId == 1 ? "NSE_EQ" : "NSE_FNO";
            var freezeLimit = GetFreezeLimitForInstrument(position.Ticker);

            var orderViewModel = new OrderEntryViewModel(
                position.SecurityId,
                position.Ticker,
                exchangeSegment,
                isBuy,
                position.LastTradedPrice,
                previousClose,
                _scripMasterService.GetLotSizeForSecurity(position.SecurityId),
                position.ProductType,
                _apiClient,
                _webSocketClient,
                _dhanClientId,
                _scripMasterService,
                freezeLimit
            );

            var orderWindow = new OrderEntryWindow { DataContext = orderViewModel, Owner = Application.Current.MainWindow };
            orderWindow.ShowDialog();
            Task.Run(LoadPortfolioAsync);
        }

        private int GetFreezeLimitForInstrument(string instrumentName)
        {
            if (string.IsNullOrEmpty(instrumentName)) return Settings.NiftyFreezeQuantity;

            if (instrumentName.Contains("BANKNIFTY")) return Settings.BankNiftyFreezeQuantity;
            if (instrumentName.Contains("FINNIFTY")) return Settings.FinNiftyFreezeQuantity;
            if (instrumentName.Contains("SENSEX")) return Settings.SensexFreezeQuantity;
            if (instrumentName.Contains("NIFTY")) return Settings.NiftyFreezeQuantity;

            return Settings.NiftyFreezeQuantity;
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
                MessageBox.Show($"A critical error occurred on startup: {ex.Message}\nThe application might not function correctly.", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PopulateIndices()
        {
            var symbolsToLoad = new Dictionary<string, string>
            {
                { "Nifty 50", "Nifty 50" },
                { "Nifty Bank", "Nifty Bank" },
                { "Sensex", "Sensex" }
            };

            foreach (var pair in symbolsToLoad)
            {
                ScripInfo? indexScripInfo = _scripMasterService.FindIndexScripInfo(pair.Value);

                if (indexScripInfo != null && !string.IsNullOrEmpty(indexScripInfo.SecurityId))
                {
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Indices.Add(new TickerIndex
                        {
                            Name = pair.Key,
                            Symbol = pair.Key,
                            ScripId = indexScripInfo.SecurityId,
                            Segment = indexScripInfo.Segment,
                            ExchId = indexScripInfo.ExchId
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
            try
            {
                await UpdateStatusAsync("Initializing Dashboard and Subscribing...");

                await InitializeDashboardAsync();
                await PreloadNearestExpiriesAsync();
                await UpdateSubscriptionsAsync();
                await _webSocketClient.SubscribeToOrderFeedAsync();
                await LoadPortfolioAsync();
                await LoadOrdersAsync();

                _optionChainRefreshTimer = new Timer(async _ => await RefreshOptionChainDataAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15));

                Application.Current.Dispatcher.InvokeAsync(() => { SelectedIndex = Indices.FirstOrDefault(i => i.Name == "Nifty 50"); });
            }
            catch (DhanApiException ex)
            {
                await UpdateStatusAsync($"API Error during initial load: {ex.Message}");
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"An unexpected error occurred during initial load: {ex.Message}");
            }
        }

        private async Task InitializeDashboardAsync()
        {
            await UpdateStatusAsync("Configuring Dashboard...");

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Debug.WriteLine("[Dashboard] Clearing existing instruments...");
                Dashboard.MonitoredInstruments.Clear();
            });

            var staticEquities = new[] { "HDFCBANK", "ICICIBANK", "RELIANCE INDUSTRIES", "INFOSYS" };
            var indexSymbols = new[] { "NIFTY 50", "NIFTY BANK", "SENSEX" };
            var futureUnderlyings = new[] { "NIFTY", "BANKNIFTY", "HDFCBANK", "ICICIBANK", "RELIANCE", "INFY" };

            var newInstruments = new List<DashboardInstrument>();

            Debug.WriteLine("[Dashboard] Adding indices...");
            foreach (var index in indexSymbols)
            {
                var info = _scripMasterService.FindIndexScripInfo(index);
                if (info != null)
                {
                    newInstruments.Add(new DashboardInstrument
                    {
                        Symbol = info.SemInstrumentName,
                        DisplayName = info.SemInstrumentName,
                        SecurityId = info.SecurityId,
                        SegmentId = _scripMasterService.GetSegmentIdFromName(info.Segment),
                        ExchId = info.ExchId,
                        FeedType = FeedTypeQuote,
                        UnderlyingSymbol = GetUnderlyingSymbolForScripMaster(info.SemInstrumentName)
                    });
                }
            }

            Debug.WriteLine("[Dashboard] Adding equities...");
            foreach (var eq in staticEquities)
            {
                var info = _scripMasterService.FindEquityScripInfo(eq);
                if (info != null)
                {
                    newInstruments.Add(new DashboardInstrument
                    {
                        Symbol = info.TradingSymbol,
                        DisplayName = info.SemInstrumentName,
                        SecurityId = info.SecurityId,
                        SegmentId = _scripMasterService.GetSegmentIdFromName(info.Segment),
                        FeedType = FeedTypeQuote,
                        UnderlyingSymbol = info.UnderlyingSymbol
                    });
                }
            }

            Debug.WriteLine("[Dashboard] Adding futures...");
            foreach (var symbol in futureUnderlyings)
            {
                var fut = _scripMasterService.FindNearMonthFutureSecurityId(symbol);
                if (fut != null)
                {
                    newInstruments.Add(new DashboardInstrument
                    {
                        Symbol = fut.SemInstrumentName,
                        DisplayName = fut.SemInstrumentName,
                        SecurityId = fut.SecurityId,
                        SegmentId = _scripMasterService.GetSegmentIdFromName(fut.Segment),
                        FeedType = FeedTypeQuote,
                        IsFuture = true,
                        UnderlyingSymbol = symbol
                    });
                }
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var item in newInstruments)
                    Dashboard.MonitoredInstruments.Add(item);
            });

            await UpdateStatusAsync("Base dashboard loaded.");
            Debug.WriteLine("[Dashboard] Base Initialization complete.");
        }

        private async Task PreloadNearestExpiriesAsync()
        {
            await UpdateStatusAsync("Fetching option expiry dates...");
            foreach (var indexInfo in Indices)
            {
                var expiryListResponse = await _apiClient.GetExpiryListAsync(indexInfo.ScripId, MapSegmentForOptionChainApi(indexInfo.Segment));
                string? nearestExpiry = expiryListResponse?.ExpiryDates?.FirstOrDefault();
                if (!string.IsNullOrEmpty(nearestExpiry))
                {
                    _nearestExpiryDates[indexInfo.Symbol] = nearestExpiry;
                }
            }
        }

        private async Task UpdateSubscriptionsAsync()
        {
            var allInstruments = Dashboard.MonitoredInstruments
                .Where(i => !string.IsNullOrEmpty(i.SecurityId))
                .DistinctBy(i => i.SecurityId)
                .ToList();

            var quoteInstruments = allInstruments
                .Where(i => i.FeedType == FeedTypeQuote)
                .ToDictionary(i => i.SecurityId, i => i.SegmentId);

            var tickerInstruments = allInstruments
                .Where(i => i.FeedType == FeedTypeTicker)
                .ToDictionary(i => i.SecurityId, i => i.SegmentId);

            if (quoteInstruments.Any())
            {
                await _webSocketClient.SubscribeToInstrumentsAsync(quoteInstruments, 17);
            }

            if (tickerInstruments.Any())
            {
                await _webSocketClient.SubscribeToInstrumentsAsync(tickerInstruments, 15);
            }
        }

        private int GetSegmentIdFromName(string segmentName)
        {
            return segmentName switch
            {
                "NSE_EQ" => 1,
                "NSE_FNO" => 2,
                "BSE_EQ" => 3,
                "BSE_FNO" => 8,
                "IDX_I" => 0,
                "I" => 0,
                _ => -1
            };
        }

        private string MapSegmentForOptionChainApi(string segmentName)
        {
            return segmentName switch
            {
                "I" => "IDX_I",
                _ => segmentName
            };
        }


        private void OnLtpUpdateReceived(TickerPacket packet)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (SelectedIndex != null && packet.SecurityId == SelectedIndex.ScripId)
                {
                    UnderlyingPrice = packet.LastPrice;
                }
                Dashboard.UpdateLtp(packet);
            });
        }

        private void OnOrderUpdateReceived(OrderBookEntry updatedOrder)
        {
            Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                var orderToUpdate = Orders.FirstOrDefault(o => o.OrderId == updatedOrder.OrderId);
                if (orderToUpdate != null)
                {
                    orderToUpdate.OrderStatus = updatedOrder.OrderStatus;
                    orderToUpdate.FilledQuantity = updatedOrder.FilledQuantity;
                    orderToUpdate.UpdateTime = updatedOrder.UpdateTime;
                    orderToUpdate.AverageTradedPrice = updatedOrder.AverageTradedPrice;

                    await UpdateStatusAsync($"Order {updatedOrder.OrderId} updated: {updatedOrder.OrderStatus}");
                }
                else
                {
                    Orders.Insert(0, updatedOrder);
                    await UpdateStatusAsync($"New order received: {updatedOrder.OrderId}");
                }

                if (updatedOrder.OrderStatus == "TRADED")
                {
                    await LoadPortfolioAsync();
                }
            });
        }

        private void OnPreviousCloseUpdateReceived(PreviousClosePacket packet)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
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

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (SelectedIndex != null && packet.SecurityId == SelectedIndex.ScripId)
                {
                    UnderlyingPrice = packet.LastPrice;
                }

                if (_optionScripMap.TryGetValue(packet.SecurityId, out var optionDetails))
                {
                    optionDetails.LTP = packet.LastPrice;
                    optionDetails.Volume = packet.Volume;
                }

                Dashboard.UpdateQuote(packet);
            });

            var indexInstrument = Dashboard.MonitoredInstruments
                                    .FirstOrDefault(i => i.SecurityId == packet.SecurityId && i.SegmentId == 0);

            if (indexInstrument != null && !_dashboardOptionsLoadedFor.Contains(packet.SecurityId))
            {
                _dashboardOptionsLoadedFor.Add(packet.SecurityId);
                Task.Run(() => LoadDashboardOptionsForIndexAsync(indexInstrument, packet.LastPrice));
            }
        }

        private void OnOiUpdateReceived(OiPacket packet)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_optionScripMap.TryGetValue(packet.SecurityId, out var optionDetails))
                {
                    optionDetails.OI = packet.OpenInterest;
                }

                Dashboard.UpdateOi(packet);
            });
        }

        public async Task LoadPortfolioAsync()
        {
            await UpdateStatusAsync("Fetching portfolio...");
            try
            {
                var positionsFromApi = await _apiClient.GetPositionsAsync();
                var fundLimitFromApi = await _apiClient.GetFundLimitAsync();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var selectedIds = new HashSet<string>(OpenPositions.Where(p => p.IsSelected).Select(p => p.SecurityId));
                    OpenPositions.Clear();
                    ClosedPositions.Clear();

                    if (positionsFromApi != null)
                    {
                        foreach (var posData in positionsFromApi.Where(p => p.NetQuantity != 0))
                        {
                            var uiPosition = new Position
                            {
                                IsSelected = selectedIds.Contains(posData.SecurityId),
                                SecurityId = posData.SecurityId,
                                Ticker = posData.TradingSymbol,
                                Quantity = posData.NetQuantity,
                                AveragePrice = posData.BuyAverage,
                                LastTradedPrice = posData.LastTradedPrice,
                                RealizedPnl = posData.RealizedProfit,
                                ProductType = posData.ProductType,
                                SellAverage = posData.SellAverage,
                                BuyQuantity = posData.BuyQuantity,
                                SellQuantity = posData.SellQuantity
                            };
                            OpenPositions.Add(uiPosition);
                        }
                        foreach (var posData in positionsFromApi.Where(p => p.NetQuantity == 0))
                        {
                            var uiPosition = new Position
                            {
                                SecurityId = posData.SecurityId,
                                Ticker = posData.TradingSymbol,
                                Quantity = posData.NetQuantity,
                                AveragePrice = posData.BuyAverage,
                                LastTradedPrice = posData.LastTradedPrice,
                                RealizedPnl = posData.RealizedProfit,
                                ProductType = posData.ProductType,
                                SellAverage = posData.SellAverage,
                                BuyQuantity = posData.BuyQuantity,
                                SellQuantity = posData.SellQuantity
                            };
                            ClosedPositions.Add(uiPosition);
                        }
                    }

                    if (fundLimitFromApi != null)
                    {
                        FundDetails.AvailableBalance = fundLimitFromApi.AvailableBalance;
                        FundDetails.UtilizedMargin = fundLimitFromApi.UtilizedAmount;
                        FundDetails.Collateral = fundLimitFromApi.CollateralAmount;
                        FundDetails.WithdrawableBalance = fundLimitFromApi.WithdrawableBalance;
                    }
                    OnPropertyChanged(nameof(BookedPnl));
                    OnPropertyChanged(nameof(OpenPnl));
                    OnPropertyChanged(nameof(NetPnl));
                });
                await UpdateStatusAsync("Portfolio updated.");
            }
            catch (DhanApiException ex)
            {
                await UpdateStatusAsync($"API Error fetching portfolio: {ex.Message}");
            }
        }

        public async Task LoadOrdersAsync()
        {
            await UpdateStatusAsync("Fetching order book...");
            try
            {
                var orders = await _apiClient.GetOrderBookAsync();
                await Application.Current.Dispatcher.InvokeAsync(() => { Orders.Clear(); if (orders != null) foreach (var order in orders.OrderByDescending(o => DateTime.Parse(o.CreateTime))) Orders.Add(order); });
                await UpdateStatusAsync("Order book updated.");
            }
            catch (DhanApiException ex)
            {
                await UpdateStatusAsync($"API Error fetching orders: {ex.Message}");
            }
        }

        private async Task LoadExpiryAndOptionChainAsync()
        {
            if (_isDataLoading || SelectedIndex == null) return;

            try
            {
                _isDataLoading = true;
                _optionChainRefreshTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                string apiSecurityId = SelectedIndex.ScripId;
                string apiSegment = MapSegmentForOptionChainApi(SelectedIndex.Segment);

                var expiryListResponse = await _apiClient.GetExpiryListAsync(apiSecurityId, apiSegment);

                if (expiryListResponse?.ExpiryDates == null || !expiryListResponse.ExpiryDates.Any())
                {
                    await UpdateStatusAsync($"Could not get expiry dates for {SelectedIndex.Name}.");
                    await Application.Current.Dispatcher.InvokeAsync(() => { ExpiryDates.Clear(); OptionChainRows.Clear(); });
                    return;
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ExpiryDates.Clear();
                    foreach (var expiry in expiryListResponse.ExpiryDates.OrderBy(d => DateTime.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                    {
                        ExpiryDates.Add(expiry);
                    }
                    _selectedExpiry = ExpiryDates.FirstOrDefault();
                    OnPropertyChanged(nameof(SelectedExpiry));
                });

                _lastApiSecurityIdUsed = apiSecurityId;
                _lastApiSegmentUsed = apiSegment;

                if (!string.IsNullOrEmpty(_selectedExpiry))
                {
                    await LoadOptionChainOnlyAsync(apiSecurityId, apiSegment);
                }
            }
            catch (DhanApiException ex)
            {
                await UpdateStatusAsync($"API Error loading option chain: {ex.Message}");
            }
            finally
            {
                _isDataLoading = false;
                _optionChainRefreshTimer?.Change(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            }
        }

        private async Task LoadOptionChainOnlyAsync(string apiSecurityId, string apiSegment)
        {
            if (SelectedIndex == null || string.IsNullOrWhiteSpace(SelectedExpiry) || string.IsNullOrEmpty(apiSecurityId) || string.IsNullOrEmpty(apiSegment)) return;

            try
            {
                await UpdateStatusAsync($"Fetching option chain for {SelectedIndex.Name} - {SelectedExpiry}...");
                var optionChainResponse = await _apiClient.GetOptionChainAsync(apiSecurityId, apiSegment, SelectedExpiry);

                if (optionChainResponse?.Data?.OptionChain == null)
                {
                    await UpdateStatusAsync($"Failed to load option chain for {SelectedIndex.Name} - {SelectedExpiry}.");
                    return;
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    OptionChainRows.Clear();
                    _optionScripMap.Clear();
                    TotalCallOi = 0; TotalPutOi = 0; TotalCallVolume = 0; TotalPutVolume = 0;
                    PcrOi = 0; MaxOi = 0; MaxOiChange = 0;
                });

                var newSubscriptions = new Dictionary<string, int>();
                int optionSegmentId = (SelectedIndex.ExchId == "BSE") ? GetSegmentIdFromName("BSE_FNO") : GetSegmentIdFromName("NSE_FNO");

                UnderlyingPrice = optionChainResponse.Data.UnderlyingLastPrice;

                var allStrikes = optionChainResponse.Data.OptionChain
                    .Select(kvp => decimal.TryParse(kvp.Key, out var p) ? new { Price = p, Data = kvp.Value } : null)
                    .Where(s => s != null).OrderBy(s => s!.Price).ToList();

                if (!allStrikes.Any()) return;

                var atmStrikeData = allStrikes.OrderBy(s => Math.Abs(s!.Price - UnderlyingPrice)).First();
                var atmIndex = allStrikes.IndexOf(atmStrikeData!);

                int displayRange = 15;
                int startIndex = Math.Max(0, atmIndex - displayRange);
                int endIndex = Math.Min(allStrikes.Count - 1, atmIndex + displayRange);

                var strikesToDisplay = allStrikes.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();
                string scripMasterUnderlying = GetUnderlyingSymbolForScripMaster(SelectedIndex.Name);

                foreach (var strikeInfo in strikesToDisplay)
                {
                    if (strikeInfo?.Data == null) continue;

                    var (callOptionDetails, _) = MapToOptionDetails(strikeInfo.Data.CallOption, strikeInfo.Price, "CE", scripMasterUnderlying, SelectedExpiry);
                    var (putOptionDetails, _) = MapToOptionDetails(strikeInfo.Data.PutOption, strikeInfo.Price, "PE", scripMasterUnderlying, SelectedExpiry);

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (!string.IsNullOrEmpty(callOptionDetails.SecurityId))
                        {
                            _optionScripMap[callOptionDetails.SecurityId] = callOptionDetails;
                            newSubscriptions[callOptionDetails.SecurityId] = optionSegmentId;
                        }
                        if (!string.IsNullOrEmpty(putOptionDetails.SecurityId))
                        {
                            _optionScripMap[putOptionDetails.SecurityId] = putOptionDetails;
                            newSubscriptions[putOptionDetails.SecurityId] = optionSegmentId;
                        }

                        OptionChainRows.Add(new OptionChainRow
                        {
                            StrikePrice = strikeInfo.Price,
                            CallOption = callOptionDetails,
                            PutOption = putOptionDetails,
                            IsAtm = strikeInfo.Price == atmStrikeData.Price,
                            CallState = strikeInfo.Price < UnderlyingPrice ? OptionState.ITM : OptionState.OTM,
                            PutState = strikeInfo.Price > UnderlyingPrice ? OptionState.ITM : OptionState.OTM
                        });
                    });
                }

                await Application.Current.Dispatcher.InvokeAsync(CalculateOptionChainAggregates);

                if (newSubscriptions.Any())
                {
                    await _webSocketClient.SubscribeToInstrumentsAsync(newSubscriptions, 17);
                }
                await UpdateStatusAsync($"Option chain for {SelectedIndex.Name} - {SelectedExpiry} loaded.");
            }
            catch (DhanApiException ex)
            {
                await UpdateStatusAsync($"API Error loading option chain: {ex.Message}");
            }
        }

        private async Task LoadDashboardOptionsForIndexAsync(DashboardInstrument indexInstrument, decimal livePrice)
        {
            try
            {
                await UpdateStatusAsync($"Loading options for {indexInstrument.DisplayName}...");

                if (!_nearestExpiryDates.TryGetValue(indexInstrument.Symbol, out var nearestExpiry) || string.IsNullOrEmpty(nearestExpiry))
                {
                    Debug.WriteLine($"[DashboardOptions] No pre-loaded expiry found for {indexInstrument.Symbol}.");
                    return;
                }

                int step = GetStrikePriceStep(indexInstrument.UnderlyingSymbol);
                decimal atmStrike = Math.Round(livePrice / step) * step;

                var strikesToLoad = new List<decimal>();
                for (int i = -4; i <= 4; i++)
                {
                    strikesToLoad.Add(atmStrike + (i * step));
                }

                var newOptionInstruments = new List<DashboardInstrument>();
                var newSubscriptions = new Dictionary<string, int>();
                int optionSegmentId = (indexInstrument.ExchId == "BSE") ? GetSegmentIdFromName("BSE_FNO") : GetSegmentIdFromName("NSE_FNO");
                string scripMasterUnderlying = GetUnderlyingSymbolForScripMaster(indexInstrument.DisplayName);

                DateTime expiryDate = DateTime.ParseExact(nearestExpiry, "yyyy-MM-dd", CultureInfo.InvariantCulture);

                foreach (var strike in strikesToLoad)
                {
                    var ceInfo = _scripMasterService.FindOptionScripInfo(scripMasterUnderlying, expiryDate, strike, "CE");
                    if (ceInfo != null)
                    {
                        var inst = new DashboardInstrument
                        {
                            Symbol = ceInfo.SemInstrumentName,
                            DisplayName = ceInfo.SemInstrumentName,
                            SecurityId = ceInfo.SecurityId,
                            FeedType = FeedTypeQuote,
                            SegmentId = optionSegmentId,
                            UnderlyingSymbol = indexInstrument.Symbol
                        };
                        newOptionInstruments.Add(inst);
                        newSubscriptions[inst.SecurityId] = inst.SegmentId;
                    }

                    var peInfo = _scripMasterService.FindOptionScripInfo(scripMasterUnderlying, expiryDate, strike, "PE");
                    if (peInfo != null)
                    {
                        var inst = new DashboardInstrument
                        {
                            Symbol = peInfo.SemInstrumentName,
                            DisplayName = peInfo.SemInstrumentName,
                            SecurityId = peInfo.SecurityId,
                            FeedType = FeedTypeQuote,
                            SegmentId = optionSegmentId,
                            UnderlyingSymbol = indexInstrument.Symbol
                        };
                        newOptionInstruments.Add(inst);
                        newSubscriptions[inst.SecurityId] = inst.SegmentId;
                    }
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var inst in newOptionInstruments)
                    {
                        if (!Dashboard.MonitoredInstruments.Any(i => i.SecurityId == inst.SecurityId))
                        {
                            Dashboard.MonitoredInstruments.Add(inst);
                        }
                    }
                });

                if (newSubscriptions.Any())
                {
                    await _webSocketClient.SubscribeToInstrumentsAsync(newSubscriptions, 17);
                }
                await UpdateStatusAsync($"Options for {indexInstrument.DisplayName} loaded.");
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"Error loading dashboard options for {indexInstrument.DisplayName}: {ex.Message}");
                _dashboardOptionsLoadedFor.Remove(indexInstrument.SecurityId);
            }
        }

        private int GetStrikePriceStep(string underlyingSymbol)
        {
            if (underlyingSymbol.Contains("SENSEX") || underlyingSymbol.Contains("BANKNIFTY"))
            {
                return 100;
            }
            return 50;
        }


        private async Task RefreshOptionChainDataAsync()
        {
            if (SelectedIndex == null || string.IsNullOrWhiteSpace(SelectedExpiry)) return;

            try
            {
                string apiSecurityId = SelectedIndex.ScripId;
                string apiSegment = MapSegmentForOptionChainApi(SelectedIndex.Segment);

                var optionChainResponse = await _apiClient.GetOptionChainAsync(apiSecurityId, apiSegment, SelectedExpiry);
                if (optionChainResponse?.Data?.OptionChain != null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var spotIndexInstrument = Dashboard.MonitoredInstruments.FirstOrDefault(i => i.SecurityId == SelectedIndex.ScripId);
                        if (spotIndexInstrument != null)
                        {
                            UnderlyingPrice = spotIndexInstrument.LTP;
                            UnderlyingPreviousClose = spotIndexInstrument.Close;
                        }

                        var newChainData = optionChainResponse.Data.OptionChain;
                        var atmStrike = newChainData.Select(kvp => decimal.TryParse(kvp.Key, out var p) ? (decimal?)p : null)
                                                    .Where(p => p.HasValue)
                                                    .OrderBy(p => Math.Abs(p.Value - UnderlyingPrice))
                                                    .FirstOrDefault();

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

                                if (atmStrike.HasValue)
                                {
                                    rowToUpdate.IsAtm = rowToUpdate.StrikePrice == atmStrike.Value;
                                    rowToUpdate.CallState = rowToUpdate.StrikePrice < UnderlyingPrice ? OptionState.ITM : OptionState.OTM;
                                    rowToUpdate.PutState = rowToUpdate.StrikePrice > UnderlyingPrice ? OptionState.ITM : OptionState.OTM;
                                }
                            }
                        }

                        CalculateOptionChainAggregates();
                    });
                }
            }
            catch (DhanApiException ex)
            {
                Debug.WriteLine($"[OptionChainRefresh] API error during periodic refresh: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OptionChainRefresh] An unexpected error occurred during periodic refresh: {ex.Message}");
            }
        }

        private string GetUnderlyingSymbolForScripMaster(string displayName)
        {
            return displayName switch
            {
                "Nifty 50" => "NIFTY",
                "Nifty Bank" => "BANKNIFTY",
                "Sensex" => "SENSEX",
                _ => displayName
            };
        }

        private (OptionDetails, string) MapToOptionDetails(DhanApi.Models.OptionData? apiData, decimal strikePrice, string optionType, string underlyingSymbol, string expiryDateStr)
        {
            if (apiData == null || string.IsNullOrEmpty(underlyingSymbol) || string.IsNullOrEmpty(expiryDateStr))
            {
                return (new OptionDetails(), string.Empty);
            }

            DateTime.TryParseExact(expiryDateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiryDate);

            var scripInfo = _scripMasterService.FindOptionScripInfo(underlyingSymbol, expiryDate, strikePrice, optionType);

            var details = new OptionDetails
            {
                SecurityId = scripInfo?.SecurityId ?? string.Empty,
                LTP = apiData.LastPrice,
                PreviousClose = apiData.PreviousClose,
                IV = apiData.ImpliedVolatility,
                OI = apiData.OpenInterest,
                OiChange = apiData.OiChange,
                OiChangePercent = apiData.OiChangePercent,
                Volume = apiData.Volume,
                Delta = apiData.Greeks?.Delta ?? 0
            };

            return (details, scripInfo?.SemInstrumentName ?? string.Empty);
        }

        private void CalculateOptionChainAggregates()
        {
            if (!OptionChainRows.Any()) return;

            long runningCallOi = 0; long runningPutOi = 0;
            long runningCallVolume = 0; long runningPutVolume = 0;

            foreach (var row in OptionChainRows)
            {
                if (row.CallOption != null) { runningCallOi += (long)row.CallOption.OI; runningCallVolume += row.CallOption.Volume; }
                if (row.PutOption != null) { runningPutOi += (long)row.PutOption.OI; runningPutVolume += row.PutOption.Volume; }
            }

            TotalCallOi = runningCallOi; TotalPutOi = runningPutOi;
            TotalCallVolume = runningCallVolume; TotalPutVolume = runningPutVolume;
            PcrOi = (TotalCallOi > 0) ? (decimal)TotalPutOi / TotalCallOi : 0;

            long maxCallOi = OptionChainRows.Any(r => r.CallOption != null) ? OptionChainRows.Max(r => (long)(r.CallOption?.OI ?? 0)) : 0;
            long maxPutOi = OptionChainRows.Any(r => r.PutOption != null) ? OptionChainRows.Max(r => (long)(r.PutOption?.OI ?? 0)) : 0;
            MaxOi = Math.Max(maxCallOi, maxPutOi);

            decimal maxCallOiChange = OptionChainRows.Any(r => r.CallOption != null) ? OptionChainRows.Max(r => Math.Abs(r.CallOption?.OiChange ?? 0)) : 0;
            decimal maxPutOiChange = OptionChainRows.Any(r => r.PutOption != null) ? OptionChainRows.Max(r => Math.Abs(r.PutOption?.OiChange ?? 0)) : 0;
            MaxOiChange = Math.Max(maxCallOiChange, maxPutOiChange);
        }

        private Task UpdateStatusAsync(string message)
        {
            return Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusMessage = message;
                OnPropertyChanged(nameof(StatusMessage));
            }).Task;
        }
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

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

        // --- SOLUTION FOR ROBUSTNESS: Prevents rapid-click API spam ---
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
                    UpdateUnderlyingData(); // Update UI for the new index
                    Task.Run(() => LoadExpiryAndOptionChainAsync());
                }
            }
        }
        private string? _selectedExpiry;
        public string? SelectedExpiry { get => _selectedExpiry; set { if (_selectedExpiry != value) { _selectedExpiry = value; OnPropertyChanged(nameof(SelectedExpiry)); Task.Run(() => LoadOptionChainOnlyAsync()); } } }
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

            Indices = new ObservableCollection<TickerIndex>
            {
                new TickerIndex { Name = "NIFTY 50", ScripId = "13", Segment = "IDX_I", Symbol = "NIFTY" },
                new TickerIndex { Name = "NIFTY BANK", ScripId = "25", Segment = "IDX_I", Symbol = "BANKNIFTY" },
                new TickerIndex { Name = "SENSEX", ScripId = "51", Segment = "IDX_I", Symbol = "SENSEX" }
            };

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
                await _webSocketClient.ConnectAsync();
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"Fatal error during startup: {ex.Message}");
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

            var instrumentsToMonitor = new List<DashboardInstrument>
            {
                new() { Symbol = "NIFTY",     FeedType = "Ticker", SegmentId = 0 },
                new() { Symbol = "BANKNIFTY", FeedType = "Ticker", SegmentId = 0 },
                new() { Symbol = "SENSEX",    FeedType = "Quote",  SegmentId = 0 },
                new() { Symbol = "NIFTY-FUT",     IsFuture = true, UnderlyingSymbol = "NIFTY",     FeedType = "Quote", SegmentId = 2 },
                new() { Symbol = "BANKNIFTY-FUT", IsFuture = true, UnderlyingSymbol = "BANKNIFTY", FeedType = "Quote", SegmentId = 2 },
                new() { Symbol = "HDFCBANK",  FeedType = "Quote", SegmentId = 1 },
                new() { Symbol = "ICICIBANK", FeedType = "Quote", SegmentId = 1 },
                new() { Symbol = "RELIANCE",  FeedType = "Quote", SegmentId = 1 },
                new() { Symbol = "INFY",      FeedType = "Quote", SegmentId = 1 },
            };

            foreach (var inst in instrumentsToMonitor)
            {
                await App.Current.Dispatcher.InvokeAsync(() => Dashboard.MonitoredInstruments.Add(inst));
            }

            await ResolveInstrumentIdsAsync(Dashboard.MonitoredInstruments);

            foreach (var indexInfo in Indices)
            {
                await AddIndexOptionsToDashboardAsync(indexInfo);
                await Task.Delay(3500);
            }
        }

        private async Task ResolveInstrumentIdsAsync(IEnumerable<DashboardInstrument> instruments)
        {
            await UpdateStatusAsync("Resolving dynamic instruments...");
            foreach (var inst in instruments)
            {
                if (string.IsNullOrEmpty(inst.SecurityId))
                {
                    if (inst.IsFuture)
                    {
                        inst.SecurityId = _scripMasterService.FindNearMonthFutureSecurityId(inst.UnderlyingSymbol) ?? string.Empty;
                    }
                    else
                    {
                        inst.SecurityId = _scripMasterService.FindIndexSecurityId(inst.Symbol) ?? _scripMasterService.FindEquitySecurityId(inst.Symbol) ?? string.Empty;
                    }
                }
            }
        }

        private async Task AddIndexOptionsToDashboardAsync(TickerIndex indexInfo)
        {
            try
            {
                await UpdateStatusAsync($"Fetching options for {indexInfo.Name}...");

                var expiryListResponse = await _apiClient.GetExpiryListAsync(indexInfo.ScripId, indexInfo.Segment);
                if (expiryListResponse == null || expiryListResponse.ExpiryDates == null || !expiryListResponse.ExpiryDates.Any())
                {
                    await UpdateStatusAsync($"Could not get expiry dates for {indexInfo.Name}. API might be offline.");
                    Debug.WriteLine($"No expiry dates found for {indexInfo.Name}");
                    return;
                }

                var nearestExpiry = expiryListResponse.ExpiryDates.FirstOrDefault();
                if (string.IsNullOrEmpty(nearestExpiry)) return;

                await Task.Delay(3100);

                var optionChainResponse = await _apiClient.GetOptionChainAsync(indexInfo.ScripId, indexInfo.Segment, nearestExpiry);
                if (optionChainResponse?.Data?.OptionChain == null)
                {
                    await UpdateStatusAsync($"Failed to load option chain for {indexInfo.Name}.");
                    Debug.WriteLine($"Could not fetch valid option chain for {indexInfo.Name}");
                    return;
                }

                var underlyingPrice = optionChainResponse.Data.UnderlyingLastPrice;
                var allStrikes = optionChainResponse.Data.OptionChain
                    .Select(kvp => decimal.TryParse(kvp.Key, out var p) ? new { Price = p, Data = kvp.Value } : null)
                    .Where(s => s != null && s.Data.CallOption?.SecurityId != null && s.Data.PutOption?.SecurityId != null)
                    .OrderBy(s => s!.Price)
                    .ToList();

                if (!allStrikes.Any()) return;

                var atmStrikeData = allStrikes.OrderBy(s => Math.Abs(s!.Price - underlyingPrice)).First();
                var atmIndex = allStrikes.IndexOf(atmStrikeData!);

                int startIndex = Math.Max(0, atmIndex - 8);
                int endIndex = Math.Min(allStrikes.Count - 1, atmIndex + 8);

                for (int i = startIndex; i <= endIndex; i++)
                {
                    var strikeInfo = allStrikes[i]!;
                    await App.Current.Dispatcher.InvokeAsync(() => {
                        Dashboard.MonitoredInstruments.Add(new DashboardInstrument
                        {
                            Symbol = $"{indexInfo.Symbol} {strikeInfo.Price} CE",
                            SecurityId = strikeInfo.Data.CallOption!.SecurityId,
                            FeedType = "Quote",
                            SegmentId = 2,
                            UnderlyingSymbol = indexInfo.Symbol
                        });
                        Dashboard.MonitoredInstruments.Add(new DashboardInstrument
                        {
                            Symbol = $"{indexInfo.Symbol} {strikeInfo.Price} PE",
                            SecurityId = strikeInfo.Data.PutOption!.SecurityId,
                            FeedType = "Quote",
                            SegmentId = 2,
                            UnderlyingSymbol = indexInfo.Symbol
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"Error adding options for {indexInfo.Name}: {ex.Message}");
                Debug.WriteLine($"Error fetching options for {indexInfo.Name}: {ex}");
            }
        }

        private async Task UpdateSubscriptionsAsync()
        {
            var allInstruments = Dashboard.MonitoredInstruments
                .Where(i => !string.IsNullOrEmpty(i.SecurityId))
                .ToList();

            var quoteInstruments = allInstruments
                .Where(i => i.FeedType == "Quote" || i.FeedType == "Ticker")
                .ToDictionary(i => i.SecurityId, i => i.SegmentId);

            var tickerInstruments = allInstruments
                .Where(i => i.FeedType == "Ticker")
                .ToDictionary(i => i.SecurityId, i => i.SegmentId);

            if (quoteInstruments.Any())
            {
                await _webSocketClient.SubscribeToInstrumentsAsync(quoteInstruments, 17); // Quote
            }

            if (tickerInstruments.Any())
            {
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
                if (SelectedIndex != null && packet.SecurityId == SelectedIndex.ScripId)
                {
                    UnderlyingPrice = packet.LastPrice;
                }

                // --- SOLUTION FOR DATA BUG: Update the Option Chain Row ---
                if (_optionScripMap.TryGetValue(packet.SecurityId, out var optionDetails))
                {
                    optionDetails.LTP = packet.LastPrice;
                    optionDetails.Volume = packet.Volume;
                    // Note: QuotePacket doesn't contain OI, so that's handled in OnOiUpdateReceived
                }

                // Update the dashboard (this was already working correctly)
                Dashboard.UpdateQuote(packet);
            });
        }

        private void OnOiUpdateReceived(OiPacket packet)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                // --- SOLUTION FOR DATA BUG: Update the Option Chain Row ---
                if (_optionScripMap.TryGetValue(packet.SecurityId, out var optionDetails))
                {
                    optionDetails.OI = packet.OpenInterest;
                }

                // Update the dashboard
                Dashboard.UpdateOi(packet);
            });
        }


        private void UpdateUnderlyingData()
        {
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
            // --- SOLUTION FOR ROBUSTNESS: Check lock ---
            if (_isDataLoading) return;

            try
            {
                // --- SOLUTION FOR ROBUSTNESS: Set lock ---
                _isDataLoading = true;
                if (SelectedIndex == null) return;

                await UpdateStatusAsync($"Fetching expiry dates for {SelectedIndex.Name}...");
                var expiryListResponse = await _apiClient.GetExpiryListAsync(SelectedIndex.ScripId, SelectedIndex.Segment);

                if (expiryListResponse == null || expiryListResponse.ExpiryDates == null || !expiryListResponse.ExpiryDates.Any())
                {
                    await UpdateStatusAsync($"Could not get expiry dates for {SelectedIndex.Name}.");
                    return;
                }

                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    ExpiryDates.Clear();
                    expiryListResponse.ExpiryDates.ForEach(ExpiryDates.Add);
                    SelectedExpiry = ExpiryDates.FirstOrDefault();
                });

                if (string.IsNullOrEmpty(SelectedExpiry)) { await UpdateStatusAsync("No valid expiry dates found."); App.Current.Dispatcher.Invoke(OptionChainRows.Clear); }
            }
            catch (DhanApiException ex)
            {
                await UpdateStatusAsync(ex.Message);
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"An unexpected error occurred: {ex.Message}");
            }
            finally
            {
                // --- SOLUTION FOR ROBUSTNESS: Release lock ---
                _isDataLoading = false;
            }
        }

        private async Task LoadOptionChainOnlyAsync()
        {
            // --- SOLUTION FOR ROBUSTNESS: Check lock ---
            if (_isDataLoading) return;

            try
            {
                // --- SOLUTION FOR ROBUSTNESS: Set lock ---
                _isDataLoading = true;
                if (SelectedIndex == null || string.IsNullOrWhiteSpace(SelectedExpiry)) return;

                _optionChainRefreshTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                await UpdateStatusAsync($"Fetching option chain for {SelectedExpiry}...");

                var optionChainResponse = await _apiClient.GetOptionChainAsync(SelectedIndex.ScripId, SelectedIndex.Segment, SelectedExpiry);
                if (optionChainResponse?.Data?.OptionChain != null)
                {
                    await App.Current.Dispatcher.InvokeAsync(() =>
                    {
                        _optionScripMap.Clear();

                        var currentUnderlyingPrice = UnderlyingPrice;
                        var allStrikes = optionChainResponse.Data.OptionChain.Select(kvp => decimal.TryParse(kvp.Key, out var p) ? new { Price = p, Data = kvp.Value } : null).Where(s => s != null).OrderBy(s => s.Price).ToList();
                        if (!allStrikes.Any()) return;
                        var atmStrike = allStrikes.OrderBy(s => Math.Abs(s.Price - currentUnderlyingPrice)).FirstOrDefault();
                        if (atmStrike == null) return;

                        int atmIndex = allStrikes.IndexOf(atmStrike);
                        const int strikesToShow = 15;
                        int startIndex = Math.Max(0, atmIndex - strikesToShow);
                        int endIndex = Math.Min(allStrikes.Count - 1, atmIndex + strikesToShow);

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
                                CallState = strikeData.StrikePrice < currentUnderlyingPrice ? OptionState.ITM : OptionState.OTM,
                                PutState = strikeData.StrikePrice > currentUnderlyingPrice ? OptionState.ITM : OptionState.OTM,
                                CallOption = MapToOptionDetails(strikeData.CallOption),
                                PutOption = MapToOptionDetails(strikeData.PutOption)
                            };
                            OptionChainRows.Add(newRow);

                            if (newRow.CallOption != null && !string.IsNullOrEmpty(newRow.CallOption.SecurityId))
                            {
                                _optionScripMap[newRow.CallOption.SecurityId] = newRow.CallOption;
                            }
                            if (newRow.PutOption != null && !string.IsNullOrEmpty(newRow.PutOption.SecurityId))
                            {
                                _optionScripMap[newRow.PutOption.SecurityId] = newRow.PutOption;
                            }
                        }

                        CalculateOptionChainAggregates();
                        ManageOptionChainRefreshTimer();
                    });

                    var instrumentsToSubscribe = _optionScripMap.Keys.ToDictionary(id => id, id => 2);
                    if (instrumentsToSubscribe.Any())
                    {
                        await _webSocketClient.SubscribeToInstrumentsAsync(instrumentsToSubscribe, 17);
                    }
                }
                else
                {
                    await UpdateStatusAsync("Failed to load option chain data.");
                }
            }
            catch (DhanApiException ex)
            {
                await UpdateStatusAsync(ex.Message);
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"An unexpected error occurred: {ex.Message}");
            }
            finally
            {
                // --- SOLUTION FOR ROBUSTNESS: Release lock ---
                _isDataLoading = false;
            }
        }

        private async Task RefreshOptionChainDataAsync()
        {
            if (SelectedIndex == null || string.IsNullOrWhiteSpace(SelectedExpiry)) return;

            try
            {
                var optionChainResponse = await _apiClient.GetOptionChainAsync(SelectedIndex.ScripId, SelectedIndex.Segment, SelectedExpiry);
                if (optionChainResponse?.Data?.OptionChain != null)
                {
                    var newChainData = optionChainResponse.Data.OptionChain;

                    await App.Current.Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var rowToUpdate in OptionChainRows)
                        {
                            if (newChainData.TryGetValue(rowToUpdate.StrikePrice.ToString("F2"), out var strikeData) ||
                                newChainData.TryGetValue(rowToUpdate.StrikePrice.ToString("F0"), out strikeData))
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
                            }
                        }

                        CalculateOptionChainAggregates();
                    });
                }
            }
            catch (DhanApiException ex)
            {
                Debug.WriteLine($"Error during periodic refresh: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"An unexpected error occurred during refresh: {ex.Message}");
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
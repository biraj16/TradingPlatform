using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using TradingConsole.Core.Models;
using TradingConsole.DhanApi;
using TradingConsole.DhanApi.Models;
using TradingConsole.DhanApi.Models.WebSocket;

namespace TradingConsole.Wpf.ViewModels
{
    // MODIFIED: Implement IDisposable to handle cleanup
    public class OrderEntryViewModel : INotifyPropertyChanged, IDisposable
    {
        #region Private Fields
        private readonly DhanApiClient _apiClient;
        private readonly DhanWebSocketClient _webSocketClient; // ADDED
        private readonly ScripMasterService _scripMasterService;
        private readonly string _securityId;
        private readonly string _dhanClientId;
        private readonly int _lotSize;
        private readonly string _exchangeSegment;
        private readonly bool _isModification = false;
        private readonly string? _orderId;
        #endregion

        #region Public Properties
        public string InstrumentName { get; }
        public bool IsBuyOrder { get; }
        public string TransactionType => IsBuyOrder ? "BUY" : "SELL";
        public string WindowTitle => _isModification ? "Modify Order" : "Place Order";

        // ADDED: Properties for Live Price Display
        private decimal _liveLtp;
        public decimal LiveLtp { get => _liveLtp; set { _liveLtp = value; OnPropertyChanged(nameof(LiveLtp)); OnPropertyChanged(nameof(LiveLtpChange)); OnPropertyChanged(nameof(LiveLtpChangePercent)); } }
        public decimal PreviousClose { get; }
        public decimal LiveLtpChange => LiveLtp - PreviousClose;
        public decimal LiveLtpChangePercent => PreviousClose == 0 ? 0 : (LiveLtpChange / PreviousClose);


        private int _quantity = 1;
        public int Quantity { get => _quantity; set { _quantity = value; OnPropertyChanged(nameof(Quantity)); OnPropertyChanged(nameof(TotalQuantity)); } }
        public int TotalQuantity => Quantity * _lotSize;

        private decimal _price;
        public decimal Price { get => _price; set { _price = value; OnPropertyChanged(nameof(Price)); } }

        private decimal _triggerPrice;
        public decimal TriggerPrice { get => _triggerPrice; set { _triggerPrice = value; OnPropertyChanged(nameof(TriggerPrice)); } }

        private decimal _targetPrice;
        public decimal TargetPrice { get => _targetPrice; set { _targetPrice = value; OnPropertyChanged(nameof(TargetPrice)); } }

        private decimal _stopLossPrice;
        public decimal StopLossPrice { get => _stopLossPrice; set { _stopLossPrice = value; OnPropertyChanged(nameof(StopLossPrice)); } }

        private bool _isTrailingStopLossEnabled;
        public bool IsTrailingStopLossEnabled { get => _isTrailingStopLossEnabled; set { _isTrailingStopLossEnabled = value; OnPropertyChanged(nameof(IsTrailingStopLossEnabled)); } }

        private decimal _trailingStopLossValue = 1;
        public decimal TrailingStopLossValue { get => _trailingStopLossValue; set { _trailingStopLossValue = value; OnPropertyChanged(nameof(TrailingStopLossValue)); } }

        private int _sliceQuantity = 1;
        public int SliceQuantity { get => _sliceQuantity; set { _sliceQuantity = value; OnPropertyChanged(nameof(SliceQuantity)); } }

        private int _interval = 1;
        public int Interval { get => _interval; set { _interval = value; OnPropertyChanged(nameof(Interval)); } }

        public List<string> OrderTypes { get; } = new List<string> { "LIMIT", "MARKET", "STOP_LOSS", "BRACKET", "BRACKET_MARKET", "COVER", "SLICE" };
        private string _selectedOrderType = "LIMIT";
        public string SelectedOrderType
        {
            get => _selectedOrderType;
            set
            {
                if (_selectedOrderType != value)
                {
                    _selectedOrderType = value;
                    OnPropertyChanged(nameof(SelectedOrderType));
                    OnPropertyChanged(nameof(IsLimitPriceVisible));
                    OnPropertyChanged(nameof(IsTriggerPriceVisible));
                    OnPropertyChanged(nameof(IsBracketOrderVisible));
                    OnPropertyChanged(nameof(IsSliceOrderVisible));
                    OnPropertyChanged(nameof(IsProductTypeSelectionEnabled));
                }
            }
        }

        public bool IsLimitPriceVisible => _selectedOrderType == "LIMIT" || _selectedOrderType == "STOP_LOSS" || _selectedOrderType == "BRACKET" || _selectedOrderType == "SLICE";
        public bool IsTriggerPriceVisible => _selectedOrderType == "STOP_LOSS" || _selectedOrderType == "COVER";
        public bool IsBracketOrderVisible => _selectedOrderType == "BRACKET" || _selectedOrderType == "BRACKET_MARKET";
        public bool IsSliceOrderVisible => _selectedOrderType == "SLICE";
        public bool IsProductTypeSelectionEnabled => true;

        public List<string> ProductTypes { get; } = new List<string> { "INTRADAY", "MARGIN", "CNC" };
        private string _selectedProductType = "INTRADAY";
        public string SelectedProductType
        {
            get => _selectedProductType;
            set { _selectedProductType = value; OnPropertyChanged(nameof(SelectedProductType)); }
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(nameof(StatusMessage)); } }

        public ICommand PlaceOrderCommand { get; }
        #endregion

        #region Constructors
        // MODIFIED: Constructor now accepts WebSocket client and previous close price
        public OrderEntryViewModel(string securityId, string instrumentName, string exchangeSegment, bool isBuy, decimal initialPrice, decimal previousClose, int lotSize, string productType, DhanApiClient apiClient, DhanWebSocketClient webSocketClient, string dhanClientId, ScripMasterService scripMasterService, OrderBookEntry? existingOrder = null)
        {
            _apiClient = apiClient;
            _webSocketClient = webSocketClient;
            _dhanClientId = dhanClientId;
            _scripMasterService = scripMasterService;

            _securityId = securityId;
            InstrumentName = instrumentName;
            _exchangeSegment = exchangeSegment;
            IsBuyOrder = isBuy;
            Price = initialPrice;
            TriggerPrice = initialPrice;
            _lotSize = lotSize;
            SelectedProductType = productType;

            // Setup for live price
            LiveLtp = initialPrice;
            PreviousClose = previousClose;

            // Handle modification scenario
            if (existingOrder != null)
            {
                _isModification = true;
                _orderId = existingOrder.OrderId;
                SelectedOrderType = existingOrder.OrderType;
                if (_lotSize > 0) { Quantity = existingOrder.Quantity / _lotSize; }
            }

            PlaceOrderCommand = new RelayCommand(async (p) => await ExecutePlaceOrModifyOrder(), (p) => CanPlaceOrder());

            // Subscribe to live updates
            _webSocketClient.OnQuoteUpdate += OnQuoteUpdateReceived;
            var subscription = new Dictionary<string, int> { { _securityId, _scripMasterService.GetSegmentIdFromName(_exchangeSegment) } };
            Task.Run(() => _webSocketClient.SubscribeToInstrumentsAsync(subscription, 17));
        }
        #endregion

        #region Command Execution
        private bool CanPlaceOrder() => !string.IsNullOrEmpty(_securityId) && _lotSize > 0;

        private async Task ExecutePlaceOrModifyOrder()
        {
            if (_isModification)
            {
                if (IsBracketOrderVisible) await ExecuteModifySuperOrder();
                else await ExecuteModifyOrder();
            }
            else
            {
                if (IsBracketOrderVisible) await ExecutePlaceSuperOrder();
                else if (SelectedOrderType == "COVER") await ExecutePlaceCoverOrder();
                else if (SelectedOrderType == "SLICE") await ExecutePlaceSliceOrder();
                else await ExecutePlaceOrder();
            }
        }

        private async Task ExecutePlaceSliceOrder()
        {
            StatusMessage = "Placing Slice Order...";

            var sliceRequest = new SliceOrderRequest
            {
                DhanClientId = _dhanClientId,
                TransactionType = this.TransactionType,
                ExchangeSegment = _exchangeSegment,
                ProductType = this.SelectedProductType,
                OrderType = "LIMIT",
                SecurityId = this._securityId,
                TotalQuantity = this.TotalQuantity,
                SliceQuantity = this.SliceQuantity * this._lotSize,
                Interval = this.Interval,
                Price = this.Price
            };

            var response = await _apiClient.PlaceSliceOrderAsync(sliceRequest);
            if (response?.OrderId != null)
            {
                StatusMessage = $"Slice Order Placed Successfully! Main Order ID: {response.OrderId}";
                MessageBox.Show(StatusMessage, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                StatusMessage = "Failed to place Slice Order.";
                MessageBox.Show(StatusMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ExecutePlaceCoverOrder()
        {
            StatusMessage = "Placing Cover Order...";

            var orderRequest = new OrderRequest
            {
                DhanClientId = _dhanClientId,
                TransactionType = this.TransactionType,
                ExchangeSegment = _exchangeSegment,
                ProductType = "INTRADAY",
                OrderType = "CO",
                SecurityId = this._securityId,
                Quantity = this.TotalQuantity,
                Price = 0,
                TriggerPrice = this.TriggerPrice
            };

            var response = await _apiClient.PlaceOrderAsync(orderRequest);
            if (response?.OrderId != null)
            {
                StatusMessage = $"Cover Order Placed Successfully! ID: {response.OrderId}";
                MessageBox.Show(StatusMessage, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                StatusMessage = "Failed to place Cover Order.";
                MessageBox.Show(StatusMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ExecutePlaceSuperOrder()
        {
            decimal entryPrice = (SelectedOrderType == "BRACKET" && Price > 0) ? Price : 0;

            if (SelectedOrderType == "BRACKET" && entryPrice <= 0)
            {
                MessageBox.Show("Please enter a valid price greater than 0 for a LIMIT Bracket Order.", "Invalid Price", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StatusMessage = "Placing Super Order...";

            var orderRequest = new SuperOrderRequest
            {
                DhanClientId = _dhanClientId,
                TransactionType = this.TransactionType,
                ExchangeSegment = _exchangeSegment,
                ProductType = this.SelectedProductType,
                OrderType = SelectedOrderType == "BRACKET_MARKET" ? "MARKET" : "LIMIT",
                SecurityId = this._securityId,
                Quantity = this.TotalQuantity,
                Price = SelectedOrderType == "BRACKET_MARKET" ? 0 : this.Price,
                TargetPrice = this.TargetPrice,
                StopLossPrice = this.StopLossPrice,
                TrailingJump = IsTrailingStopLossEnabled ? (decimal?)TrailingStopLossValue : null
            };

            var response = await _apiClient.PlaceSuperOrderAsync(orderRequest);
            if (response?.OrderId != null)
            {
                StatusMessage = $"Super Order Placed Successfully! ID: {response.OrderId}";
                MessageBox.Show(StatusMessage, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                StatusMessage = "Failed to place Super Order.";
                MessageBox.Show(StatusMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ExecuteModifySuperOrder()
        {
            if (string.IsNullOrEmpty(_orderId)) return;

            StatusMessage = "Modifying Super Order...";
            var modifyRequest = new ModifySuperOrderRequest
            {
                DhanClientId = _dhanClientId,
                OrderId = _orderId,
                Quantity = this.TotalQuantity,
                Price = this.Price,
                TargetPrice = this.TargetPrice,
                StopLossPrice = this.StopLossPrice,
                TrailingJump = IsTrailingStopLossEnabled ? (decimal?)TrailingStopLossValue : null,
            };
            var response = await _apiClient.ModifySuperOrderAsync(modifyRequest);
            if (response?.OrderId != null)
            {
                StatusMessage = $"Super Order Modified Successfully! ID: {response.OrderId}";
                MessageBox.Show(StatusMessage, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                StatusMessage = "Failed to modify Super Order.";
                MessageBox.Show(StatusMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ExecutePlaceOrder()
        {
            StatusMessage = "Placing order...";
            var orderRequest = new OrderRequest
            {
                DhanClientId = _dhanClientId,
                TransactionType = this.TransactionType,
                ExchangeSegment = _exchangeSegment,
                ProductType = this.SelectedProductType,
                OrderType = this.SelectedOrderType,
                SecurityId = this._securityId,
                Quantity = this.TotalQuantity,
                Price = (SelectedOrderType == "LIMIT" || SelectedOrderType == "STOP_LOSS") ? this.Price : 0,
                TriggerPrice = (SelectedOrderType == "STOP_LOSS") ? this.TriggerPrice : 0
            };
            var response = await _apiClient.PlaceOrderAsync(orderRequest);
            if (response?.OrderId != null)
            {
                StatusMessage = $"Order Placed Successfully! ID: {response.OrderId}";
                MessageBox.Show(StatusMessage, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                StatusMessage = "Failed to place order.";
                MessageBox.Show(StatusMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ExecuteModifyOrder()
        {
            if (string.IsNullOrEmpty(_orderId)) return;
            StatusMessage = "Modifying order...";
            var modifyRequest = new ModifyOrderRequest
            {
                DhanClientId = _dhanClientId,
                OrderId = _orderId,
                OrderType = this.SelectedOrderType,
                Quantity = this.TotalQuantity,
                Price = this.Price,
                TriggerPrice = this.TriggerPrice,
                Validity = "DAY"
            };
            var response = await _apiClient.ModifyOrderAsync(modifyRequest);
            if (response?.OrderId != null)
            {
                StatusMessage = $"Order Modified Successfully! ID: {response.OrderId}";
                MessageBox.Show(StatusMessage, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                StatusMessage = "Failed to modify order.";
                MessageBox.Show(StatusMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion

        // ADDED: Handler for live quote updates
        private void OnQuoteUpdateReceived(QuotePacket packet)
        {
            if (packet.SecurityId == _securityId)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    LiveLtp = packet.LastPrice;
                    OnPropertyChanged(nameof(LiveLtp));
                    OnPropertyChanged(nameof(LiveLtpChange));
                    OnPropertyChanged(nameof(LiveLtpChangePercent));
                });
            }
        }

        #region IDisposable Implementation
        public void Dispose()
        {
            // Unsubscribe from the event to prevent memory leaks
            if (_webSocketClient != null)
            {
                _webSocketClient.OnQuoteUpdate -= OnQuoteUpdateReceived;
            }
        }
        #endregion

        #region INotifyPropertyChanged Implementation
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        #endregion
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using TradingConsole.Core.Models;
using TradingConsole.DhanApi;
using TradingConsole.DhanApi.Models;

namespace TradingConsole.Wpf.ViewModels
{
    public class OrderEntryViewModel : INotifyPropertyChanged
    {
        #region Private Fields
        private readonly DhanApiClient _apiClient;
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

        public List<string> OrderTypes { get; } = new List<string> { "LIMIT", "MARKET", "STOP_LOSS", "BRACKET", "BRACKET_MARKET" };
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
                    OnPropertyChanged(nameof(IsProductTypeSelectionEnabled));
                }
            }
        }

        public bool IsLimitPriceVisible => _selectedOrderType == "LIMIT" || _selectedOrderType == "STOP_LOSS" || _selectedOrderType == "BRACKET";
        public bool IsTriggerPriceVisible => _selectedOrderType == "STOP_LOSS";
        public bool IsBracketOrderVisible => _selectedOrderType == "BRACKET" || _selectedOrderType == "BRACKET_MARKET";
        public bool IsProductTypeSelectionEnabled => true; // Always allow selection now

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
        public OrderEntryViewModel(OptionDetails option, bool isBuy, string instrumentName, DhanApiClient apiClient, string dhanClientId, ScripMasterService scripMasterService, string exchangeSegment)
        {
            _apiClient = apiClient;
            _securityId = option.SecurityId;
            _dhanClientId = dhanClientId;
            _scripMasterService = scripMasterService;
            _exchangeSegment = exchangeSegment;
            InstrumentName = instrumentName;
            IsBuyOrder = isBuy;
            Price = option.LTP;
            TriggerPrice = option.LTP;
            _lotSize = _scripMasterService.GetLotSizeForSecurity(_securityId);
            PlaceOrderCommand = new RelayCommand(async (p) => await ExecutePlaceOrModifyOrder(), (p) => CanPlaceOrder());
        }

        public OrderEntryViewModel(Position position, bool isBuy, DhanApiClient apiClient, string dhanClientId, ScripMasterService scripMasterService, string exchangeSegment)
        {
            _apiClient = apiClient;
            _securityId = position.SecurityId;
            _dhanClientId = dhanClientId;
            _scripMasterService = scripMasterService;
            _exchangeSegment = exchangeSegment;
            InstrumentName = position.Ticker;
            IsBuyOrder = isBuy;
            Price = position.LastTradedPrice;
            TriggerPrice = position.LastTradedPrice;
            SelectedProductType = position.ProductType;
            _lotSize = _scripMasterService.GetLotSizeForSecurity(_securityId);
            if (!isBuy && _lotSize > 0) { Quantity = Math.Abs(position.Quantity) / _lotSize; }
            PlaceOrderCommand = new RelayCommand(async (p) => await ExecutePlaceOrModifyOrder(), (p) => CanPlaceOrder());
        }

        public OrderEntryViewModel(OrderBookEntry order, DhanApiClient apiClient, string dhanClientId, ScripMasterService scripMasterService)
        {
            _apiClient = apiClient;
            _dhanClientId = dhanClientId;
            _scripMasterService = scripMasterService;
            _isModification = true;
            _orderId = order.OrderId;
            _securityId = order.SecurityId;
            _exchangeSegment = order.ExchangeSegment;
            InstrumentName = order.TradingSymbol;
            IsBuyOrder = order.TransactionType == "BUY";
            Price = order.Price;
            TriggerPrice = order.TriggerPrice;
            SelectedOrderType = order.OrderType;
            SelectedProductType = order.ProductType;
            _lotSize = _scripMasterService.GetLotSizeForSecurity(_securityId);
            if (_lotSize > 0) { Quantity = order.Quantity / _lotSize; }
            PlaceOrderCommand = new RelayCommand(async (p) => await ExecutePlaceOrModifyOrder(), (p) => CanPlaceOrder());
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
                else await ExecutePlaceOrder();
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

        #region INotifyPropertyChanged Implementation
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        #endregion
    }
}
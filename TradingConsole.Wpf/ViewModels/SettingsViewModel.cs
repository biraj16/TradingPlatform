// In TradingConsole.Wpf/ViewModels/SettingsViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using TradingConsole.Core.Models;
using TradingConsole.Wpf.Services;

namespace TradingConsole.Wpf.ViewModels
{
    public class SettingsViewModel : INotifyPropertyChanged
    {
        private readonly SettingsService _settingsService;
        private AppSettings _settings;

        // Expose the list of monitored symbols for other ViewModels to read
        public ReadOnlyCollection<string> MonitoredSymbols => new ReadOnlyCollection<string>(_settings.MonitoredSymbols);

        private int _niftyFreezeQuantity;
        public int NiftyFreezeQuantity
        {
            get => _niftyFreezeQuantity;
            set { _niftyFreezeQuantity = value; OnPropertyChanged(); }
        }

        private int _bankNiftyFreezeQuantity;
        public int BankNiftyFreezeQuantity
        {
            get => _bankNiftyFreezeQuantity;
            set { _bankNiftyFreezeQuantity = value; OnPropertyChanged(); }
        }

        private int _finNiftyFreezeQuantity;
        public int FinNiftyFreezeQuantity
        {
            get => _finNiftyFreezeQuantity;
            set { _finNiftyFreezeQuantity = value; OnPropertyChanged(); }
        }

        private int _sensexFreezeQuantity;
        public int SensexFreezeQuantity
        {
            get => _sensexFreezeQuantity;
            set { _sensexFreezeQuantity = value; OnPropertyChanged(); }
        }

        // NEW: Short EMA Length property
        private int _shortEmaLength;
        public int ShortEmaLength
        {
            get => _shortEmaLength;
            set { if (_shortEmaLength != value) { _shortEmaLength = value; OnPropertyChanged(); } }
        }

        // NEW: Long EMA Length property
        private int _longEmaLength;
        public int LongEmaLength
        {
            get => _longEmaLength;
            set { if (_longEmaLength != value) { _longEmaLength = value; OnPropertyChanged(); } }
        }

        public ICommand SaveSettingsCommand { get; }

        // NEW: Event to notify subscribers when settings are saved
        public event EventHandler? SettingsSaved;

        public SettingsViewModel(SettingsService settingsService)
        {
            _settingsService = settingsService;
            _settings = _settingsService.LoadSettings();
            LoadSettingsIntoViewModel();

            SaveSettingsCommand = new RelayCommand(ExecuteSaveSettings);
        }

        private void LoadSettingsIntoViewModel()
        {
            NiftyFreezeQuantity = _settings.FreezeQuantities.GetValueOrDefault("NIFTY", 1800);
            BankNiftyFreezeQuantity = _settings.FreezeQuantities.GetValueOrDefault("BANKNIFTY", 900);
            FinNiftyFreezeQuantity = _settings.FreezeQuantities.GetValueOrDefault("FINNIFTY", 1800);
            SensexFreezeQuantity = _settings.FreezeQuantities.GetValueOrDefault("SENSEX", 1000);

            // NEW: Load EMA lengths
            ShortEmaLength = _settings.ShortEmaLength;
            LongEmaLength = _settings.LongEmaLength;
        }

        private void ExecuteSaveSettings(object? parameter)
        {
            // Update the settings object from the view model properties
            _settings.FreezeQuantities["NIFTY"] = NiftyFreezeQuantity;
            _settings.FreezeQuantities["BANKNIFTY"] = BankNiftyFreezeQuantity;
            _settings.FreezeQuantities["FINNIFTY"] = FinNiftyFreezeQuantity;
            _settings.FreezeQuantities["SENSEX"] = SensexFreezeQuantity;

            // NEW: Save EMA lengths
            _settings.ShortEmaLength = ShortEmaLength;
            _settings.LongEmaLength = LongEmaLength;

            // Save the updated settings to the file
            _settingsService.SaveSettings(_settings);

            MessageBox.Show("Settings saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

            // NEW: Raise the SettingsSaved event
            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Adds a symbol to the monitored list and saves the settings.
        /// </summary>
        public void AddMonitoredSymbol(string symbol)
        {
            if (!_settings.MonitoredSymbols.Any(s => s.Equals(symbol, StringComparison.OrdinalIgnoreCase)))
            {
                _settings.MonitoredSymbols.Add(symbol);
                _settingsService.SaveSettings(_settings);
                OnPropertyChanged(nameof(MonitoredSymbols));
                SettingsSaved?.Invoke(this, EventArgs.Empty); // Also notify on symbol change
            }
        }

        /// <summary>
        /// Removes a symbol from the monitored list and saves the settings.
        /// </summary>
        public void RemoveMonitoredSymbol(string symbol)
        {
            var symbolToRemove = _settings.MonitoredSymbols.FirstOrDefault(s => s.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            if (symbolToRemove != null)
            {
                _settings.MonitoredSymbols.Remove(symbolToRemove);
                _settingsService.SaveSettings(_settings);
                OnPropertyChanged(nameof(MonitoredSymbols));
                SettingsSaved?.Invoke(this, EventArgs.Empty); // Also notify on symbol change
            }
        }

        public void ReplaceMonitoredSymbols(List<string> newSymbols)
        {
            _settings.MonitoredSymbols = newSymbols;
            _settingsService.SaveSettings(_settings);
            OnPropertyChanged(nameof(MonitoredSymbols));
            SettingsSaved?.Invoke(this, EventArgs.Empty); // Also notify on symbol change
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

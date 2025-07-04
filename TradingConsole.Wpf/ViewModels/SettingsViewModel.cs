﻿// In TradingConsole.Wpf/ViewModels/SettingsViewModel.cs
using System.ComponentModel;
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

        public ICommand SaveSettingsCommand { get; }

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
        }

        private void ExecuteSaveSettings(object? parameter)
        {
            // Update the settings object from the view model properties
            _settings.FreezeQuantities["NIFTY"] = NiftyFreezeQuantity;
            _settings.FreezeQuantities["BANKNIFTY"] = BankNiftyFreezeQuantity;
            _settings.FreezeQuantities["FINNIFTY"] = FinNiftyFreezeQuantity;
            _settings.FreezeQuantities["SENSEX"] = SensexFreezeQuantity;

            // Save the updated settings to the file
            _settingsService.SaveSettings(_settings);

            MessageBox.Show("Settings saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

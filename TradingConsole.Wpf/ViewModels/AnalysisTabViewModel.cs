using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using TradingConsole.Wpf.Services; // Ensure this is referenced for AnalysisResult

namespace TradingConsole.Wpf.ViewModels
{
    public class AnalysisTabViewModel : INotifyPropertyChanged
    {
        // Collection to hold the analysis results displayed in the tab
        public ObservableCollection<AnalysisResult> AnalysisResults { get; } = new ObservableCollection<AnalysisResult>();

        public AnalysisTabViewModel()
        {
            // Constructor, can be used for initial setup if needed.
        }

        /// <summary>
        /// Updates an existing analysis result or adds a new one to the collection.
        /// This ensures the UI always shows the latest analysis for each instrument.
        /// </summary>
        /// <param name="newResult">The new or updated analysis result packet.</param>
        public void UpdateAnalysisResult(AnalysisResult newResult)
        {
            // Find if an existing result for this security ID is already in the collection.
            var existingResult = AnalysisResults.FirstOrDefault(r => r.SecurityId == newResult.SecurityId);

            if (existingResult != null)
            {
                // If an existing result is found, update its properties.
                // Because AnalysisResult now inherits ObservableModel, property changes
                // within 'existingResult' will automatically notify the UI.
                existingResult.Vwap = newResult.Vwap;
                existingResult.Ema = newResult.Ema;
                existingResult.TradingSignal = newResult.TradingSignal;
                existingResult.CurrentIv = newResult.CurrentIv;
                existingResult.AvgIv = newResult.AvgIv;
                existingResult.IvSignal = newResult.IvSignal;
                existingResult.CurrentVolume = newResult.CurrentVolume;
                existingResult.AvgVolume = newResult.AvgVolume;
                existingResult.VolumeSignal = newResult.VolumeSignal;
                existingResult.Symbol = newResult.Symbol; // Ensure symbol is also updated
            }
            else
            {
                // If no existing result is found, add the new result to the collection.
                AnalysisResults.Add(newResult);
            }
        }

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion
    }
}

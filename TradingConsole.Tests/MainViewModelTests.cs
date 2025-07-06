// In TradingConsole.Tests/MainViewModelTests.cs

using Microsoft.VisualStudio.TestTools.UnitTesting;
using TradingConsole.Core.Models;
using TradingConsole.Wpf.ViewModels;

namespace TradingConsole.Tests
{
    [TestClass]
    public class MainViewModelTests
    {
        [TestMethod]
        public void PnlProperties_ShouldCalculateCorrectly_WithMultiplePositions()
        {
            // --- ARRANGE ---
            // Create the ViewModel. We still use dummy values for the constructor
            // as we are not testing API connectivity here.
            var viewModel = new MainViewModel("dummy_id", "dummy_token");

            // --- Corrected Setup for Open Positions ---
            // To test the calculated UnrealizedPnl, we now set the properties
            // that are used in its calculation.

            // Scenario 1: A winning open position with UnrealizedPnl of +150
            viewModel.OpenPositions.Add(new Position
            {
                Quantity = 10,
                AveragePrice = 100,
                LastTradedPrice = 115 // PnL = 10 * (115 - 100) = 150
            });

            // Scenario 2: A losing open position with UnrealizedPnl of -50
            viewModel.OpenPositions.Add(new Position
            {
                Quantity = 5,
                AveragePrice = 200,
                LastTradedPrice = 190 // PnL = 5 * (190 - 200) = -50
            });


            // The setup for closed positions was correct, as RealizedPnl is a settable property.
            viewModel.ClosedPositions.Add(new Position { RealizedPnl = 200m });
            viewModel.ClosedPositions.Add(new Position { RealizedPnl = -75m });


            // --- ACT ---
            // The "Act" is simply reading the values of the calculated properties in the ViewModel.
            decimal openPnl = viewModel.OpenPnl;
            decimal bookedPnl = viewModel.BookedPnl;
            decimal netPnl = viewModel.NetPnl;


            // --- ASSERT ---
            // Verify the final calculations based on our new arrangement.
            // Expected Open PnL: 150 + (-50) = 100
            // Expected Booked PnL: 200 + (-75) = 125
            // Expected Net PnL: 100 + 125 = 225
            Assert.AreEqual(100m, openPnl, "Open PnL was not calculated correctly.");
            Assert.AreEqual(125m, bookedPnl, "Booked PnL was not calculated correctly.");
            Assert.AreEqual(225m, netPnl, "Net PnL was not calculated correctly.");
        }
    }
}
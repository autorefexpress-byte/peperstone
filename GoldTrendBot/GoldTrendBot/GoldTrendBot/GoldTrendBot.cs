using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // ============================================================================
    // GoldTrendBot
    // ----------------------------------------------------------------------------
    // Strategy: Trend-following on XAU/USD using a fast/slow Moving Average
    // crossover, filtered by ATR (Average True Range) so trades are only taken
    // when volatility/trend strength is rising, not in a flat/choppy market.
    //
    // Risk management:
    //   - Position size is calculated from a fixed % of account balance risked
    //     per trade (not a fixed lot size).
    //   - Initial stop loss is placed at a multiple of ATR from entry.
    //   - An ATR-based trailing stop protects profits once the trade moves in
    //     our favour.
    //   - An optional fixed take-profit (also a multiple of ATR) can be set
    //     at entry time, in addition to the trailing stop.
    //
    // IMPORTANT: This is a starting point, NOT a guaranteed money-maker.
    // No strategy works 100% of the time. Always test on a DEMO account first,
    // run it through the Backtesting tab in cTrader, and only go live with
    // money you can afford to lose, starting small.
    // ============================================================================
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class GoldTrendBot : Robot
    {
        [Parameter("Fast MA Period", Group = "Trend", DefaultValue = 20, MinValue = 2)]
        public int FastMaPeriod { get; set; }

        [Parameter("Slow MA Period", Group = "Trend", DefaultValue = 50, MinValue = 5)]
        public int SlowMaPeriod { get; set; }

        [Parameter("MA Type", Group = "Trend", DefaultValue = MovingAverageType.Exponential)]
        public MovingAverageType MaType { get; set; }

        [Parameter("ATR Period", Group = "Volatility Filter", DefaultValue = 14, MinValue = 2)]
        public int AtrPeriod { get; set; }

        [Parameter("ATR Average Period", Group = "Volatility Filter", DefaultValue = 50, MinValue = 5)]
        public int AtrAveragePeriod { get; set; }

        [Parameter("ATR Filter Threshold (%)", Group = "Volatility Filter", DefaultValue = 80, MinValue = 0, MaxValue = 200,
            Description = "Current ATR must be at least this % of its own average ATR for a signal to be valid. Filters out flat/choppy conditions.")]
        public double AtrFilterThresholdPercent { get; set; }

        [Parameter("Stop Loss (x ATR)", Group = "Risk Management", DefaultValue = 2.0, MinValue = 0.5)]
        public double StopLossAtrMultiplier { get; set; }

        [Parameter("Trailing Stop (x ATR)", Group = "Risk Management", DefaultValue = 2.0, MinValue = 0.5)]
        public double TrailingStopAtrMultiplier { get; set; }

        [Parameter("Use Fixed Take Profit", Group = "Risk Management", DefaultValue = false,
            Description = "Optional fixed take-profit set at entry, in addition to the ATR trailing stop. Leave off to rely on the trailing stop / opposite signal only.")]
        public bool UseTakeProfit { get; set; }

        [Parameter("Take Profit (x ATR)", Group = "Risk Management", DefaultValue = 4.0, MinValue = 0.5)]
        public double TakeProfitAtrMultiplier { get; set; }

        [Parameter("Risk per Trade (%)", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 10)]
        public double RiskPercent { get; set; }

        [Parameter("Max Spread (pips)", Group = "Safety", DefaultValue = 50, MinValue = 0,
            Description = "Skip new entries if the current spread is wider than this, to avoid trading during illiquid/news spikes.")]
        public double MaxSpreadPips { get; set; }

        [Parameter("Label", Group = "Misc", DefaultValue = "GoldTrendBot")]
        public string Label { get; set; }

        private MovingAverage _fastMa;
        private MovingAverage _slowMa;
        private AverageTrueRange _atr;
        private MovingAverage _atrAverage;

        protected override void OnStart()
        {
            _fastMa = Indicators.MovingAverage(Bars.ClosePrices, FastMaPeriod, MaType);
            _slowMa = Indicators.MovingAverage(Bars.ClosePrices, SlowMaPeriod, MaType);
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);

            // Smoothed average of the ATR itself, used as the volatility filter baseline.
            _atrAverage = Indicators.MovingAverage(_atr.Result, AtrAveragePeriod, MovingAverageType.Simple);

            Print("GoldTrendBot started on {0} {1}", SymbolName, TimeFrame);
        }

        protected override void OnBarClosed()
        {
            ManageTrailingStop();

            // Need enough bars for the slower indicators to be meaningful.
            var minBars = Math.Max(SlowMaPeriod, AtrAveragePeriod) + 2;
            if (Bars.ClosePrices.Count < minBars)
                return;

            var existingPosition = Positions.Find(Label, SymbolName);

            var fastPrev = _fastMa.Result.Last(2);
            var slowPrev = _slowMa.Result.Last(2);
            var fastLast = _fastMa.Result.Last(1);
            var slowLast = _slowMa.Result.Last(1);

            var bullishCross = fastPrev <= slowPrev && fastLast > slowLast;
            var bearishCross = fastPrev >= slowPrev && fastLast < slowLast;

            var atrLast = _atr.Result.Last(1);
            var atrAvgLast = _atrAverage.Result.Last(1);
            var volatilityOk = atrAvgLast > 0 && atrLast >= atrAvgLast * (AtrFilterThresholdPercent / 100.0);

            // If we hold a position against the new signal, close it first.
            if (existingPosition != null)
            {
                var isLong = existingPosition.TradeType == TradeType.Buy;

                if ((isLong && bearishCross) || (!isLong && bullishCross))
                {
                    ClosePosition(existingPosition);
                    existingPosition = null;
                }
            }

            if (existingPosition != null)
                return; // already in a position aligned with (or unrelated to) the current signal

            if (!volatilityOk)
                return; // market too flat right now, skip this bar

            if (Symbol.Spread / Symbol.PipSize > MaxSpreadPips)
            {
                Print("Spread too wide ({0} pips), skipping entry.", Symbol.Spread / Symbol.PipSize);
                return;
            }

            if (bullishCross)
                TryEnter(TradeType.Buy, atrLast);
            else if (bearishCross)
                TryEnter(TradeType.Sell, atrLast);
        }

        private void TryEnter(TradeType tradeType, double atrValue)
        {
            var stopLossDistance = atrValue * StopLossAtrMultiplier;
            var stopLossPips = stopLossDistance / Symbol.PipSize;

            if (stopLossPips <= 0)
                return;

            var volume = CalculatePositionVolume(stopLossPips);

            if (volume <= 0)
            {
                Print("Calculated volume is 0, skipping entry (risk settings vs. account size).");
                return;
            }

            double? takeProfitPips = null;
            if (UseTakeProfit)
                takeProfitPips = (atrValue * TakeProfitAtrMultiplier) / Symbol.PipSize;

            var result = ExecuteMarketOrder(tradeType, SymbolName, volume, Label, stopLossPips, takeProfitPips);

            if (result.IsSuccessful)
                Print("{0} entry filled. Volume: {1}, SL distance: {2} pips (ATR-based), TP distance: {3} (ATR-based)",
                    tradeType, volume, Math.Round(stopLossPips, 1), takeProfitPips.HasValue ? Math.Round(takeProfitPips.Value, 1).ToString() : "none");
            else
                Print("Order failed: {0}", result.Error);
        }

        private double CalculatePositionVolume(double stopLossPips)
        {
            var riskAmount = Account.Balance * (RiskPercent / 100.0);

            // Symbol.PipValue = value of 1 pip for 1 unit of volume, in account currency.
            var rawVolume = riskAmount / (stopLossPips * Symbol.PipValue);

            var normalized = Symbol.NormalizeVolumeInUnits(rawVolume, RoundingMode.Down);

            if (normalized < Symbol.VolumeInUnitsMin)
                return 0;

            if (normalized > Symbol.VolumeInUnitsMax)
                normalized = Symbol.VolumeInUnitsMax;

            return normalized;
        }

        private void ManageTrailingStop()
        {
            var position = Positions.Find(Label, SymbolName);
            if (position == null)
                return;

            var atrLast = _atr.Result.Last(1);
            var trailDistance = atrLast * TrailingStopAtrMultiplier;

            if (position.TradeType == TradeType.Buy)
            {
                var newStop = Symbol.Bid - trailDistance;

                if (!position.StopLoss.HasValue || newStop > position.StopLoss.Value)
                    ModifyPosition(position, newStop, position.TakeProfit);
            }
            else
            {
                var newStop = Symbol.Ask + trailDistance;

                if (!position.StopLoss.HasValue || newStop < position.StopLoss.Value)
                    ModifyPosition(position, newStop, position.TakeProfit);
            }
        }
    }
}

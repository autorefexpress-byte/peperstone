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

        [Parameter("Allow Min Volume Fallback", Group = "Risk Management", DefaultValue = true,
            Description = "On a small account the risk-based volume can fall below the broker minimum (0.01 lot). If on, trade the minimum volume instead, but only when its real risk stays under 'Max Risk at Min Volume (%)'.")]
        public bool AllowMinVolumeFallback { get; set; }

        [Parameter("Max Risk at Min Volume (%)", Group = "Risk Management", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 10,
            Description = "Hard cap on the real % of balance risked when the minimum volume fallback is used. Entries whose stop would risk more are skipped.")]
        public double MaxRiskAtMinVolumePercent { get; set; }

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

            Positions.Closed += OnPositionClosed;

            Print("GoldTrendBot started on {0} {1}", SymbolName, TimeFrame);
            Print("Symbol info: PipSize {0}, PipValue {1}, TickSize {2}, TickValue {3}, pip value per unit used {4}, min volume {5} units.",
                Symbol.PipSize, Symbol.PipValue, Symbol.TickSize, Symbol.TickValue, PipValuePerUnit(), Symbol.VolumeInUnitsMin);
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

        // Logs each closed trade's result so a backtest log can be analysed
        // on its own, without the cTrader results tab.
        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;
            if (position.Label != Label || position.SymbolName != SymbolName)
                return;

            Print("Position closed ({0}, {1}). Net: {2:0.00} {3}. Balance: {4:0.00}.",
                position.TradeType, args.Reason, position.NetProfit, Account.Asset.Name, Account.Balance);
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

            var rawVolume = riskAmount / (stopLossPips * PipValuePerUnit());

            var normalized = Symbol.NormalizeVolumeInUnits(rawVolume, RoundingMode.Down);

            if (normalized < Symbol.VolumeInUnitsMin)
                return MinVolumeIfRiskAcceptable(stopLossPips);

            if (normalized > Symbol.VolumeInUnitsMax)
                normalized = Symbol.VolumeInUnitsMax;

            return normalized;
        }

        // Without this, a ~200 EUR account never trades gold: the risk-based
        // volume is always below 0.01 lot and the entry is skipped.
        private double MinVolumeIfRiskAcceptable(double stopLossPips)
        {
            if (!AllowMinVolumeFallback || Account.Balance <= 0)
                return 0;

            var minVolume = Symbol.VolumeInUnitsMin;
            var riskAtMinPercent = stopLossPips * PipValuePerUnit() * minVolume / Account.Balance * 100.0;

            if (riskAtMinPercent > MaxRiskAtMinVolumePercent)
            {
                Print("Min volume would risk {0:0.0}% (> {1:0.0}%), skipping entry.", riskAtMinPercent, MaxRiskAtMinVolumePercent);
                return 0;
            }

            Print("Risk-based volume below broker minimum: using min volume, real risk {0:0.0}%.", riskAtMinPercent);
            return minVolume;
        }

        // Value of one pip for one unit of volume, in account currency, derived
        // from the tick value. Symbol.PipValue gave a far too small value on
        // XAUUSD in backtest: with 1% risk on 200 EUR the bot still opened
        // trades losing 10-15% of the balance, and the risk cap never applied.
        private double PipValuePerUnit()
        {
            return Symbol.TickValue / Symbol.TickSize * Symbol.PipSize;
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

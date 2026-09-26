// Zone detection derived from "Liquidity Swings [LuxAlgo]" (c) LuxAlgo,
// licensed under CC BY-NC-SA 4.0 https://creativecommons.org/licenses/by-nc-sa/4.0/
// This cBot is shared under the same license: non-commercial use only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // ============================================================================
    // LiquiditySweepBot
    // ----------------------------------------------------------------------------
    // Trade les zones de liquidite de l'indicateur "Liquidity Swings [LuxAlgo]"
    // (voir LiquiditySwings/LiquiditySwings.cs) : au-dessus d'un swing high et
    // sous un swing low s'accumulent des stops. Quand une bougie va chercher ces
    // stops (meche au-dela du niveau) puis CLOTURE de nouveau a l'interieur, la
    // liquidite a ete prise sans cassure reelle -> entree en sens inverse.
    //
    //   - Sweep d'un swing high + cloture sous le niveau  -> VENTE
    //   - Sweep d'un swing low  + cloture au-dessus       -> ACHAT
    //   - Cloture au-dela du niveau (vraie cassure)       -> zone abandonnee
    //
    // Filtres : tendance HTF (EMA), creneau horaire, annonces US, spread, stop
    // max en ATR. Gestion du risque et sorties reprises d'IctSmcIchimokuBot.
    //
    // Differences avec l'indicateur :
    //   - Les retours dans la zone sont comptes des la bougie suivant le pivot
    //     (l'indicateur les compte avec un decalage de `length` bougies ; le
    //     total est le meme une fois le pivot confirme).
    //   - Volume = tick volume cTrader.
    //
    // IMPORTANT : strategie non validee. Backtest long puis demo avant le reel.
    // ============================================================================
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class LiquiditySweepBot : Robot
    {
        public enum SwingAreaMode
        {
            WickExtremity,
            FullRange
        }

        // -- Liquidity Swings --
        [Parameter("Pivot Lookback", Group = "Liquidity Swings", DefaultValue = 14, MinValue = 2, MaxValue = 100)]
        public int PivotLength { get; set; }

        [Parameter("Swing Area", Group = "Liquidity Swings", DefaultValue = SwingAreaMode.WickExtremity)]
        public SwingAreaMode Area { get; set; }

        [Parameter("Retours min dans la zone", Group = "Liquidity Swings", DefaultValue = 0, MinValue = 0, MaxValue = 50,
            Description = "Nombre minimum de bougies revenues dans la zone avant le sweep (equivalent du filtre 'Count' de l'indicateur). 0 = toutes les zones.")]
        public int MinTouches { get; set; }

        [Parameter("Age max d'une zone (bougies)", Group = "Liquidity Swings", DefaultValue = 1000, MinValue = 20, MaxValue = 10000,
            Description = "Zones plus anciennes oubliees. 1000 bougies = environ 3,5 jours en 5 min, 10 jours en 15 min.")]
        public int MaxZoneAgeBars { get; set; }

        [Parameter("Zones suivies par cote", Group = "Liquidity Swings", DefaultValue = 10, MinValue = 1, MaxValue = 50)]
        public int MaxZonesPerSide { get; set; }

        [Parameter("Logs detailles", Group = "Liquidity Swings", DefaultValue = true,
            Description = "Ecrit dans le log chaque franchissement de zone et la raison exacte quand aucun trade n'est pris.")]
        public bool VerboseLogs { get; set; }

        [Parameter("Afficher les zones", Group = "Liquidity Swings", DefaultValue = true)]
        public bool DrawZones { get; set; }

        // -- Filtre de tendance --
        [Parameter("Filtre de tendance", Group = "Tendance", DefaultValue = true,
            Description = "Vend les sweeps de swing high seulement en tendance baissiere (cloture HTF sous l'EMA), achete les sweeps de swing low seulement en tendance haussiere.")]
        public bool UseTrendFilter { get; set; }

        [Parameter("Timeframe tendance", Group = "Tendance", DefaultValue = "Hour4")]
        public TimeFrame TrendTimeFrame { get; set; }

        [Parameter("Periode EMA tendance", Group = "Tendance", DefaultValue = 50, MinValue = 5, MaxValue = 500)]
        public int TrendEmaPeriod { get; set; }

        // -- Stops --
        [Parameter("Periode ATR", Group = "Stops", DefaultValue = 14, MinValue = 2, MaxValue = 200)]
        public int AtrPeriod { get; set; }

        [Parameter("Buffer SL (x ATR)", Group = "Stops", DefaultValue = 0.2, MinValue = 0, MaxValue = 5, Step = 0.05,
            Description = "Marge au-dela de la meche du sweep pour le stop loss.")]
        public double StopLossBufferAtr { get; set; }

        [Parameter("SL minimum (pips)", Group = "Stops", DefaultValue = 8.0, MinValue = 0, Step = 0.5)]
        public double MinStopLossPips { get; set; }

        [Parameter("SL max (x ATR)", Group = "Stops", DefaultValue = 3.0, MinValue = 0, MaxValue = 50, Step = 0.5,
            Description = "Ignore le signal si le stop depasse ce multiple de l'ATR (meche de sweep demesuree). 0 = desactive.")]
        public double MaxStopLossAtr { get; set; }

        [Parameter("Risk:Reward (R)", Group = "Stops", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10, Step = 0.1)]
        public double RiskRewardRatio { get; set; }

        [Parameter("Break-even a 1R", Group = "Stops", DefaultValue = false)]
        public bool UseBreakEvenAt1R { get; set; }

        // -- Gestion du risque --
        [Parameter("Lot fixe (0 = calcul au risque)", Group = "Risk Management", DefaultValue = 0.0, MinValue = 0.0, Step = 0.01)]
        public double FixedLots { get; set; }

        [Parameter("Risque par trade (%)", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 10)]
        public double RiskPercent { get; set; }

        [Parameter("Autoriser volume minimum (petit compte)", Group = "Risk Management", DefaultValue = true)]
        public bool AllowMinVolumeFallback { get; set; }

        [Parameter("Risque max au volume minimum (%)", Group = "Risk Management", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 10)]
        public double MaxRiskAtMinVolumePercent { get; set; }

        [Parameter("Max pertes consecutives", Group = "Risk Management", DefaultValue = 3, MinValue = 1, MaxValue = 50)]
        public int MaxConsecutiveLosses { get; set; }

        [Parameter("Reprendre le lendemain apres la pause", Group = "Risk Management", DefaultValue = true,
            Description = "Remet le compteur de pertes consecutives a zero au changement de jour (UTC), au lieu de bloquer le bot jusqu'a un gain qui ne peut plus arriver.")]
        public bool ResumeNextDay { get; set; }

        [Parameter("Activer limite de perte journaliere", Group = "Risk Management", DefaultValue = true)]
        public bool UseDailyLossLimit { get; set; }

        [Parameter("Perte journaliere max (%)", Group = "Risk Management", DefaultValue = 5.0, MinValue = 0.5, MaxValue = 50.0, Step = 0.5)]
        public double MaxDailyLossPercent { get; set; }

        // -- Creneau d'entree --
        [Parameter("Debut des entrees (UTC)", Group = "Session", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int SessionStartHour { get; set; }

        [Parameter("Fin des entrees (UTC)", Group = "Session", DefaultValue = 17, MinValue = 1, MaxValue = 24)]
        public int SessionEndHour { get; set; }

        // -- Sorties par le temps --
        [Parameter("Fermer en fin de journee", Group = "Sortie", DefaultValue = true)]
        public bool UseDailyClose { get; set; }

        [Parameter("Heure de fermeture (UTC)", Group = "Sortie", DefaultValue = 21, MinValue = 1, MaxValue = 23)]
        public int DailyCloseHour { get; set; }

        [Parameter("Heure de fermeture vendredi (UTC)", Group = "Sortie", DefaultValue = 20, MinValue = 1, MaxValue = 23)]
        public int FridayCloseHour { get; set; }

        [Parameter("Derniere entree (heures avant fermeture)", Group = "Sortie", DefaultValue = 1, MinValue = 0, MaxValue = 12)]
        public int NoEntryHoursBeforeClose { get; set; }

        [Parameter("Duree max en position (heures)", Group = "Sortie", DefaultValue = 0, MinValue = 0, MaxValue = 500)]
        public int MaxHoursInTrade { get; set; }

        // -- Filtre news --
        [Parameter("Filtre news", Group = "News", DefaultValue = true)]
        public bool UseNewsFilter { get; set; }

        [Parameter("Heures news (heure de New York)", Group = "News", DefaultValue = "08:30")]
        public string NewsTimesNewYork { get; set; }

        [Parameter("Minutes avant l'annonce", Group = "News", DefaultValue = 15, MinValue = 0, MaxValue = 240)]
        public int NewsMinutesBefore { get; set; }

        [Parameter("Minutes apres l'annonce", Group = "News", DefaultValue = 15, MinValue = 0, MaxValue = 240)]
        public int NewsMinutesAfter { get; set; }

        // -- Securite --
        [Parameter("Max Spread (pips)", Group = "Safety", DefaultValue = 5, MinValue = 0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Label", Group = "Misc", DefaultValue = "LiqSweep")]
        public string Label { get; set; }

        private sealed class Zone
        {
            public int Id;
            public bool IsHigh;
            public double Top;
            public double Bottom;
            public int PivotIndex;
            public int Touches;
            public bool Active = true;

            // Niveau chasse : le haut de la zone pour un swing high, le bas pour un swing low.
            public double Level => IsHigh ? Top : Bottom;
        }

        private readonly List<Zone> _highZones = new List<Zone>();
        private readonly List<Zone> _lowZones = new List<Zone>();
        private readonly List<TimeSpan> _newsTimesNy = new List<TimeSpan>();
        private int _zoneCounter;

        private AverageTrueRange _atr;
        private Bars _trendBars;
        private ExponentialMovingAverage _trendEma;

        private bool _breakEvenDone;
        private int _consecutiveLosses;
        private DateTime _currentDay = DateTime.MinValue;
        private double _dayStartBalance;
        private bool _dailyLossLimitHit;

        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(Bars, AtrPeriod, MovingAverageType.Simple);

            if (UseTrendFilter)
            {
                _trendBars = MarketData.GetBars(TrendTimeFrame, SymbolName);
                _trendEma = Indicators.ExponentialMovingAverage(_trendBars.ClosePrices, TrendEmaPeriod);
            }

            ParseNewsTimes();
            Positions.Closed += OnPositionClosed;

            Print("LiquiditySweepBot started on {0} {1} (tendance: {2})", SymbolName, TimeFrame,
                UseTrendFilter ? TrendTimeFrame + " EMA " + TrendEmaPeriod : "desactivee");
            Print("Symbole: PipSize {0}, valeur pip/unite {1}, volume min {2} unites.",
                Symbol.PipSize, PipValuePerUnit(), Symbol.VolumeInUnitsMin);
        }

        protected override void OnTick()
        {
            ManageTimeExits();
            ManageBreakEven();
        }

        protected override void OnBarClosed()
        {
            var n = Bars.Count - 2;
            if (n < PivotLength * 2 + 1)
                return;

            var barTimeUtc = Bars.OpenTimes[n];
            UpdateDailyState(barTimeUtc);

            DetectNewZones(n);
            var signal = UpdateZonesAndFindSweep(n);

            if (signal == null)
                return;

            TryEnter(signal, n);
        }

        // ------------------------------------------------------------------
        // Zones de liquidite (logique Liquidity Swings)
        // ------------------------------------------------------------------

        private void DetectNewZones(int n)
        {
            var p = n - PivotLength;
            if (p - PivotLength < 0)
                return;

            var o = Bars.OpenPrices[p];
            var c = Bars.ClosePrices[p];

            if (IsPivotHigh(p))
            {
                var zone = new Zone
                {
                    Id = ++_zoneCounter,
                    IsHigh = true,
                    Top = Bars.HighPrices[p],
                    Bottom = Area == SwingAreaMode.WickExtremity ? Math.Max(c, o) : Bars.LowPrices[p],
                    PivotIndex = p
                };
                zone.Touches = CountTouches(zone, p + 1, n);
                AddZone(_highZones, zone, n);
            }

            if (IsPivotLow(p))
            {
                var zone = new Zone
                {
                    Id = ++_zoneCounter,
                    IsHigh = false,
                    Top = Area == SwingAreaMode.WickExtremity ? Math.Min(c, o) : Bars.HighPrices[p],
                    Bottom = Bars.LowPrices[p],
                    PivotIndex = p
                };
                zone.Touches = CountTouches(zone, p + 1, n);
                AddZone(_lowZones, zone, n);
            }
        }

        private int CountTouches(Zone zone, int from, int to)
        {
            var touches = 0;
            for (var i = from; i <= to; i++)
            {
                if (Bars.LowPrices[i] < zone.Top && Bars.HighPrices[i] > zone.Bottom)
                    touches++;
            }
            return touches;
        }

        private void AddZone(List<Zone> zones, Zone zone, int n)
        {
            // Un pivot deja casse pendant sa confirmation n'est pas une liquidite en attente.
            for (var i = zone.PivotIndex + 1; i <= n; i++)
            {
                var close = Bars.ClosePrices[i];
                if (zone.IsHigh ? close > zone.Top : close < zone.Bottom)
                    return;
            }

            zones.Add(zone);
            while (zones.Count > MaxZonesPerSide)
            {
                RemoveZoneDrawing(zones[0]);
                zones.RemoveAt(0);
            }
        }

        // Equivalent de ta.pivothigh(length, length).
        private bool IsPivotHigh(int p)
        {
            var value = Bars.HighPrices[p];
            for (var i = 1; i <= PivotLength; i++)
            {
                if (Bars.HighPrices[p - i] >= value || Bars.HighPrices[p + i] > value)
                    return false;
            }
            return true;
        }

        private bool IsPivotLow(int p)
        {
            var value = Bars.LowPrices[p];
            for (var i = 1; i <= PivotLength; i++)
            {
                if (Bars.LowPrices[p - i] <= value || Bars.LowPrices[p + i] < value)
                    return false;
            }
            return true;
        }

        private sealed class SweepSignal
        {
            public TradeType TradeType;
            public Zone Zone;
            public double SweepExtreme;
        }

        // Met a jour les zones avec la bougie cloturee `n` et renvoie un sweep
        // eventuel. Une zone ne peut etre chassee qu'une fois.
        private SweepSignal UpdateZonesAndFindSweep(int n)
        {
            var high = Bars.HighPrices[n];
            var low = Bars.LowPrices[n];
            var close = Bars.ClosePrices[n];
            SweepSignal signal = null;

            foreach (var zone in _highZones.Where(z => z.Active && z.PivotIndex + PivotLength < n))
            {
                if (n - zone.PivotIndex > MaxZoneAgeBars)
                {
                    Deactivate(zone, n, false);
                }
                else if (high > zone.Top && close < zone.Top)
                {
                    // Plusieurs niveaux chasses par la meme meche : on garde le plus haut.
                    if (zone.Touches < MinTouches)
                        Log("Sweep swing high {0} ignore : {1} retours < {2} requis.", zone.Top, zone.Touches, MinTouches);
                    else if (signal == null || zone.Top > signal.Zone.Top)
                        signal = new SweepSignal { TradeType = TradeType.Sell, Zone = zone, SweepExtreme = high };
                    Deactivate(zone, n, true);
                }
                else if (close > zone.Top)
                {
                    Log("Swing high {0} casse en cloture ({1}) : vraie cassure, pas un sweep -> pas de trade.", zone.Top, close);
                    Deactivate(zone, n, false);
                }
                else if (low < zone.Top && high > zone.Bottom)
                {
                    zone.Touches++;
                }
            }

            SweepSignal buySignal = null;
            foreach (var zone in _lowZones.Where(z => z.Active && z.PivotIndex + PivotLength < n))
            {
                if (n - zone.PivotIndex > MaxZoneAgeBars)
                {
                    Deactivate(zone, n, false);
                }
                else if (low < zone.Bottom && close > zone.Bottom)
                {
                    if (zone.Touches < MinTouches)
                        Log("Sweep swing low {0} ignore : {1} retours < {2} requis.", zone.Bottom, zone.Touches, MinTouches);
                    else if (buySignal == null || zone.Bottom < buySignal.Zone.Bottom)
                        buySignal = new SweepSignal { TradeType = TradeType.Buy, Zone = zone, SweepExtreme = low };
                    Deactivate(zone, n, true);
                }
                else if (close < zone.Bottom)
                {
                    Log("Swing low {0} casse en cloture ({1}) : vraie cassure, pas un sweep -> pas de trade.", zone.Bottom, close);
                    Deactivate(zone, n, false);
                }
                else if (low < zone.Top && high > zone.Bottom)
                {
                    zone.Touches++;
                }
            }

            _highZones.RemoveAll(z => !z.Active);
            _lowZones.RemoveAll(z => !z.Active);

            foreach (var zone in _highZones.Concat(_lowZones))
                DrawZone(zone, n);

            // Une bougie qui chasse les deux cotes a la fois n'est pas un signal clair.
            if (signal != null && buySignal != null)
            {
                Log("Sweep des deux cotes sur la meme bougie : signal ambigu, pas de trade.");
                return null;
            }

            return signal ?? buySignal;
        }

        private void Deactivate(Zone zone, int n, bool swept)
        {
            zone.Active = false;
            if (!DrawZones)
                return;

            if (swept)
            {
                Chart.DrawTrendLine(ZoneName(zone), zone.PivotIndex, zone.Level, n, zone.Level,
                    zone.IsHigh ? Color.Red : Color.Teal, 1, LineStyle.Lines);
                Chart.DrawIcon(ZoneName(zone) + "_sweep", zone.IsHigh ? ChartIconType.DownArrow : ChartIconType.UpArrow,
                    n, zone.IsHigh ? Bars.HighPrices[n] : Bars.LowPrices[n], zone.IsHigh ? Color.Red : Color.Teal);
            }
            else
            {
                RemoveZoneDrawing(zone);
            }
        }

        private void DrawZone(Zone zone, int n)
        {
            if (!DrawZones)
                return;

            var color = zone.IsHigh ? Color.Red : Color.Teal;
            Chart.DrawTrendLine(ZoneName(zone), zone.PivotIndex, zone.Level, n + 3, zone.Level, color, 1, LineStyle.Solid);
            var rect = Chart.DrawRectangle(ZoneName(zone) + "_area", zone.PivotIndex, zone.Top, n + 3, zone.Bottom,
                Color.FromArgb(40, color));
            rect.IsFilled = true;
        }

        private void RemoveZoneDrawing(Zone zone)
        {
            if (!DrawZones)
                return;

            Chart.RemoveObject(ZoneName(zone));
            Chart.RemoveObject(ZoneName(zone) + "_area");
        }

        private static string ZoneName(Zone zone)
        {
            return "LiqSweep_zone_" + zone.Id;
        }

        // ------------------------------------------------------------------
        // Entree
        // ------------------------------------------------------------------

        private void TryEnter(SweepSignal signal, int n)
        {
            var side = signal.TradeType == TradeType.Sell ? "swing high" : "swing low";
            Log("Sweep {0} {1} detecte (meche {2}, cloture {3}, {4} retours) -> signal {5}.",
                side, signal.Zone.Level, signal.SweepExtreme, Bars.ClosePrices[n], signal.Zone.Touches, signal.TradeType);

            if (Positions.Find(Label, SymbolName) != null)
            {
                Log("  -> pas de trade : une position est deja ouverte.");
                return;
            }

            var hour = Server.Time.Hour;
            if (hour < SessionStartHour || hour >= SessionEndHour)
            {
                Log("  -> pas de trade : hors creneau d'entree ({0}h-{1}h UTC, il est {2:HH:mm} UTC).", SessionStartHour, SessionEndHour, Server.Time);
                return;
            }

            if (UseDailyClose && hour >= CloseHourFor(Server.Time) - NoEntryHoursBeforeClose)
            {
                Log("  -> pas de trade : trop proche de la fermeture de fin de journee.");
                return;
            }

            if (_consecutiveLosses >= MaxConsecutiveLosses)
            {
                Log("  -> pas de trade : pause apres {0} pertes consecutives.", _consecutiveLosses);
                return;
            }

            if (_dailyLossLimitHit)
            {
                Log("  -> pas de trade : limite de perte journaliere atteinte.");
                return;
            }

            var spreadPips = Symbol.Spread / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips)
            {
                Log("  -> pas de trade : spread {0:0.0} pips > {1} max.", spreadPips, MaxSpreadPips);
                return;
            }

            if (IsNewsWindow(Server.Time))
            {
                Log("  -> pas de trade : fenetre news.");
                return;
            }

            if (UseTrendFilter && !IsTrendAligned(signal.TradeType))
            {
                Log("  -> pas de trade : contre la tendance {0} (cloture {1} vs EMA {2} {3:0.#####}).", TrendTimeFrame,
                    _trendBars.ClosePrices.Last(1), TrendEmaPeriod, _trendEma.Result.Last(1));
                return;
            }

            var atr = _atr.Result[n];
            if (double.IsNaN(atr) || atr <= 0)
                return;

            var entryPrice = signal.TradeType == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            var buffer = atr * StopLossBufferAtr;
            var stopPrice = signal.TradeType == TradeType.Buy ? signal.SweepExtreme - buffer : signal.SweepExtreme + buffer;
            var stopLossPips = Math.Max(Math.Abs(entryPrice - stopPrice) / Symbol.PipSize, MinStopLossPips);

            if (MaxStopLossAtr > 0 && stopLossPips * Symbol.PipSize > atr * MaxStopLossAtr)
            {
                Log("  -> pas de trade : stop de {0:0.0} pips > {1:0.0} x ATR ({2:0.0} pips).", stopLossPips, MaxStopLossAtr, atr * MaxStopLossAtr / Symbol.PipSize);
                return;
            }

            var takeProfitPips = stopLossPips * RiskRewardRatio;
            var volume = CalculatePositionVolume(stopLossPips);
            if (volume <= 0)
            {
                Log("  -> pas de trade : volume calcule nul (voir message ci-dessus).");
                return;
            }

            var reason = signal.TradeType == TradeType.Sell ? "Sweep swing high" : "Sweep swing low";
            var result = ExecuteMarketOrder(signal.TradeType, SymbolName, volume, Label, stopLossPips, takeProfitPips, reason);
            if (!result.IsSuccessful)
            {
                Print("Echec d'entree: {0}", result.Error);
                return;
            }

            _breakEvenDone = false;
            Print("{0} entree remplie ({1}, niveau {2}, {3} retours). Volume: {4}, risque: {5:0.0}%, SL: {6:0.0} pips, TP: {7:0.0} pips ({8}R)",
                signal.TradeType, reason, signal.Zone.Level, signal.Zone.Touches, volume,
                stopLossPips * PipValuePerUnit() * volume / Account.Balance * 100.0,
                stopLossPips, takeProfitPips, RiskRewardRatio);
        }

        private void Log(string format, params object[] args)
        {
            if (VerboseLogs)
                Print(format, args);
        }

        private bool IsTrendAligned(TradeType tradeType)
        {
            if (_trendEma == null || _trendBars.ClosePrices.Count < TrendEmaPeriod + 2)
                return false;

            var htfClose = _trendBars.ClosePrices.Last(1);
            var ema = _trendEma.Result.Last(1);
            return tradeType == TradeType.Buy ? htfClose > ema : htfClose < ema;
        }

        // ------------------------------------------------------------------
        // Taille de position
        // ------------------------------------------------------------------

        private double CalculatePositionVolume(double stopLossPips)
        {
            if (FixedLots > 0)
            {
                var fixedUnits = Symbol.QuantityToVolumeInUnits(FixedLots);
                if (fixedUnits < Symbol.VolumeInUnitsMin)
                {
                    Print("Lot fixe {0} inferieur au minimum du broker, entree ignoree.", FixedLots);
                    return 0;
                }
                return Math.Min(Symbol.NormalizeVolumeInUnits(fixedUnits, RoundingMode.Down), Symbol.VolumeInUnitsMax);
            }

            var riskAmount = Account.Balance * (RiskPercent / 100.0);
            var rawVolume = riskAmount / (stopLossPips * PipValuePerUnit());

            // Test sur le volume BRUT : NormalizeVolumeInUnits remonte un volume trop
            // petit au minimum du broker, ce qui ferait risquer bien plus que prevu.
            if (rawVolume < Symbol.VolumeInUnitsMin)
            {
                if (!AllowMinVolumeFallback || Account.Balance <= 0)
                    return 0;

                var riskAtMin = stopLossPips * PipValuePerUnit() * Symbol.VolumeInUnitsMin / Account.Balance * 100.0;
                if (riskAtMin > MaxRiskAtMinVolumePercent)
                {
                    Print("Le volume minimum risquerait {0:0.0}% (> {1:0.0}%), entree ignoree.", riskAtMin, MaxRiskAtMinVolumePercent);
                    return 0;
                }
                return Symbol.VolumeInUnitsMin;
            }

            return Math.Min(Symbol.NormalizeVolumeInUnits(rawVolume, RoundingMode.Down), Symbol.VolumeInUnitsMax);
        }

        private double PipValuePerUnit()
        {
            return Symbol.TickValue / Symbol.TickSize * Symbol.PipSize;
        }

        // ------------------------------------------------------------------
        // Gestion de position
        // ------------------------------------------------------------------

        private void ManageBreakEven()
        {
            if (!UseBreakEvenAt1R || _breakEvenDone)
                return;

            var position = Positions.Find(Label, SymbolName);
            if (position == null || !position.StopLoss.HasValue)
                return;

            var entry = position.EntryPrice;
            var slDistance = Math.Abs(entry - position.StopLoss.Value);
            if (slDistance <= 0)
                return;

            var reached = position.TradeType == TradeType.Buy
                ? Symbol.Bid >= entry + slDistance
                : Symbol.Ask <= entry - slDistance;

            if (reached)
            {
                ModifyPosition(position, entry, position.TakeProfit);
                _breakEvenDone = true;
            }
        }

        private void ManageTimeExits()
        {
            if (!UseDailyClose && MaxHoursInTrade <= 0)
                return;

            var position = Positions.Find(Label, SymbolName);
            if (position == null)
                return;

            var now = Server.Time;
            string reason = null;
            if (UseDailyClose && (now.Hour >= CloseHourFor(now) || position.EntryTime.Date < now.Date))
                reason = string.Format("fin de journee ({0}h UTC)", CloseHourFor(now));
            else if (MaxHoursInTrade > 0 && now - position.EntryTime >= TimeSpan.FromHours(MaxHoursInTrade))
                reason = string.Format("duree max atteinte ({0}h)", MaxHoursInTrade);

            if (reason == null)
                return;

            Print("Fermeture position : {0}.", reason);
            ClosePosition(position);
        }

        private int CloseHourFor(DateTime timeUtc)
        {
            return timeUtc.DayOfWeek == DayOfWeek.Friday ? FridayCloseHour : DailyCloseHour;
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;
            if (position.SymbolName != SymbolName || position.Label != Label)
                return;

            if (position.NetProfit < 0)
                _consecutiveLosses++;
            else
                _consecutiveLosses = 0;

            Print("Position fermee ({0}). Net: {1:0.00} {2} ({3:0.0} pips). Pertes consecutives: {4}/{5}",
                position.TradeType, position.NetProfit, Account.Asset.Name, position.Pips, _consecutiveLosses, MaxConsecutiveLosses);
        }

        private void UpdateDailyState(DateTime barTimeUtc)
        {
            var day = barTimeUtc.Date;
            if (day != _currentDay)
            {
                _currentDay = day;
                _dayStartBalance = Account.Balance;
                _dailyLossLimitHit = false;

                if (ResumeNextDay && _consecutiveLosses >= MaxConsecutiveLosses)
                {
                    Print("Nouveau jour : reprise apres {0} pertes consecutives.", _consecutiveLosses);
                    _consecutiveLosses = 0;
                }
            }

            if (!UseDailyLossLimit || _dailyLossLimitHit || _dayStartBalance <= 0)
                return;

            var dailyLossPercent = (_dayStartBalance - Account.Equity) / _dayStartBalance * 100.0;
            if (dailyLossPercent >= MaxDailyLossPercent)
            {
                _dailyLossLimitHit = true;
                Print("Limite de perte journaliere atteinte ({0:0.0}% >= {1:0.0}%), entrees suspendues jusqu'a demain (UTC).",
                    dailyLossPercent, MaxDailyLossPercent);
            }
        }

        // ------------------------------------------------------------------
        // Filtre news (heure de New York, heure d'ete US geree)
        // ------------------------------------------------------------------

        private void ParseNewsTimes()
        {
            _newsTimesNy.Clear();
            if (!UseNewsFilter || string.IsNullOrWhiteSpace(NewsTimesNewYork))
                return;

            foreach (var part in NewsTimesNewYork.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (TimeSpan.TryParse(part.Trim(), CultureInfo.InvariantCulture, out var time) && time < TimeSpan.FromDays(1))
                    _newsTimesNy.Add(time);
                else
                    Print("Heure news ignoree (format attendu HH:MM) : {0}", part);
            }

            Print("Filtre news : entrees bloquees de {0} min avant a {1} min apres {2} (heure de New York).",
                NewsMinutesBefore, NewsMinutesAfter, string.Join(", ", _newsTimesNy.ConvertAll(t => t.ToString(@"hh\:mm"))));
        }

        private bool IsNewsWindow(DateTime timeUtc)
        {
            if (!UseNewsFilter || _newsTimesNy.Count == 0)
                return false;

            var newYork = timeUtc.AddHours(IsUsDaylightSaving(timeUtc) ? -4 : -5);
            foreach (var newsTime in _newsTimesNy)
            {
                var minutesFromNews = (newYork - (newYork.Date + newsTime)).TotalMinutes;
                if (minutesFromNews >= -NewsMinutesBefore && minutesFromNews < NewsMinutesAfter)
                    return true;
            }
            return false;
        }

        private static bool IsUsDaylightSaving(DateTime timeUtc)
        {
            var year = timeUtc.Year;
            var start = NthSunday(year, 3, 2).AddHours(7);
            var end = NthSunday(year, 11, 1).AddHours(6);
            return timeUtc >= start && timeUtc < end;
        }

        private static DateTime NthSunday(int year, int month, int n)
        {
            var first = new DateTime(year, month, 1);
            var offset = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
            return first.AddDays(offset + 7 * (n - 1));
        }
    }
}

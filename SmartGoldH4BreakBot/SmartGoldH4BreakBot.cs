using System;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum DirectionMode
    {
        AchatSeulement,
        VenteSeulement,
        LesDeux
    }

    public enum EntryModeType
    {
        Retest,
        Immediate
    }

    // ============================================================================
    // SmartGoldH4BreakBot
    // ----------------------------------------------------------------------------
    // Cassure de structure 4H (logique SMC) sur XAUUSD, entree de precision 1H.
    //
    //   1. Structure 4H : detection des swings (pivots) confirmes. Une bougie 4H
    //      qui CLOTURE au-dessus du dernier swing high (ou sous le dernier swing
    //      low) = Break of Structure (BOS). Seule la premiere cloture au-dela du
    //      niveau compte (cassure "fraiche").
    //   2. Filtres de la bougie de cassure 4H :
    //        - pic de volume (VSA) : volume tick >= X fois la moyenne ;
    //        - bougie pas trop grande (<= X ATR 4H) et cloture dans le haut de
    //          la bougie (acheteurs en controle, pas une meche) ;
    //        - ADR : la journee n'a pas deja consomme l'essentiel de son range
    //          moyen (on ne court pas apres un mouvement epuise) ;
    //        - tendance : cloture du bon cote de l'EMA 4H.
    //   3. Entree 1H (mode Retest) : on attend que le prix revienne tester le
    //      niveau casse, puis une bougie 1H qui cloture de nouveau au-dela dans
    //      le sens du setup. Une cloture 1H nettement de l'autre cote du niveau
    //      = fausse cassure, le setup est abandonne. Le setup expire apres N
    //      heures : le bot peut attendre plusieurs jours sans rien faire.
    //   4. Stop sous le dernier creux 1H (+ marge ATR), borne entre un minimum
    //      et un maximum d'ATR 1H. TP en multiple du risque (R).
    //   5. Gestion : break-even a 1R, puis trailing stop ATR 1H.
    //   6. Protections : perte journaliere max, drawdown max depuis le plus
    //      haut du capital (arret du bot), pause apres une perte, nombre max de
    //      trades par semaine, pas d'entree le vendredi soir, filtre de spread.
    //
    // Implementation personnelle a partir d'une description de strategie ; ce
    // n'est pas le code d'un bot commercial. Aucune strategie ne gagne a tous
    // les coups : backtest long en tick data, puis compte demo, avant le reel.
    // ============================================================================
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class SmartGoldH4BreakBot : Robot
    {
        // --- Structure 4H ---
        [Parameter("Direction", Group = "Structure 4H", DefaultValue = DirectionMode.AchatSeulement)]
        public DirectionMode Direction { get; set; }

        [Parameter("Force des swings (bougies)", Group = "Structure 4H", DefaultValue = 2, MinValue = 1, MaxValue = 10,
            Description = "Nombre de bougies 4H de chaque cote pour confirmer un swing high/low.")]
        public int SwingStrength { get; set; }

        [Parameter("Recherche structure (bougies 4H)", Group = "Structure 4H", DefaultValue = 60, MinValue = 10, MaxValue = 500)]
        public int StructureLookback { get; set; }

        [Parameter("Filtre tendance EMA 4H", Group = "Structure 4H", DefaultValue = true)]
        public bool UseTrendFilter { get; set; }

        [Parameter("Periode EMA 4H", Group = "Structure 4H", DefaultValue = 50, MinValue = 5)]
        public int TrendEmaPeriod { get; set; }

        // --- Volume (VSA) ---
        [Parameter("Filtre pic de volume", Group = "Volume (VSA)", DefaultValue = true)]
        public bool UseVolumeFilter { get; set; }

        [Parameter("Moyenne volume (bougies 4H)", Group = "Volume (VSA)", DefaultValue = 20, MinValue = 5)]
        public int VolumeAveragePeriod { get; set; }

        [Parameter("Pic de volume (x moyenne)", Group = "Volume (VSA)", DefaultValue = 1.3, MinValue = 1.0, MaxValue = 5.0)]
        public double VolumeSpikeMultiplier { get; set; }

        // --- Volatilite ---
        [Parameter("Periode ATR", Group = "Volatilite (ATR/ADR)", DefaultValue = 14, MinValue = 2)]
        public int AtrPeriod { get; set; }

        [Parameter("Bougie de cassure max (x ATR 4H)", Group = "Volatilite (ATR/ADR)", DefaultValue = 2.0, MinValue = 0.5,
            Description = "Au-dela, la bougie de cassure est jugee trop etiree (mouvement deja fait).")]
        public double MaxBreakoutCandleAtr { get; set; }

        [Parameter("Force de cloture min (%)", Group = "Volatilite (ATR/ADR)", DefaultValue = 50, MinValue = 0, MaxValue = 100,
            Description = "Achat : la cloture doit etre dans le haut de la bougie (0 % = bas, 100 % = haut). Inverse pour la vente.")]
        public double MinCloseStrengthPercent { get; set; }

        [Parameter("Filtre ADR", Group = "Volatilite (ATR/ADR)", DefaultValue = true)]
        public bool UseAdrFilter { get; set; }

        [Parameter("Periode ADR (jours)", Group = "Volatilite (ATR/ADR)", DefaultValue = 14, MinValue = 3)]
        public int AdrPeriod { get; set; }

        [Parameter("ADR deja consomme max (%)", Group = "Volatilite (ATR/ADR)", DefaultValue = 80, MinValue = 10, MaxValue = 300)]
        public double MaxAdrUsedPercent { get; set; }

        // --- Entree 1H ---
        [Parameter("Mode d'entree", Group = "Entree 1H", DefaultValue = EntryModeType.Retest,
            Description = "Retest : attend le retour sur le niveau casse puis confirmation 1H. Immediate : entre des la cloture 4H de cassure.")]
        public EntryModeType EntryMode { get; set; }

        [Parameter("Validite du setup (heures)", Group = "Entree 1H", DefaultValue = 48, MinValue = 1, MaxValue = 240)]
        public int SetupValidityHours { get; set; }

        [Parameter("Tolerance retest (x ATR 1H)", Group = "Entree 1H", DefaultValue = 0.3, MinValue = 0)]
        public double RetestToleranceAtr { get; set; }

        [Parameter("Invalidation (x ATR 1H)", Group = "Entree 1H", DefaultValue = 0.5, MinValue = 0,
            Description = "Une cloture 1H au-dela du niveau, du mauvais cote, de plus que cette marge = fausse cassure.")]
        public double InvalidationAtr { get; set; }

        // --- Stop / objectif ---
        [Parameter("Creux 1H pour le stop (bougies)", Group = "Stop / Objectif", DefaultValue = 6, MinValue = 1, MaxValue = 50)]
        public int StopLookbackH1 { get; set; }

        [Parameter("Marge du stop (x ATR 1H)", Group = "Stop / Objectif", DefaultValue = 0.3, MinValue = 0)]
        public double StopBufferAtr { get; set; }

        [Parameter("Stop min (x ATR 1H)", Group = "Stop / Objectif", DefaultValue = 1.0, MinValue = 0.2)]
        public double MinStopAtr { get; set; }

        [Parameter("Stop max (x ATR 1H)", Group = "Stop / Objectif", DefaultValue = 3.0, MinValue = 0.5)]
        public double MaxStopAtr { get; set; }

        [Parameter("Utiliser take profit", Group = "Stop / Objectif", DefaultValue = true)]
        public bool UseTakeProfit { get; set; }

        [Parameter("Take profit (x risque R)", Group = "Stop / Objectif", DefaultValue = 2.5, MinValue = 0.5)]
        public double RewardRisk { get; set; }

        // --- Gestion de position ---
        [Parameter("Break-even a (R)", Group = "Trailing adaptatif", DefaultValue = 1.0, MinValue = 0,
            Description = "0 = desactive.")]
        public double BreakEvenAtR { get; set; }

        [Parameter("Marge break-even (pips)", Group = "Trailing adaptatif", DefaultValue = 5, MinValue = 0)]
        public double BreakEvenOffsetPips { get; set; }

        [Parameter("Trailing a partir de (R)", Group = "Trailing adaptatif", DefaultValue = 1.5, MinValue = 0,
            Description = "0 = desactive.")]
        public double TrailStartR { get; set; }

        [Parameter("Distance trailing (x ATR 1H)", Group = "Trailing adaptatif", DefaultValue = 2.0, MinValue = 0.5)]
        public double TrailAtr { get; set; }

        // --- Risque ---
        [Parameter("Risque par trade (%)", Group = "Risque", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 10)]
        public double RiskPercent { get; set; }

        [Parameter("Autoriser volume minimum", Group = "Risque", DefaultValue = true,
            Description = "Petit compte : si le volume calcule est sous 0.01 lot, prendre 0.01 lot tant que le risque reel reste sous le plafond.")]
        public bool AllowMinVolumeFallback { get; set; }

        [Parameter("Risque max au volume minimum (%)", Group = "Risque", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 20)]
        public double MaxRiskAtMinVolumePercent { get; set; }

        // --- Protections ---
        [Parameter("Perte journaliere max (%)", Group = "Protections", DefaultValue = 5.0, MinValue = 0,
            Description = "Plus de nouvelle entree le reste de la journee (UTC). 0 = desactive.")]
        public double MaxDailyLossPercent { get; set; }

        [Parameter("Drawdown max (%)", Group = "Protections", DefaultValue = 20.0, MinValue = 0,
            Description = "Baisse du capital depuis son plus haut : ferme tout et arrete le bot. 0 = desactive.")]
        public double MaxDrawdownPercent { get; set; }

        [Parameter("Pause apres une perte (heures)", Group = "Protections", DefaultValue = 24, MinValue = 0)]
        public int CooldownHoursAfterLoss { get; set; }

        [Parameter("Trades max par semaine", Group = "Protections", DefaultValue = 3, MinValue = 1)]
        public int MaxTradesPerWeek { get; set; }

        [Parameter("Pas d'entree vendredi apres (heure UTC)", Group = "Protections", DefaultValue = 16, MinValue = 0, MaxValue = 24,
            Description = "Evite d'ouvrir juste avant le gap du week-end. 24 = desactive.")]
        public int NoEntryFridayAfterHour { get; set; }

        [Parameter("Spread max (pips)", Group = "Protections", DefaultValue = 50, MinValue = 0)]
        public double MaxSpreadPips { get; set; }

        // --- Divers ---
        [Parameter("Dessiner les niveaux", Group = "Divers", DefaultValue = true)]
        public bool DrawLevels { get; set; }

        [Parameter("Label", Group = "Divers", DefaultValue = "SmartGoldH4BreakBot")]
        public string Label { get; set; }

        private const string LevelLineName = "SmartGoldH4Break_level";

        private Bars _h4Bars;
        private Bars _h1Bars;
        private Bars _d1Bars;
        private AverageTrueRange _atrH4;
        private AverageTrueRange _atrH1;
        private ExponentialMovingAverage _emaH4;

        private class Setup
        {
            public bool Active;
            public TradeType Type;
            public double Level;
            public DateTime SwingTime;
            public DateTime Expiry;
            public bool Retested;
        }

        private readonly Setup _setup = new Setup();
        private DateTime _lastUsedBuySwing = DateTime.MinValue;
        private DateTime _lastUsedSellSwing = DateTime.MinValue;

        private double _initialRiskDistance;
        private DateTime _currentDay = DateTime.MinValue;
        private double _dayStartBalance;
        private bool _dailyLimitHit;
        private double _peakEquity;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private DateTime _currentWeekStart = DateTime.MinValue;
        private int _tradesThisWeek;

        protected override void OnStart()
        {
            _h4Bars = MarketData.GetBars(TimeFrame.Hour4, SymbolName);
            _h1Bars = TimeFrame == TimeFrame.Hour ? Bars : MarketData.GetBars(TimeFrame.Hour, SymbolName);
            _d1Bars = MarketData.GetBars(TimeFrame.Daily, SymbolName);

            _atrH4 = Indicators.AverageTrueRange(_h4Bars, AtrPeriod, MovingAverageType.Exponential);
            _atrH1 = Indicators.AverageTrueRange(_h1Bars, AtrPeriod, MovingAverageType.Exponential);
            _emaH4 = Indicators.ExponentialMovingAverage(_h4Bars.ClosePrices, TrendEmaPeriod);

            _h4Bars.BarClosed += OnH4BarClosed;
            _h1Bars.BarClosed += OnH1BarClosed;
            Positions.Closed += OnPositionClosed;

            _peakEquity = Account.Equity;
            ResetDayIfNeeded();

            Print("SmartGoldH4BreakBot started on {0} {1}. Direction: {2}, entree: {3}.", SymbolName, TimeFrame, Direction, EntryMode);
            Print("Symbol info: PipSize {0}, pip value per unit {1}, min volume {2} units.",
                Symbol.PipSize, PipValuePerUnit(), Symbol.VolumeInUnitsMin);
        }

        protected override void OnTick()
        {
            CheckMaxDrawdown();
        }

        // ------------------------------------------------------------------
        // Structure 4H : detection du Break of Structure
        // ------------------------------------------------------------------
        private void OnH4BarClosed(BarClosedEventArgs args)
        {
            var c = _h4Bars.Count - 2; // derniere bougie 4H cloturee
            var minBars = Math.Max(Math.Max(StructureLookback, TrendEmaPeriod), VolumeAveragePeriod) + SwingStrength * 2 + 2;
            if (c < minBars)
                return;

            if (Positions.Find(Label, SymbolName) != null)
                return;

            if (Direction != DirectionMode.VenteSeulement && TryDetectBos(TradeType.Buy, c))
                return;

            if (Direction != DirectionMode.AchatSeulement)
                TryDetectBos(TradeType.Sell, c);
        }

        private bool TryDetectBos(TradeType type, int c)
        {
            var isBuy = type == TradeType.Buy;

            int swingIndex;
            if (!TryFindLastSwing(isBuy, c, out swingIndex))
                return false;

            var level = isBuy ? _h4Bars.HighPrices[swingIndex] : _h4Bars.LowPrices[swingIndex];
            var swingTime = _h4Bars.OpenTimes[swingIndex];

            var lastUsed = isBuy ? _lastUsedBuySwing : _lastUsedSellSwing;
            if (swingTime <= lastUsed)
                return false; // niveau deja trade

            var open = _h4Bars.OpenPrices[c];
            var high = _h4Bars.HighPrices[c];
            var low = _h4Bars.LowPrices[c];
            var close = _h4Bars.ClosePrices[c];

            var broke = isBuy ? close > level && close > open : close < level && close < open;
            if (!broke)
                return false;

            // Cassure fraiche : aucune cloture au-dela du niveau depuis le swing.
            for (var i = swingIndex + 1; i < c; i++)
            {
                var ci = _h4Bars.ClosePrices[i];
                if (isBuy ? ci > level : ci < level)
                    return false;
            }

            var reasons = "";

            if (UseTrendFilter)
            {
                var ema = _emaH4.Result[c];
                if (isBuy ? close <= ema : close >= ema)
                    reasons += " tendance EMA contraire;";
            }

            var range = high - low;
            var atr = _atrH4.Result[c];
            if (atr > 0 && range > MaxBreakoutCandleAtr * atr)
                reasons += string.Format(" bougie trop etiree ({0:0.0} ATR);", range / atr);

            if (range > 0)
            {
                var strength = isBuy ? (close - low) / range : (high - close) / range;
                if (strength * 100.0 < MinCloseStrengthPercent)
                    reasons += string.Format(" cloture faible ({0:0} %);", strength * 100.0);
            }

            if (UseVolumeFilter)
            {
                var avg = AverageTickVolume(c);
                var vol = _h4Bars.TickVolumes[c];
                if (avg > 0 && vol < VolumeSpikeMultiplier * avg)
                    reasons += string.Format(" pas de pic de volume ({0:0.00}x);", vol / avg);
            }

            if (UseAdrFilter)
            {
                var adr = AverageDailyRange();
                var todayRange = _d1Bars.HighPrices.LastValue - _d1Bars.LowPrices.LastValue;
                if (adr > 0 && todayRange / adr * 100.0 > MaxAdrUsedPercent)
                    reasons += string.Format(" ADR deja consomme ({0:0} %);", todayRange / adr * 100.0);
            }

            if (reasons.Length > 0)
            {
                Print("BOS {0} a {1} rejete :{2}", isBuy ? "haussier" : "baissier", Math.Round(level, Symbol.Digits), reasons);
                // On marque le niveau comme utilise : une cassure rejetee ne
                // sera pas reprise plus tard sur le meme swing.
                MarkSwingUsed(type, swingTime);
                return false;
            }

            _setup.Active = true;
            _setup.Type = type;
            _setup.Level = level;
            _setup.SwingTime = swingTime;
            _setup.Expiry = Server.Time.AddHours(SetupValidityHours);
            _setup.Retested = false;

            Print("BOS {0} valide sur {1}. Attente {2} (max {3} h).", isBuy ? "haussier" : "baissier",
                Math.Round(level, Symbol.Digits), EntryMode == EntryModeType.Retest ? "du retest 1H" : "entree immediate", SetupValidityHours);

            if (DrawLevels)
                Chart.DrawHorizontalLine(LevelLineName, level, isBuy ? Color.LimeGreen : Color.OrangeRed, 1, LineStyle.Dots);

            if (EntryMode == EntryModeType.Immediate)
            {
                TryEnter();
                CancelSetup("entree immediate impossible");
            }

            return true;
        }

        private bool TryFindLastSwing(bool high, int c, out int swingIndex)
        {
            var k = SwingStrength;
            var newest = c - 1 - k; // le swing doit etre confirme avant la bougie de cassure
            var oldest = Math.Max(k, c - StructureLookback);

            for (var i = newest; i >= oldest; i--)
            {
                if (IsSwing(high, i, k))
                {
                    swingIndex = i;
                    return true;
                }
            }

            swingIndex = -1;
            return false;
        }

        private bool IsSwing(bool high, int i, int k)
        {
            for (var j = 1; j <= k; j++)
            {
                if (high)
                {
                    if (!(_h4Bars.HighPrices[i] > _h4Bars.HighPrices[i - j] && _h4Bars.HighPrices[i] >= _h4Bars.HighPrices[i + j]))
                        return false;
                }
                else
                {
                    if (!(_h4Bars.LowPrices[i] < _h4Bars.LowPrices[i - j] && _h4Bars.LowPrices[i] <= _h4Bars.LowPrices[i + j]))
                        return false;
                }
            }
            return true;
        }

        private double AverageTickVolume(int c)
        {
            double sum = 0;
            for (var i = c - VolumeAveragePeriod; i < c; i++)
                sum += _h4Bars.TickVolumes[i];
            return sum / VolumeAveragePeriod;
        }

        private double AverageDailyRange()
        {
            var last = _d1Bars.Count - 2; // jours complets uniquement
            if (last - AdrPeriod + 1 < 0)
                return 0;

            double sum = 0;
            for (var i = last - AdrPeriod + 1; i <= last; i++)
                sum += _d1Bars.HighPrices[i] - _d1Bars.LowPrices[i];
            return sum / AdrPeriod;
        }

        private void MarkSwingUsed(TradeType type, DateTime swingTime)
        {
            if (type == TradeType.Buy)
                _lastUsedBuySwing = swingTime;
            else
                _lastUsedSellSwing = swingTime;
        }

        private void CancelSetup(string reason)
        {
            if (!_setup.Active)
                return;

            _setup.Active = false;
            MarkSwingUsed(_setup.Type, _setup.SwingTime);
            Print("Setup abandonne : {0}.", reason);

            if (DrawLevels)
                Chart.RemoveObject(LevelLineName);
        }

        // ------------------------------------------------------------------
        // Entree et gestion sur les clotures 1H
        // ------------------------------------------------------------------
        private void OnH1BarClosed(BarClosedEventArgs args)
        {
            ResetDayIfNeeded();
            CheckDailyLoss();
            ManagePosition();

            if (!_setup.Active || EntryMode != EntryModeType.Retest)
                return;

            if (Server.Time > _setup.Expiry)
            {
                CancelSetup("expire sans retest valide");
                return;
            }

            var h = _h1Bars.Count - 2;
            var atr = _atrH1.Result[h];
            var open = _h1Bars.OpenPrices[h];
            var close = _h1Bars.ClosePrices[h];
            var isBuy = _setup.Type == TradeType.Buy;

            var invalidated = isBuy
                ? close < _setup.Level - InvalidationAtr * atr
                : close > _setup.Level + InvalidationAtr * atr;
            if (invalidated)
            {
                CancelSetup("fausse cassure (cloture 1H de l'autre cote du niveau)");
                return;
            }

            var touched = isBuy
                ? _h1Bars.LowPrices[h] <= _setup.Level + RetestToleranceAtr * atr
                : _h1Bars.HighPrices[h] >= _setup.Level - RetestToleranceAtr * atr;
            if (touched)
                _setup.Retested = true;

            var confirmed = isBuy ? close > _setup.Level && close > open : close < _setup.Level && close < open;
            if (_setup.Retested && confirmed)
                TryEnter();
        }

        private void TryEnter()
        {
            if (!CanOpenNewTrade())
                return;

            var isBuy = _setup.Type == TradeType.Buy;
            var h = _h1Bars.Count - 2;
            var atr = _atrH1.Result[h];
            if (atr <= 0)
                return;

            var entry = isBuy ? Symbol.Ask : Symbol.Bid;

            var extreme = isBuy ? double.MaxValue : double.MinValue;
            for (var i = h; i > h - StopLookbackH1 && i >= 0; i--)
                extreme = isBuy ? Math.Min(extreme, _h1Bars.LowPrices[i]) : Math.Max(extreme, _h1Bars.HighPrices[i]);

            var stopPrice = isBuy ? extreme - StopBufferAtr * atr : extreme + StopBufferAtr * atr;
            var distance = isBuy ? entry - stopPrice : stopPrice - entry;
            distance = Math.Max(distance, MinStopAtr * atr);
            distance = Math.Min(distance, MaxStopAtr * atr);

            var stopPips = distance / Symbol.PipSize;
            var volume = CalculatePositionVolume(stopPips);
            if (volume <= 0)
                return; // le setup reste actif : un stop plus court peut passer plus tard

            double? tpPips = null;
            if (UseTakeProfit)
                tpPips = stopPips * RewardRisk;

            var result = ExecuteMarketOrder(_setup.Type, SymbolName, volume, Label, stopPips, tpPips);
            if (!result.IsSuccessful)
            {
                Print("Ordre refuse : {0}", result.Error);
                return;
            }

            _initialRiskDistance = distance;
            _tradesThisWeek++;
            Print("{0} ouvert. Volume {1}, stop {2:0.0} pips, TP {3}.", _setup.Type, volume, stopPips,
                tpPips.HasValue ? string.Format("{0:0.0} pips", tpPips.Value) : "aucun");

            MarkSwingUsed(_setup.Type, _setup.SwingTime);
            _setup.Active = false;
            if (DrawLevels)
                Chart.RemoveObject(LevelLineName);
        }

        private bool CanOpenNewTrade()
        {
            if (Positions.Find(Label, SymbolName) != null)
                return false;

            if (_dailyLimitHit)
                return false;

            var now = Server.Time;

            if (now < _cooldownUntil)
            {
                Print("Entree ignoree : pause apres perte jusqu'au {0:dd/MM HH:mm}.", _cooldownUntil);
                return false;
            }

            ResetWeekIfNeeded();
            if (_tradesThisWeek >= MaxTradesPerWeek)
            {
                Print("Entree ignoree : {0} trades deja pris cette semaine.", _tradesThisWeek);
                return false;
            }

            if (now.DayOfWeek == DayOfWeek.Friday && now.Hour >= NoEntryFridayAfterHour)
                return false;

            var spreadPips = Symbol.Spread / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips)
            {
                Print("Entree ignoree : spread trop large ({0:0.0} pips).", spreadPips);
                return false;
            }

            return true;
        }

        private void ManagePosition()
        {
            var position = Positions.Find(Label, SymbolName);
            if (position == null)
                return;

            var isBuy = position.TradeType == TradeType.Buy;
            var risk = _initialRiskDistance;
            if (risk <= 0 && position.StopLoss.HasValue)
                risk = Math.Abs(position.EntryPrice - position.StopLoss.Value); // apres un redemarrage du bot
            if (risk <= 0)
                return;

            var price = isBuy ? Symbol.Bid : Symbol.Ask;
            var r = (isBuy ? price - position.EntryPrice : position.EntryPrice - price) / risk;

            double? target = null;

            if (BreakEvenAtR > 0 && r >= BreakEvenAtR)
            {
                var offset = BreakEvenOffsetPips * Symbol.PipSize;
                target = isBuy ? position.EntryPrice + offset : position.EntryPrice - offset;
            }

            if (TrailStartR > 0 && r >= TrailStartR)
            {
                var h = _h1Bars.Count - 2;
                var trail = TrailAtr * _atrH1.Result[h];
                var close = _h1Bars.ClosePrices[h];
                var trailStop = isBuy ? close - trail : close + trail;
                target = !target.HasValue ? trailStop : (isBuy ? Math.Max(target.Value, trailStop) : Math.Min(target.Value, trailStop));
            }

            if (!target.HasValue)
                return;

            var newStop = Math.Round(target.Value, Symbol.Digits);
            var current = position.StopLoss;
            var improves = !current.HasValue || (isBuy ? newStop > current.Value : newStop < current.Value);
            var valid = isBuy ? newStop < Symbol.Bid : newStop > Symbol.Ask;

            if (improves && valid)
                ModifyPosition(position, newStop, position.TakeProfit);
        }

        // ------------------------------------------------------------------
        // Protections du capital
        // ------------------------------------------------------------------
        private void ResetDayIfNeeded()
        {
            var today = Server.Time.Date;
            if (today == _currentDay)
                return;

            _currentDay = today;
            _dayStartBalance = Account.Balance;
            _dailyLimitHit = false;
        }

        private void CheckDailyLoss()
        {
            if (MaxDailyLossPercent <= 0 || _dailyLimitHit || _dayStartBalance <= 0)
                return;

            if (Account.Equity <= _dayStartBalance * (1 - MaxDailyLossPercent / 100.0))
            {
                _dailyLimitHit = true;
                Print("Perte journaliere max atteinte ({0:0.0} %) : plus d'entree aujourd'hui.", MaxDailyLossPercent);
            }
        }

        private void CheckMaxDrawdown()
        {
            var equity = Account.Equity;
            if (equity > _peakEquity)
                _peakEquity = equity;

            if (MaxDrawdownPercent <= 0 || _peakEquity <= 0)
                return;

            if (equity <= _peakEquity * (1 - MaxDrawdownPercent / 100.0))
            {
                Print("Drawdown max atteint ({0:0.0} % depuis le plus haut {1:0.00}) : fermeture et arret du bot.",
                    MaxDrawdownPercent, _peakEquity);
                foreach (var position in Positions.FindAll(Label, SymbolName))
                    ClosePosition(position);
                Stop();
            }
        }

        private void ResetWeekIfNeeded()
        {
            var date = Server.Time.Date;
            var weekStart = date.AddDays(-(((int)date.DayOfWeek + 6) % 7)); // lundi
            if (weekStart == _currentWeekStart)
                return;

            _currentWeekStart = weekStart;
            _tradesThisWeek = 0;
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;
            if (position.Label != Label || position.SymbolName != SymbolName)
                return;

            _initialRiskDistance = 0;

            if (position.NetProfit < 0 && CooldownHoursAfterLoss > 0)
                _cooldownUntil = Server.Time.AddHours(CooldownHoursAfterLoss);

            Print("Position closed ({0}, {1}). Net: {2:0.00} {3}. Balance: {4:0.00}.",
                position.TradeType, args.Reason, position.NetProfit, Account.Asset.Name, Account.Balance);
        }

        // ------------------------------------------------------------------
        // Taille de position
        // ------------------------------------------------------------------
        private double CalculatePositionVolume(double stopLossPips)
        {
            var riskAmount = Account.Balance * (RiskPercent / 100.0);
            var rawVolume = riskAmount / (stopLossPips * PipValuePerUnit());

            // Verifie avant de normaliser : NormalizeVolumeInUnits remonte au
            // minimum du broker et contournerait le plafond de risque.
            if (rawVolume < Symbol.VolumeInUnitsMin)
                return MinVolumeIfRiskAcceptable(stopLossPips);

            var normalized = Symbol.NormalizeVolumeInUnits(rawVolume, RoundingMode.Down);
            return Math.Min(normalized, Symbol.VolumeInUnitsMax);
        }

        private double MinVolumeIfRiskAcceptable(double stopLossPips)
        {
            if (!AllowMinVolumeFallback || Account.Balance <= 0)
                return 0;

            var minVolume = Symbol.VolumeInUnitsMin;
            var riskAtMinPercent = stopLossPips * PipValuePerUnit() * minVolume / Account.Balance * 100.0;

            if (riskAtMinPercent > MaxRiskAtMinVolumePercent)
            {
                Print("Volume minimum = {0:0.0} % de risque (> {1:0.0} %), entree ignoree.", riskAtMinPercent, MaxRiskAtMinVolumePercent);
                return 0;
            }

            Print("Volume minimum utilise, risque reel {0:0.0} %.", riskAtMinPercent);
            return minVolume;
        }

        private double PipValuePerUnit()
        {
            return Symbol.TickValue / Symbol.TickSize * Symbol.PipSize;
        }
    }
}

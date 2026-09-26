using System;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // ============================================================================
    // ChikouCloudBot
    // ----------------------------------------------------------------------------
    // Ichimoku : signal quand la Chikou Span sort du nuage (vert ou rouge), puis
    // entree seulement quand d'autres elements Ichimoku confirment.
    //
    //   1. Signal : la Chikou Span (cloture actuelle, tracee 26 bougies en
    //      arriere) CLOTURE au-dessus du nuage de cette epoque (achat) ou en
    //      dessous (vente), alors qu'elle etait dedans ou de l'autre cote a la
    //      bougie precedente. La couleur du nuage traverse n'a pas d'importance.
    //   2. Confirmation (dans les N bougies qui suivent, toutes celles activees) :
    //        - le prix cloture au-dessus / en dessous du nuage actuel ;
    //        - Tenkan au-dessus / en dessous de Kijun ;
    //        - la Chikou est libre : au-dessus du plus haut (ou sous le plus bas)
    //          de la bougie d'il y a 26 periodes ;
    //        - (option) nuage futur de la bonne couleur ;
    //        - (option) bougie de confirmation dans le sens du trade.
    //      Si la Chikou retombe dans le nuage avant confirmation, le signal est
    //      abandonne ; s'il n'est pas confirme a temps, il expire.
    //   3. Stop sous la Kijun (+ marge ATR), borne entre un minimum et un
    //      maximum d'ATR. Sortie classique : cloture de l'autre cote de la
    //      Kijun. Break-even a 1R, take profit en R optionnel.
    //   4. Cycle de 5 jours : pas d'entree le week-end ni le vendredi soir,
    //      tout est ferme le vendredi, perte max par semaine et par jour, ligne
    //      WEEK SUMMARY chaque semaine.
    //
    // Le nuage est calcule a partir des prix bruts (comme IctSmcIchimokuBot)
    // pour maitriser exactement le decalage de 26 periodes. Aucune strategie ne
    // gagne a tous les coups : backtest long en tick data sur plusieurs periodes,
    // puis compte demo, avant le reel.
    // ============================================================================
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class ChikouCloudBot : Robot
    {
        // --- Ichimoku ---
        [Parameter("Tenkan-sen (periode)", Group = "Ichimoku", DefaultValue = 9, MinValue = 2)]
        public int TenkanPeriod { get; set; }

        [Parameter("Kijun-sen (periode)", Group = "Ichimoku", DefaultValue = 26, MinValue = 2,
            Description = "Sert aussi de decalage du nuage et de la Chikou Span (26 en standard).")]
        public int KijunPeriod { get; set; }

        [Parameter("Senkou Span B (periode)", Group = "Ichimoku", DefaultValue = 52, MinValue = 2)]
        public int SenkouSpanBPeriod { get; set; }

        [Parameter("Autoriser les achats", Group = "Ichimoku", DefaultValue = true)]
        public bool AllowBuys { get; set; }

        [Parameter("Autoriser les ventes", Group = "Ichimoku", DefaultValue = true)]
        public bool AllowSells { get; set; }

        // --- Confirmation ---
        [Parameter("Bougies pour confirmer", Group = "Confirmation", DefaultValue = 12, MinValue = 1, MaxValue = 100,
            Description = "Apres la sortie de la Chikou, nombre de bougies pour que les confirmations s'alignent. Au-dela, le signal expire.")]
        public int ConfirmationBars { get; set; }

        [Parameter("Prix hors du nuage actuel", Group = "Confirmation", DefaultValue = true)]
        public bool ConfirmPriceOutsideCloud { get; set; }

        [Parameter("Tenkan / Kijun dans le bon sens", Group = "Confirmation", DefaultValue = true)]
        public bool ConfirmTenkanKijun { get; set; }

        [Parameter("Chikou libre (au-dela du prix d'il y a 26)", Group = "Confirmation", DefaultValue = true)]
        public bool ConfirmChikouFree { get; set; }

        [Parameter("Nuage futur de la bonne couleur", Group = "Confirmation", DefaultValue = false)]
        public bool ConfirmFutureCloud { get; set; }

        [Parameter("Bougie de confirmation", Group = "Confirmation", DefaultValue = false,
            Description = "La bougie qui declenche l'entree doit cloturer dans le sens du trade.")]
        public bool ConfirmCandle { get; set; }

        // --- Stop / sortie ---
        [Parameter("Periode ATR", Group = "Stop / Sortie", DefaultValue = 14, MinValue = 2)]
        public int AtrPeriod { get; set; }

        [Parameter("Marge sous la Kijun (x ATR)", Group = "Stop / Sortie", DefaultValue = 0.5, MinValue = 0)]
        public double StopBufferAtr { get; set; }

        [Parameter("Stop min (x ATR)", Group = "Stop / Sortie", DefaultValue = 1.0, MinValue = 0.2)]
        public double MinStopAtr { get; set; }

        [Parameter("Stop max (x ATR)", Group = "Stop / Sortie", DefaultValue = 3.0, MinValue = 0.5)]
        public double MaxStopAtr { get; set; }

        [Parameter("Sortie sur cloture au-dela de la Kijun", Group = "Stop / Sortie", DefaultValue = true,
            Description = "Sortie Ichimoku classique : un achat est ferme quand une bougie cloture sous la Kijun.")]
        public bool ExitOnKijunClose { get; set; }

        [Parameter("Sortie si la Chikou rentre dans le nuage", Group = "Stop / Sortie", DefaultValue = false)]
        public bool ExitOnChikouBackInCloud { get; set; }

        [Parameter("Break-even a (R, 0=off)", Group = "Stop / Sortie", DefaultValue = 1.0, MinValue = 0)]
        public double BreakEvenAtR { get; set; }

        [Parameter("Utiliser take profit", Group = "Stop / Sortie", DefaultValue = false)]
        public bool UseTakeProfit { get; set; }

        [Parameter("Take profit (x R)", Group = "Stop / Sortie", DefaultValue = 3.0, MinValue = 0.5)]
        public double RewardRisk { get; set; }

        // --- Risque ---
        [Parameter("Risque par trade (%)", Group = "Risque", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 10)]
        public double RiskPercent { get; set; }

        [Parameter("Autoriser volume minimum", Group = "Risque", DefaultValue = true,
            Description = "Petit compte : si le volume calcule est sous le minimum du broker, prendre le minimum tant que le risque reel reste sous le plafond.")]
        public bool AllowMinVolumeFallback { get; set; }

        [Parameter("Risque max au volume minimum (%)", Group = "Risque", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 20)]
        public double MaxRiskAtMinVolumePercent { get; set; }

        [Parameter("Spread max (pips)", Group = "Risque", DefaultValue = 3, MinValue = 0)]
        public double MaxSpreadPips { get; set; }

        // --- Semaine ---
        [Parameter("Perte journaliere max (%)", Group = "Semaine (5 jours)", DefaultValue = 4.0, MinValue = 0,
            Description = "Plus de nouvelle entree le reste de la journee. 0 = off.")]
        public double MaxDailyLossPercent { get; set; }

        [Parameter("Perte hebdo max (%)", Group = "Semaine (5 jours)", DefaultValue = 6.0, MinValue = 0,
            Description = "Tout fermer et ne plus trader jusqu'a lundi. 0 = off.")]
        public double MaxWeeklyLossPercent { get; set; }

        [Parameter("Pas d'entree vendredi apres (heure UTC)", Group = "Semaine (5 jours)", DefaultValue = 16, MinValue = 0, MaxValue = 24)]
        public int FridayNoEntryHour { get; set; }

        [Parameter("Tout fermer vendredi a (heure UTC)", Group = "Semaine (5 jours)", DefaultValue = 20, MinValue = 0, MaxValue = 24,
            Description = "24 = off.")]
        public int FridayCloseHour { get; set; }

        [Parameter("Label", Group = "Divers", DefaultValue = "ChikouCloudBot")]
        public string Label { get; set; }

        private AverageTrueRange _atr;

        private bool _setupActive;
        private TradeType _setupType;
        private int _setupBarsLeft;

        private double _initialRiskDistance;
        private bool _breakEvenDone;

        private DateTime _currentDay = DateTime.MinValue;
        private double _dayStartBalance;
        private bool _dailyLimitHit;

        private DateTime _weekStart = DateTime.MinValue;
        private double _weekStartBalance;
        private bool _weekLocked;
        private int _weekTrades;

        private int _statSignals, _statConfirmed, _statExpired, _statInvalidated, _statSkipRisk, _statSkipBlocked, _statTrades;

        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            Positions.Closed += OnPositionClosed;

            Print("ChikouCloudBot started on {0} {1}. Ichimoku {2}/{3}/{4}.", SymbolName, TimeFrame, TenkanPeriod, KijunPeriod, SenkouSpanBPeriod);
            Print("Symbol info: PipSize {0}, pip value per unit {1}, min volume {2} units.", Symbol.PipSize, PipValuePerUnit(), Symbol.VolumeInUnitsMin);
        }

        protected override void OnTick()
        {
            UpdateDayAndWeek();

            var now = Server.Time;
            if (now.DayOfWeek == DayOfWeek.Friday && now.Hour >= FridayCloseHour)
                CloseAllPositions("fermeture du vendredi avant le week-end");

            if (_weekLocked || _weekStartBalance <= 0 || MaxWeeklyLossPercent <= 0)
                return;

            var weekPercent = (Account.Equity - _weekStartBalance) / _weekStartBalance * 100.0;
            if (weekPercent <= -MaxWeeklyLossPercent)
            {
                _weekLocked = true;
                Print("Perte hebdo max atteinte ({0:0.0} %) : plus de trade jusqu'a lundi.", weekPercent);
                CloseAllPositions("perte hebdo max");
            }
        }

        protected override void OnStop()
        {
            if (_weekStart != DateTime.MinValue)
                PrintWeekSummary();

            Print("===== Resume ChikouCloudBot =====");
            Print("Sorties de la Chikou hors du nuage : {0}. Confirmees : {1}, expirees : {2}, annulees (Chikou revenue dans le nuage) : {3}.",
                _statSignals, _statConfirmed, _statExpired, _statInvalidated);
            Print("Entrees ignorees : risque trop grand {0}, bloquees (spread, perte jour/semaine, week-end) {1}. Trades ouverts : {2}.",
                _statSkipRisk, _statSkipBlocked, _statTrades);
        }

        protected override void OnBarClosed()
        {
            var i = Bars.Count - 2; // derniere bougie cloturee
            if (i < 2 * KijunPeriod + Math.Max(SenkouSpanBPeriod, KijunPeriod) + 2)
                return;

            UpdateDayAndWeek();
            CheckDailyLoss();
            ManagePosition(i);

            var hasPosition = Positions.Find(Label, SymbolName) != null;

            DetectChikouSignal(i, hasPosition);

            if (_setupActive && !hasPosition)
                TryConfirmAndEnter(i);
        }

        // ------------------------------------------------------------------
        // Signal : la Chikou sort du nuage
        // ------------------------------------------------------------------
        private void DetectChikouSignal(int i, bool hasPosition)
        {
            var close = Bars.ClosePrices[i];
            var prevClose = Bars.ClosePrices[i - 1];
            var d = KijunPeriod;

            var crossUp = close > CloudTop(i - d) && prevClose <= CloudTop(i - 1 - d);
            var crossDown = close < CloudBottom(i - d) && prevClose >= CloudBottom(i - 1 - d);

            if (crossUp && AllowBuys)
                StartSetup(TradeType.Buy, hasPosition);
            else if (crossDown && AllowSells)
                StartSetup(TradeType.Sell, hasPosition);
        }

        private void StartSetup(TradeType type, bool hasPosition)
        {
            _statSignals++;

            if (hasPosition)
            {
                Print("Chikou sortie du nuage ({0}) mais une position est deja ouverte : signal ignore.", type == TradeType.Buy ? "achat" : "vente");
                return;
            }

            _setupActive = true;
            _setupType = type;
            _setupBarsLeft = ConfirmationBars;
            Print("Chikou sortie {0} du nuage : attente de confirmation ({1} bougies max).",
                type == TradeType.Buy ? "au-dessus" : "en dessous", ConfirmationBars);
        }

        private void TryConfirmAndEnter(int i)
        {
            var isBuy = _setupType == TradeType.Buy;
            var close = Bars.ClosePrices[i];
            var d = KijunPeriod;

            var chikouStillOut = isBuy ? close > CloudTop(i - d) : close < CloudBottom(i - d);
            if (!chikouStillOut)
            {
                _setupActive = false;
                _statInvalidated++;
                Print("Signal annule : la Chikou est revenue dans le nuage.");
                return;
            }

            var missing = MissingConfirmations(i, isBuy);
            if (missing.Length == 0)
            {
                _setupActive = false;
                _statConfirmed++;
                Enter(i, _setupType);
                return;
            }

            _setupBarsLeft--;
            if (_setupBarsLeft <= 0)
            {
                _setupActive = false;
                _statExpired++;
                Print("Signal expire sans confirmation (manquait :{0}).", missing);
            }
        }

        private string MissingConfirmations(int i, bool isBuy)
        {
            var missing = "";
            var close = Bars.ClosePrices[i];
            var d = KijunPeriod;

            if (ConfirmPriceOutsideCloud && !(isBuy ? close > CloudTop(i) : close < CloudBottom(i)))
                missing += " prix hors du nuage;";

            if (ConfirmTenkanKijun)
            {
                var tenkan = Tenkan(i);
                var kijun = Kijun(i);
                if (!(isBuy ? tenkan > kijun : tenkan < kijun))
                    missing += " Tenkan/Kijun;";
            }

            if (ConfirmChikouFree && !(isBuy ? close > Bars.HighPrices[i - d] : close < Bars.LowPrices[i - d]))
                missing += " Chikou libre;";

            if (ConfirmFutureCloud)
            {
                var a = RawSenkouA(i);
                var b = RawSenkouB(i);
                if (!(isBuy ? a > b : a < b))
                    missing += " nuage futur;";
            }

            if (ConfirmCandle)
            {
                var open = Bars.OpenPrices[i];
                if (!(isBuy ? close > open : close < open))
                    missing += " bougie de confirmation;";
            }

            return missing;
        }

        // ------------------------------------------------------------------
        // Entree et gestion
        // ------------------------------------------------------------------
        private void Enter(int i, TradeType type)
        {
            if (!EntryAllowed())
            {
                _statSkipBlocked++;
                return;
            }

            var isBuy = type == TradeType.Buy;
            var atr = _atr.Result[i];
            if (atr <= 0)
                return;

            var entry = isBuy ? Symbol.Ask : Symbol.Bid;
            var kijun = Kijun(i);
            var stopPrice = isBuy ? kijun - StopBufferAtr * atr : kijun + StopBufferAtr * atr;
            var distance = isBuy ? entry - stopPrice : stopPrice - entry;
            distance = Math.Max(distance, MinStopAtr * atr);
            distance = Math.Min(distance, MaxStopAtr * atr);

            var stopPips = distance / Symbol.PipSize;
            var volume = CalculatePositionVolume(stopPips);
            if (volume <= 0)
            {
                _statSkipRisk++;
                return;
            }

            double? tpPips = null;
            if (UseTakeProfit)
                tpPips = stopPips * RewardRisk;

            var result = ExecuteMarketOrder(type, SymbolName, volume, Label, stopPips, tpPips);
            if (!result.IsSuccessful)
            {
                Print("Ordre refuse : {0}", result.Error);
                return;
            }

            _initialRiskDistance = distance;
            _breakEvenDone = false;
            _statTrades++;
            _weekTrades++;
            Print("{0} ouvert (signal confirme). Volume {1}, stop {2:0.0} pips, TP {3}.", type, volume, stopPips,
                tpPips.HasValue ? string.Format("{0:0.0} pips", tpPips.Value) : "aucun (sortie sur Kijun)");
        }

        private void ManagePosition(int i)
        {
            var position = Positions.Find(Label, SymbolName);
            if (position == null)
                return;

            var isBuy = position.TradeType == TradeType.Buy;
            var close = Bars.ClosePrices[i];

            if (ExitOnKijunClose)
            {
                var kijun = Kijun(i);
                if (isBuy ? close < kijun : close > kijun)
                {
                    Print("Cloture de l'autre cote de la Kijun : sortie.");
                    ClosePosition(position);
                    return;
                }
            }

            if (ExitOnChikouBackInCloud)
            {
                var d = KijunPeriod;
                var back = isBuy ? close <= CloudTop(i - d) : close >= CloudBottom(i - d);
                if (back)
                {
                    Print("Chikou revenue dans le nuage : sortie.");
                    ClosePosition(position);
                    return;
                }
            }

            if (BreakEvenAtR <= 0 || _breakEvenDone)
                return;

            var risk = _initialRiskDistance;
            if (risk <= 0 && position.StopLoss.HasValue)
                risk = Math.Abs(position.EntryPrice - position.StopLoss.Value);
            if (risk <= 0)
                return;

            var price = isBuy ? Symbol.Bid : Symbol.Ask;
            var r = (isBuy ? price - position.EntryPrice : position.EntryPrice - price) / risk;
            if (r < BreakEvenAtR)
                return;

            var offset = Symbol.Spread;
            var newStop = Math.Round(isBuy ? position.EntryPrice + offset : position.EntryPrice - offset, Symbol.Digits);
            var improves = !position.StopLoss.HasValue || (isBuy ? newStop > position.StopLoss.Value : newStop < position.StopLoss.Value);
            var valid = isBuy ? newStop < Symbol.Bid : newStop > Symbol.Ask;
            if (improves && valid)
            {
                ModifyPosition(position, newStop, position.TakeProfit);
                _breakEvenDone = true;
                Print("Break-even : stop deplace au prix d'entree.");
            }
        }

        private bool EntryAllowed()
        {
            if (_dailyLimitHit || _weekLocked)
                return false;

            var now = Server.Time;
            if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)
                return false;
            if (now.DayOfWeek == DayOfWeek.Friday && now.Hour >= FridayNoEntryHour)
                return false;

            var spreadPips = Symbol.Spread / Symbol.PipSize;
            if (MaxSpreadPips > 0 && spreadPips > MaxSpreadPips)
            {
                Print("Entree ignoree : spread trop large ({0:0.0} pips).", spreadPips);
                return false;
            }

            return true;
        }

        // ------------------------------------------------------------------
        // Ichimoku calcule a partir des prix bruts
        // ------------------------------------------------------------------
        private double Highest(int i, int period)
        {
            var max = double.MinValue;
            for (var k = i - period + 1; k <= i; k++)
                max = Math.Max(max, Bars.HighPrices[k]);
            return max;
        }

        private double Lowest(int i, int period)
        {
            var min = double.MaxValue;
            for (var k = i - period + 1; k <= i; k++)
                min = Math.Min(min, Bars.LowPrices[k]);
            return min;
        }

        private double Tenkan(int i)
        {
            return (Highest(i, TenkanPeriod) + Lowest(i, TenkanPeriod)) / 2.0;
        }

        private double Kijun(int i)
        {
            return (Highest(i, KijunPeriod) + Lowest(i, KijunPeriod)) / 2.0;
        }

        // Valeurs calculees a la bougie i, affichees 26 bougies plus loin.
        private double RawSenkouA(int i)
        {
            return (Tenkan(i) + Kijun(i)) / 2.0;
        }

        private double RawSenkouB(int i)
        {
            return (Highest(i, SenkouSpanBPeriod) + Lowest(i, SenkouSpanBPeriod)) / 2.0;
        }

        // Nuage tel qu'il est affiche a la bougie j.
        private double CloudTop(int j)
        {
            return Math.Max(RawSenkouA(j - KijunPeriod), RawSenkouB(j - KijunPeriod));
        }

        private double CloudBottom(int j)
        {
            return Math.Min(RawSenkouA(j - KijunPeriod), RawSenkouB(j - KijunPeriod));
        }

        // ------------------------------------------------------------------
        // Jour / semaine
        // ------------------------------------------------------------------
        private void UpdateDayAndWeek()
        {
            var date = Server.Time.Date;

            if (date != _currentDay)
            {
                _currentDay = date;
                _dayStartBalance = Account.Balance;
                _dailyLimitHit = false;
            }

            var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
            if (monday == _weekStart)
                return;

            if (_weekStart != DateTime.MinValue)
                PrintWeekSummary();

            _weekStart = monday;
            _weekStartBalance = Account.Balance;
            _weekLocked = false;
            _weekTrades = 0;
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

        private void PrintWeekSummary()
        {
            var result = Account.Balance - _weekStartBalance;
            var percent = _weekStartBalance > 0 ? result / _weekStartBalance * 100.0 : 0;
            Print("WEEK SUMMARY {0:dd/MM/yyyy}: start {1:0.00}, end {2:0.00}, result {3:+0.00;-0.00} ({4:+0.0;-0.0}%), trades {5}.",
                _weekStart, _weekStartBalance, Account.Balance, result, percent, _weekTrades);
        }

        private void CloseAllPositions(string reason)
        {
            foreach (var position in Positions.FindAll(Label, SymbolName))
            {
                Print("Fermeture de la position ({0}).", reason);
                ClosePosition(position);
            }
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;
            if (position.Label != Label || position.SymbolName != SymbolName)
                return;

            _initialRiskDistance = 0;
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

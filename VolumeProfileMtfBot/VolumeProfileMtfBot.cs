using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // ============================================================================
    // VolumeProfileMtfBot
    // ----------------------------------------------------------------------------
    // Port en cBot cTrader de la strategie TradingView "VP Robot MTF - BUY/SELL Auto"
    // (Pine Script v5) : profil de volume (POC / VAH / VAL) + filtres de tendance
    // multi-timeframe (15 min et 1H) + confirmation RSI/volume + gestion du risque
    // avec sortie en deux temps (TP1/TP2) et break-even.
    //
    // Differences assumees par rapport au script Pine d'origine :
    //   - Le profil de volume est recalcule a CHAQUE cloture de bougie (OnBarClosed),
    //     pas seulement sur `barstate.islast` comme dans le script Pine. Dans le
    //     script original, cette restriction fait que le profil (et donc les
    //     signaux) ne se met quasiment jamais a jour pendant un backtest Pine,
    //     seulement sur la derniere bougie de l'historique charge. Recalculer a
    //     chaque bougie rend la strategie reellement testable/tradable en continu,
    //     en backtest cTrader comme en live.
    //   - Le "volume" utilise est le tick volume cTrader (proxy standard pour le
    //     forex/CFD, qui n'ont pas de volume reel centralise, contrairement aux
    //     actions/crypto sur lesquelles tourne generalement TradingView).
    //   - La sortie partielle TP1/TP2 du script Pine (strategy.exit avec
    //     qty_percent=50 sur deux jambes) est reproduite avec DEUX positions
    //     distinctes ouvertes simultanement (moitie du volume chacune), chacune
    //     avec son propre stop loss et son propre take profit. C'est le pattern
    //     natif cTrader pour une sortie partielle : le SL/TP est gere par le
    //     broker, pas par une surveillance manuelle du prix bougie par bougie.
    //   - Taille de position calculee au RISQUE (% du capital perdu si le stop
    //     est touche) plutot qu'en % de notionnel comme default_qty_value dans le
    //     script Pine : sans levier, un % de notionnel ne depasse jamais le volume
    //     minimum du broker sur un petit compte (200 EUR). Si le capital ne permet
    //     pas deux jambes TP1/TP2, le bot ouvre une seule position (TP2, avec
    //     break-even quand le niveau TP1 est atteint) - voir TryEnter.
    //   - Le tableau de bord et l'histogramme de volume (boxes colorees) du script
    //     Pine ne sont pas reproduits a l'identique : seules les lignes POC/VAH/VAL
    //     et un texte de statut condense sont affiches sur le graphique cTrader.
    //
    // IMPORTANT : comme pour tout bot automatise, testez en backtest puis en
    // compte demo avant d'envisager un passage en reel.
    // ============================================================================
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class VolumeProfileMtfBot : Robot
    {
        // -- Volume Profile --
        // Defauts calibres pour un chart de base en 4H (30 barres ~ 1 semaine de
        // trading = 6 barres/jour x 5 jours). Sur ce timeframe on est en swing
        // trading, la logique "1 session intraday" du script Pine d'origine (78
        // barres en 5 min) ne s'applique plus - fenetre hebdomadaire a la place.
        [Parameter("Barres (fenetre du profil)", Group = "Volume Profile", DefaultValue = 30, MinValue = 10, MaxValue = 500)]
        public int VpBars { get; set; }

        [Parameter("Lignes (Row Size)", Group = "Volume Profile", DefaultValue = 24, MinValue = 5, MaxValue = 100)]
        public int VpRows { get; set; }

        [Parameter("Value Area %", Group = "Volume Profile", DefaultValue = 70.0, MinValue = 0, MaxValue = 100)]
        public double VpValueAreaPercent { get; set; }

        // -- Filtres Multi-Timeframe --
        [Parameter("Activer filtre HTF1", Group = "Multi-Timeframe", DefaultValue = true)]
        public bool UseHtf1Filter { get; set; }

        [Parameter("Activer filtre HTF2", Group = "Multi-Timeframe", DefaultValue = true)]
        public bool UseHtf2Filter { get; set; }

        [Parameter("HTF1 (intermediaire)", Group = "Multi-Timeframe", DefaultValue = "Daily")]
        public TimeFrame Htf1TimeFrame { get; set; }

        [Parameter("HTF2 (haut)", Group = "Multi-Timeframe", DefaultValue = "Weekly")]
        public TimeFrame Htf2TimeFrame { get; set; }

        // -- Signaux --
        [Parameter("Tolerance zone (%)", Group = "Signaux", DefaultValue = 0.15, MinValue = 0.01, MaxValue = 1.0, Step = 0.01)]
        public double ZoneTolerancePercent { get; set; }

        [Parameter("Multiplicateur volume min", Group = "Signaux", DefaultValue = 1.2, MinValue = 1.0, MaxValue = 3.0, Step = 0.1)]
        public double VolumeMultiplier { get; set; }

        [Parameter("Confirmer avec RSI", Group = "Signaux", DefaultValue = true)]
        public bool UseRsi { get; set; }

        [Parameter("Longueur RSI", Group = "Signaux", DefaultValue = 14, MinValue = 2)]
        public int RsiPeriod { get; set; }

        [Parameter("RSI Overbought", Group = "Signaux", DefaultValue = 65.0, MinValue = 50.0)]
        public double RsiOverbought { get; set; }

        [Parameter("RSI Oversold", Group = "Signaux", DefaultValue = 35.0, MinValue = 10.0)]
        public double RsiOversold { get; set; }

        // -- Gestion du risque --
        [Parameter("Lot fixe (0 = calcul au risque)", Group = "Risk Management", DefaultValue = 0.0, MinValue = 0.0, Step = 0.01,
            Description = "Si > 0, trade toujours ce nombre de lots (reparti entre TP1/TP2 si possible) au lieu du calcul au risque. Le risque reel est affiche dans le log a chaque entree.")]
        public double FixedLots { get; set; }

        [Parameter("Risque par trade (%)", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 10,
            Description = "% du solde perdu si le stop loss est touche (toutes jambes confondues). Le volume est calcule a partir de ce risque et de la distance du stop.")]
        public double RiskPercent { get; set; }

        [Parameter("Autoriser volume minimum (petit compte)", Group = "Risk Management", DefaultValue = true,
            Description = "Sur un petit compte, le volume calcule au risque peut etre inferieur au minimum du broker (0,01 lot). Si active, trade le volume minimum a la place, mais seulement si son risque reel reste sous 'Risque max au volume minimum (%)'.")]
        public bool AllowMinVolumeFallback { get; set; }

        [Parameter("Risque max au volume minimum (%)", Group = "Risk Management", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 10,
            Description = "Plafond du % reel du capital risque quand le volume minimum est utilise. Les entrees dont le stop risquerait plus sont ignorees.")]
        public double MaxRiskAtMinVolumePercent { get; set; }

        [Parameter("Scinder en TP1/TP2", Group = "Risk Management", DefaultValue = true,
            Description = "Ouvre deux positions (moitie TP1, moitie TP2) comme le script Pine. Si le capital ne le permet pas, ou si desactive, ouvre une seule position visant TP2 avec break-even au niveau TP1.")]
        public bool SplitTakeProfits { get; set; }

        // -- SL/TP en multiples d'ATR --
        [Parameter("SL/TP bases sur l'ATR", Group = "Stops ATR", DefaultValue = true,
            Description = "Si active, SL/TP1/TP2 = multiples de l'ATR du graphique : les distances s'adaptent a la volatilite, au timeframe et a l'actif. Sinon, les % du groupe Risk Management sont utilises.")]
        public bool UseAtrStops { get; set; }

        [Parameter("Periode ATR", Group = "Stops ATR", DefaultValue = 14, MinValue = 2, MaxValue = 200)]
        public int AtrPeriod { get; set; }

        [Parameter("SL (x ATR)", Group = "Stops ATR", DefaultValue = 1.5, MinValue = 0.1, MaxValue = 20, Step = 0.1)]
        public double StopLossAtrMultiplier { get; set; }

        [Parameter("TP1 (x ATR)", Group = "Stops ATR", DefaultValue = 1.5, MinValue = 0.1, MaxValue = 20, Step = 0.1)]
        public double TakeProfit1AtrMultiplier { get; set; }

        [Parameter("TP2 (x ATR)", Group = "Stops ATR", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 40, Step = 0.1)]
        public double TakeProfit2AtrMultiplier { get; set; }

        [Parameter("SL minimum (pips)", Group = "Stops ATR", DefaultValue = 5.0, MinValue = 0, Step = 0.5,
            Description = "Plancher du stop loss en mode ATR, pour qu'un marche tres calme ne donne pas un stop plus petit que le bruit / le spread. TP1 et TP2 sont agrandis dans la meme proportion.")]
        public double MinStopLossPips { get; set; }

        // Mode % (si "SL/TP bases sur l'ATR" est desactive).
        // SL/TP1/TP2 elargis x4 par rapport aux defauts 5 min d'origine (0.4/0.4/0.8),
        // pour tenir compte de l'amplitude bien plus grande des bougies 4H (swing
        // trading). Ratio 1:1:2 conserve - a reaffiner par backtest, pas une
        // calibration precise.
        [Parameter("Stop Loss (%)", Group = "Risk Management", DefaultValue = 2.5, MinValue = 0.05, MaxValue = 10.0, Step = 0.05)]
        public double StopLossPercent { get; set; }

        [Parameter("TP1 (%)", Group = "Risk Management", DefaultValue = 2.5, MinValue = 0.05, MaxValue = 10.0, Step = 0.05)]
        public double TakeProfit1Percent { get; set; }

        [Parameter("TP2 (%)", Group = "Risk Management", DefaultValue = 5.0, MinValue = 0.05, MaxValue = 15.0, Step = 0.05)]
        public double TakeProfit2Percent { get; set; }

        [Parameter("Break-even apres TP1", Group = "Risk Management", DefaultValue = true)]
        public bool UseBreakEven { get; set; }

        [Parameter("Max pertes consecutives", Group = "Risk Management", DefaultValue = 3, MinValue = 1, MaxValue = 10)]
        public int MaxConsecutiveLosses { get; set; }

        [Parameter("Activer limite de perte journaliere", Group = "Risk Management", DefaultValue = true)]
        public bool UseDailyLossLimit { get; set; }

        [Parameter("Perte journaliere max (%)", Group = "Risk Management", DefaultValue = 5.0, MinValue = 0.5, MaxValue = 50.0, Step = 0.5,
            Description = "Suspend les nouvelles entrees jusqu'au lendemain (UTC) si la perte cumulee depuis le debut de la journee depasse ce % du solde de debut de journee.")]
        public double MaxDailyLossPercent { get; set; }

        // -- Sorties par le temps --
        [Parameter("Fermer en fin de journee", Group = "Sortie", DefaultValue = true,
            Description = "Ferme les positions a l'heure ci-dessous et bloque les nouvelles entrees apres cette heure : pas de position la nuit ni le week-end (swap, gaps). A desactiver pour du swing sur 4H.")]
        public bool UseDailyClose { get; set; }

        [Parameter("Heure de fermeture (UTC)", Group = "Sortie", DefaultValue = 21, MinValue = 1, MaxValue = 23)]
        public int DailyCloseHour { get; set; }

        [Parameter("Heure de fermeture vendredi (UTC)", Group = "Sortie", DefaultValue = 20, MinValue = 1, MaxValue = 23,
            Description = "Le marche ferme vers 21h UTC le vendredi : apres, plus aucun tick n'arrive et une position ne peut plus etre fermee avant la reouverture du dimanche soir. A mettre avant la cloture du broker.")]
        public int FridayCloseHour { get; set; }

        [Parameter("Derniere entree (heures avant fermeture)", Group = "Sortie", DefaultValue = 1, MinValue = 0, MaxValue = 12,
            Description = "Bloque les nouvelles entrees ce nombre d'heures avant l'heure de fermeture, pour ne pas ouvrir un trade qui sera coupe presque aussitot.")]
        public int NoEntryHoursBeforeClose { get; set; }

        [Parameter("Duree max en position (heures)", Group = "Sortie", DefaultValue = 0, MinValue = 0, MaxValue = 500,
            Description = "Ferme la position apres ce nombre d'heures si ni le SL ni le TP n'ont ete touches. 0 = desactive.")]
        public int MaxHoursInTrade { get; set; }

        // -- Securite --
        [Parameter("Max Spread (pips)", Group = "Safety", DefaultValue = 50, MinValue = 0,
            Description = "Ignore les nouvelles entrees si le spread courant depasse ce seuil, pour eviter de trader en pleine actu/illiquidite.")]
        public double MaxSpreadPips { get; set; }

        // -- Filtre de session --
        [Parameter("Filtrer les sessions", Group = "Session", DefaultValue = true)]
        public bool UseSessionFilter { get; set; }

        [Parameter("Session Londres (08-17 UTC)", Group = "Session", DefaultValue = true)]
        public bool UseLondonSession { get; set; }

        [Parameter("Session New York (13-22 UTC)", Group = "Session", DefaultValue = true)]
        public bool UseNySession { get; set; }

        [Parameter("Eviter 30min apres ouverture", Group = "Session", DefaultValue = true)]
        public bool AvoidSessionOpen { get; set; }

        // -- Divers --
        [Parameter("Label", Group = "Misc", DefaultValue = "VPRobotMTF")]
        public string Label { get; set; }

        private const string Tp1Suffix = "_TP1";
        private const string Tp2Suffix = "_TP2";

        private Bars _htf1Bars;
        private Bars _htf2Bars;
        private MovingAverage _htf1Ema20;
        private MovingAverage _htf1Ema50;
        private MovingAverage _htf2Ema20;
        private MovingAverage _htf2Ema50;
        private RelativeStrengthIndex _rsi;
        private AverageTrueRange _atr;
        private MovingAverage _volumeAverage;

        private double _poc = double.NaN;
        private double _vah = double.NaN;
        private double _val = double.NaN;

        private int _consecutiveLosses;

        // Un "round" = les deux jambes (TP1 + TP2) ouvertes pour un meme signal.
        // Les pertes consecutives sont evaluees une fois par round (PnL combine des
        // deux jambes), pas par jambe individuelle, pour ne pas compter une seule
        // sortie sur stop loss (qui ferme generalement TP1 et TP2 ensemble) comme
        // deux pertes distinctes.
        private int _openLegsInRound;
        private double _roundNetProfit;

        // Mode une seule position : prix auquel remonter le stop au break-even
        // (niveau TP1). NaN quand il n'y a rien a surveiller.
        private double _singleLegBreakEvenTrigger = double.NaN;

        private DateTime _currentDay = DateTime.MinValue;
        private double _dayStartBalance;
        private bool _dailyLossLimitHit;

        protected override void OnStart()
        {
            _rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);
            _atr = Indicators.AverageTrueRange(Bars, AtrPeriod, MovingAverageType.Simple);
            _volumeAverage = Indicators.MovingAverage(Bars.TickVolumes, 20, MovingAverageType.Simple);

            if (UseHtf1Filter)
            {
                _htf1Bars = MarketData.GetBars(Htf1TimeFrame, SymbolName);
                _htf1Ema20 = Indicators.MovingAverage(_htf1Bars.ClosePrices, 20, MovingAverageType.Exponential);
                _htf1Ema50 = Indicators.MovingAverage(_htf1Bars.ClosePrices, 50, MovingAverageType.Exponential);
            }

            if (UseHtf2Filter)
            {
                _htf2Bars = MarketData.GetBars(Htf2TimeFrame, SymbolName);
                _htf2Ema20 = Indicators.MovingAverage(_htf2Bars.ClosePrices, 20, MovingAverageType.Exponential);
                _htf2Ema50 = Indicators.MovingAverage(_htf2Bars.ClosePrices, 50, MovingAverageType.Exponential);
            }

            Positions.Closed += OnPositionClosed;

            Print("VolumeProfileMtfBot started on {0} {1}", SymbolName, TimeFrame);
            Print("Symbole: PipSize {0}, PipValue {1}, TickSize {2}, TickValue {3}, valeur pip/unite utilisee {4}, volume min {5} unites.",
                Symbol.PipSize, Symbol.PipValue, Symbol.TickSize, Symbol.TickValue, PipValuePerUnit(), Symbol.VolumeInUnitsMin);
        }

        protected override void OnTick()
        {
            ManageTimeExits();
            ManageSingleLegBreakEven();
        }

        protected override void OnBarClosed()
        {
            if (!TryCalculateVolumeProfile())
                return;

            DrawLevels();

            var minBars = Math.Max(VpBars, RsiPeriod) + 2;
            if (Bars.ClosePrices.Count < minBars)
                return;

            var barTimeUtc = Bars.OpenTimes.Last(1);
            UpdateDailyLossState(barTimeUtc);

            if (HasOpenPosition())
                return;

            if (!IsSessionOk(barTimeUtc))
                return;

            if (UseDailyClose && Server.Time.Hour >= CloseHourFor(Server.Time) - NoEntryHoursBeforeClose)
                return;

            if (_consecutiveLosses >= MaxConsecutiveLosses)
                return;

            if (_dailyLossLimitHit)
                return;

            if (Symbol.Spread / Symbol.PipSize > MaxSpreadPips)
            {
                Print("Spread trop large ({0} pips), entrees ignorees ce cycle.", Math.Round(Symbol.Spread / Symbol.PipSize, 1));
                return;
            }

            var close = Bars.ClosePrices.Last(1);
            var prevClose = Bars.ClosePrices.Last(2);
            var open = Bars.OpenPrices.Last(1);
            var high = Bars.HighPrices.Last(1);
            var low = Bars.LowPrices.Last(1);
            var volume = Bars.TickVolumes.Last(1);
            var volAvg = _volumeAverage.Result.Last(1);

            var tol = close * ZoneTolerancePercent / 100.0;
            var volOk = volAvg > 0 && volume > volAvg * VolumeMultiplier;

            var nearVal = Math.Abs(close - _val) <= tol;
            var nearPoc = Math.Abs(close - _poc) <= tol;
            var nearVah = Math.Abs(close - _vah) <= tol;

            var crossVah = prevClose <= _vah && close > _vah;
            var crossVal = prevClose >= _val && close < _val;
            var crossPocUp = prevClose <= _poc && close > _poc;
            var crossPocDown = prevClose >= _poc && close < _poc;

            var bullCandle = close > open && (close - open) > (high - close) * 0.5;
            var bearCandle = close < open && (open - close) > (close - low) * 0.5;

            var rsiValue = _rsi.Result.Last(1);
            var rsiBuyOk = !UseRsi || rsiValue < RsiOverbought;
            var rsiSellOk = !UseRsi || rsiValue > RsiOversold;

            bool htf1Bull, htf1Bear, htf2Bull, htf2Bear;
            GetHtfTrend(out htf1Bull, out htf1Bear, out htf2Bull, out htf2Bear);

            var trendOkBull = (!UseHtf1Filter || htf1Bull) && (!UseHtf2Filter || htf2Bull);
            var trendOkBear = (!UseHtf1Filter || htf1Bear) && (!UseHtf2Filter || htf2Bear);

            var buyVal = nearVal && bullCandle && volOk && trendOkBull && rsiBuyOk;
            var buyPoc = nearPoc && crossPocUp && bullCandle && volOk && trendOkBull && rsiBuyOk;
            var buyVah = crossVah && bullCandle && volOk && trendOkBull && rsiBuyOk;
            var buySignal = buyVal || buyPoc || buyVah;

            var sellVah = nearVah && bearCandle && volOk && trendOkBear && rsiSellOk;
            var sellPoc = nearPoc && crossPocDown && bearCandle && volOk && trendOkBear && rsiSellOk;
            var sellVal = crossVal && bearCandle && volOk && trendOkBear && rsiSellOk;
            var sellSignal = sellVah || sellPoc || sellVal;

            if (buySignal)
            {
                var reason = buyVal ? "Rebond VAL" : buyPoc ? "Rebond POC" : "Cassure VAH";
                TryEnter(TradeType.Buy, close, reason);
            }
            else if (sellSignal)
            {
                var reason = sellVah ? "Rejet VAH" : sellPoc ? "Rejet POC" : "Cassure VAL";
                TryEnter(TradeType.Sell, close, reason);
            }
        }

        private void TryEnter(TradeType tradeType, double closePrice, string reason)
        {
            double stopLossPips, tp1Pips, tp2Pips;
            if (UseAtrStops)
            {
                var atrPips = _atr.Result.Last(1) / Symbol.PipSize;
                if (double.IsNaN(atrPips) || atrPips <= 0)
                    return;

                stopLossPips = atrPips * StopLossAtrMultiplier;
                tp1Pips = atrPips * TakeProfit1AtrMultiplier;
                tp2Pips = atrPips * TakeProfit2AtrMultiplier;

                // Plancher : on agrandit SL et TP ensemble pour garder le ratio R.
                if (stopLossPips < MinStopLossPips)
                {
                    var scale = MinStopLossPips / stopLossPips;
                    stopLossPips *= scale;
                    tp1Pips *= scale;
                    tp2Pips *= scale;
                }
            }
            else
            {
                stopLossPips = (closePrice * StopLossPercent / 100.0) / Symbol.PipSize;
                tp1Pips = (closePrice * TakeProfit1Percent / 100.0) / Symbol.PipSize;
                tp2Pips = (closePrice * TakeProfit2Percent / 100.0) / Symbol.PipSize;
            }

            if (stopLossPips <= 0 || tp1Pips <= 0 || tp2Pips <= 0)
                return;

            if (!TryGetPositionSize(stopLossPips, out var legs, out var legVolume))
                return;

            if (legs == 1)
            {
                var result = ExecuteMarketOrder(tradeType, SymbolName, legVolume, Label + Tp2Suffix, stopLossPips, tp2Pips, reason);
                if (!result.IsSuccessful)
                {
                    Print("Echec d'entree: {0}", result.Error);
                    return;
                }

                _openLegsInRound = 1;
                _roundNetProfit = 0;

                var entry = result.Position.EntryPrice;
                var tp1Distance = tp1Pips * Symbol.PipSize;
                _singleLegBreakEvenTrigger = UseBreakEven && tp1Pips < tp2Pips
                    ? (tradeType == TradeType.Buy ? entry + tp1Distance : entry - tp1Distance)
                    : double.NaN;

                Print("{0} entree remplie ({1}), position unique. Volume: {2}, risque: {3:0.0}%, SL: {4} pips, TP: {5} pips{6}",
                    tradeType, reason, legVolume, RiskPercentFor(legVolume, stopLossPips), Math.Round(stopLossPips, 1), Math.Round(tp2Pips, 1),
                    double.IsNaN(_singleLegBreakEvenTrigger) ? "" : ", break-even au niveau TP1");
                return;
            }

            var tp1Result = ExecuteMarketOrder(tradeType, SymbolName, legVolume, Label + Tp1Suffix, stopLossPips, tp1Pips, reason + " (TP1)");
            var tp2Result = ExecuteMarketOrder(tradeType, SymbolName, legVolume, Label + Tp2Suffix, stopLossPips, tp2Pips, reason + " (TP2)");

            if (tp1Result.IsSuccessful && tp2Result.IsSuccessful)
            {
                _openLegsInRound = 2;
                _roundNetProfit = 0;
                _singleLegBreakEvenTrigger = double.NaN;

                Print("{0} entree remplie ({1}). Volume: 2 x {2}, risque: {3:0.0}%, SL: {4} pips, TP1: {5} pips, TP2: {6} pips",
                    tradeType, reason, legVolume, RiskPercentFor(legVolume * 2, stopLossPips), Math.Round(stopLossPips, 1), Math.Round(tp1Pips, 1), Math.Round(tp2Pips, 1));
            }
            else
            {
                Print("Echec d'entree - TP1: {0}, TP2: {1}", tp1Result.Error, tp2Result.Error);

                // Si une seule des deux jambes a pu s'ouvrir, on la referme aussitot
                // pour eviter une position orpheline (taille reduite, non geree par
                // le suivi de round / break-even qui attend les deux jambes).
                if (tp1Result.IsSuccessful && tp1Result.Position != null)
                    ClosePosition(tp1Result.Position);

                if (tp2Result.IsSuccessful && tp2Result.Position != null)
                    ClosePosition(tp2Result.Position);
            }
        }

        // Choisit le nombre de jambes (2 = TP1/TP2, 1 = position unique) et le
        // volume de chaque jambe pour que la perte au stop reste sous RiskPercent,
        // ou sous MaxRiskAtMinVolumePercent quand le volume minimum est force.
        private bool TryGetPositionSize(double stopLossPips, out int legs, out double legVolume)
        {
            legs = 0;
            legVolume = 0;

            if (Account.Balance <= 0)
                return false;

            var minVolume = Symbol.VolumeInUnitsMin;

            if (FixedLots > 0)
            {
                var fixedVolume = Math.Min(Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(FixedLots), RoundingMode.Down), Symbol.VolumeInUnitsMax);
                if (Symbol.QuantityToVolumeInUnits(FixedLots) < minVolume)
                {
                    Print("Lot fixe {0} inferieur au minimum du broker ({1} lot), entree ignoree.", FixedLots, Symbol.VolumeInUnitsToQuantity(minVolume));
                    return false;
                }

                var fixedHalf = Symbol.NormalizeVolumeInUnits(fixedVolume / 2.0, RoundingMode.Down);
                if (SplitTakeProfits && fixedVolume / 2.0 >= minVolume)
                {
                    legs = 2;
                    legVolume = fixedHalf;
                }
                else
                {
                    legs = 1;
                    legVolume = fixedVolume;
                }

                return true;
            }

            var riskAmount = Account.Balance * (RiskPercent / 100.0);
            // Volume BRUT (avant normalisation) : NormalizeVolumeInUnits remonte un
            // volume trop petit au minimum du broker et contournerait le risque.
            var riskVolume = riskAmount / (stopLossPips * PipValuePerUnit());

            if (SplitTakeProfits)
            {
                if (riskVolume / 2.0 >= minVolume)
                {
                    legs = 2;
                    legVolume = Math.Min(Symbol.NormalizeVolumeInUnits(riskVolume / 2.0, RoundingMode.Down), Symbol.VolumeInUnitsMax);
                    return true;
                }

                if (AllowMinVolumeFallback && RiskPercentFor(minVolume * 2, stopLossPips) <= MaxRiskAtMinVolumePercent)
                {
                    legs = 2;
                    legVolume = minVolume;
                    return true;
                }
            }

            if (riskVolume >= minVolume)
            {
                legs = 1;
                legVolume = Math.Min(Symbol.NormalizeVolumeInUnits(riskVolume, RoundingMode.Down), Symbol.VolumeInUnitsMax);
                return true;
            }

            var riskAtMin = RiskPercentFor(minVolume, stopLossPips);
            if (AllowMinVolumeFallback && riskAtMin <= MaxRiskAtMinVolumePercent)
            {
                legs = 1;
                legVolume = minVolume;
                return true;
            }

            Print("Le volume minimum risquerait {0:0.0}% du solde (> {1:0.0}%{2}), entree ignoree.",
                riskAtMin, AllowMinVolumeFallback ? MaxRiskAtMinVolumePercent : RiskPercent,
                AllowMinVolumeFallback ? "" : ", volume minimum non autorise");
            return false;
        }

        private double RiskPercentFor(double volume, double stopLossPips)
        {
            return stopLossPips * PipValuePerUnit() * volume / Account.Balance * 100.0;
        }

        // Valeur d'un pip pour une unite de volume, en devise du compte, derivee
        // de la tick value (Symbol.PipValue donnait une valeur bien trop faible
        // sur XAUUSD en backtest, cf. GoldTrendBot).
        private double PipValuePerUnit()
        {
            return Symbol.TickValue / Symbol.TickSize * Symbol.PipSize;
        }

        private int CloseHourFor(DateTime timeUtc)
        {
            return timeUtc.DayOfWeek == DayOfWeek.Friday ? FridayCloseHour : DailyCloseHour;
        }

        // Ferme les jambes du bot en fin de journee (et celles ouvertes un jour
        // precedent, ex. apres un redemarrage) et au-dela de la duree max.
        private void ManageTimeExits()
        {
            if (!UseDailyClose && MaxHoursInTrade <= 0)
                return;

            var now = Server.Time;
            foreach (var position in Positions.ToArray())
            {
                if (position.SymbolName != SymbolName)
                    continue;
                if (position.Label != Label + Tp1Suffix && position.Label != Label + Tp2Suffix)
                    continue;

                string reason = null;
                if (UseDailyClose && (now.Hour >= CloseHourFor(now) || position.EntryTime.Date < now.Date))
                    reason = string.Format("fin de journee ({0}h UTC)", CloseHourFor(now));
                else if (MaxHoursInTrade > 0 && now - position.EntryTime >= TimeSpan.FromHours(MaxHoursInTrade))
                    reason = string.Format("duree max atteinte ({0}h)", MaxHoursInTrade);

                if (reason == null)
                    continue;

                Print("Fermeture {0} : {1}.", position.Label, reason);
                ClosePosition(position);
            }
        }

        private void ManageSingleLegBreakEven()
        {
            if (double.IsNaN(_singleLegBreakEvenTrigger))
                return;

            var position = Positions.Find(Label + Tp2Suffix, SymbolName);
            if (position == null || _openLegsInRound != 1)
            {
                _singleLegBreakEvenTrigger = double.NaN;
                return;
            }

            var reached = position.TradeType == TradeType.Buy
                ? Symbol.Bid >= _singleLegBreakEvenTrigger
                : Symbol.Ask <= _singleLegBreakEvenTrigger;

            if (!reached)
                return;

            ModifyPosition(position, position.EntryPrice, position.TakeProfit);
            _singleLegBreakEvenTrigger = double.NaN;
            Print("Niveau TP1 atteint, stop remonte au break-even.");
        }

        private bool HasOpenPosition()
        {
            return Positions.Find(Label + Tp1Suffix, SymbolName) != null || Positions.Find(Label + Tp2Suffix, SymbolName) != null;
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;
            if (position.SymbolName != SymbolName)
                return;

            var isTp1 = position.Label == Label + Tp1Suffix;
            var isTp2 = position.Label == Label + Tp2Suffix;
            if (!isTp1 && !isTp2)
                return;

            Print("Position fermee ({0}, {1}). Entree: {2}, Net: {3:0.00} {4} ({5:0.0} pips)",
                isTp1 ? "TP1" : "TP2", position.TradeType, position.EntryPrice, position.NetProfit, Account.Asset.Name, position.Pips);

            // _openLegsInRound == 0 ici signifie que cette position n'appartient pas
            // a un round correctement ouvert (ex: jambe orpheline refermee juste
            // apres un echec partiel dans TryEnter) : on l'ignore pour le suivi des
            // pertes consecutives et le break-even.
            if (_openLegsInRound <= 0)
                return;

            _roundNetProfit += position.NetProfit;
            _openLegsInRound--;

            if (_openLegsInRound <= 0)
            {
                if (_roundNetProfit < 0)
                    _consecutiveLosses++;
                else
                    _consecutiveLosses = 0;

                Print("Round termine. PnL combine: {0:0.00} {1}. Pertes consecutives: {2}/{3}",
                    _roundNetProfit, Account.Asset.Name, _consecutiveLosses, MaxConsecutiveLosses);

                _roundNetProfit = 0;
                _singleLegBreakEvenTrigger = double.NaN;
            }

            if (isTp1 && UseBreakEven && position.NetProfit > 0)
            {
                var remaining = Positions.Find(Label + Tp2Suffix, SymbolName);
                if (remaining != null)
                    ModifyPosition(remaining, remaining.EntryPrice, remaining.TakeProfit);
            }
        }

        private void GetHtfTrend(out bool htf1Bull, out bool htf1Bear, out bool htf2Bull, out bool htf2Bear)
        {
            htf1Bull = htf1Bear = htf2Bull = htf2Bear = false;

            if (UseHtf1Filter && _htf1Ema20.Result.Count > 0 && _htf1Ema50.Result.Count > 0)
            {
                var e20 = _htf1Ema20.Result.LastValue;
                var e50 = _htf1Ema50.Result.LastValue;
                htf1Bull = e20 > e50;
                htf1Bear = e20 < e50;
            }

            if (UseHtf2Filter && _htf2Ema20.Result.Count > 0 && _htf2Ema50.Result.Count > 0)
            {
                var e20 = _htf2Ema20.Result.LastValue;
                var e50 = _htf2Ema50.Result.LastValue;
                htf2Bull = e20 > e50;
                htf2Bear = e20 < e50;
            }
        }

        private void UpdateDailyLossState(DateTime barTimeUtc)
        {
            var day = barTimeUtc.Date;
            if (day != _currentDay)
            {
                _currentDay = day;
                _dayStartBalance = Account.Balance;
                _dailyLossLimitHit = false;
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

        private bool IsSessionOk(DateTime barTimeUtc)
        {
            var hour = barTimeUtc.Hour;
            var inLondon = hour >= 8 && hour < 17;
            var inNy = hour >= 13 && hour < 22;

            if (!UseSessionFilter)
                return true;

            var sessOk = (UseLondonSession && inLondon) || (UseNySession && inNy);

            // Fenetres a eviter (reprises telles quelles du script Pine d'origine) :
            // 08h00-08h30 UTC (ouverture Londres) et 13h30-14h00 UTC (fenetre
            // habituelle des publications macro US type NFP/CPI, 8h30 ET) - pas
            // 13h00 UTC (ouverture NY), qui n'a volontairement pas de fenetre dediee.
            var minuteOfDay = hour * 60 + barTimeUtc.Minute;
            var openAvoid = AvoidSessionOpen &&
                ((minuteOfDay >= 480 && minuteOfDay < 510) ||
                 (minuteOfDay >= 810 && minuteOfDay < 840));

            return sessOk && !openAvoid;
        }

        private bool TryCalculateVolumeProfile()
        {
            if (Bars.ClosePrices.Count <= VpBars + 1)
                return false;

            var top = double.MinValue;
            var bot = double.MaxValue;

            for (var i = 1; i <= VpBars; i++)
            {
                top = Math.Max(top, Bars.HighPrices.Last(i));
                bot = Math.Min(bot, Bars.LowPrices.Last(i));
            }

            if (top <= bot)
                return false;

            var step = (top - bot) / VpRows;
            var levels = new double[VpRows + 1];
            for (var x = 0; x <= VpRows; x++)
                levels[x] = bot + step * x;

            var volumesUp = new double[VpRows];
            var volumesDown = new double[VpRows];

            for (var i = 1; i <= VpBars; i++)
            {
                var o = Bars.OpenPrices.Last(i);
                var c = Bars.ClosePrices.Last(i);
                var h = Bars.HighPrices.Last(i);
                var l = Bars.LowPrices.Last(i);
                var v = Bars.TickVolumes.Last(i);

                var bt = Math.Max(c, o);
                var bb = Math.Min(c, o);
                var green = c >= o;
                var tw = h - bt;
                var bw = bb - l;
                var bd = bt - bb;
                var den = 2 * tw + 2 * bw + bd;

                if (den <= 0)
                    continue;

                var bodyVolume = bd * v / den;
                var topWickVolume = 2 * tw * v / den;
                var bottomWickVolume = 2 * bw * v / den;

                for (var x = 0; x < VpRows; x++)
                {
                    var lx = levels[x];
                    var lx1 = levels[x + 1];

                    var bodyVol = GetVol(lx, lx1, bb, bt, bd, bodyVolume);
                    var upperWickVol = GetVol(lx, lx1, bt, h, tw, topWickVolume) / 2;
                    var lowerWickVol = GetVol(lx, lx1, bb, l, bw, bottomWickVolume) / 2;

                    volumesUp[x] += (green ? bodyVol : 0) + upperWickVol + lowerWickVol;
                    volumesDown[x] += (green ? 0 : bodyVol) + upperWickVol + lowerWickVol;
                }
            }

            var totalVols = new double[VpRows];
            var totalSum = 0.0;
            for (var x = 0; x < VpRows; x++)
            {
                totalVols[x] = volumesUp[x] + volumesDown[x];
                totalSum += totalVols[x];
            }

            if (totalSum <= 0)
                return false;

            var pocIdx = 0;
            var maxVol = totalVols[0];
            for (var x = 1; x < VpRows; x++)
            {
                if (totalVols[x] > maxVol)
                {
                    maxVol = totalVols[x];
                    pocIdx = x;
                }
            }

            var target = totalSum * VpValueAreaPercent / 100.0;
            var vaTotal = totalVols[pocIdx];
            var upIdx = pocIdx;
            var dnIdx = pocIdx;

            for (var x = 0; x < VpRows; x++)
            {
                if (vaTotal >= target)
                    break;

                var uv = upIdx < VpRows - 1 ? totalVols[upIdx + 1] : 0.0;
                var lv = dnIdx > 0 ? totalVols[dnIdx - 1] : 0.0;

                if (uv == 0.0 && lv == 0.0)
                    break;

                if (uv >= lv)
                {
                    vaTotal += uv;
                    upIdx++;
                }
                else
                {
                    vaTotal += lv;
                    dnIdx--;
                }
            }

            _poc = (levels[pocIdx] + levels[pocIdx + 1]) / 2.0;
            _vah = levels[upIdx + 1];
            _val = levels[dnIdx];

            return true;
        }

        private static double GetVol(double y11, double y12, double y21, double y22, double h, double vol)
        {
            if (h <= 0)
                return 0;

            var overlap = Math.Max(Math.Min(Math.Max(y11, y12), Math.Max(y21, y22)) - Math.Max(Math.Min(y11, y12), Math.Min(y21, y22)), 0);
            return overlap * vol / h;
        }

        private void DrawLevels()
        {
            if (Chart == null)
                return;

            Chart.DrawHorizontalLine("vp_poc", _poc, Color.Red, 2, LineStyle.Solid);
            Chart.DrawHorizontalLine("vp_vah", _vah, Color.LimeGreen, 1, LineStyle.Dots);
            Chart.DrawHorizontalLine("vp_val", _val, Color.OrangeRed, 1, LineStyle.Dots);

            var robotOn = _consecutiveLosses < MaxConsecutiveLosses && !_dailyLossLimitHit;
            var statusColor = robotOn ? Color.LimeGreen : Color.Red;
            var status = robotOn ? "ACTIF" : "PAUSE";

            var text = string.Format(
                "VP Robot MTF - {0}\nPOC: {1:0.####} | VAH: {2:0.####} | VAL: {3:0.####}\nPertes consecutives: {4}/{5}{6}",
                status, _poc, _vah, _val, _consecutiveLosses, MaxConsecutiveLosses,
                _dailyLossLimitHit ? "\nLimite perte journaliere atteinte" : "");

            Chart.DrawStaticText("vp_status", text, VerticalAlignment.Top, HorizontalAlignment.Right, statusColor);
        }
    }
}

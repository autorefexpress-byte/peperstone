using System;
using System.Collections.Generic;
using System.Globalization;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // ============================================================================
    // IctSmcIchimokuBot
    // ----------------------------------------------------------------------------
    // cBot cTrader combinant des concepts Smart Money Concepts / ICT (liquidite,
    // structure de marche, order blocks, Fair Value Gaps) avec un filtre de biais
    // Ichimoku, actif uniquement sur les creneaux horaires Londres et New York.
    //
    // IMPORTANT - "SMC/ICT" n'a pas de definition canonique unique (contrairement
    // a une simple croisee de moyennes mobiles) : chaque trader/formateur a ses
    // propres variantes de regles. Ce bot fait des choix d'interpretation precis
    // et documentes ci-dessous. C'est une base de travail plus experimentale que
    // GoldTrendBot ou VolumeProfileMtfBot : testez-la BEAUCOUP plus longtemps en
    // backtest et en demo avant meme d'envisager un compte reel.
    //
    // Logique generale (par bougie cloturee) :
    //   1. Detection de structure : points pivots (swing high/low) confirmes par
    //      un nombre de bougies de part et d'autre (fractal simple).
    //   2. Liquidity sweep : une bougie meche au-dela d'un swing recent puis
    //      cloture a l'interieur (chasse aux stops classique ICT).
    //   3. Change of Character / Break of Structure : apres un sweep, la cloture
    //      franchit le swing oppose le plus recent -> confirme un retournement.
    //   4. Order Block : la derniere bougie de sens oppose juste avant l'impulsion
    //      qui a cause le BOS.
    //   5. Fair Value Gap : premier gap a 3 bougies (imbalance) trouve entre le
    //      sweep et le BOS.
    //   6. Zone d'entree : Order Block et FVG sont fusionnes en une seule zone de
    //      surveillance (simplification assumee - dans la theorie ICT ce sont deux
    //      zones distinctes avec des priorites differentes).
    //   7. Entree : quand le prix revient dans la zone et qu'une bougie de rejet
    //      se forme dans le sens du setup, ET que le biais Ichimoku est aligne, ET
    //      qu'on est dans un creneau horaire actif (Londres/New York) -> entree.
    //   8. Stop loss au-dela de la zone (+ buffer), take profit a un multiple R du
    //      risque, break-even optionnel a 1R.
    //
    // Simplifications/choix assumes :
    //   - Le cloud Ichimoku (Senkou Span A/B) est recalcule manuellement a partir
    //     des prix bruts, plutot que via l'indicateur Ichimoku natif cAlgo, pour
    //     controler precisement le decalage vers l'avant (Kijun periods) et
    //     eviter toute ambiguite sur la convention interne de l'indicateur natif.
    //     A verifier visuellement sur un graphique avec l'indicateur Ichimoku
    //     cTrader avant usage reel.
    //   - Par defaut, ce nuage Ichimoku est calcule sur un timeframe SUPERIEUR
    //     (4H par defaut, via MarketData.GetBars) a celui du graphique d'execution
    //     (structure/entrees ICT), pattern classique "biais HTF + entrees LTF".
    //     Desactivable (UseHtfIchimoku=false) pour tout calculer sur le meme chart.
    //   - Un seul setup (Buy) et un seul setup (Sell) sont suivis a la fois.
    //   - Une zone OB/FVG n'est valable que pour un seul contact par defaut
    //     (FirstTouchOnly=true) : si le prix la touche sans bougie de rejet
    //     valide, elle est abandonnee plutot que de rester active pour un 2e/3e
    //     retest (un order block se "mitige" a chaque retest en theorie ICT).
    //   - Les creneaux horaires par defaut (Londres 07h-10h UTC, New York 12h-15h
    //     UTC) sont les "killzones" ICT classiques, plus etroites que les sessions
    //     de trading generales Londres/New York (08h-17h / 13h-22h UTC) utilisees
    //     dans VolumeProfileMtfBot. Reglables via les parametres. Pas d'ajustement
    //     automatique pour le changement d'heure ete/hiver (UTC fixe).
    //   - La detection de structure/sweep/BOS tourne en continu (24h/24), seule
    //     l'ENTREE est restreinte aux creneaux horaires actifs.
    // ============================================================================
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class IctSmcIchimokuBot : Robot
    {
        // -- Ichimoku (biais de tendance) --
        [Parameter("Tenkan-sen (periode)", Group = "Ichimoku", DefaultValue = 9, MinValue = 2)]
        public int TenkanPeriod { get; set; }

        [Parameter("Kijun-sen (periode)", Group = "Ichimoku", DefaultValue = 26, MinValue = 2)]
        public int KijunPeriod { get; set; }

        [Parameter("Senkou Span B (periode)", Group = "Ichimoku", DefaultValue = 52, MinValue = 2)]
        public int SenkouSpanBPeriod { get; set; }

        [Parameter("Exiger croisement Tenkan/Kijun", Group = "Ichimoku", DefaultValue = false,
            Description = "Filtre supplementaire plus strict : exige Tenkan > Kijun (achat) ou Tenkan < Kijun (vente) en plus de la position par rapport au nuage.")]
        public bool RequireTenkanKijunCross { get; set; }

        [Parameter("Ichimoku sur timeframe superieur", Group = "Ichimoku", DefaultValue = true,
            Description = "Calcule le biais Ichimoku sur un timeframe superieur a celui du graphique d'execution (pattern ICT classique : structure/entrees sur un timeframe bas, biais de tendance sur un timeframe haut).")]
        public bool UseHtfIchimoku { get; set; }

        [Parameter("Timeframe Ichimoku", Group = "Ichimoku", DefaultValue = "Hour4")]
        public TimeFrame IchimokuTimeFrame { get; set; }

        // -- Structure (SMC/ICT) --
        [Parameter("Lookback structure (swing)", Group = "Structure", DefaultValue = 3, MinValue = 1, MaxValue = 10,
            Description = "Nombre de bougies de part et d'autre requises pour confirmer un swing high/low.")]
        public int SwingLookback { get; set; }

        [Parameter("Fenetre de validite (barres)", Group = "Structure", DefaultValue = 15, MinValue = 3, MaxValue = 100,
            Description = "Nombre de barres pendant lesquelles un sweep en attente de BOS, ou une zone en attente de retracement, reste valide avant expiration.")]
        public int EntryWindowBars { get; set; }

        [Parameter("Entree au premier contact uniquement", Group = "Structure", DefaultValue = true,
            Description = "Un order block/FVG se 'mitige' a chaque retest en theorie ICT. Si active, la zone est abandonnee des le premier contact sans bougie de rejet valide, plutot que de rester active pour un 2e/3e retest.")]
        public bool FirstTouchOnly { get; set; }

        // -- Gestion du risque --
        [Parameter("Lot fixe (0 = calcul au risque)", Group = "Risk Management", DefaultValue = 0.0, MinValue = 0.0, Step = 0.01,
            Description = "Si > 0, trade toujours ce nombre de lots au lieu du calcul au risque. Le risque reel est affiche dans le log a chaque entree.")]
        public double FixedLots { get; set; }

        [Parameter("Risque par trade (%)", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 10)]
        public double RiskPercent { get; set; }

        [Parameter("Risk:Reward (R)", Group = "Risk Management", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10)]
        public double RiskRewardRatio { get; set; }

        [Parameter("Buffer Stop Loss (pips)", Group = "Risk Management", DefaultValue = 20, MinValue = 0,
            Description = "Marge au-dela de la zone pour le stop loss. Utilise seulement si le mode ATR est desactive.")]
        public double StopLossBufferPips { get; set; }

        // -- Stops en multiples d'ATR --
        [Parameter("Stops bases sur l'ATR", Group = "Stops ATR", DefaultValue = true,
            Description = "Si active, le buffer du stop loss est un multiple de l'ATR du graphique (au lieu d'un nombre de pips fixe) et les zones qui donneraient un stop trop large sont abandonnees.")]
        public bool UseAtrStops { get; set; }

        [Parameter("Periode ATR", Group = "Stops ATR", DefaultValue = 14, MinValue = 2, MaxValue = 200)]
        public int AtrPeriod { get; set; }

        [Parameter("Buffer SL (x ATR)", Group = "Stops ATR", DefaultValue = 0.3, MinValue = 0, MaxValue = 5, Step = 0.05,
            Description = "Marge ajoutee au-dela de la zone OB/FVG pour le stop loss, en multiple de l'ATR.")]
        public double StopLossBufferAtr { get; set; }

        [Parameter("SL max (x ATR)", Group = "Stops ATR", DefaultValue = 4.0, MinValue = 0, MaxValue = 50, Step = 0.5,
            Description = "Abandonne la zone si le stop (entree -> au-dela de la zone) depasse ce multiple de l'ATR : zone trop large pour le timeframe. 0 = desactive.")]
        public double MaxStopLossAtr { get; set; }

        [Parameter("SL minimum (pips)", Group = "Stops ATR", DefaultValue = 8.0, MinValue = 0, Step = 0.5,
            Description = "Plancher du stop loss (tous modes) : un stop de 3 pips sur une petite zone donne un gros volume, a la merci du spread et du moindre glissement (news). Le TP (R x stop) suit.")]
        public double MinStopLossPips { get; set; }

        [Parameter("Autoriser volume minimum (petit compte)", Group = "Risk Management", DefaultValue = true,
            Description = "Sur un petit compte, le volume calcule au risque peut etre inferieur au minimum du broker (0,01 lot). Si active, trade le volume minimum a la place, mais seulement si son risque reel reste sous 'Risque max au volume minimum (%)'.")]
        public bool AllowMinVolumeFallback { get; set; }

        [Parameter("Risque max au volume minimum (%)", Group = "Risk Management", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 10,
            Description = "Plafond du % reel du capital risque quand le volume minimum est utilise. Les entrees dont le stop risquerait plus sont ignorees.")]
        public double MaxRiskAtMinVolumePercent { get; set; }

        [Parameter("Break-even a 1R", Group = "Risk Management", DefaultValue = true)]
        public bool UseBreakEvenAt1R { get; set; }

        [Parameter("Max pertes consecutives", Group = "Risk Management", DefaultValue = 3, MinValue = 1, MaxValue = 10)]
        public int MaxConsecutiveLosses { get; set; }

        [Parameter("Activer limite de perte journaliere", Group = "Risk Management", DefaultValue = true)]
        public bool UseDailyLossLimit { get; set; }

        [Parameter("Perte journaliere max (%)", Group = "Risk Management", DefaultValue = 5.0, MinValue = 0.5, MaxValue = 50.0, Step = 0.5)]
        public double MaxDailyLossPercent { get; set; }

        // -- Sorties par le temps --
        [Parameter("Fermer en fin de journee", Group = "Sortie", DefaultValue = true,
            Description = "Ferme la position a l'heure ci-dessous et bloque les nouvelles entrees apres cette heure : pas de position la nuit ni le week-end (swap, gaps).")]
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

        // -- Filtre news --
        [Parameter("Filtre news", Group = "News", DefaultValue = true,
            Description = "Bloque les nouvelles entrees autour des heures d'annonces US (glissements et spreads extremes). Les positions deja ouvertes ne sont pas touchees.")]
        public bool UseNewsFilter { get; set; }

        [Parameter("Heures news (heure de New York)", Group = "News", DefaultValue = "08:30",
            Description = "Heures des annonces en heure de New York, separees par des virgules (ex. 08:30,10:00). Converties en UTC avec le changement d'heure americain : 08:30 NY = 12:30 UTC en ete, 13:30 UTC en hiver.")]
        public string NewsTimesNewYork { get; set; }

        [Parameter("Minutes avant l'annonce", Group = "News", DefaultValue = 15, MinValue = 0, MaxValue = 240)]
        public int NewsMinutesBefore { get; set; }

        [Parameter("Minutes apres l'annonce", Group = "News", DefaultValue = 15, MinValue = 0, MaxValue = 240)]
        public int NewsMinutesAfter { get; set; }

        // -- Securite --
        [Parameter("Max Spread (pips)", Group = "Safety", DefaultValue = 50, MinValue = 0)]
        public double MaxSpreadPips { get; set; }

        // -- Creneaux horaires (killzones ICT) --
        [Parameter("Activer creneau Londres", Group = "Killzones", DefaultValue = true)]
        public bool UseLondonKillzone { get; set; }

        [Parameter("Londres - heure debut (UTC)", Group = "Killzones", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int LondonKzStartHour { get; set; }

        [Parameter("Londres - heure fin (UTC)", Group = "Killzones", DefaultValue = 10, MinValue = 0, MaxValue = 23)]
        public int LondonKzEndHour { get; set; }

        [Parameter("Activer creneau New York", Group = "Killzones", DefaultValue = true)]
        public bool UseNyKillzone { get; set; }

        [Parameter("New York - heure debut (UTC)", Group = "Killzones", DefaultValue = 12, MinValue = 0, MaxValue = 23)]
        public int NyKzStartHour { get; set; }

        [Parameter("New York - heure fin (UTC)", Group = "Killzones", DefaultValue = 15, MinValue = 0, MaxValue = 23)]
        public int NyKzEndHour { get; set; }

        // -- Divers --
        [Parameter("Label", Group = "Misc", DefaultValue = "IctSmcIchimoku")]
        public string Label { get; set; }

        private struct SwingPoint
        {
            public double Price;
            public long BarIndex;
        }

        private struct SweepEvent
        {
            public bool Active;
            public double ExtremePrice;
            public long BarIndex;
            public long ExpiryBarIndex;
        }

        private struct PendingSetup
        {
            public bool Active;
            public double ZoneLow;
            public double ZoneHigh;
            public double InvalidationPrice;
            public long ExpiryBarIndex;
            public DateTime FormedAt;
        }

        private readonly List<SwingPoint> _swingHighs = new List<SwingPoint>();
        private readonly List<SwingPoint> _swingLows = new List<SwingPoint>();

        private SweepEvent _pendingSweepHigh;
        private SweepEvent _pendingSweepLow;
        private PendingSetup _pendingBuySetup;
        private PendingSetup _pendingSellSetup;

        private long _barCounter = -1;
        private bool _breakEvenDone;
        private int _consecutiveLosses;

        private DateTime _currentDay = DateTime.MinValue;
        private double _dayStartBalance;
        private bool _dailyLossLimitHit;

        private Bars _ichimokuBars;
        private AverageTrueRange _atr;
        private readonly List<TimeSpan> _newsTimesNy = new List<TimeSpan>();

        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(Bars, AtrPeriod, MovingAverageType.Simple);
            ParseNewsTimes();
            _ichimokuBars = UseHtfIchimoku && IchimokuTimeFrame != TimeFrame ? MarketData.GetBars(IchimokuTimeFrame, SymbolName) : Bars;

            Positions.Closed += OnPositionClosed;
            Print("IctSmcIchimokuBot started on {0} {1} (Ichimoku sur {2})", SymbolName, TimeFrame, _ichimokuBars.TimeFrame);
            Print("Symbole: PipSize {0}, PipValue {1}, TickSize {2}, TickValue {3}, valeur pip/unite utilisee {4}, volume min {5} unites.",
                Symbol.PipSize, Symbol.PipValue, Symbol.TickSize, Symbol.TickValue, PipValuePerUnit(), Symbol.VolumeInUnitsMin);
        }

        protected override void OnTick()
        {
            ManageTimeExits();
        }

        protected override void OnBarClosed()
        {
            _barCounter++;

            ManageBreakEven();
            UpdateSwingPoints();

            var minBars = SwingLookback * 2 + 2;
            if (Bars.ClosePrices.Count < minBars)
                return;

            var barTimeUtc = Bars.OpenTimes.Last(1);
            UpdateDailyLossState(barTimeUtc);

            DetectBullishSweep();
            CheckBullishBos();
            DetectBearishSweep();
            CheckBearishBos();

            DrawDebugInfo();

            if (HasOpenPosition())
                return;

            if (!IsKillzone(barTimeUtc))
                return;

            if (UseDailyClose && Server.Time.Hour >= CloseHourFor(Server.Time) - NoEntryHoursBeforeClose)
                return;

            if (_consecutiveLosses >= MaxConsecutiveLosses || _dailyLossLimitHit)
                return;

            if (Symbol.Spread / Symbol.PipSize > MaxSpreadPips)
                return;

            if (IsNewsWindow(Server.Time))
                return;

            TryTriggerEntry();
        }

        // -- Structure : swing points --------------------------------------

        private void UpdateSwingPoints()
        {
            if (TryDetectSwingHigh(out var highPrice))
            {
                _swingHighs.Add(new SwingPoint { Price = highPrice, BarIndex = _barCounter - SwingLookback });
                TrimList(_swingHighs);
            }

            if (TryDetectSwingLow(out var lowPrice))
            {
                _swingLows.Add(new SwingPoint { Price = lowPrice, BarIndex = _barCounter - SwingLookback });
                TrimList(_swingLows);
            }
        }

        private bool TryDetectSwingHigh(out double price)
        {
            price = 0;
            var required = SwingLookback * 2 + 1;
            if (Bars.HighPrices.Count <= required)
                return false;

            var candidateShift = SwingLookback + 1;
            var candidate = Bars.HighPrices.Last(candidateShift);

            for (var i = 1; i <= SwingLookback; i++)
            {
                if (Bars.HighPrices.Last(i) >= candidate)
                    return false;

                if (Bars.HighPrices.Last(candidateShift + i) >= candidate)
                    return false;
            }

            price = candidate;
            return true;
        }

        private bool TryDetectSwingLow(out double price)
        {
            price = 0;
            var required = SwingLookback * 2 + 1;
            if (Bars.LowPrices.Count <= required)
                return false;

            var candidateShift = SwingLookback + 1;
            var candidate = Bars.LowPrices.Last(candidateShift);

            for (var i = 1; i <= SwingLookback; i++)
            {
                if (Bars.LowPrices.Last(i) <= candidate)
                    return false;

                if (Bars.LowPrices.Last(candidateShift + i) <= candidate)
                    return false;
            }

            price = candidate;
            return true;
        }

        private static void TrimList(List<SwingPoint> list)
        {
            const int maxSize = 20;
            if (list.Count > maxSize)
                list.RemoveRange(0, list.Count - maxSize);
        }

        private int ShiftFor(long absoluteIndex)
        {
            return (int)(_barCounter - absoluteIndex + 1);
        }

        // -- Liquidity sweep + Change of Character / Break of Structure ----

        private void DetectBullishSweep()
        {
            if (_pendingSweepLow.Active || _pendingBuySetup.Active || _swingLows.Count == 0)
                return;

            var lastSwingLow = _swingLows[_swingLows.Count - 1];

            var low = Bars.LowPrices.Last(1);
            var close = Bars.ClosePrices.Last(1);

            if (low < lastSwingLow.Price && close > lastSwingLow.Price)
            {
                _pendingSweepLow = new SweepEvent
                {
                    Active = true,
                    ExtremePrice = low,
                    BarIndex = _barCounter,
                    ExpiryBarIndex = _barCounter + EntryWindowBars
                };
            }
        }

        private void CheckBullishBos()
        {
            if (!_pendingSweepLow.Active)
                return;

            if (_barCounter > _pendingSweepLow.ExpiryBarIndex)
            {
                _pendingSweepLow.Active = false;
                return;
            }

            // Reference relue a chaque bougie (plutot que figee au moment du sweep)
            // pour reagir au swing high le plus proche/pertinent forme entre-temps.
            if (_swingHighs.Count == 0)
                return;

            var referenceSwingHigh = _swingHighs[_swingHighs.Count - 1].Price;
            var close = Bars.ClosePrices.Last(1);
            if (close > referenceSwingHigh)
            {
                BuildBullishSetup(_pendingSweepLow.BarIndex, _barCounter, _pendingSweepLow.ExtremePrice);
                _pendingSweepLow.Active = false;
            }
        }

        private void DetectBearishSweep()
        {
            if (_pendingSweepHigh.Active || _pendingSellSetup.Active || _swingHighs.Count == 0)
                return;

            var lastSwingHigh = _swingHighs[_swingHighs.Count - 1];

            var high = Bars.HighPrices.Last(1);
            var close = Bars.ClosePrices.Last(1);

            if (high > lastSwingHigh.Price && close < lastSwingHigh.Price)
            {
                _pendingSweepHigh = new SweepEvent
                {
                    Active = true,
                    ExtremePrice = high,
                    BarIndex = _barCounter,
                    ExpiryBarIndex = _barCounter + EntryWindowBars
                };
            }
        }

        private void CheckBearishBos()
        {
            if (!_pendingSweepHigh.Active)
                return;

            if (_barCounter > _pendingSweepHigh.ExpiryBarIndex)
            {
                _pendingSweepHigh.Active = false;
                return;
            }

            if (_swingLows.Count == 0)
                return;

            var referenceSwingLow = _swingLows[_swingLows.Count - 1].Price;
            var close = Bars.ClosePrices.Last(1);
            if (close < referenceSwingLow)
            {
                BuildBearishSetup(_pendingSweepHigh.BarIndex, _barCounter, _pendingSweepHigh.ExtremePrice);
                _pendingSweepHigh.Active = false;
            }
        }

        // -- Order Block + Fair Value Gap -> zone d'entree ------------------

        private void BuildBullishSetup(long sweepBarIndex, long bosBarIndex, double sweepExtreme)
        {
            long obIndex = -1;
            for (var idx = bosBarIndex; idx >= sweepBarIndex; idx--)
            {
                var shift = ShiftFor(idx);
                if (shift < 1) continue;

                if (Bars.ClosePrices.Last(shift) < Bars.OpenPrices.Last(shift))
                {
                    obIndex = idx;
                    break;
                }
            }

            // Si aucune bougie baissiere n'est trouvee dans la jambe impulsive (ex:
            // retournement en "V" sur une seule bougie, ou impulsion entierement
            // haussiere), on se replie sur la bougie de sweep elle-meme : c'est le
            // point le plus extreme du mouvement, une reference raisonnable meme
            // si elle n'est pas de couleur opposee, plutot que d'abandonner le setup.
            var obReferenceIndex = obIndex >= 0 ? obIndex : sweepBarIndex;
            var obShift = ShiftFor(obReferenceIndex);
            var obLow = Bars.LowPrices.Last(obShift);
            var obHigh = Bars.HighPrices.Last(obShift);

            double? fvgLow = null, fvgHigh = null;
            for (var idx = sweepBarIndex; idx <= bosBarIndex - 2; idx++)
            {
                var s1 = ShiftFor(idx);
                var s3 = ShiftFor(idx + 2);
                if (s3 < 1) continue;

                var c1High = Bars.HighPrices.Last(s1);
                var c3Low = Bars.LowPrices.Last(s3);
                if (c1High < c3Low)
                {
                    fvgLow = c1High;
                    fvgHigh = c3Low;
                    break;
                }
            }

            _pendingBuySetup = new PendingSetup
            {
                Active = true,
                ZoneLow = Math.Min(obLow, fvgLow ?? obLow),
                ZoneHigh = Math.Max(obHigh, fvgHigh ?? obHigh),
                InvalidationPrice = sweepExtreme,
                ExpiryBarIndex = _barCounter + EntryWindowBars,
                FormedAt = Bars.OpenTimes.Last(1)
            };
        }

        private void BuildBearishSetup(long sweepBarIndex, long bosBarIndex, double sweepExtreme)
        {
            long obIndex = -1;
            for (var idx = bosBarIndex; idx >= sweepBarIndex; idx--)
            {
                var shift = ShiftFor(idx);
                if (shift < 1) continue;

                if (Bars.ClosePrices.Last(shift) > Bars.OpenPrices.Last(shift))
                {
                    obIndex = idx;
                    break;
                }
            }

            var obReferenceIndex = obIndex >= 0 ? obIndex : sweepBarIndex;
            var obShift = ShiftFor(obReferenceIndex);
            var obLow = Bars.LowPrices.Last(obShift);
            var obHigh = Bars.HighPrices.Last(obShift);

            double? fvgLow = null, fvgHigh = null;
            for (var idx = sweepBarIndex; idx <= bosBarIndex - 2; idx++)
            {
                var s1 = ShiftFor(idx);
                var s3 = ShiftFor(idx + 2);
                if (s3 < 1) continue;

                var c1Low = Bars.LowPrices.Last(s1);
                var c3High = Bars.HighPrices.Last(s3);
                if (c1Low > c3High)
                {
                    fvgLow = c3High;
                    fvgHigh = c1Low;
                    break;
                }
            }

            _pendingSellSetup = new PendingSetup
            {
                Active = true,
                ZoneLow = Math.Min(obLow, fvgLow ?? obLow),
                ZoneHigh = Math.Max(obHigh, fvgHigh ?? obHigh),
                InvalidationPrice = sweepExtreme,
                ExpiryBarIndex = _barCounter + EntryWindowBars,
                FormedAt = Bars.OpenTimes.Last(1)
            };
        }

        // -- Biais Ichimoku (calcule manuellement, voir commentaire d'en-tete) --

        private bool GetIchimokuBias(out bool bullishBias, out bool bearishBias)
        {
            bullishBias = bearishBias = false;

            var required = Math.Max(SenkouSpanBPeriod, KijunPeriod) + KijunPeriod + 1;
            if (_ichimokuBars.ClosePrices.Count < required)
                return false;

            var tenkanAtShift = (HighestHigh(_ichimokuBars, TenkanPeriod, KijunPeriod + 1) + LowestLow(_ichimokuBars, TenkanPeriod, KijunPeriod + 1)) / 2.0;
            var kijunAtShift = (HighestHigh(_ichimokuBars, KijunPeriod, KijunPeriod + 1) + LowestLow(_ichimokuBars, KijunPeriod, KijunPeriod + 1)) / 2.0;
            var senkouA = (tenkanAtShift + kijunAtShift) / 2.0;
            var senkouB = (HighestHigh(_ichimokuBars, SenkouSpanBPeriod, KijunPeriod + 1) + LowestLow(_ichimokuBars, SenkouSpanBPeriod, KijunPeriod + 1)) / 2.0;

            var cloudTop = Math.Max(senkouA, senkouB);
            var cloudBottom = Math.Min(senkouA, senkouB);
            // Prix du graphique d'execution compare au nuage calcule sur le
            // timeframe Ichimoku (potentiellement superieur, voir UseHtfIchimoku).
            var close = Bars.ClosePrices.Last(1);

            bullishBias = close > cloudTop;
            bearishBias = close < cloudBottom;

            if (RequireTenkanKijunCross)
            {
                var tenkanNow = (HighestHigh(_ichimokuBars, TenkanPeriod) + LowestLow(_ichimokuBars, TenkanPeriod)) / 2.0;
                var kijunNow = (HighestHigh(_ichimokuBars, KijunPeriod) + LowestLow(_ichimokuBars, KijunPeriod)) / 2.0;
                bullishBias &= tenkanNow > kijunNow;
                bearishBias &= tenkanNow < kijunNow;
            }

            return true;
        }

        private static double HighestHigh(Bars source, int period, int shift = 1)
        {
            var max = double.MinValue;
            for (var i = shift; i < shift + period; i++)
                max = Math.Max(max, source.HighPrices.Last(i));
            return max;
        }

        private static double LowestLow(Bars source, int period, int shift = 1)
        {
            var min = double.MaxValue;
            for (var i = shift; i < shift + period; i++)
                min = Math.Min(min, source.LowPrices.Last(i));
            return min;
        }

        // -- Declenchement d'entree ------------------------------------------

        private void TryTriggerEntry()
        {
            if (!GetIchimokuBias(out var bullishBias, out var bearishBias))
                return;

            if (_pendingBuySetup.Active)
            {
                if (_barCounter > _pendingBuySetup.ExpiryBarIndex)
                {
                    _pendingBuySetup.Active = false;
                }
                else
                {
                    var close = Bars.ClosePrices.Last(1);
                    if (close < _pendingBuySetup.InvalidationPrice)
                    {
                        _pendingBuySetup.Active = false;
                    }
                    else
                    {
                        var low = Bars.LowPrices.Last(1);
                        var high = Bars.HighPrices.Last(1);
                        var open = Bars.OpenPrices.Last(1);
                        var overlap = low <= _pendingBuySetup.ZoneHigh && high >= _pendingBuySetup.ZoneLow;

                        if (overlap)
                        {
                            var validRejection = close > open;

                            if (validRejection && bullishBias)
                            {
                                // Ne consomme la zone que si l'ordre part reellement -
                                // un echec technique la laisse active pour retenter.
                                if (IsStopTooWide(TradeType.Buy, _pendingBuySetup.ZoneLow))
                                {
                                    _pendingBuySetup.Active = false;
                                    return;
                                }

                                if (ExecuteEntry(TradeType.Buy, _pendingBuySetup.ZoneLow, "Retracement OB/FVG haussier"))
                                    _pendingBuySetup.Active = false;
                                return;
                            }

                            // Contact sans bougie de rejet valide : zone "mitigee",
                            // on l'abandonne plutot que d'attendre un 2e/3e retest.
                            // Une bougie de rejet correcte mais un biais Ichimoku pas
                            // encore aligne laisse la zone active (ce n'est pas la
                            // reaction de prix qui est en cause).
                            if (FirstTouchOnly && !validRejection)
                                _pendingBuySetup.Active = false;
                        }
                    }
                }
            }

            if (_pendingSellSetup.Active)
            {
                if (_barCounter > _pendingSellSetup.ExpiryBarIndex)
                {
                    _pendingSellSetup.Active = false;
                }
                else
                {
                    var close = Bars.ClosePrices.Last(1);
                    if (close > _pendingSellSetup.InvalidationPrice)
                    {
                        _pendingSellSetup.Active = false;
                    }
                    else
                    {
                        var low = Bars.LowPrices.Last(1);
                        var high = Bars.HighPrices.Last(1);
                        var open = Bars.OpenPrices.Last(1);
                        var overlap = low <= _pendingSellSetup.ZoneHigh && high >= _pendingSellSetup.ZoneLow;

                        if (overlap)
                        {
                            var validRejection = close < open;

                            if (validRejection && bearishBias)
                            {
                                if (IsStopTooWide(TradeType.Sell, _pendingSellSetup.ZoneHigh))
                                {
                                    _pendingSellSetup.Active = false;
                                    return;
                                }

                                if (ExecuteEntry(TradeType.Sell, _pendingSellSetup.ZoneHigh, "Retracement OB/FVG baissier"))
                                    _pendingSellSetup.Active = false;
                            }
                            else if (FirstTouchOnly && !validRejection)
                            {
                                _pendingSellSetup.Active = false;
                            }
                        }
                    }
                }
            }
        }

        private double GetStopLossPips(TradeType tradeType, double zoneExtreme)
        {
            var slBufferPrice = UseAtrStops ? _atr.Result.Last(1) * StopLossBufferAtr : StopLossBufferPips * Symbol.PipSize;
            var entryPrice = tradeType == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            var stopLossPrice = tradeType == TradeType.Buy ? zoneExtreme - slBufferPrice : zoneExtreme + slBufferPrice;

            return Math.Abs(entryPrice - stopLossPrice) / Symbol.PipSize;
        }

        // Zone OB/FVG trop large pour le timeframe (ex. stop de 677 pips sur un
        // graphique 5 min XAUUSD) : on l'abandonne plutot que de prendre un stop
        // demesure ou de la retenter a chaque bougie.
        private bool IsStopTooWide(TradeType tradeType, double zoneExtreme)
        {
            if (!UseAtrStops || MaxStopLossAtr <= 0)
                return false;

            var atrPips = _atr.Result.Last(1) / Symbol.PipSize;
            if (double.IsNaN(atrPips) || atrPips <= 0)
                return false;

            var stopLossPips = GetStopLossPips(tradeType, zoneExtreme);
            if (stopLossPips <= atrPips * MaxStopLossAtr)
                return false;

            Print("Zone {0} abandonnee : stop de {1:0.0} pips > {2:0.0} x ATR ({3:0.0} pips).",
                tradeType, stopLossPips, MaxStopLossAtr, atrPips * MaxStopLossAtr);
            return true;
        }

        private bool ExecuteEntry(TradeType tradeType, double zoneExtreme, string reason)
        {
            var stopLossPips = GetStopLossPips(tradeType, zoneExtreme);
            if (double.IsNaN(stopLossPips))
                return false;

            // Plancher : on eloigne le stop plutot que de trader une zone minuscule
            // avec un volume enorme (vu : SL de 2,9 pips pour 7000 unites).
            stopLossPips = Math.Max(stopLossPips, MinStopLossPips);
            if (stopLossPips <= 0)
                return false;

            var takeProfitPips = stopLossPips * RiskRewardRatio;

            var volume = CalculatePositionVolume(stopLossPips);
            if (volume <= 0)
            {
                Print("Volume calcule = 0, entree ignoree (risque vs capital).");
                return false;
            }

            var result = ExecuteMarketOrder(tradeType, SymbolName, volume, Label, stopLossPips, takeProfitPips, reason);

            if (result.IsSuccessful)
            {
                _breakEvenDone = false;
                Print("{0} entree remplie ({1}). Volume: {2}, risque: {3:0.0}%, SL: {4} pips, TP: {5} pips ({6}R)",
                    tradeType, reason, volume, stopLossPips * PipValuePerUnit() * volume / Account.Balance * 100.0,
                    Math.Round(stopLossPips, 1), Math.Round(takeProfitPips, 1), RiskRewardRatio);
                return true;
            }

            Print("Echec d'entree: {0}", result.Error);
            return false;
        }

        private double CalculatePositionVolume(double stopLossPips)
        {
            if (FixedLots > 0)
            {
                var fixedUnits = Symbol.QuantityToVolumeInUnits(FixedLots);
                if (fixedUnits < Symbol.VolumeInUnitsMin)
                {
                    Print("Lot fixe {0} inferieur au minimum du broker ({1} lot), entree ignoree.", FixedLots, Symbol.VolumeInUnitsToQuantity(Symbol.VolumeInUnitsMin));
                    return 0;
                }

                return Math.Min(Symbol.NormalizeVolumeInUnits(fixedUnits, RoundingMode.Down), Symbol.VolumeInUnitsMax);
            }

            var riskAmount = Account.Balance * (RiskPercent / 100.0);
            var rawVolume = riskAmount / (stopLossPips * PipValuePerUnit());

            // Test sur le volume BRUT : NormalizeVolumeInUnits remonte un volume
            // trop petit au minimum du symbole, ce qui ferait risquer bien plus
            // que prevu sur un petit compte (vu sur XAUUSD : SL de 87 a 677 pips,
            // tous a 1 unite) au lieu de passer par le plafond ci-dessous.
            if (rawVolume < Symbol.VolumeInUnitsMin)
                return MinVolumeIfRiskAcceptable(stopLossPips);

            var normalized = Symbol.NormalizeVolumeInUnits(rawVolume, RoundingMode.Down);

            if (normalized > Symbol.VolumeInUnitsMax)
                normalized = Symbol.VolumeInUnitsMax;

            return normalized;
        }

        private double MinVolumeIfRiskAcceptable(double stopLossPips)
        {
            if (!AllowMinVolumeFallback || Account.Balance <= 0)
                return 0;

            var minVolume = Symbol.VolumeInUnitsMin;
            var riskAtMinPercent = stopLossPips * PipValuePerUnit() * minVolume / Account.Balance * 100.0;

            if (riskAtMinPercent > MaxRiskAtMinVolumePercent)
            {
                Print("Le volume minimum risquerait {0:0.0}% (> {1:0.0}%), entree ignoree.", riskAtMinPercent, MaxRiskAtMinVolumePercent);
                return 0;
            }

            return minVolume;
        }

        // Valeur d'un pip pour une unite de volume, en devise du compte, derivee
        // de la tick value (Symbol.PipValue donnait une valeur bien trop faible
        // sur XAUUSD en backtest, cf. GoldTrendBot).
        private double PipValuePerUnit()
        {
            return Symbol.TickValue / Symbol.TickSize * Symbol.PipSize;
        }

        // -- Filtre news -----------------------------------------------------

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

        // Heure d'ete US : du 2e dimanche de mars 2h (7h UTC) au 1er dimanche de
        // novembre 2h (6h UTC). Calcule a la main pour ne pas dependre des
        // fuseaux horaires installes sur la machine.
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

        private int CloseHourFor(DateTime timeUtc)
        {
            return timeUtc.DayOfWeek == DayOfWeek.Friday ? FridayCloseHour : DailyCloseHour;
        }

        // Ferme la position du bot en fin de journee (et celle ouverte un jour
        // precedent, ex. apres un redemarrage) et au-dela de la duree max.
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

        private bool HasOpenPosition()
        {
            return Positions.Find(Label, SymbolName) != null;
        }

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

            if (position.TradeType == TradeType.Buy)
            {
                if (Symbol.Bid >= entry + slDistance)
                {
                    ModifyPosition(position, entry, position.TakeProfit);
                    _breakEvenDone = true;
                }
            }
            else
            {
                if (Symbol.Ask <= entry - slDistance)
                {
                    ModifyPosition(position, entry, position.TakeProfit);
                    _breakEvenDone = true;
                }
            }
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

        // -- Perte journaliere ------------------------------------------------

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

        // -- Creneaux horaires (killzones) ------------------------------------

        private bool IsKillzone(DateTime barTimeUtc)
        {
            var hour = barTimeUtc.Hour;
            var inLondon = UseLondonKillzone && IsHourInRange(hour, LondonKzStartHour, LondonKzEndHour);
            var inNy = UseNyKillzone && IsHourInRange(hour, NyKzStartHour, NyKzEndHour);
            return inLondon || inNy;
        }

        private static bool IsHourInRange(int hour, int startHour, int endHour)
        {
            if (startHour <= endHour)
                return hour >= startHour && hour < endHour;

            return hour >= startHour || hour < endHour;
        }

        // -- Affichage graphique ------------------------------------------------

        private void DrawDebugInfo()
        {
            if (Chart == null)
                return;

            if (_pendingBuySetup.Active)
            {
                var rect = Chart.DrawRectangle("ict_buy_zone", _pendingBuySetup.FormedAt, _pendingBuySetup.ZoneLow, Bars.OpenTimes.Last(1), _pendingBuySetup.ZoneHigh, Color.LimeGreen);
                rect.IsFilled = true;
            }
            else
            {
                SafeRemoveObject("ict_buy_zone");
            }

            if (_pendingSellSetup.Active)
            {
                var rect = Chart.DrawRectangle("ict_sell_zone", _pendingSellSetup.FormedAt, _pendingSellSetup.ZoneLow, Bars.OpenTimes.Last(1), _pendingSellSetup.ZoneHigh, Color.OrangeRed);
                rect.IsFilled = true;
            }
            else
            {
                SafeRemoveObject("ict_sell_zone");
            }

            var robotOn = _consecutiveLosses < MaxConsecutiveLosses && !_dailyLossLimitHit;
            var text = string.Format(
                "ICT/SMC + Ichimoku - {0}\nSetup Buy: {1} | Setup Sell: {2}\nPertes consecutives: {3}/{4}{5}",
                robotOn ? "ACTIF" : "PAUSE",
                _pendingBuySetup.Active ? "actif" : "-",
                _pendingSellSetup.Active ? "actif" : "-",
                _consecutiveLosses, MaxConsecutiveLosses,
                _dailyLossLimitHit ? "\nLimite perte journaliere atteinte" : "");

            Chart.DrawStaticText("ict_status", text, VerticalAlignment.Top, HorizontalAlignment.Right, robotOn ? Color.LimeGreen : Color.Red);
        }

        private void SafeRemoveObject(string name)
        {
            try
            {
                Chart.RemoveObject(name);
            }
            catch
            {
                // no-op si l'objet n'existe pas
            }
        }
    }
}

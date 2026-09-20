using System;
using System.Collections.Generic;
using cAlgo.API;
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
        [Parameter("Risque par trade (%)", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 10)]
        public double RiskPercent { get; set; }

        [Parameter("Risk:Reward (R)", Group = "Risk Management", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10)]
        public double RiskRewardRatio { get; set; }

        [Parameter("Buffer Stop Loss (pips)", Group = "Risk Management", DefaultValue = 20, MinValue = 0)]
        public double StopLossBufferPips { get; set; }

        [Parameter("Break-even a 1R", Group = "Risk Management", DefaultValue = true)]
        public bool UseBreakEvenAt1R { get; set; }

        [Parameter("Max pertes consecutives", Group = "Risk Management", DefaultValue = 3, MinValue = 1, MaxValue = 10)]
        public int MaxConsecutiveLosses { get; set; }

        [Parameter("Activer limite de perte journaliere", Group = "Risk Management", DefaultValue = true)]
        public bool UseDailyLossLimit { get; set; }

        [Parameter("Perte journaliere max (%)", Group = "Risk Management", DefaultValue = 5.0, MinValue = 0.5, MaxValue = 50.0, Step = 0.5)]
        public double MaxDailyLossPercent { get; set; }

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

        protected override void OnStart()
        {
            _ichimokuBars = UseHtfIchimoku && IchimokuTimeFrame != TimeFrame ? MarketData.GetBars(IchimokuTimeFrame, SymbolName) : Bars;

            Positions.Closed += OnPositionClosed;
            Print("IctSmcIchimokuBot started on {0} {1} (Ichimoku sur {2})", SymbolName, TimeFrame, _ichimokuBars.TimeFrame);
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

            if (_consecutiveLosses >= MaxConsecutiveLosses || _dailyLossLimitHit)
                return;

            if (Symbol.Spread / Symbol.PipSize > MaxSpreadPips)
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
                                ExecuteEntry(TradeType.Buy, _pendingBuySetup.ZoneLow, "Retracement OB/FVG haussier");
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
                                ExecuteEntry(TradeType.Sell, _pendingSellSetup.ZoneHigh, "Retracement OB/FVG baissier");
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

        private void ExecuteEntry(TradeType tradeType, double zoneExtreme, string reason)
        {
            var slBufferPrice = StopLossBufferPips * Symbol.PipSize;
            var entryPrice = tradeType == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            var stopLossPrice = tradeType == TradeType.Buy ? zoneExtreme - slBufferPrice : zoneExtreme + slBufferPrice;

            var stopLossPips = Math.Abs(entryPrice - stopLossPrice) / Symbol.PipSize;
            if (stopLossPips <= 0)
                return;

            var takeProfitPips = stopLossPips * RiskRewardRatio;

            var volume = CalculatePositionVolume(stopLossPips);
            if (volume <= 0)
            {
                Print("Volume calcule = 0, entree ignoree (risque vs capital).");
                return;
            }

            var result = ExecuteMarketOrder(tradeType, SymbolName, volume, Label, stopLossPips, takeProfitPips, reason);

            if (result.IsSuccessful)
            {
                _breakEvenDone = false;
                Print("{0} entree remplie ({1}). SL: {2} pips, TP: {3} pips ({4}R)",
                    tradeType, reason, Math.Round(stopLossPips, 1), Math.Round(takeProfitPips, 1), RiskRewardRatio);
            }
            else
            {
                Print("Echec d'entree: {0}", result.Error);
            }
        }

        private double CalculatePositionVolume(double stopLossPips)
        {
            var riskAmount = Account.Balance * (RiskPercent / 100.0);
            var rawVolume = riskAmount / (stopLossPips * Symbol.PipValue);
            var normalized = Symbol.NormalizeVolumeInUnits(rawVolume, RoundingMode.Down);

            if (normalized < Symbol.VolumeInUnitsMin)
                return 0;

            if (normalized > Symbol.VolumeInUnitsMax)
                normalized = Symbol.VolumeInUnitsMax;

            return normalized;
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

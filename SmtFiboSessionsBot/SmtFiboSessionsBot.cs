using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // ============================================================================
    // SmtFiboSessionsBot
    // ----------------------------------------------------------------------------
    // cBot cTrader base sur le modele de liquidite inter-sessions Tokyo -> Londres
    // -> New York, avec entree sur retracement Fibonacci (zone OTE) et confirmation
    // optionnelle par divergence SMT (Smart Money Technique) contre un symbole
    // correle. Base directement sur l'observation : "Londres recupere toujours la
    // liquidite de Tokyo, et New York continue le mouvement de Londres".
    //
    // IMPORTANT - comme IctSmcIchimokuBot, ce n'est pas un jeu de regles ICT/SMT
    // canonique : ce sont des choix d'interpretation precis, documentes ci-dessous.
    // Base de travail experimentale : testez tres longuement en backtest et en
    // demo avant meme d'envisager un compte reel.
    //
    // Logique generale (une seule direction de trade suivie par jour UTC) :
    //   1. Range asiatique : plus haut/plus bas releves pendant la session Tokyo.
    //   2. Liquidity sweep : pendant la session Londres, une bougie meche au-dela
    //      du plus haut ou plus bas asiatique puis cloture a l'interieur -> fixe
    //      le biais directionnel du jour (sweep du low -> biais haussier, et
    //      inversement).
    //   3. Confirmation SMT (optionnelle) : au moment du sweep, on verifie si un
    //      symbole correle a fait (ou pas) le meme sweep sur son propre range
    //      asiatique. L'absence de sweep cote du correle = divergence = signal
    //      renforce.
    //   4. Change of Character / Break of Structure : la cloture franchit le swing
    //      oppose le plus recent -> confirme le retournement post-sweep.
    //   5. Zone Fibonacci (OTE) : mesuree entre le point du sweep (A) et le plus
    //      haut/bas atteint depuis (B, mis a jour bougie par bougie tant que le
    //      prix ne retrace pas) -> zone d'entree = retracement 61.8%-78.6% de A-B.
    //   6. Entree : quand le prix revient dans la zone avec une bougie de rejet
    //      dans le sens du setup, pendant la session Londres OU New York (celle-ci
    //      est censee prolonger le mouvement initie par Londres) -> entree.
    //   7. Stop loss au niveau du sweep (+ buffer), take profit a une extension
    //      Fibonacci du mouvement A-B (1.618 par defaut), break-even optionnel a 1R.
    //
    // Multi-timeframe : range asiatique / sweep / BOS / zone Fibonacci sont
    // calcules sur un timeframe "structure" dedie (parametre StructureTimeFrame,
    // 15 min par defaut, via MarketData.GetBars + abonnement a Bars.BarClosed).
    // Le declenchement d'entree (retracement + bougie de rejet) est lui surveille
    // sur le graphique d'execution (celui sur lequel le bot est glisse), qui peut
    // etre plus bas pour un timing plus precis - schema classique ICT "recit sur
    // timeframe haut, gachette sur timeframe bas".
    //
    // Simplifications/choix assumes :
    //   - Un seul setup (biais haussier OU baissier) est suivi par jour UTC : le
    //     premier sweep valide de la journee fixe la direction, aucun retournement
    //     de biais n'est cherche ensuite le meme jour.
    //   - Le range asiatique doit etre entierement forme (au moins une bougie
    //     capturee) avant qu'un sweep puisse etre detecte ; si le bot demarre en
    //     cours de session Tokyo, le premier jour peut ne rien trader.
    //   - Zone Fibonacci valable pour un seul contact par defaut (FirstTouchOnly) :
    //     memes principes de "mitigation" que dans IctSmcIchimokuBot.
    //   - La divergence SMT est verifiee sur les valeurs de plus haut/bas de la
    //     bougie de sweep elle-meme, en assumant un alignement temporel des
    //     bougies entre le symbole principal et le symbole correle (memes broker
    //     et timeframe). Si le symbole correle est invalide/indisponible, le
    //     filtre SMT est desactive automatiquement (log d'avertissement), la
    //     strategie continue sans lui.
    //   - Horaires de session par defaut (UTC, sans ajustement ete/hiver) : Tokyo
    //     00h-07h, Londres 07h-16h, New York 13h-22h.
    // ============================================================================
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class SmtFiboSessionsBot : Robot
    {
        // -- Multi-Timeframe --
        [Parameter("Timeframe structure/sessions", Group = "Multi-Timeframe", DefaultValue = "Minute15",
            Description = "Timeframe sur lequel sont calcules le range asiatique, le sweep, le BOS et la zone Fibonacci. Le graphique d'execution (ou vous glissez le bot) sert au declenchement d'entree : mettez-le egal ou plus bas que ce timeframe pour un timing plus precis.")]
        public TimeFrame StructureTimeFrame { get; set; }

        // -- Sessions (UTC) --
        [Parameter("Tokyo - heure debut", Group = "Sessions", DefaultValue = 0, MinValue = 0, MaxValue = 23)]
        public int TokyoStartHour { get; set; }

        [Parameter("Tokyo - heure fin", Group = "Sessions", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int TokyoEndHour { get; set; }

        [Parameter("Londres - heure debut", Group = "Sessions", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int LondonStartHour { get; set; }

        [Parameter("Londres - heure fin", Group = "Sessions", DefaultValue = 16, MinValue = 0, MaxValue = 23)]
        public int LondonEndHour { get; set; }

        [Parameter("New York - heure debut", Group = "Sessions", DefaultValue = 13, MinValue = 0, MaxValue = 23)]
        public int NyStartHour { get; set; }

        [Parameter("New York - heure fin", Group = "Sessions", DefaultValue = 22, MinValue = 0, MaxValue = 23)]
        public int NyEndHour { get; set; }

        // -- Structure --
        [Parameter("Lookback structure (swing)", Group = "Structure", DefaultValue = 3, MinValue = 1, MaxValue = 10,
            Description = "Nombre de bougies de part et d'autre requises pour confirmer un swing high/low, utilise comme reference de Break of Structure.")]
        public int SwingLookback { get; set; }

        [Parameter("Entree au premier contact uniquement", Group = "Structure", DefaultValue = true,
            Description = "Si active, la zone Fibonacci est abandonnee des le premier contact sans bougie de rejet valide, plutot que de rester active pour un 2e/3e retest.")]
        public bool FirstTouchOnly { get; set; }

        // -- Fibonacci --
        [Parameter("OTE - debut retracement", Group = "Fibonacci", DefaultValue = 0.618, MinValue = 0.3, MaxValue = 0.9, Step = 0.01)]
        public double FiboOteStart { get; set; }

        [Parameter("OTE - fin retracement", Group = "Fibonacci", DefaultValue = 0.786, MinValue = 0.3, MaxValue = 0.95, Step = 0.01)]
        public double FiboOteEnd { get; set; }

        [Parameter("Extension take-profit", Group = "Fibonacci", DefaultValue = 1.618, MinValue = 1.0, MaxValue = 3.0, Step = 0.01,
            Description = "Take profit = A + (B-A) x cette extension (A = point du sweep, B = extremum du mouvement post-sweep).")]
        public double FiboExtensionTarget { get; set; }

        // -- Divergence SMT --
        [Parameter("Activer divergence SMT", Group = "SMT", DefaultValue = false,
            Description = "Compare le sweep du symbole trade avec un symbole correle. Necessite que le symbole correle soit disponible chez votre broker.")]
        public bool UseSmtDivergence { get; set; }

        [Parameter("Symbole correle", Group = "SMT", DefaultValue = "XAGUSD",
            Description = "Ex: XAGUSD pour XAUUSD, GBPUSD pour EURUSD, etc. Ajustez selon l'instrument trade.")]
        public string CorrelatedSymbolName { get; set; }

        [Parameter("Exiger la divergence SMT pour trader", Group = "SMT", DefaultValue = false,
            Description = "Si active, un sweep n'est valide que si le symbole correle n'a PAS fait le meme sweep (divergence). Sinon, la divergence est juste affichee a titre informatif.")]
        public bool RequireSmtDivergence { get; set; }

        // -- Gestion du risque --
        [Parameter("Risque par trade (%)", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 10)]
        public double RiskPercent { get; set; }

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

        // -- Divers --
        [Parameter("Label", Group = "Misc", DefaultValue = "SmtFiboSessions")]
        public string Label { get; set; }

        private struct SwingPoint
        {
            public double Price;
        }

        private struct DailySetup
        {
            public bool SweepDetected;
            public bool IsBullish;
            public double SweepExtreme;
            public double LegExtreme;
            public bool BosConfirmed;
            public bool Done;
            public bool SmtDivergenceConfirmed;
            public DateTime SweepTime;
        }

        private readonly List<SwingPoint> _swingHighs = new List<SwingPoint>();
        private readonly List<SwingPoint> _swingLows = new List<SwingPoint>();

        private Bars _structureBars;
        private Bars _correlatedBars;
        private bool _smtAvailable;

        private DateTime _currentDay = DateTime.MinValue;
        private bool _asianRangeReady;
        private double _asianHigh;
        private double _asianLow;
        private double _correlatedAsianHigh;
        private double _correlatedAsianLow;

        private DailySetup _setup;

        private double _dayStartBalance;
        private bool _dailyLossLimitHit;

        private bool _breakEvenDone;
        private int _consecutiveLosses;

        protected override void OnStart()
        {
            _structureBars = StructureTimeFrame == TimeFrame ? Bars : MarketData.GetBars(StructureTimeFrame, SymbolName);
            _structureBars.BarClosed += OnStructureBarClosed;

            if (UseSmtDivergence)
            {
                try
                {
                    _correlatedBars = MarketData.GetBars(StructureTimeFrame, CorrelatedSymbolName);
                    _smtAvailable = true;
                }
                catch (Exception ex)
                {
                    _smtAvailable = false;
                    Print("SMT desactive : impossible de charger le symbole correle '{0}' ({1}).", CorrelatedSymbolName, ex.Message);
                }
            }

            Positions.Closed += OnPositionClosed;
            ResetDailyState();

            Print("SmtFiboSessionsBot started - execution: {0} {1}, structure: {2}", SymbolName, TimeFrame, _structureBars.TimeFrame);
        }

        // Range asiatique, sweep, BOS, zone Fibonacci : tout tourne sur le
        // timeframe "structure" (potentiellement different du graphique
        // d'execution), sur chacune de ses propres clotures de bougie.
        private void OnStructureBarClosed(BarClosedEventArgs obj)
        {
            UpdateSwingPoints();

            var minBars = SwingLookback * 2 + 2;
            if (_structureBars.ClosePrices.Count < minBars)
                return;

            var barTimeUtc = _structureBars.OpenTimes.Last(1);
            HandleDayRollover(barTimeUtc);
            CheckDailyLossLimit();

            var hour = barTimeUtc.Hour;
            UpdateAsianRange(hour);

            if (_asianRangeReady && !_setup.Done)
            {
                if (!_setup.SweepDetected && IsHourInRange(hour, LondonStartHour, LondonEndHour))
                    DetectSweep();

                if (_setup.SweepDetected && !_setup.BosConfirmed)
                    CheckBos();

                if (_setup.BosConfirmed && !_setup.Done)
                {
                    var close = _structureBars.ClosePrices.Last(1);
                    var invalidated = _setup.IsBullish ? close < _setup.SweepExtreme : close > _setup.SweepExtreme;
                    if (invalidated)
                        _setup.Done = true;
                    else
                        UpdateLegExtreme();
                }
            }

            DrawDebugInfo();
        }

        // Declenchement d'entree : surveille sur le graphique d'execution, pour
        // un timing plus precis que le timeframe structure.
        protected override void OnBarClosed()
        {
            ManageBreakEven();

            if (HasOpenPosition())
                return;

            if (!_setup.BosConfirmed || _setup.Done)
                return;

            var hour = Bars.OpenTimes.Last(1).Hour;
            var inEntrySession = IsHourInRange(hour, LondonStartHour, LondonEndHour) || IsHourInRange(hour, NyStartHour, NyEndHour);
            if (!inEntrySession)
                return;

            if (_consecutiveLosses >= MaxConsecutiveLosses || _dailyLossLimitHit)
                return;

            if (Symbol.Spread / Symbol.PipSize > MaxSpreadPips)
                return;

            TryTriggerEntry();
        }

        // -- Reset quotidien (UTC) -------------------------------------------

        private void HandleDayRollover(DateTime barTimeUtc)
        {
            var day = barTimeUtc.Date;
            if (day == _currentDay)
                return;

            _currentDay = day;
            _dayStartBalance = Account.Balance;
            _dailyLossLimitHit = false;
            ResetDailyState();
        }

        private void ResetDailyState()
        {
            _asianRangeReady = false;
            _asianHigh = double.MinValue;
            _asianLow = double.MaxValue;
            _correlatedAsianHigh = double.MinValue;
            _correlatedAsianLow = double.MaxValue;
            _setup = default(DailySetup);
        }

        private void CheckDailyLossLimit()
        {
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

        // -- Range asiatique --------------------------------------------------

        private void UpdateAsianRange(int hour)
        {
            if (_asianRangeReady)
                return;

            if (IsHourInRange(hour, TokyoStartHour, TokyoEndHour))
            {
                _asianHigh = Math.Max(_asianHigh, _structureBars.HighPrices.Last(1));
                _asianLow = Math.Min(_asianLow, _structureBars.LowPrices.Last(1));

                if (_smtAvailable && _correlatedBars.HighPrices.Count > 0 && _correlatedBars.LowPrices.Count > 0)
                {
                    _correlatedAsianHigh = Math.Max(_correlatedAsianHigh, _correlatedBars.HighPrices.Last(1));
                    _correlatedAsianLow = Math.Min(_correlatedAsianLow, _correlatedBars.LowPrices.Last(1));
                }
            }
            else if (_asianHigh > double.MinValue && _asianLow < double.MaxValue)
            {
                _asianRangeReady = true;
            }
        }

        // -- Liquidity sweep + SMT --------------------------------------------

        private void DetectSweep()
        {
            var high = _structureBars.HighPrices.Last(1);
            var low = _structureBars.LowPrices.Last(1);
            var close = _structureBars.ClosePrices.Last(1);

            var sweptLow = low < _asianLow && close > _asianLow;
            var sweptHigh = high > _asianHigh && close < _asianHigh;

            if (!sweptLow && !sweptHigh)
                return;

            // Si les deux se produisent sur la meme bougie (range tres etroit),
            // on privilegie par convention le sweep du low (biais haussier).
            var isBullish = sweptLow;

            var smtDivergence = _smtAvailable && CheckSmtDivergence(isBullish);

            if (RequireSmtDivergence && _smtAvailable && !smtDivergence)
                return;

            _setup = new DailySetup
            {
                SweepDetected = true,
                IsBullish = isBullish,
                SweepExtreme = isBullish ? low : high,
                LegExtreme = isBullish ? high : low,
                SmtDivergenceConfirmed = smtDivergence,
                SweepTime = _structureBars.OpenTimes.Last(1)
            };
        }

        private bool CheckSmtDivergence(bool isBullish)
        {
            if (_correlatedBars.HighPrices.Count == 0 || _correlatedBars.LowPrices.Count == 0)
                return false;

            var correlatedLow = _correlatedBars.LowPrices.Last(1);
            var correlatedHigh = _correlatedBars.HighPrices.Last(1);

            // Divergence = le symbole correle n'a PAS fait un extreme equivalent.
            return isBullish
                ? correlatedLow >= _correlatedAsianLow
                : correlatedHigh <= _correlatedAsianHigh;
        }

        private void CheckBos()
        {
            var close = _structureBars.ClosePrices.Last(1);

            if (_setup.IsBullish)
            {
                if (_swingHighs.Count == 0)
                    return;

                var reference = _swingHighs[_swingHighs.Count - 1].Price;
                if (close > reference)
                {
                    _setup.BosConfirmed = true;
                    _setup.LegExtreme = Math.Max(_setup.LegExtreme, _structureBars.HighPrices.Last(1));
                }
            }
            else
            {
                if (_swingLows.Count == 0)
                    return;

                var reference = _swingLows[_swingLows.Count - 1].Price;
                if (close < reference)
                {
                    _setup.BosConfirmed = true;
                    _setup.LegExtreme = Math.Min(_setup.LegExtreme, _structureBars.LowPrices.Last(1));
                }
            }
        }

        private void UpdateLegExtreme()
        {
            if (_setup.IsBullish)
                _setup.LegExtreme = Math.Max(_setup.LegExtreme, _structureBars.HighPrices.Last(1));
            else
                _setup.LegExtreme = Math.Min(_setup.LegExtreme, _structureBars.LowPrices.Last(1));
        }

        // -- Structure : swing points (reference de BOS) ----------------------

        private void UpdateSwingPoints()
        {
            if (TryDetectSwingHigh(out var highPrice))
            {
                _swingHighs.Add(new SwingPoint { Price = highPrice });
                TrimList(_swingHighs);
            }

            if (TryDetectSwingLow(out var lowPrice))
            {
                _swingLows.Add(new SwingPoint { Price = lowPrice });
                TrimList(_swingLows);
            }
        }

        private bool TryDetectSwingHigh(out double price)
        {
            price = 0;
            var required = SwingLookback * 2 + 1;
            if (_structureBars.HighPrices.Count <= required)
                return false;

            var candidateShift = SwingLookback + 1;
            var candidate = _structureBars.HighPrices.Last(candidateShift);

            for (var i = 1; i <= SwingLookback; i++)
            {
                if (_structureBars.HighPrices.Last(i) >= candidate)
                    return false;

                if (_structureBars.HighPrices.Last(candidateShift + i) >= candidate)
                    return false;
            }

            price = candidate;
            return true;
        }

        private bool TryDetectSwingLow(out double price)
        {
            price = 0;
            var required = SwingLookback * 2 + 1;
            if (_structureBars.LowPrices.Count <= required)
                return false;

            var candidateShift = SwingLookback + 1;
            var candidate = _structureBars.LowPrices.Last(candidateShift);

            for (var i = 1; i <= SwingLookback; i++)
            {
                if (_structureBars.LowPrices.Last(i) <= candidate)
                    return false;

                if (_structureBars.LowPrices.Last(candidateShift + i) <= candidate)
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

        // -- Zone Fibonacci (OTE) ----------------------------------------------

        private void GetFiboZone(out double zoneLow, out double zoneHigh)
        {
            var a = _setup.SweepExtreme;
            var b = _setup.LegExtreme;
            var legLength = Math.Abs(b - a);

            if (_setup.IsBullish)
            {
                zoneHigh = b - legLength * FiboOteStart;
                zoneLow = b - legLength * FiboOteEnd;
            }
            else
            {
                zoneLow = b + legLength * FiboOteStart;
                zoneHigh = b + legLength * FiboOteEnd;
            }
        }

        // -- Declenchement d'entree ------------------------------------------

        private void TryTriggerEntry()
        {
            GetFiboZone(out var zoneLow, out var zoneHigh);

            var low = Bars.LowPrices.Last(1);
            var high = Bars.HighPrices.Last(1);
            var open = Bars.OpenPrices.Last(1);
            var close = Bars.ClosePrices.Last(1);

            var overlap = low <= zoneHigh && high >= zoneLow;
            if (!overlap)
                return;

            var validRejection = _setup.IsBullish ? close > open : close < open;

            if (validRejection)
            {
                // Ne consomme le setup du jour que si l'ordre part reellement -
                // un echec technique (volume nul, rejet broker) laisse la zone
                // active pour retenter sur une prochaine bougie de rejet valide.
                if (ExecuteEntry())
                    _setup.Done = true;
            }
            else if (FirstTouchOnly)
            {
                _setup.Done = true;
            }
        }

        private bool ExecuteEntry()
        {
            var isBullish = _setup.IsBullish;
            var a = _setup.SweepExtreme;
            var b = _setup.LegExtreme;

            var tradeType = isBullish ? TradeType.Buy : TradeType.Sell;
            var slBufferPrice = StopLossBufferPips * Symbol.PipSize;
            var stopLossPrice = isBullish ? a - slBufferPrice : a + slBufferPrice;
            var entryPrice = isBullish ? Symbol.Ask : Symbol.Bid;
            var stopLossPips = Math.Abs(entryPrice - stopLossPrice) / Symbol.PipSize;

            if (stopLossPips <= 0)
                return false;

            var takeProfitPrice = isBullish ? a + (b - a) * FiboExtensionTarget : a - (a - b) * FiboExtensionTarget;
            var takeProfitPips = Math.Abs(takeProfitPrice - entryPrice) / Symbol.PipSize;

            if (takeProfitPips <= 0)
                return false;

            var volume = CalculatePositionVolume(stopLossPips);
            if (volume <= 0)
            {
                Print("Volume calcule = 0, entree ignoree (risque vs capital).");
                return false;
            }

            var reason = (isBullish ? "OTE haussier" : "OTE baissier") + " (sweep Asie -> BOS Londres)";
            var result = ExecuteMarketOrder(tradeType, SymbolName, volume, Label, stopLossPips, takeProfitPips, reason);

            if (result.IsSuccessful)
            {
                _breakEvenDone = false;
                Print("{0} entree remplie ({1}). SL: {2} pips, TP: {3} pips, SMT: {4}",
                    tradeType, reason, Math.Round(stopLossPips, 1), Math.Round(takeProfitPips, 1),
                    !_smtAvailable ? "desactive" : (_setup.SmtDivergenceConfirmed ? "confirmee" : "non confirmee"));
                return true;
            }

            Print("Echec d'entree: {0}", result.Error);
            return false;
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

        // -- Sessions -----------------------------------------------------------

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

            if (_asianRangeReady)
            {
                Chart.DrawHorizontalLine("smt_asian_high", _asianHigh, Color.Gray, 1, LineStyle.Dots);
                Chart.DrawHorizontalLine("smt_asian_low", _asianLow, Color.Gray, 1, LineStyle.Dots);
            }
            else
            {
                SafeRemoveObject("smt_asian_high");
                SafeRemoveObject("smt_asian_low");
            }

            if (_setup.BosConfirmed && !_setup.Done)
            {
                GetFiboZone(out var zoneLow, out var zoneHigh);
                var color = _setup.IsBullish ? Color.LimeGreen : Color.OrangeRed;
                var rect = Chart.DrawRectangle("smt_ote_zone", _setup.SweepTime, zoneLow, _structureBars.OpenTimes.Last(1), zoneHigh, color);
                rect.IsFilled = true;
            }
            else
            {
                SafeRemoveObject("smt_ote_zone");
            }

            var robotOn = _consecutiveLosses < MaxConsecutiveLosses && !_dailyLossLimitHit;
            var biasTxt = !_setup.SweepDetected
                ? "aucun"
                : (_setup.IsBullish ? "haussier" : "baissier") + (_setup.BosConfirmed ? " (BOS confirme)" : " (attente BOS)");
            var smtTxt = !_smtAvailable ? "desactivee" : (_setup.SmtDivergenceConfirmed ? "confirmee" : "non confirmee");

            var text = string.Format(
                "SMT/ICT Fibo Sessions - {0}\nBiais du jour: {1}\nDivergence SMT: {2}\nPertes consecutives: {3}/{4}{5}",
                robotOn ? "ACTIF" : "PAUSE", biasTxt, smtTxt,
                _consecutiveLosses, MaxConsecutiveLosses,
                _dailyLossLimitHit ? "\nLimite perte journaliere atteinte" : "");

            Chart.DrawStaticText("smt_status", text, VerticalAlignment.Top, HorizontalAlignment.Right, robotOn ? Color.LimeGreen : Color.Red);
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

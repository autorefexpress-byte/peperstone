using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum RiskPreset
    {
        Custom,
        LowRisk,
        HighRisk
    }

    public enum GridSideMode
    {
        Both,
        BuyOnly,
        SellOnly
    }

    public enum SeedDirectionMode
    {
        BuyOnly,
        SellOnly,
        Auto
    }

    public enum SessionExitAction
    {
        FlattenAll,
        CancelPendingsOnly,
        CloseIfNetProfitPositive,
        CloseIfGrossProfitPositive
    }

    public enum DailyTradeCapMode
    {
        KeepPendingOrders,
        CancelPendingOrders
    }

    public enum SpreadAction
    {
        BlockNewOrders,
        BlockAndCancelPendings
    }

    // ============================================================================
    // MayaStyleGoldGridBot
    // ----------------------------------------------------------------------------
    // Bot de grille adaptative ATR pour XAU/USD, reconstruit a partir de la fiche
    // parametres publique fournie par l'utilisateur pour le produit commercial
    // "Maya Gold Grid ATR". Ce n'est PAS une copie du produit (aucun acces a son
    // code), seulement une reproduction de l'architecture decrite : seed initial,
    // grille d'ordres limites autour d'une ancre, SL/TP par niveau, gestion de
    // panier, garde-fous de risque multiples, filtre de spread avec hysteresis,
    // sortie de session configurable.
    //
    // ============================================================================
    // AVERTISSEMENT - LIRE AVANT TOUTE UTILISATION, MEME EN DEMO
    // ============================================================================
    // Contrairement a la premiere version de ce fichier, CHAQUE position (seed et
    // niveaux de grille) a bien son propre stop loss et take profit en pips - ce
    // n'est donc pas un martingale a risque totalement illimite. Le risque reste
    // neanmoins structurellement plus eleve que GoldTrendBot/VolumeProfileMtfBot/
    // IctSmcIchimokuBot/SmtFiboSessionsBot :
    //   - Plusieurs positions correlees (meme instrument, souvent meme sens)
    //     peuvent etre ouvertes simultanement, chacune avec sa propre marge.
    //   - Le "Rebuild Missing Orders" replace automatiquement un niveau stoppe :
    //     une tendance soutenue peut donc enchainer de nombreux stop loss
    //     individuels a la suite, chacun limite mais dont la somme peut etre
    //     lourde, surtout avec un Multiplicateur de volume par niveau > 1.
    //   - Les protections de compte (Max Drawdown, Drawdown flottant) agissent
    //     APRES coup (une fois le seuil franchi), pas en amont.
    // Testez sur plusieurs annees incluant des phases de forte tendance avant la
    // demo, et gardez les plafonds serres plutot que d'optimiser sur un backtest.
    // ============================================================================
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class MayaStyleGoldGridBot : Robot
    {
        // -- Preset --
        [Parameter("Preset de risque", Group = "Preset", DefaultValue = RiskPreset.Custom,
            Description = "LowRisk/HighRisk remplacent : Multiplicateur ATR, Niveaux par cote, Multiplicateur de volume, TP/SL par niveau (pips), Drawdown flottant max et Max Drawdown (capital initial). Tout le reste (montants panier, session, volumes de base...) reste sur vos reglages. Custom = 100% manuel.")]
        public RiskPreset Preset { get; set; }

        // -- Session --
        [Parameter("Utiliser une fenetre de trading", Group = "Session", DefaultValue = false)]
        public bool UseTradingWindow { get; set; }

        [Parameter("Heure debut session (UTC)", Group = "Session", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int StartTradingHour { get; set; }

        [Parameter("Heure fin session (UTC)", Group = "Session", DefaultValue = 21, MinValue = 0, MaxValue = 23)]
        public int EndTradingHour { get; set; }

        [Parameter("Action a la fin de session", Group = "Session", DefaultValue = SessionExitAction.FlattenAll,
            Description = "FlattenAll: ferme tout. CancelPendingsOnly: annule les ordres en attente, laisse courir les positions. CloseIfNetProfitPositive/CloseIfGrossProfitPositive: annule les ordres en attente puis ferme les positions seulement si le panier est en profit (net ou brut).")]
        public SessionExitAction ExitAction { get; set; }

        [Parameter("Cloturer en fin de journee (EOD)", Group = "Session", DefaultValue = false,
            Description = "Ferme tout, chaque jour, a l'heure indiquee, independamment de la logique de session.")]
        public bool CloseEndOfDay { get; set; }

        [Parameter("Heure de cloture EOD (UTC)", Group = "Session", DefaultValue = 21, MinValue = 0, MaxValue = 23)]
        public int CloseEodHour { get; set; }

        // -- Grid Core --
        [Parameter("ATR - periode", Group = "Grid Core", DefaultValue = 14, MinValue = 2)]
        public int AtrPeriod { get; set; }

        [Parameter("Multiplicateur ATR (espacement)", Group = "Grid Core", DefaultValue = 1.5, MinValue = 0.2, MaxValue = 10, Step = 0.1)]
        public double AtrMultiplier { get; set; }

        [Parameter("TP par niveau (pips)", Group = "Grid Core", DefaultValue = 150, MinValue = 1)]
        public double TakeProfitPerLevelPips { get; set; }

        [Parameter("SL par niveau (pips)", Group = "Grid Core", DefaultValue = 300, MinValue = 1)]
        public double StopLossPerLevelPips { get; set; }

        [Parameter("Niveaux par cote", Group = "Grid Core", DefaultValue = 5, MinValue = 1, MaxValue = 20)]
        public int LevelsPerSide { get; set; }

        [Parameter("Reconstruire les ordres manquants", Group = "Grid Core", DefaultValue = true,
            Description = "Replace automatiquement un niveau ferme (SL/TP touche) ou un ordre en attente annule par un nouvel ordre a la meme position de grille.")]
        public bool RebuildMissingOrders { get; set; }

        [Parameter("Delai avant reconstruction (minutes, 0=off)", Group = "Grid Core", DefaultValue = 15, MinValue = 0,
            Description = "Attente minimale apres la fermeture d'un niveau avant de le replacer au meme prix. Evite les rafales de SL/TP repetes sur un niveau pendant un mouvement rapide.")]
        public int RebuildCooldownMinutes { get; set; }

        [Parameter("Recentrer la grille apres (espacements, 0=off)", Group = "Grid Core", DefaultValue = 3, MinValue = 0, Step = 0.5,
            Description = "Quand aucune position n'est ouverte et que le prix s'est eloigne de l'ancre de plus de ce nombre d'espacements, les ordres en attente sont annules et un nouveau seed repart du prix courant. Evite que le bot reste bloque des jours avec des ordres hors de portee.")]
        public double ResetGridDistanceSteps { get; set; }

        [Parameter("Cote de la grille", Group = "Grid Core", DefaultValue = GridSideMode.BuyOnly,
            Description = "Both: buy limits sous l'ancre + sell limits au-dessus (double l'exposition). BuyOnly/SellOnly: un seul cote.")]
        public GridSideMode GridSide { get; set; }

        // -- Volume --
        [Parameter("Volume de base (lots)", Group = "Volume", DefaultValue = 0.01, MinValue = 0.01, Step = 0.01)]
        public double BaseVolume { get; set; }

        [Parameter("Volume total max (lots)", Group = "Volume", DefaultValue = 1.0, MinValue = 0.01,
            Description = "Plafond dur sur le volume cumule (positions ouvertes + ordres en attente) de toute la grille.")]
        public double MaxTotalVolume { get; set; }

        [Parameter("Multiplicateur de volume par niveau", Group = "Volume", DefaultValue = 1.0, MinValue = 1.0, MaxValue = 3.0, Step = 0.05,
            Description = "1.0 = volume constant par niveau. > 1.0 = croissance geometrique (risque fortement accru).")]
        public double VolumeMultiplierPerLevel { get; set; }

        // -- Seeding --
        [Parameter("Auto-seed quand a plat", Group = "Seeding", DefaultValue = true,
            Description = "Quand aucune position ni ordre en attente n'existe, ouvre un trade au marche (le 'seed') avant de reconstruire la grille autour de son prix d'entree (l'ancre).")]
        public bool AutoSeedWhenFlat { get; set; }

        [Parameter("Seed uniquement dans la session", Group = "Seeding", DefaultValue = true,
            Description = "N'a d'effet que si 'Utiliser une fenetre de trading' est active.")]
        public bool SeedOnlyInsideSession { get; set; }

        [Parameter("Direction du seed", Group = "Seeding", DefaultValue = SeedDirectionMode.SellOnly,
            Description = "Auto = biais simple base sur une EMA longue (au-dessus = Buy, en-dessous = Sell). Concerne uniquement le trade de depart ; le cote de la grille elle-meme est controle par 'Cote de la grille'.")]
        public SeedDirectionMode SeedDirection { get; set; }

        [Parameter("EMA periode (direction Auto)", Group = "Seeding", DefaultValue = 200, MinValue = 10)]
        public int SeedTrendEmaPeriod { get; set; }

        // -- Basket --
        [Parameter("TP panier (montant)", Group = "Basket", DefaultValue = 20.0, MinValue = 0,
            Description = "Ferme toute la grille (positions + ordres en attente) quand le profit net cumule atteint ce montant, dans la devise du compte. 0 = desactive.")]
        public double BasketTpMoney { get; set; }

        [Parameter("TP panier (pips vs VWAP)", Group = "Basket", DefaultValue = 0, MinValue = 0,
            Description = "Ferme toute la grille quand le prix s'eloigne du prix moyen pondere par le volume (VWAP) du cote net dominant par ce nombre de pips. 0 = desactive. Non applicable si le panier est parfaitement equilibre (Both, volumes Buy = Sell).")]
        public double BasketTpPipsVsAvg { get; set; }

        [Parameter("Coupe panier (montant)", Group = "Basket", DefaultValue = 50.0, MinValue = 0,
            Description = "Ferme toute la grille (protection) quand la perte nette cumulee atteint ce montant. 0 = desactive (deconseille).")]
        public double BasketCutMoney { get; set; }

        // -- Risk Management --
        [Parameter("Limite de trades journaliere", Group = "Risk Management", DefaultValue = 20, MinValue = 1,
            Description = "Compte par session si 'Utiliser une fenetre de trading' est actif, sinon par jour UTC.")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Mode de plafond de trades", Group = "Risk Management", DefaultValue = DailyTradeCapMode.KeepPendingOrders,
            Description = "KeepPendingOrders: arrete les nouveaux ordres mais garde ceux deja places. CancelPendingOrders: annule aussi tous les ordres en attente restants.")]
        public DailyTradeCapMode TradeCapMode { get; set; }

        [Parameter("Perte journaliere max (%)", Group = "Risk Management", DefaultValue = 5.0, MinValue = 0, MaxValue = 50, Step = 0.5,
            Description = "Perte REALISEE (positions cloturees) cumulee dans la journee, en % du solde de debut de journee. 0 = desactive.")]
        public double DailyLossLimitPercent { get; set; }

        [Parameter("Tout fermer a la perte journaliere max", Group = "Risk Management", DefaultValue = true,
            Description = "Oui : des que la perte journaliere max est atteinte, ferme aussi les positions ouvertes et annule les ordres en attente, pour que la perte du jour ne depasse pas la limite. Non : bloque seulement les nouveaux ordres.")]
        public bool FlattenOnDailyLossLimit { get; set; }

        [Parameter("Max Drawdown depuis capital initial (%)", Group = "Risk Management", DefaultValue = 20.0, MinValue = 0, MaxValue = 90, Step = 1,
            Description = "Protection compte dure et PERMANENTE : au-dela, fermeture complete et arret definitif du bot (redemarrage manuel requis). Mesuree depuis l'equity au demarrage du bot. 0 = desactive.")]
        public double MaxDrawdownStartEquityPercent { get; set; }

        [Parameter("Drawdown flottant max (%)", Group = "Risk Management", DefaultValue = 10.0, MinValue = 0, MaxValue = 90, Step = 1,
            Description = "Mesure de Balance a Equity (pertes latentes). Au-dela, fermeture complete mais le bot peut reconstruire une nouvelle grille ensuite. 0 = desactive.")]
        public double FloatingDrawdownLimitPercent { get; set; }

        [Parameter("Niveau de marge min apres ordre (%)", Group = "Risk Management", DefaultValue = 100.0, MinValue = 0,
            Description = "Empeche un nouvel ordre si le niveau de marge projete apres son execution (Equity / Marge utilisee x 100, la meme convention qu'affichee par cTrader) tomberait sous ce seuil. 100-200% sont des valeurs typiques. Estimation approximative (notionnel / levier du compte).")]
        public double MinFreeMarginAfterOrderPercent { get; set; }

        // -- Risk Management Pro --
        [Parameter("Activer le time-stop (positions perdantes)", Group = "Risk Management Pro", DefaultValue = false)]
        public bool UseTimeStop { get; set; }

        [Parameter("Duree max de detention (minutes)", Group = "Risk Management Pro", DefaultValue = 240, MinValue = 1,
            Description = "Une position en perte depuis plus longtemps que ce delai est fermee automatiquement, independamment de son SL.")]
        public int MaxHoldingTimeMinutes { get; set; }

        // -- Execution --
        [Parameter("Max Spread (pips, 0=off)", Group = "Execution", DefaultValue = 50, MinValue = 0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Action si spread trop eleve", Group = "Execution", DefaultValue = SpreadAction.BlockNewOrders,
            Description = "BlockNewOrders: bloque les nouveaux ordres, garde ceux deja places. BlockAndCancelPendings: annule aussi les ordres en attente existants (recommande pendant les actus/faible liquidite).")]
        public SpreadAction OnMaxSpreadAction { get; set; }

        [Parameter("Buffer de recuperation spread (pips)", Group = "Execution", DefaultValue = 5, MinValue = 0,
            Description = "Marge sous le seuil avant reautorisation des ordres, pour eviter les allers-retours autour du seuil.")]
        public double SpreadRecoveryBufferPips { get; set; }

        // -- General --
        [Parameter("Label", Group = "General", DefaultValue = "MayaStyleGrid")]
        public string Label { get; set; }

        private AverageTrueRange _atrIndicator;
        private MovingAverage _seedTrendEma;
        private double _currentAtr;

        private double? _gridAnchor;

        // Niveaux (labels) deja places au moins une fois pour l'ancre courante :
        // un niveau absent qui n'est PAS dans cet ensemble est une construction
        // initiale de grille (toujours tentee) ; s'il y est deja, un niveau vide
        // est un trou a reconstruire (soumis a RebuildMissingOrders).
        private readonly HashSet<string> _levelsEverPlaced = new HashSet<string>();

        // Heure de fermeture de chaque niveau, pour le delai avant reconstruction.
        private readonly Dictionary<string, DateTime> _levelClosedAtUtc = new Dictionary<string, DateTime>();

        private DateTime _currentDay = DateTime.MinValue;
        private double _dayStartBalance;
        private double _dailyRealizedPnl;
        private bool _dailyLossLimitHit;
        private int _tradesOpenedToday;
        private bool _dailyTradeCapReached;
        private bool _wasWithinSession;
        private bool _sessionExitHandled;
        private bool _eodHandledToday;

        private double _startEquity;
        private bool _hardStopped;

        private bool _spreadBlocked;

        protected override void OnStart()
        {
            if (string.IsNullOrWhiteSpace(Label))
            {
                Print("Label vide ou invalide : arret du bot pour eviter de gerer des positions d'autres bots/trades manuels.");
                Stop();
                return;
            }

            _atrIndicator = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            _currentAtr = _atrIndicator.Result.Count > 0 ? _atrIndicator.Result.LastValue : 0;

            if (SeedDirection == SeedDirectionMode.Auto)
                _seedTrendEma = Indicators.MovingAverage(Bars.ClosePrices, SeedTrendEmaPeriod, MovingAverageType.Exponential);

            Positions.Opened += OnPositionOpened;
            Positions.Closed += OnPositionClosed;

            _currentDay = Server.TimeInUtc.Date;
            _dayStartBalance = Account.Balance;
            _startEquity = Account.Equity;

            RecoverGridAnchor();

            Print("MayaStyleGoldGridBot started on {0} {1}. Grid side: {2}, Preset: {3}", SymbolName, TimeFrame, GridSide, Preset);
        }

        // Un redemarrage du cBot (VPS, mise a jour cTrader...) alors qu'une grille
        // est deja ouverte perdrait sinon l'ancre en memoire, desactivant
        // silencieusement la reconstruction des niveaux et le suivi de drawdown
        // flottant pour le reste de l'execution.
        private void RecoverGridAnchor()
        {
            var seed = Positions.Find(Label + "_SEED", SymbolName);
            if (seed != null)
            {
                _gridAnchor = seed.EntryPrice;
                Print("Ancre de grille recuperee depuis le seed existant: {0:0.00000}", _gridAnchor);
            }
            else if (HasAnyActivity())
            {
                // Pas de seed retrouve mais des niveaux existent : impossible de
                // reconstruire fiablement l'ancre d'origine (l'ATR/l'espacement a
                // pu changer depuis le placement initial - extrapoler depuis un
                // niveau survivant avec le pas COURANT donnerait une ancre fausse,
                // potentiellement du mauvais cote du marche). On repart plutot du
                // prix courant comme nouvelle ancre pour les FUTURS niveaux
                // reconstruits ; les positions existantes restent gerees
                // normalement (panier, time-stop, SL/TP propres) quel que soit
                // leur alignement avec cette nouvelle ancre.
                _gridAnchor = Symbol.Bid;
                Print("Seed introuvable mais positions/ordres existants detectes : ancre reinitialisee au prix courant ({0:0.00000}).", _gridAnchor);
            }

            // Marque tous les niveaux deja actifs comme "deja places" : les
            // niveaux VIDES trouves apres redemarrage sont des trous a reconstruire
            // (soumis a RebuildMissingOrders), pas une construction initiale.
            for (var level = 0; level < EffectiveLevelsPerSide(); level++)
            {
                var buyLabel = Label + "_BUY_L" + level;
                if (HasLevelActivity(buyLabel))
                    _levelsEverPlaced.Add(buyLabel);

                var sellLabel = Label + "_SELL_L" + level;
                if (HasLevelActivity(sellLabel))
                    _levelsEverPlaced.Add(sellLabel);
            }

            if (_gridAnchor == null && HasAnyActivity())
            {
                Print("Attention : positions/ordres existants detectes au demarrage mais aucune ancre n'a pu etre recuperee. Reconstruction de grille desactivee jusqu'a une remise a plat complete.");
            }
        }

        protected override void OnBarClosed()
        {
            _currentAtr = _atrIndicator.Result.LastValue;
            DrawDebugInfo();
        }

        protected override void OnTick()
        {
            var nowUtc = Server.TimeInUtc;
            HandleDayRollover(nowUtc);
            HandleSessionTradeCounterReset(nowUtc);

            CheckHardDrawdown();
            if (_hardStopped)
                return;

            CheckFloatingDrawdown();
            CheckTimeStop();
            CheckBasketExits();

            if (UseTradingWindow)
                CheckSessionExit(nowUtc);

            CheckCloseEod(nowUtc);

            var spreadBlocked = IsSpreadBlocked();

            CheckStaleGrid();

            if (!HasAnyActivity())
                TrySeed(spreadBlocked);
            else if (_gridAnchor.HasValue)
                ManageGrid(spreadBlocked);
        }

        // Sans cela, une grille dont il ne reste que des ordres en attente loin
        // du prix (le marche est parti dans l'autre sens) bloque le bot : il
        // n'est pas "a plat" donc ne re-seed pas, et les ordres restants
        // occupent tout le Volume total max. Constate en backtest : aucun trade
        // pendant plus de deux mois.
        private void CheckStaleGrid()
        {
            if (ResetGridDistanceSteps <= 0 || !_gridAnchor.HasValue)
                return;

            foreach (var p in Positions)
            {
                if (IsOwned(p))
                    return;
            }

            var stepPips = GetGridStepPips();
            if (stepPips <= 0)
                return;

            var mid = (Symbol.Bid + Symbol.Ask) / 2.0;
            var distanceSteps = Math.Abs(mid - _gridAnchor.Value) / (stepPips * Symbol.PipSize);
            if (distanceSteps < ResetGridDistanceSteps)
                return;

            Print("Prix eloigne de l'ancre ({0:0.0} espacements >= {1:0.0}) sans position ouverte : ordres en attente annules, la grille repartira du prix courant.",
                distanceSteps, ResetGridDistanceSteps);
            FlattenAll();
        }

        // -- Seed ---------------------------------------------------------------

        private void TrySeed(bool spreadBlocked)
        {
            if (!AutoSeedWhenFlat || _hardStopped || _dailyLossLimitHit || _dailyTradeCapReached)
                return;

            if (SeedOnlyInsideSession && UseTradingWindow && !IsWithinSession(Server.TimeInUtc))
                return;

            if (spreadBlocked)
                return;

            var volume = NormalizedVolume(BaseVolume);
            if (volume <= 0)
                return;

            if (!CanAddVolume(0) || !HasEnoughFreeMargin(volume))
                return;

            var direction = ResolveSeedDirection();
            if (direction == null)
                return; // Auto : EMA pas encore prete, on attend plutot que de deviner

            var result = ExecuteMarketOrder(direction.Value, SymbolName, volume, Label + "_SEED",
                EffectiveSlPerLevelPips(), EffectiveTpPerLevelPips(), "Seed");

            if (!result.IsSuccessful)
                Print("Echec du seed {0}: {1}", direction, result.Error);
        }

        private TradeType? ResolveSeedDirection()
        {
            if (SeedDirection == SeedDirectionMode.BuyOnly)
                return TradeType.Buy;

            if (SeedDirection == SeedDirectionMode.SellOnly)
                return TradeType.Sell;

            if (_seedTrendEma != null && _seedTrendEma.Result.Count > 0)
                return Bars.ClosePrices.LastValue > _seedTrendEma.Result.LastValue ? TradeType.Buy : TradeType.Sell;

            return null;
        }

        // -- Grille d'ordres limites ---------------------------------------------

        // Les niveaux sont places en alternant les cotes (Buy L0, Sell L0,
        // Buy L1...) plutot que tout le cote Buy d'abord : avec un Volume total
        // max serre, l'ancienne boucle par cote remplissait le plafond avec des
        // Buy uniquement et le mode Both n'avait jamais de Sell.
        private void ManageGrid(bool spreadBlocked)
        {
            var stepPips = GetGridStepPips();
            if (stepPips <= 0)
                return;

            var step = stepPips * Symbol.PipSize;
            var levels = EffectiveLevelsPerSide();

            // Calcule le volume total une seule fois puis l'incremente localement
            // a chaque placement reussi, plutot que de rescanner Positions/
            // PendingOrders a chaque niveau de la boucle.
            var currentTotalLots = GetTotalOpenAndPendingVolumeLots();

            for (var level = 0; level < levels; level++)
            {
                foreach (var side in new[] { TradeType.Buy, TradeType.Sell })
                {
                    if (GridSide == GridSideMode.BuyOnly && side == TradeType.Sell)
                        continue;

                    if (GridSide == GridSideMode.SellOnly && side == TradeType.Buy)
                        continue;

                    var sideName = side == TradeType.Buy ? "BUY" : "SELL";
                    var levelLabel = Label + "_" + sideName + "_L" + level;
                    if (HasLevelActivity(levelLabel))
                        continue;

                    // Construction initiale (niveau jamais place pour cette ancre) :
                    // toujours tentee. Niveau deja place puis vide (SL/TP/annulation) :
                    // seulement reconstruit si RebuildMissingOrders est actif, et
                    // apres le delai de reconstruction.
                    var isInitialPlacement = !_levelsEverPlaced.Contains(levelLabel);
                    if (!isInitialPlacement && (!RebuildMissingOrders || IsInRebuildCooldown(levelLabel)))
                        continue;

                    if (_dailyLossLimitHit || _dailyTradeCapReached || _hardStopped || spreadBlocked)
                        continue;

                    var nextVolumeLots = BaseVolume * Math.Pow(EffectiveVolumeMultiplier(), level);
                    if (currentTotalLots + nextVolumeLots > MaxTotalVolume)
                        continue;

                    var levelPrice = side == TradeType.Buy
                        ? _gridAnchor.Value - step * (level + 1)
                        : _gridAnchor.Value + step * (level + 1);

                    // Niveau deja traverse par le prix : un Buy limit au-dessus
                    // de l'Ask (ou un Sell limit sous le Bid) est execute tout de
                    // suite au marche. En backtest, la grille rachetait ainsi en
                    // boucle une baisse (limite 4056.96 remplie a 4043.82).
                    var levelAlreadyCrossed = side == TradeType.Buy ? levelPrice >= Symbol.Ask : levelPrice <= Symbol.Bid;
                    if (levelAlreadyCrossed)
                        continue;

                    if (PlacePendingLevel(side, levelLabel, levelPrice, level))
                        currentTotalLots += nextVolumeLots;
                }
            }
        }

        private bool IsInRebuildCooldown(string levelLabel)
        {
            if (RebuildCooldownMinutes <= 0 || !_levelClosedAtUtc.TryGetValue(levelLabel, out var closedAt))
                return false;

            return (Server.TimeInUtc - closedAt).TotalMinutes < RebuildCooldownMinutes;
        }

        private bool PlacePendingLevel(TradeType side, string levelLabel, double price, int level)
        {
            var volume = NormalizedVolume(BaseVolume * Math.Pow(EffectiveVolumeMultiplier(), level));
            if (volume <= 0)
                return false;

            if (!HasEnoughFreeMargin(volume))
                return false;

            var result = PlaceLimitOrder(side, SymbolName, volume, price, levelLabel,
                EffectiveSlPerLevelPips(), EffectiveTpPerLevelPips());

            if (result.IsSuccessful)
            {
                _levelsEverPlaced.Add(levelLabel);
                return true;
            }

            Print("Echec placement ordre limite {0} niveau {1}: {2}", side, level + 1, result.Error);
            return false;
        }

        // Predicat d'appartenance centralise : evite de dupliquer la condition
        // "meme symbole + label prefixe par le nom de ce bot" dans chaque methode
        // qui parcourt Positions/PendingOrders (une seule ligne a corriger si la
        // regle change, ex: garde contre un Label vide).
        private bool IsOwned(Position p)
        {
            return p.SymbolName == SymbolName && !string.IsNullOrEmpty(p.Label) && p.Label.StartsWith(Label);
        }

        private bool IsOwned(PendingOrder o)
        {
            return o.SymbolName == SymbolName && !string.IsNullOrEmpty(o.Label) && o.Label.StartsWith(Label);
        }

        private bool HasLevelActivity(string levelLabel)
        {
            if (Positions.Find(levelLabel, SymbolName) != null)
                return true;

            foreach (var o in PendingOrders)
            {
                if (o.SymbolName == SymbolName && o.Label == levelLabel)
                    return true;
            }

            return false;
        }

        private bool HasAnyActivity()
        {
            foreach (var p in Positions)
            {
                if (IsOwned(p))
                    return true;
            }

            foreach (var o in PendingOrders)
            {
                if (IsOwned(o))
                    return true;
            }

            return false;
        }

        // -- Volume / marge -------------------------------------------------------

        private bool CanAddVolume(int level)
        {
            var nextVolumeLots = BaseVolume * Math.Pow(EffectiveVolumeMultiplier(), level);
            var currentTotalLots = GetTotalOpenAndPendingVolumeLots();
            return currentTotalLots + nextVolumeLots <= MaxTotalVolume;
        }

        private double GetTotalOpenAndPendingVolumeLots()
        {
            double totalUnits = 0;

            foreach (var p in Positions)
            {
                if (IsOwned(p))
                    totalUnits += p.VolumeInUnits;
            }

            foreach (var o in PendingOrders)
            {
                if (IsOwned(o))
                    totalUnits += o.VolumeInUnits;
            }

            return Symbol.VolumeInUnitsToQuantity(totalUnits);
        }

        private double NormalizedVolume(double lots)
        {
            var rawUnits = Symbol.QuantityToVolumeInUnits(lots);
            var normalized = Symbol.NormalizeVolumeInUnits(rawUnits, RoundingMode.Down);

            if (normalized < Symbol.VolumeInUnitsMin)
                return 0;

            if (normalized > Symbol.VolumeInUnitsMax)
                return Symbol.VolumeInUnitsMax;

            return normalized;
        }

        // Utilise la convention standard "niveau de marge" (Equity / Marge
        // utilisee x 100), la meme que celle affichee par cTrader et la plupart
        // des brokers, ou 100-200% sont des seuils courants. Une version
        // precedente comparait la marge libre restante a un % de l'equity
        // directement, ce qui rendait le seuil quasi impossible a satisfaire des
        // qu'un ordre consommait la moindre marge (aucune position n'etait
        // jamais ouverte, meme avec les reglages par defaut).
        private bool HasEnoughFreeMargin(double volumeUnits)
        {
            if (volumeUnits <= 0 || MinFreeMarginAfterOrderPercent <= 0)
                return true;

            // Estimation approximative de la marge requise : notionnel / levier du
            // compte. Ne tient pas compte des conversions de devises eventuelles.
            var notional = volumeUnits * Symbol.Bid;
            var estimatedMargin = Account.Leverage > 0 ? notional / Account.Leverage : notional;

            var currentUsedMargin = Account.Equity - Account.FreeMargin;
            var projectedUsedMargin = currentUsedMargin + estimatedMargin;

            if (projectedUsedMargin <= 0)
                return true;

            var projectedMarginLevel = Account.Equity / projectedUsedMargin * 100.0;
            return projectedMarginLevel >= MinFreeMarginAfterOrderPercent;
        }

        private double GetGridStepPips()
        {
            if (double.IsNaN(_currentAtr) || _currentAtr <= 0)
                return 0;

            return (_currentAtr / Symbol.PipSize) * EffectiveAtrMultiplier();
        }

        // -- Panier ---------------------------------------------------------------

        private void CheckBasketExits()
        {
            GetBasketTotals(out var netProfit, out var grossProfit, out var totalVolume, out var count);
            if (count == 0)
                return;

            if (BasketTpMoney > 0 && netProfit >= BasketTpMoney)
            {
                Print("TP panier (montant) atteint: {0:0.00} {1}. Fermeture complete.", netProfit, Account.Asset.Name);
                FlattenAll();
                return;
            }

            if (BasketTpPipsVsAvg > 0 && totalVolume > 0)
            {
                var netIsBuy = GetNetSideIsBuy();
                if (netIsBuy.HasValue)
                {
                    var side = netIsBuy.Value ? TradeType.Buy : TradeType.Sell;
                    var vwap = GetSideVwap(side, out var sideVolume);

                    if (sideVolume > 0)
                    {
                        var distancePips = netIsBuy.Value
                            ? (Symbol.Bid - vwap) / Symbol.PipSize
                            : (vwap - Symbol.Ask) / Symbol.PipSize;

                        if (distancePips >= BasketTpPipsVsAvg)
                        {
                            Print("TP panier (pips vs VWAP) atteint: {0:0.0} pips. Fermeture complete.", distancePips);
                            FlattenAll();
                            return;
                        }
                    }
                }
            }

            if (BasketCutMoney > 0 && netProfit <= -BasketCutMoney)
            {
                Print("Coupe panier atteinte: {0:0.00} {1}. Fermeture complete (protection).", netProfit, Account.Asset.Name);
                FlattenAll();
            }
        }

        private void GetBasketTotals(out double netProfit, out double grossProfit, out double totalVolume, out int count)
        {
            netProfit = 0;
            grossProfit = 0;
            totalVolume = 0;
            count = 0;

            foreach (var p in Positions)
            {
                if (!IsOwned(p))
                    continue;

                netProfit += p.NetProfit;
                grossProfit += p.GrossProfit;
                totalVolume += p.VolumeInUnits;
                count++;
            }
        }

        // VWAP calcule sur un seul cote (Buy ou Sell), utilise pour le TP panier
        // "pips vs VWAP" applique au cote net dominant - un VWAP sur l'ensemble
        // du panier (Buy + Sell melanges) n'aurait pas de sens directionnel clair.
        private double GetSideVwap(TradeType side, out double sideVolume)
        {
            var weightedSum = 0.0;
            sideVolume = 0;

            foreach (var p in Positions)
            {
                if (!IsOwned(p) || p.TradeType != side)
                    continue;

                weightedSum += p.EntryPrice * p.VolumeInUnits;
                sideVolume += p.VolumeInUnits;
            }

            return sideVolume > 0 ? weightedSum / sideVolume : 0;
        }

        private bool? GetNetSideIsBuy()
        {
            double buyVolume = 0, sellVolume = 0;

            foreach (var p in Positions)
            {
                if (!IsOwned(p))
                    continue;

                if (p.TradeType == TradeType.Buy)
                    buyVolume += p.VolumeInUnits;
                else
                    sellVolume += p.VolumeInUnits;
            }

            if (buyVolume > sellVolume)
                return true;

            if (sellVolume > buyVolume)
                return false;

            return null;
        }

        // N'efface l'ancre que si la remise a plat a reellement reussi (toutes
        // les fermetures/annulations confirmees) - sinon un echec partiel
        // (requote, deconnexion) desactiverait silencieusement la reconstruction
        // de grille et le suivi de drawdown flottant pour le reste de l'execution.
        private void FlattenAll()
        {
            ClosePositions();
            CancelAllPendings();
            ResetAnchorIfFlat();
        }

        private void CloseAllPositionsOnly()
        {
            ClosePositions();
            ResetAnchorIfFlat();
        }

        private void ResetAnchorIfFlat()
        {
            if (HasAnyActivity())
                return;

            _gridAnchor = null;
            _levelsEverPlaced.Clear();
            _levelClosedAtUtc.Clear();
        }

        private void ClosePositions()
        {
            var toClose = new List<Position>();
            foreach (var p in Positions)
            {
                if (IsOwned(p))
                    toClose.Add(p);
            }

            foreach (var p in toClose)
            {
                var result = ClosePosition(p);
                if (!result.IsSuccessful)
                    Print("Echec de fermeture de {0}: {1}", p.Label, result.Error);
            }
        }

        private void CancelAllPendings()
        {
            var toCancel = new List<PendingOrder>();
            foreach (var o in PendingOrders)
            {
                if (IsOwned(o))
                    toCancel.Add(o);
            }

            foreach (var o in toCancel)
            {
                var result = CancelPendingOrder(o);
                if (!result.IsSuccessful)
                    Print("Echec d'annulation de l'ordre {0}: {1}", o.Label, result.Error);
            }
        }

        // -- Time stop --------------------------------------------------------

        private void CheckTimeStop()
        {
            if (!UseTimeStop)
                return;

            var nowUtc = Server.TimeInUtc;
            var toClose = new List<Position>();

            foreach (var p in Positions)
            {
                if (!IsOwned(p))
                    continue;

                if (p.NetProfit >= 0)
                    continue;

                if ((nowUtc - p.EntryTime).TotalMinutes >= MaxHoldingTimeMinutes)
                    toClose.Add(p);
            }

            foreach (var p in toClose)
            {
                Print("Time-stop : fermeture de {0} apres {1:0} minutes en perte.", p.Label, (nowUtc - p.EntryTime).TotalMinutes);
                var result = ClosePosition(p);
                if (!result.IsSuccessful)
                    Print("Echec de fermeture (time-stop) de {0}: {1}", p.Label, result.Error);
            }
        }

        // -- Drawdown / limites de compte --------------------------------------

        private void CheckHardDrawdown()
        {
            if (_hardStopped || MaxDrawdownStartEquityPercent <= 0 || _startEquity <= 0)
                return;

            var ddPercent = (_startEquity - Account.Equity) / _startEquity * 100.0;
            if (ddPercent < EffectiveMaxDrawdownStartEquityPercent())
                return;

            _hardStopped = true;
            Print("ARRET D'URGENCE : drawdown depuis le capital de depart = {0:0.0}% >= {1:0.0}%. Fermeture complete. Le bot ne rouvrira plus de position (redemarrage manuel requis).",
                ddPercent, EffectiveMaxDrawdownStartEquityPercent());
            FlattenAll();
        }

        private void CheckFloatingDrawdown()
        {
            if (_hardStopped || FloatingDrawdownLimitPercent <= 0 || Account.Balance <= 0 || !_gridAnchor.HasValue)
                return;

            var floatingDdPercent = (Account.Balance - Account.Equity) / Account.Balance * 100.0;
            if (floatingDdPercent < EffectiveFloatingDrawdownLimitPercent())
                return;

            Print("Drawdown flottant atteint: {0:0.0}% >= {1:0.0}%. Fermeture complete (une nouvelle grille pourra se reconstruire).",
                floatingDdPercent, EffectiveFloatingDrawdownLimitPercent());
            FlattenAll();
        }

        // -- Perte journaliere / plafond de trades -----------------------------

        private void HandleDayRollover(DateTime nowUtc)
        {
            var day = nowUtc.Date;
            if (day == _currentDay)
                return;

            _currentDay = day;
            _dayStartBalance = Account.Balance;
            _dailyRealizedPnl = 0;
            _dailyLossLimitHit = false;
            _sessionExitHandled = false;
            _eodHandledToday = false;

            if (!UseTradingWindow)
            {
                _tradesOpenedToday = 0;
                _dailyTradeCapReached = false;
            }
        }

        private void HandleSessionTradeCounterReset(DateTime nowUtc)
        {
            if (!UseTradingWindow)
                return;

            var withinNow = IsWithinSession(nowUtc);
            if (withinNow && !_wasWithinSession)
            {
                _tradesOpenedToday = 0;
                _dailyTradeCapReached = false;
                Print("Nouvelle session de trading : compteur de trades reinitialise.");
            }

            _wasWithinSession = withinNow;
        }

        private void RegisterTradeOpened()
        {
            _tradesOpenedToday++;
            if (_tradesOpenedToday < MaxTradesPerDay || _dailyTradeCapReached)
                return;

            _dailyTradeCapReached = true;
            Print("Limite de trades atteinte ({0}/{1}).", _tradesOpenedToday, MaxTradesPerDay);

            if (TradeCapMode == DailyTradeCapMode.CancelPendingOrders)
                CancelAllPendings();
        }

        // -- Session ---------------------------------------------------------------

        private bool IsWithinSession(DateTime nowUtc)
        {
            // Debut == fin interprete comme "pas de restriction" (24h/24) plutot
            // que comme une fenetre de duree nulle qui bloquerait tout.
            if (StartTradingHour == EndTradingHour)
                return true;

            var hour = nowUtc.Hour;
            if (StartTradingHour < EndTradingHour)
                return hour >= StartTradingHour && hour < EndTradingHour;

            return hour >= StartTradingHour || hour < EndTradingHour;
        }

        private void CheckSessionExit(DateTime nowUtc)
        {
            if (_sessionExitHandled || IsWithinSession(nowUtc))
                return;

            _sessionExitHandled = true;

            switch (ExitAction)
            {
                case SessionExitAction.FlattenAll:
                    Print("Fin de session : fermeture complete (FlattenAll).");
                    FlattenAll();
                    break;

                case SessionExitAction.CancelPendingsOnly:
                    Print("Fin de session : annulation des ordres en attente uniquement.");
                    CancelAllPendings();
                    break;

                case SessionExitAction.CloseIfNetProfitPositive:
                    CancelAllPendings();
                    GetBasketTotals(out var netProfit, out _, out _, out var netCount);
                    if (netCount > 0 && netProfit > 0)
                    {
                        Print("Fin de session : NetProfit positif ({0:0.00}), fermeture des positions.", netProfit);
                        CloseAllPositionsOnly();
                    }
                    break;

                case SessionExitAction.CloseIfGrossProfitPositive:
                    CancelAllPendings();
                    GetBasketTotals(out _, out var grossProfit, out _, out var grossCount);
                    if (grossCount > 0 && grossProfit > 0)
                    {
                        Print("Fin de session : GrossProfit positif ({0:0.00}), fermeture des positions.", grossProfit);
                        CloseAllPositionsOnly();
                    }
                    break;
            }
        }

        private void CheckCloseEod(DateTime nowUtc)
        {
            if (!CloseEndOfDay || _eodHandledToday || nowUtc.Hour != CloseEodHour)
                return;

            Print("Cloture de fin de journee (EOD) : fermeture complete.");
            FlattenAll();
            _eodHandledToday = true;
        }

        // -- Spread ---------------------------------------------------------------

        private bool IsSpreadBlocked()
        {
            if (MaxSpreadPips <= 0)
                return false;

            var currentSpreadPips = Symbol.Spread / Symbol.PipSize;

            if (!_spreadBlocked && currentSpreadPips > MaxSpreadPips)
            {
                _spreadBlocked = true;
                Print("Spread trop eleve ({0:0.0} pips > {1:0.0}), nouvelles entrees bloquees.", currentSpreadPips, MaxSpreadPips);

                if (OnMaxSpreadAction == SpreadAction.BlockAndCancelPendings)
                    CancelAllPendings();
            }
            // Le seuil de reprise ne descend jamais sous la moitie du Max Spread :
            // avec Max Spread 2 et un buffer de 5 (EURUSD), il valait -3 pips et
            // le bot restait bloque pour toujours apres le premier pic de spread.
            else if (_spreadBlocked && currentSpreadPips <= Math.Max(MaxSpreadPips - SpreadRecoveryBufferPips, MaxSpreadPips * 0.5))
            {
                _spreadBlocked = false;
                Print("Spread revenu a la normale ({0:0.0} pips), entrees reautorisees.", currentSpreadPips);
            }

            return _spreadBlocked;
        }

        // -- Evenements de position --------------------------------------------

        private void OnPositionOpened(PositionOpenedEventArgs args)
        {
            var position = args.Position;
            if (!IsOwned(position))
                return;

            if (position.Label == Label + "_SEED")
            {
                _gridAnchor = position.EntryPrice;
                _levelsEverPlaced.Clear();
                Print("Seed {0} rempli a {1:0.00000}. Ancrage de grille etabli.", position.TradeType, position.EntryPrice);
            }

            RegisterTradeOpened();
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;
            if (!IsOwned(position))
                return;

            _dailyRealizedPnl += position.NetProfit;

            if (position.Label != Label + "_SEED")
                _levelClosedAtUtc[position.Label] = Server.TimeInUtc;

            Print("Position fermee ({0}, {1}). Net: {2:0.00} {3}.", position.Label, position.TradeType, position.NetProfit, Account.Asset.Name);

            if (_dailyLossLimitHit || DailyLossLimitPercent <= 0 || _dayStartBalance <= 0 || _dailyRealizedPnl >= 0)
                return;

            var lossPercent = -_dailyRealizedPnl / _dayStartBalance * 100.0;
            if (lossPercent >= DailyLossLimitPercent)
            {
                _dailyLossLimitHit = true;
                Print("Limite de perte journaliere (cloturee) atteinte: {0:0.0}% >= {1:0.0}%.", lossPercent, DailyLossLimitPercent);

                // Sinon les positions deja ouvertes continuent et la perte du
                // jour depasse la limite (vu en backtest : -12% pour 5%).
                if (FlattenOnDailyLossLimit)
                {
                    Print("Fermeture complete (perte journaliere max).");
                    FlattenAll();
                }
            }
        }

        // -- Presets ----------------------------------------------------------

        private double EffectiveAtrMultiplier()
        {
            if (Preset == RiskPreset.LowRisk) return 2.5;
            if (Preset == RiskPreset.HighRisk) return 1.0;
            return AtrMultiplier;
        }

        private int EffectiveLevelsPerSide()
        {
            if (Preset == RiskPreset.LowRisk) return 3;
            if (Preset == RiskPreset.HighRisk) return 8;
            return LevelsPerSide;
        }

        private double EffectiveVolumeMultiplier()
        {
            if (Preset == RiskPreset.LowRisk) return 1.0;
            if (Preset == RiskPreset.HighRisk) return 1.3;
            return VolumeMultiplierPerLevel;
        }

        private double EffectiveTpPerLevelPips()
        {
            if (Preset == RiskPreset.LowRisk) return 100;
            if (Preset == RiskPreset.HighRisk) return 200;
            return TakeProfitPerLevelPips;
        }

        private double EffectiveSlPerLevelPips()
        {
            if (Preset == RiskPreset.LowRisk) return 250;
            if (Preset == RiskPreset.HighRisk) return 400;
            return StopLossPerLevelPips;
        }

        private double EffectiveFloatingDrawdownLimitPercent()
        {
            if (Preset == RiskPreset.LowRisk) return 6.0;
            if (Preset == RiskPreset.HighRisk) return 15.0;
            return FloatingDrawdownLimitPercent;
        }

        private double EffectiveMaxDrawdownStartEquityPercent()
        {
            if (Preset == RiskPreset.LowRisk) return 12.0;
            if (Preset == RiskPreset.HighRisk) return 30.0;
            return MaxDrawdownStartEquityPercent;
        }

        // -- Affichage graphique ------------------------------------------------

        private void DrawDebugInfo()
        {
            if (Chart == null)
                return;

            GetBasketTotals(out var netProfit, out _, out var totalVolume, out var count);

            var status = _hardStopped ? "ARRET D'URGENCE" : (_dailyLossLimitHit || _dailyTradeCapReached ? "PAUSE" : "ACTIF");
            var text = string.Format(
                "Maya-style Grid ATR - {0}\nEspacement: {1:0.0} pips\nPositions: {2}, Volume: {3:0.00} lots, PnL: {4:0.00}\nAncre: {5}{6}",
                status, GetGridStepPips(), count, Symbol.VolumeInUnitsToQuantity(totalVolume), netProfit,
                _gridAnchor.HasValue ? _gridAnchor.Value.ToString("0.00") : "-",
                Preset != RiskPreset.Custom ? "\nPreset: " + Preset : "");

            var color = _hardStopped ? Color.Red : (status == "PAUSE" ? Color.Orange : Color.LimeGreen);
            Chart.DrawStaticText("maya_grid_status", text, VerticalAlignment.Top, HorizontalAlignment.Right, color);
        }
    }
}

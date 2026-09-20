# MayaStyleGoldGridBot — cBot cTrader

Bot de grille adaptative basée sur l'ATR pour XAU/USD, reconstruit à partir de la fiche paramètres publique du produit commercial **Maya Gold Grid ATR** (ordres en grille autour d'une ancre, SL/TP par niveau, seed initial, gestion de panier, garde-fous de risque multiples, filtre de spread avec hystérésis, sortie de session configurable).

**Ce n'est pas une copie du produit.** Je n'ai aucun accès à son code — seulement à la description de ses paramètres, fournie par l'utilisateur. Les chiffres de performance annoncés (+35%/mois, cohérence sur 6 ans) ne sont ni vérifiables ni reproductibles ici et ne doivent surtout pas être pris comme une attente pour ce bot.

## ⚠️ Avertissement — à lire avant même la démo

Contrairement à une première ébauche de ce bot, **chaque position (seed et niveaux de grille) a bien son propre stop loss et take profit en pips** — ce n'est donc pas un martingale à risque totalement illimité par position. Le risque reste néanmoins structurellement plus élevé que GoldTrendBot, VolumeProfileMtfBot, IctSmcIchimokuBot ou SmtFiboSessionsBot :

- **Plusieurs positions corrélées** (même instrument, souvent même sens) peuvent être ouvertes simultanément, chacune consommant sa propre marge.
- **"Reconstruire les ordres manquants"** replace automatiquement un niveau stoppé : une tendance soutenue peut donc enchaîner de nombreux stop loss individuels à la suite. Chacun est limité, mais leur somme peut être lourde — surtout avec un `Multiplicateur de volume par niveau` > 1.
- Les protections de compte (**Max Drawdown**, **Drawdown flottant**) agissent **après coup**, une fois le seuil déjà franchi, pas en amont.

**Avant toute utilisation, même en démo :**

1. Backtestez sur **plusieurs années incluant des phases de forte tendance** (pas seulement des marchés en range)
2. Gardez les plafonds (`Niveaux par côté`, `Volume total max`, `Coupe panier`, `Drawdown flottant max`) **volontairement serrés** plutôt que d'optimiser agressivement sur un backtest
3. Surveillez le **niveau de marge** en continu si vous le lancez en réel

## Logique de la stratégie

1. **Seed** : quand le bot est complètement à plat (aucune position, aucun ordre en attente), il ouvre un trade au marché dans `Direction du seed` (Buy/Sell/Auto via une EMA longue) — ce prix d'entrée devient l'**ancre** de la grille
2. **Grille d'ordres limites** : autour de l'ancre, le bot place des ordres limites espacés de `Multiplicateur ATR × ATR courant`, jusqu'à `Niveaux par côté`, du côté (ou des deux côtés) défini par `Cote de la grille`
3. **SL/TP par niveau** : chaque position (seed compris) a son propre stop loss et take profit en pips — un niveau peut donc se fermer tout seul, indépendamment du panier
4. **Reconstruction automatique** : si `Reconstruire les ordres manquants` est actif, tout niveau vidé (stoppé, pris en profit, ou annulé) est immédiatement remplacé par un nouvel ordre limite à la même position de grille
5. **Panier unique** : toutes les positions du bot (seed + tous les niveaux, tous côtés confondus) forment **un seul panier** suivi par PnL cumulé
   - **TP panier (montant)** : ferme tout dès que le profit net cumulé atteint ce montant
   - **TP panier (pips vs VWAP)** : ferme tout quand le prix s'éloigne suffisamment du prix moyen pondéré par le volume, calculé côté net dominant (non applicable si le panier est parfaitement équilibré)
   - **Coupe panier (montant)** : ferme tout si la perte nette cumulée atteint ce montant (protection)
6. **Garde-fous de risque** : limite de trades par jour/session (avec mode "garder" ou "annuler" les ordres en attente restants), perte journalière réalisée max (%), Max Drawdown depuis le capital initial (%, arrêt **permanent** du bot), drawdown flottant max (%, ferme tout mais la grille peut se reconstruire), marge libre minimale après chaque ordre
7. **Time-stop optionnel** : ferme une position en perte depuis trop longtemps, indépendamment de son SL
8. **Filtre de spread avec hystérésis** : bloque les nouveaux ordres (et optionnellement annule les ordres en attente) si le spread est trop large, avec une marge de récupération pour éviter les allers-retours
9. **Session** : fenêtre horaire optionnelle avec 4 actions de sortie possibles (tout fermer / annuler les ordres en attente seulement / fermer si profit net positif / fermer si profit brut positif), plus une clôture de fin de journée indépendante

Le bot tourne sur `OnTick` (pas seulement à la clôture de bougie) car la grille doit réagir au prix en continu.

## Choix d'interprétation (spécification ambiguë sur ces points)

- **Panier unique combiné** plutôt qu'un panier par côté : plus cohérent pour un mode `Both` (grille couverte des deux côtés), où un PnL net global a plus de sens que deux paniers séparés.
- **TP panier (pips vs VWAP)** utilise le côté net dominant (plus de volume Buy que Sell, ou l'inverse) pour définir le sens de la distance ; si le panier est exactement équilibré, ce déclencheur est ignoré ce tick-là.
- **Niveau de marge min après ordre** : utilise la convention standard "niveau de marge" (Equity ÷ Marge utilisée × 100, la même qu'affichée par cTrader), pas un % de l'equity directement — 100-200% sont des valeurs typiques. Estimation de la marge requise approximative (notionnel ÷ levier du compte), sans tenir compte d'éventuelles conversions de devises.
- **Presets** : ne remplacent que les paramètres qui définissent la "forme du risque" (espacement, niveaux, multiplicateur de volume, TP/SL par niveau, drawdown flottant, max drawdown) — pas les montants en devise du panier (TP/coupe en $), qui dépendent trop de la taille du compte pour être préréglés utilement.

## Installation dans cTrader

1. Ouvrez **cTrader**, connectez-vous à votre compte Pepperstone (**démo obligatoire en premier**, voir avertissement ci-dessus)
2. Allez dans l'onglet **Automate**
3. Cliquez sur **Add** → **New cBot**
4. Copiez le contenu de [`MayaStyleGoldGridBot.cs`](MayaStyleGoldGridBot.cs) dans l'éditeur de code intégré
5. Cliquez sur **Build** — ça doit compiler sans erreur
6. Glissez le cBot sur un graphique **XAUUSD** (le timeframe du graphique ne sert qu'au calcul de l'ATR et à l'affichage ; la logique de grille tourne sur chaque tick)
7. Choisissez un preset (`LowRisk` recommandé pour commencer) ou réglez les paramètres manuellement
8. Lancez en **backtest sur plusieurs années**, puis en **compte démo pendant plusieurs semaines/mois**

## Workflow avec Git / GitHub

Même principe que les autres bots du dépôt : ce dépôt reste la source de vérité versionnée. À chaque modification, recopiez le contenu mis à jour dans l'éditeur cTrader puis **Build** à nouveau.

## Paramètres clés

| Paramètre | Défaut | Rôle |
|---|---|---|
| Preset de risque | Custom | LowRisk/HighRisk/Custom |
| Cote de la grille | BuyOnly | Both / BuyOnly / SellOnly |
| Multiplicateur ATR | 1.5 | Espacement = ATR × ce multiple |
| TP / SL par niveau (pips) | 150 / 300 | Sur chaque position individuellement |
| Niveaux par cote | 5 | Plafond du nombre de niveaux |
| Volume de base (lots) | 0.01 | Taille du seed et du niveau 0 |
| Multiplicateur de volume par niveau | 1.0 | 1.0 = constant, >1.0 = martingale |
| Volume total max (lots) | 1.0 | Plafond dur d'exposition |
| Direction du seed | SellOnly | BuyOnly / SellOnly / Auto (EMA) |
| TP panier (montant) | 20.0 | Ferme tout en profit |
| Coupe panier (montant) | 50.0 | Ferme tout en perte (protection) |
| Limite de trades journaliere | 20 | Par session ou par jour UTC |
| Perte journaliere max (%) | 5.0 | Basee sur le PnL realise |
| Max Drawdown capital initial (%) | 20.0 | Arret permanent du bot |
| Drawdown flottant max (%) | 10.0 | Ferme tout, la grille peut se reconstruire |
| Niveau de marge min apres ordre (%) | 100.0 | Bloque un ordre si le niveau de marge projete (Equity/Marge utilisee) tomberait sous ce seuil |
| Max Spread (pips) | 50 | 0 = desactive |

### Valeurs des presets

| | LowRisk | HighRisk |
|---|---|---|
| Multiplicateur ATR | 2.5 (espacement plus large) | 1.0 (espacement plus serré) |
| Niveaux par côté | 3 | 8 |
| Multiplicateur de volume | 1.0 | 1.3 |
| TP / SL par niveau (pips) | 100 / 250 | 200 / 400 |
| Drawdown flottant max (%) | 6 | 15 |
| Max Drawdown capital initial (%) | 12 | 30 |

## Prochaines étapes possibles

- Distinguer un panier par côté en mode `Both`, avec une option pour choisir panier combiné vs séparé
- Journaliser l'historique des paniers (niveaux, durée, PnL) dans un fichier pour analyse
- Remplacer l'estimation de marge approximative par un appel natif cTrader si l'API en expose un plus précis

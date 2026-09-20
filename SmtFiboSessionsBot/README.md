# SmtFiboSessionsBot — cBot cTrader

cBot basé sur le modèle de liquidité inter-sessions **Tokyo → Londres → New York**, avec entrée sur retracement **Fibonacci** (zone OTE) et confirmation optionnelle par **divergence SMT** contre un symbole corrélé. Multi-timeframe : structure sur un timeframe dédié, déclenchement d'entrée sur le graphique d'exécution.

Construit directement sur l'observation : *"Londres récupère toujours la liquidité de Tokyo, et la session de New York continue le mouvement de Londres."*

## ⚠️ Avertissement important — expérimental, comme IctSmcIchimokuBot

**"SMT/ICT" n'a pas de règles canoniques uniques.** Ce bot fait des choix d'interprétation précis (voir "Différences" ci-dessous). Avant même d'envisager un compte démo :

1. **Backtestez très longuement** (plusieurs mois/années) — c'est une stratégie à un seul trade potentiel par jour, il faut donc beaucoup de jours pour juger statistiquement
2. **Vérifiez visuellement** les niveaux dessinés (range asiatique en pointillés gris, zone OTE en rectangle vert/orange) par rapport à ce que vous attendez
3. **Si vous activez la divergence SMT**, vérifiez que le symbole corrélé choisi est pertinent pour l'instrument tradé et disponible chez votre broker
4. **Ne passez en réel qu'après une validation statistique sérieuse**, avec un capital que vous pouvez perdre

## Logique de la stratégie

Une seule direction de trade est suivie par jour (UTC) :

1. **Range asiatique** : plus haut/plus bas relevés pendant la session Tokyo
2. **Liquidity sweep** : pendant la session Londres, une bougie mèche au-delà du plus haut ou plus bas asiatique puis clôture à l'intérieur → fixe le biais directionnel du jour (sweep du low → biais haussier, et inversement)
3. **Divergence SMT (optionnelle)** : au moment du sweep, vérifie si un symbole corrélé a fait (ou pas) le même sweep sur son propre range asiatique — l'absence de sweep côté du corrélé = divergence = signal renforcé
4. **Change of Character / Break of Structure (BOS)** : la clôture franchit le swing opposé le plus récent → confirme le retournement post-sweep
5. **Zone Fibonacci (OTE)** : mesurée entre le point du sweep (A) et le plus haut/bas atteint depuis (B, mis à jour en continu tant que le prix ne retrace pas) → zone d'entrée = retracement 61.8%-78.6% de A-B
6. **Entrée** : quand le prix revient dans la zone avec une bougie de rejet dans le sens du setup, pendant la session Londres **ou** New York (celle-ci est censée prolonger le mouvement initié par Londres) → entrée
7. **Premier contact uniquement** (par défaut) : une zone touchée sans rejet valide est abandonnée plutôt que de rester active pour un retest ultérieur
8. **Stop loss** au niveau du sweep (+ buffer), **take profit** à une extension Fibonacci du mouvement A-B (1.618 par défaut), **break-even optionnel à 1R**

## Multi-timeframe

- Le **range asiatique, le sweep, le BOS et la zone Fibonacci** sont calculés sur un timeframe "structure" dédié (`Timeframe structure/sessions`, 15 min par défaut), via un abonnement direct aux clôtures de bougie de ce timeframe (indépendant du graphique sur lequel tourne le bot).
- Le **déclenchement d'entrée** (retracement dans la zone + bougie de rejet) est surveillé sur le **graphique d'exécution** — celui sur lequel vous glissez le cBot. Mettez-le égal ou plus bas que le timeframe structure (ex: structure en 15 min, exécution en 1-5 min) pour un timing d'entrée plus précis, un stop loss plus serré et un meilleur ratio risque/rendement, sans changer l'analyse de fond.
- Si vous laissez les deux timeframes identiques, le bot fonctionne normalement sur un seul flux de données (pas de flux redondant créé).

## Différences et choix assumés

- **Un seul setup par jour UTC** : le premier sweep valide de la journée fixe la direction ; aucun retournement de biais n'est cherché ensuite le même jour.
- **Range asiatique complet requis** avant qu'un sweep puisse être détecté ; si le bot démarre en cours de session Tokyo, le premier jour peut ne rien trader.
- **Zone Fibonacci valable pour un seul contact par défaut** (`Entree au premier contact uniquement`), mêmes principes de "mitigation" que dans IctSmcIchimokuBot.
- **Divergence SMT** vérifiée sur les valeurs de plus haut/bas de la bougie de sweep elle-même, en assumant un alignement temporel des bougies entre le symbole principal et le symbole corrélé (même broker et timeframe). Si le symbole corrélé est invalide/indisponible, le filtre SMT se désactive automatiquement (log d'avertissement) et la stratégie continue sans lui.
- **Take profit basé sur une extension Fibonacci** (A + (B-A) × extension) plutôt qu'un simple multiple R, pour rester cohérent avec le thème Fibonacci de bout en bout.
- **Horaires de session par défaut** (UTC, sans ajustement été/hiver) : Tokyo 00h-07h, Londres 07h-16h, New York 13h-22h.
- **Sécurités reprises des autres bots du dépôt** : filtre de spread, pause après pertes consécutives, limite de perte journalière, logs de fermeture de position.

## Installation dans cTrader

1. Ouvrez **cTrader**, connectez-vous à votre compte Pepperstone (démo recommandé en premier lieu)
2. Allez dans l'onglet **Automate**
3. Cliquez sur **Add** → **New cBot**
4. Copiez le contenu de [`SmtFiboSessionsBot.cs`](SmtFiboSessionsBot.cs) dans l'éditeur de code intégré
5. Cliquez sur **Build** — ça doit compiler sans erreur
6. Glissez le cBot sur un graphique **1-5 min** (le timeframe structure interne gère l'analyse 15 min par défaut), réglez les paramètres
7. Si vous activez la divergence SMT, renseignez un symbole corrélé disponible chez votre broker (ex: XAGUSD pour XAUUSD)
8. Lancez d'abord en mode **backtest** sur une longue période, puis en **compte démo**

## Workflow avec Git / GitHub

Même principe que les autres bots du dépôt : ce dépôt reste la source de vérité versionnée. À chaque modification, recopiez le contenu mis à jour dans l'éditeur cTrader puis **Build** à nouveau.

## Paramètres clés

| Paramètre | Défaut | Rôle |
|---|---|---|
| Timeframe structure/sessions | 15 min | Timeframe de calcul du range asiatique/sweep/BOS/Fibonacci |
| Tokyo / Londres / New York (heures UTC) | 00-07 / 07-16 / 13-22 | Fenêtres de session |
| Lookback structure (swing) | 3 | Bougies de part et d'autre pour confirmer un pivot (référence BOS) |
| Entrée au premier contact uniquement | true | Abandonne la zone si le premier contact ne donne pas de rejet valide |
| OTE début/fin retracement | 0.618 / 0.786 | Bornes de la zone d'entrée Fibonacci |
| Extension take-profit | 1.618 | Multiple du mouvement A-B pour le take profit |
| Activer divergence SMT | false | Compare le sweep avec un symbole corrélé |
| Symbole corrélé | XAGUSD | Instrument utilisé pour la divergence SMT |
| Risque par trade (%) | 1.0 | % du capital risqué par trade |
| Break-even à 1R | true | Sécurise la position une fois 1R de profit atteint |
| Max pertes consécutives | 3 | Pause du robot après ce nombre de pertes d'affilée |
| Perte journalière max (%) | 5.0 | Coupe-circuit journalier |
| Max Spread (pips) | 50 | Sécurité anti-spread élevé |

## Prochaines étapes possibles

- Ajouter un ajustement automatique heure été/hiver pour les sessions
- Permettre un 2e setup le même jour si le premier échoue avant BOS
- Reproduire visuellement le range asiatique en rectangle (pas seulement des lignes horizontales)
- Comparer statistiquement (backtest) la divergence SMT activée vs désactivée pour juger de son apport réel

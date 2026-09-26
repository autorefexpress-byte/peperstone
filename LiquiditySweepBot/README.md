# LiquiditySweepBot — cBot cTrader

cBot qui trade les zones de liquidité de l'indicateur **"Liquidity Swings [LuxAlgo]"** (port cTrader : [LiquiditySwings](../LiquiditySwings/README.md)).

> **Licence** : la détection des zones dérive de "Liquidity Swings" © LuxAlgo, sous licence [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/). Ce bot est partagé sous la même licence : **usage non commercial uniquement**.

## ⚠️ Avertissement

Stratégie **non validée** : backtest long (6-12 mois minimum), puis démo, avant tout passage en réel. Aucun gain n'est garanti.

## Stratégie

Au-dessus d'un swing high et sous un swing low s'accumulent des ordres stop (la « liquidité »). Le bot repère ces zones comme l'indicateur, puis attend qu'une bougie aille les chercher :

| Bougie clôturée | Lecture | Action |
|---|---|---|
| Mèche au-dessus d'un swing high, **clôture en dessous** | Liquidité prise, pas de vraie cassure | **VENTE** |
| Mèche sous un swing low, **clôture au-dessus** | Liquidité prise, pas de vraie cassure | **ACHAT** |
| Clôture au-delà du niveau | Vraie cassure | Zone abandonnée, pas de trade (mode `Sweeps`) |

### Mode cassures (option)

Le paramètre **Mode de trading** choisit ce qui est tradé :

| Mode | Sweep (mèche + clôture à l'intérieur) | Cassure (clôture au-delà) |
|---|---|---|
| `Sweeps` (défaut) | Trade **en sens inverse** | Zone abandonnée |
| `Breakouts` | Zone abandonnée | Trade **dans le sens de la cassure** : achat au-dessus d'un swing high, vente sous un swing low |
| `Both` | Trade en sens inverse | Trade dans le sens de la cassure |

Pour une cassure, le stop se place au-delà de l'extrême opposé de la bougie de cassure (+ buffer ATR). Le filtre de tendance s'applique aussi : une cassure haussière n'est achetée qu'en tendance haussière. Si une même bougie donne un achat et une vente (ex. cassure d'un swing high et sweep d'un autre plus haut), le bot s'abstient.

- **Stop loss** : au-delà de la mèche du sweep + un buffer en ATR (plancher en pips, plafond en ATR)
- **Take profit** : `Risk:Reward` × stop (2R par défaut)
- **Filtre de tendance** : vend seulement si le prix est sous l'EMA 50 en 4H, achète seulement s'il est au-dessus
- Une zone ne peut servir qu'une fois ; seules les `Zones suivies par côté` les plus récentes sont gardées

Protections reprises d'IctSmcIchimokuBot : sizing au risque (ou lot fixe), plafond de risque au volume minimum, pause après pertes consécutives (avec reprise le lendemain), limite de perte journalière, fermeture en fin de journée et avant la clôture du vendredi, filtre news US (heure de New York, heure d'été gérée), filtre de spread.

## Installation dans cTrader

1. Onglet **Automate** → **Add** → **New cBot**
2. Copiez [`LiquiditySweepBot.cs`](LiquiditySweepBot.cs) dans l'éditeur, puis **Build**
3. Glissez le bot sur un graphique **EURUSD 15 min** (point de départ conseillé)
4. Backtest long d'abord, puis démo

## Paramètres clés

| Paramètre | Défaut | Rôle |
|---|---|---|
| Mode de trading | Sweeps | `Sweeps`, `Breakouts` ou `Both` (voir ci-dessus) |
| Pivot Lookback | 14 | Bougies de chaque côté pour confirmer un swing |
| Swing Area | WickExtremity | Zone = mèche du pivot, ou bougie entière (`FullRange`) |
| Retours min dans la zone | 0 | Ne trade que les zones revisitées au moins N fois avant le sweep |
| Âge max d'une zone (bougies) | 1000 | Zones plus anciennes oubliées (≈ 3,5 jours en 5 min, 10 jours en 15 min) |
| Zones suivies par côté | 10 | Nombre de swings hauts/bas gardés en mémoire |
| Filtre de tendance / Timeframe / EMA | true / Hour4 / 50 | Sens autorisé selon la tendance de fond |
| Buffer SL (x ATR) | 0.2 | Marge au-delà de la mèche du sweep |
| SL minimum (pips) | 8 | Plancher du stop |
| SL max (x ATR) | 3.0 | Signal ignoré si le stop est plus grand |
| Risk:Reward (R) | 2.0 | TP = stop × R |
| Break-even à 1R | false | Remonte le stop au prix d'entrée à +1R |
| Lot fixe (0 = calcul au risque) | 0 | Lot imposé au lieu du calcul au risque |
| Risque par trade (%) | 1.0 | % du solde perdu au stop |
| Risque max au volume minimum (%) | 3.0 | Plafond quand 0,01 lot est forcé (petit compte) |
| Max pertes consécutives | 3 | Pause ; reprise le lendemain si `Reprendre le lendemain` = true |
| Début / fin des entrées (UTC) | 7 / 17 | Créneau d'entrée (Londres + début New York) |
| Fermer en fin de journée | true | 21h UTC (20h le vendredi), pas d'entrée dans la dernière heure |
| Filtre news | true | Pas d'entrée 15 min avant/après 08:30 New York |
| Max Spread (pips) | 5 | Sécurité anti-spread |
| Logs détaillés | true | Chaque franchissement de zone et la raison exacte quand aucun trade n'est pris |
| Afficher les zones | true | Lignes/zones sur le graphique, flèche sur chaque sweep |

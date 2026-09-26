# IctSmcIchimokuBot — cBot cTrader

cBot combinant des concepts **Smart Money Concepts / ICT** (liquidité, structure de marché, order blocks, Fair Value Gaps) avec un **filtre de biais Ichimoku**, actif uniquement sur les créneaux horaires **Londres** et **New York**.

## ⚠️ Avertissement important — plus expérimental que les autres bots du dépôt

**"SMC/ICT" n'a pas de règles canoniques uniques** — contrairement à un simple croisement de moyennes mobiles, chaque trader/formateur ICT a ses propres variantes (définition exacte d'un order block, d'une liquidity sweep, gestion des FVG, etc.). Ce bot fait des **choix d'interprétation précis**, documentés ci-dessous, mais ce ne sont pas *"les"* règles ICT universelles.

Conséquence pratique : ce bot est **plus expérimental** que [GoldTrendBot](../GoldTrendBot/GoldTrendBot/README.md) et [VolumeProfileMtfBot](../VolumeProfileMtfBot/README.md). Avant même d'envisager un compte démo :

1. **Backtestez très longuement** (plusieurs mois/années), en vérifiant visuellement sur le graphique que les zones dessinées (rectangles vert/orange) correspondent bien à ce que vous attendez d'un order block / FVG
2. **Vérifiez le biais Ichimoku** : ce bot recalcule le nuage manuellement plutôt que d'utiliser l'indicateur Ichimoku natif de cTrader (voir "Différences" ci-dessous) — comparez visuellement avec l'indicateur Ichimoku natif ajouté sur le même graphique avant de faire confiance au filtre
3. **Ne passez en réel qu'après une validation statistique sérieuse**, avec un capital que vous pouvez perdre

## Logique de la stratégie

Sur chaque bougie clôturée :

1. **Structure de marché** : détection de points pivots (swing high/low) confirmés par un nombre de bougies de part et d'autre (fractal simple)
2. **Liquidity sweep** : une bougie mèche au-delà d'un swing récent puis clôture à l'intérieur (chasse aux stops classique ICT)
3. **Change of Character / Break of Structure (CHoCH/BOS)** : après un sweep, la clôture franchit le swing opposé le plus récent → confirme un retournement de structure
4. **Order Block** : la dernière bougie de sens opposé juste avant l'impulsion qui a causé le BOS
5. **Fair Value Gap (FVG)** : premier gap à 3 bougies (imbalance) trouvé entre le sweep et le BOS
6. **Zone d'entrée** : Order Block et FVG sont fusionnés en une seule zone de surveillance (simplification — en théorie ICT ce sont deux zones distinctes avec des priorités différentes)
7. **Entrée** : quand le prix revient dans la zone avec une bougie de rejet dans le sens du setup, **ET** que le biais Ichimoku est aligné (prix au-dessus/en-dessous du nuage), **ET** qu'on est dans un créneau horaire actif (Londres/New York) → entrée
8. **Premier contact uniquement** (par défaut) : si le prix touche la zone sans donner de bougie de rejet valide, elle est abandonnée plutôt que de rester en attente pour un 2e/3e retest — un order block se "mitige" à chaque retest en théorie ICT
9. **Stop loss** au-delà de la zone (+ buffer en pips), **take profit** à un multiple R du risque, **break-even optionnel à 1R**

La détection de structure/sweep/BOS tourne en continu (24h/24) ; seule l'**entrée** est restreinte aux créneaux horaires actifs.

## Différences et choix assumés

- **Ichimoku recalculé manuellement** à partir des prix bruts (plus haut/plus bas glissants), plutôt que via l'indicateur `IchimokuKinkoHyo` natif de cAlgo. Raison : le décalage vers l'avant du nuage (Senkou Span A/B, généralement +26 périodes) peut être géré différemment selon la convention interne de l'indicateur natif, et une erreur de décalage inverserait silencieusement tout le filtre de tendance. Le calcul manuel permet de contrôler précisément ce décalage. **À vérifier visuellement** en comparant avec l'indicateur Ichimoku natif sur le même graphique avant tout usage réel.
- **Ichimoku sur timeframe supérieur par défaut** (`UseHtfIchimoku` = true, 4H par défaut) : le nuage est calculé sur un timeframe plus haut que le graphique d'exécution (où tournent la structure/les entrées), pattern ICT classique "biais HTF + entrées LTF". Comme le calcul est fait à la main (voir point ci-dessus), cette lecture multi-timeframe est directe et fiable — pas besoin de dépendre d'un indicateur natif pour ça. Désactivable pour tout calculer sur le même graphique.
- **Un seul setup Buy et un seul setup Sell suivis à la fois** (pas d'empilement de plusieurs zones en attente).
- **Zone valable pour un seul contact par défaut** (`FirstTouchOnly` = true) : si le prix touche la zone sans bougie de rejet valide, elle est abandonnée plutôt que de rester active pour un retest ultérieur. Désactivable si vous préférez autoriser plusieurs tentatives sur la même zone.
- **Créneaux horaires par défaut** : Londres 07h-10h UTC, New York 12h-15h UTC — ce sont les "killzones" ICT classiques, plus étroites que les sessions générales Londres/New York (08h-17h / 13h-22h UTC) utilisées dans VolumeProfileMtfBot. Réglables via les paramètres. **Pas d'ajustement automatique heure été/hiver** (UTC fixe).
- **Order Block + FVG fusionnés en une seule zone** de surveillance, par simplicité, plutôt que deux zones avec des règles de priorité distinctes.
- **Position sizing basé sur le risque** (comme GoldTrendBot) : volume calculé à partir d'un % du capital risqué et de la distance du stop loss, pas d'un % de notionnel.
- **Sécurités reprises des autres bots du dépôt** : filtre de spread, pause après pertes consécutives, limite de perte journalière (coupe-circuit), logs de fermeture de position.

## Installation dans cTrader

1. Ouvrez **cTrader**, connectez-vous à votre compte Pepperstone (démo recommandé en premier lieu)
2. Allez dans l'onglet **Automate**
3. Cliquez sur **Add** → **New cBot**
4. Copiez le contenu de [`IctSmcIchimokuBot.cs`](IctSmcIchimokuBot.cs) dans l'éditeur de code intégré
5. Cliquez sur **Build** (le bouton marteau) — ça doit compiler sans erreur
6. Glissez le cBot sur un graphique **5 ou 15 min** (les concepts ICT s'utilisent typiquement sur des timeframes courts à l'intérieur des créneaux horaires), réglez les paramètres
7. Ajoutez l'indicateur **Ichimoku Kinko Hyo** natif de cTrader sur un graphique **au timeframe configuré dans `Timeframe Ichimoku`** (4H par défaut, pas le graphique d'exécution) pour comparer visuellement avec le calcul interne du bot avant de lui faire confiance
8. Lancez d'abord en mode **backtest** sur une longue période, puis en **compte démo**

## Workflow avec Git / GitHub

Même principe que les autres bots du dépôt : ce dépôt reste la source de vérité versionnée. À chaque modification, recopiez le contenu mis à jour dans l'éditeur cTrader puis **Build** à nouveau.

## Paramètres clés

| Paramètre | Défaut | Rôle |
|---|---|---|
| Tenkan-sen / Kijun-sen / Senkou Span B (périodes) | 9 / 26 / 52 | Périodes Ichimoku classiques |
| Ichimoku sur timeframe supérieur | true (4H) | Calcule le nuage sur un timeframe plus haut que le graphique d'exécution |
| Exiger croisement Tenkan/Kijun | false | Filtre de biais plus strict (optionnel) |
| Lookback structure (swing) | 3 | Bougies de part et d'autre pour confirmer un pivot |
| Fenêtre de validité (barres) | 15 | Durée de vie d'un sweep en attente de BOS, ou d'une zone en attente de retracement |
| Entrée au premier contact uniquement | true | Abandonne la zone si le premier contact ne donne pas de rejet valide |
| Lot fixe (0 = calcul au risque) | 0 | Si > 0 (ex. 0.01), trade toujours ce lot au lieu du calcul au risque ; le risque réel est affiché dans le log |
| Risque par trade (%) | 1.0 | % du capital risqué par trade |
| Risk:Reward (R) | 2.0 | Take profit = distance du stop × ce multiple |
| Stops basés sur l'ATR | true | Buffer du stop en multiple d'ATR au lieu de pips fixes, et abandon des zones trop larges |
| Période ATR | 14 | ATR calculé sur le graphique d'exécution |
| Buffer SL (x ATR) | 0.3 | Marge ajoutée au-delà de la zone pour le stop loss |
| SL max (x ATR) | 4.0 | Zone abandonnée si le stop dépasse ce multiple de l'ATR (0 = désactivé) — évite les stops démesurés type 677 pips en 5 min |
| SL minimum (pips) | 8 | Plancher du stop (tous modes) : évite les stops de 3 pips avec un gros volume, balayés par le spread ou un glissement |
| Buffer Stop Loss (pips) | 20 | Marge fixe au-delà de la zone, utilisée seulement si le mode ATR est désactivé |
| Autoriser volume minimum (petit compte) | true | Si le volume calculé au risque est sous le minimum du broker (0,01 lot), trade ce minimum… |
| Risque max au volume minimum (%) | 3.0 | …seulement si le risque réel du stop reste sous ce plafond, sinon l'entrée est ignorée (message dans le log) |
| Break-even à 1R | true | Sécurise la position une fois 1R de profit atteint |
| Max pertes consécutives | 3 | Pause du robot après ce nombre de pertes d'affilée |
| Perte journalière max (%) | 5.0 | Coupe-circuit : suspend les entrées pour le reste de la journée (UTC) |
| Fermer en fin de journée | true | Ferme la position à l'heure de fermeture et bloque les entrées après : rien d'ouvert la nuit ni le week-end |
| Heure de fermeture (UTC) | 21 | Heure de cette fermeture quotidienne |
| Heure de fermeture vendredi (UTC) | 20 | Fermeture avancée le vendredi : le marché ferme vers 21h UTC, après il n'y a plus de tick pour fermer avant la réouverture du dimanche |
| Dernière entrée (heures avant fermeture) | 1 | Pas de nouvelle entrée dans la dernière heure avant la fermeture |
| Durée max en position (heures) | 0 | Ferme une position qui n'a touché ni SL ni TP après ce délai (0 = désactivé) |
| Filtre news | true | Bloque les nouvelles entrées autour des annonces US (les positions ouvertes ne sont pas touchées) |
| Heures news (heure de New York) | 08:30 | Heures des annonces, séparées par des virgules (ex. `08:30,10:00`) ; converties automatiquement en UTC avec l'heure d'été américaine (12:30 UTC en été, 13:30 en hiver) |
| Minutes avant / après l'annonce | 15 / 15 | Largeur de la fenêtre bloquée |
| Max Spread (pips) | 50 | Sécurité anti-spread élevé |
| Créneau Londres | 07h-10h UTC | Killzone ICT classique |
| Créneau New York | 12h-15h UTC | Killzone ICT classique |

## Prochaines étapes possibles

- Distinguer Order Block et FVG comme deux zones séparées avec priorités différentes, plutôt que de les fusionner
- Ajouter la détection de liquidité externe (equal highs/lows) comme cible de take profit alternative
- Ajouter un ajustement automatique heure été/hiver pour les créneaux horaires
- Comparer statistiquement (backtest) le filtre Ichimoku activé vs désactivé pour juger de son apport réel

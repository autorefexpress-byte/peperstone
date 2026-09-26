# VolumeProfileMtfBot — cBot cTrader

Port en cBot cTrader (C#) d'une stratégie TradingView "Volume Profile Multi-Timeframe" (Pine Script v5) : entrées sur zones de profil de volume (POC / VAH / VAL) avec filtres de tendance multi-timeframe, confirmation RSI/volume, gestion du risque avec sortie en deux temps (TP1/TP2) et pause automatique après une série de pertes.

## ⚠️ Avertissement important

**Aucune stratégie de trading n'est fiable à 100%.** Comme pour [GoldTrendBot](../GoldTrendBot/GoldTrendBot/README.md), ce bot doit être :

1. **Testé en backtest** dans cTrader (onglet *Backtesting*) sur plusieurs années de données
2. **Lancé en compte démo** pendant plusieurs semaines/mois avant de risquer de l'argent réel
3. **Utilisé uniquement avec de l'argent que vous pouvez vous permettre de perdre**, en commençant petit

## Différences par rapport au script Pine d'origine

Le script TradingView fourni calcule le profil de volume (POC/VAH/VAL) uniquement dans un bloc `if barstate.islast`. En pratique, cela signifie qu'en **backtest Pine**, ce calcul (et donc les signaux) ne se met quasiment jamais à jour sur l'historique — seulement sur la toute dernière bougie chargée. Ce n'est pas gênant pour un indicateur purement visuel, mais ça rend la stratégie peu fiable à tester bar par bar.

Dans cette version cTrader :

- Le profil de volume est **recalculé à chaque clôture de bougie** (`OnBarClosed`), pour que la stratégie soit réellement testable et tradable en continu, en backtest comme en live.
- Le **volume** utilisé est le *tick volume* cTrader (proxy standard pour le forex/CFD, qui n'ont pas de volume centralisé, contrairement aux marchés sur lesquels tourne TradingView).
- La sortie partielle **TP1/TP2** (`strategy.exit` avec deux jambes à 50% dans le script Pine) est reproduite avec **deux positions distinctes** ouvertes en même temps (moitié du volume chacune), chacune avec son propre stop loss et son propre take profit. C'est le pattern natif cTrader pour une sortie partielle : le SL/TP est géré côté broker, pas par une surveillance manuelle du prix.
- La **taille de position est calculée au risque** (% du solde perdu si le stop est touché), et non en % de notionnel comme `default_qty_value` dans le script Pine. Sans levier, un % de notionnel ne dépasse jamais le volume minimum du broker sur un petit compte (avec 200 €, même 100 % n'y arrive pas) : le bot n'aurait jamais tradé. Si le capital ne permet pas deux jambes TP1/TP2, le bot ouvre **une seule position** visant TP2, avec break-even quand le niveau TP1 est atteint.
- Le **tableau de bord** et l'**histogramme de volume** (boxes colorées) du script Pine ne sont pas reproduits à l'identique : seules les lignes POC/VAH/VAL et un texte de statut condensé (robot actif/en pause, niveaux, pertes consécutives) sont affichés sur le graphique cTrader.

La logique de signal (zones VAL/POC/VAH, bougie directionnelle, filtre de volume, filtres de tendance HTF, RSI, filtre de session, pause après pertes consécutives, break-even après TP1) est conservée fidèlement.

## Stratégie

- **Profil de volume** : calculé sur les N dernières bougies (`Barres`, 30 par défaut ≈ 1 semaine de trading en 4H, 6 barres/jour × 5 jours — sur ce timeframe on est en swing trading, la logique "1 session intraday" d'origine ne s'applique plus), avec POC (Point of Control), VAH (Value Area High) et VAL (Value Area Low) sur une Value Area à 70% par défaut
- **Filtres multi-timeframe** : tendance haussière/baissière déterminée par EMA20 vs EMA50 sur un timeframe intermédiaire (Daily par défaut) et un timeframe haut (Weekly par défaut)
- **Signaux d'entrée** (5 conditions réunies) :
  - Niveau VP touché (rebond sur VAL/POC, ou cassure de VAH) selon le sens
  - Bougie directionnelle (corps > mèche opposée)
  - Volume supérieur à sa moyenne × un multiplicateur
  - Tendance HTF alignée
  - RSI pas déjà en zone opposée (optionnel)
- **Gestion du risque** :
  - Stop loss et deux niveaux de take profit (TP1/TP2) définis en % du prix
  - Taille de position calculée au risque (% du solde perdu au stop), répartie entre les deux jambes TP1/TP2, ou une seule position si le capital est trop petit pour deux (voir "Petit compte")
  - Break-even automatique sur la jambe TP2 restante une fois TP1 atteint (optionnel)
  - Pause automatique après un nombre configurable de pertes consécutives (évaluées par round TP1+TP2 combiné, pas par jambe)
  - Limite de perte journalière (%) : suspend les nouvelles entrées jusqu'au lendemain (UTC) si la perte cumulée depuis le début de journée dépasse ce seuil
- **Filtre de session** : trading limité aux sessions Londres/New York (UTC), avec option pour éviter les 30 premières minutes après l'ouverture de chaque session
- **Filtre de spread** : ignore les nouvelles entrées si le spread courant dépasse un seuil configurable (protection anti-actu/illiquidité, comme GoldTrendBot)
- **Logs de fermeture de position** : chaque jambe fermée (TP1/TP2) est journalisée avec son P&L, ainsi qu'un résumé combiné (round complet + compteur de pertes consécutives) une fois les deux jambes closes

Tous ces paramètres sont réglables dans l'interface cTrader (onglet Parameters du bot), regroupés par catégorie (Volume Profile, Multi-Timeframe, Signaux, Risk Management, Session).

## Installation dans cTrader

1. Ouvrez **cTrader**, connectez-vous à votre compte Pepperstone (démo ou réel)
2. Allez dans l'onglet **Automate**
3. Cliquez sur **Add** → **New cBot**
4. Copiez le contenu de [`VolumeProfileMtfBot.cs`](VolumeProfileMtfBot.cs) dans l'éditeur de code intégré
5. Cliquez sur **Build** (le bouton marteau) — ça doit compiler sans erreur
6. Glissez le cBot sur un graphique **4H** (timeframe principal attendu par la stratégie), réglez les paramètres
7. Lancez d'abord en mode **backtest**, puis en **compte démo**

## Workflow avec Git / GitHub

Même principe que pour GoldTrendBot : ce dépôt reste la source de vérité versionnée. À chaque modification, recopiez le contenu mis à jour dans l'éditeur cTrader puis **Build** à nouveau. Git ne fait tourner aucun code — cTrader doit rester ouvert (ou tourner sur un VPS) pour que le bot exécute des trades.

## Paramètres clés

| Paramètre | Défaut | Rôle |
|---|---|---|
| Barres (fenêtre du profil) | 30 | Nombre de bougies utilisées pour calculer le profil de volume |
| Lignes (Row Size) | 24 | Résolution du profil (nombre de niveaux de prix) |
| Value Area % | 70 | % du volume total inclus dans VAH/VAL |
| HTF1 / HTF2 | Daily / Weekly | Timeframes des filtres de tendance |
| Tolérance zone (%) | 0.15 | Distance max au niveau VP pour considérer "proche" |
| Multiplicateur volume min | 1.2 | Volume requis vs sa moyenne pour valider un signal |
| RSI Overbought / Oversold | 65 / 35 | Bornes RSI empêchant un signal à contre-sens |
| Lot fixe (0 = calcul au risque) | 0 | Si > 0 (ex. 0.01), trade toujours ce lot au lieu du calcul au risque ; le risque réel est affiché dans le log |
| Risque par trade (%) | 1.0 | % du solde perdu si le stop est touché (toutes jambes confondues) |
| Autoriser volume minimum (petit compte) | true | Si le volume calculé est sous 0,01 lot, trade ce minimum… |
| Risque max au volume minimum (%) | 3.0 | …seulement si son risque réel reste sous ce plafond, sinon l'entrée est ignorée (message dans le log) |
| Scinder en TP1/TP2 | true | Deux positions comme le script Pine ; bascule automatiquement sur une seule position si le capital ne le permet pas |
| SL/TP basés sur l'ATR | true | SL/TP1/TP2 en multiples de l'ATR du graphique : s'adaptent tout seuls à la volatilité, au timeframe et à l'actif (EURUSD, or…) |
| Période ATR | 14 | Nombre de bougies pour l'ATR |
| SL / TP1 / TP2 (x ATR) | 1.5 / 1.5 / 3.0 | Distances en multiples d'ATR (ratio 1:1:2 du script d'origine) |
| SL minimum (pips) | 5 | Plancher du stop en marché très calme ; TP1/TP2 sont agrandis dans la même proportion |
| Stop Loss / TP1 / TP2 (%) | 2.5 / 2.5 / 5.0 | Utilisés seulement si le mode ATR est désactivé. Distances en % du prix (x4 vs les défauts 5 min d'origine, ratio 1:1:2 conservé — a réaffiner par backtest) |
| Fermer en fin de journée | true | Ferme les positions à l'heure de fermeture et bloque les entrées après : rien d'ouvert la nuit ni le week-end. À désactiver pour du swing en 4H |
| Heure de fermeture (UTC) | 21 | Heure de cette fermeture quotidienne |
| Heure de fermeture vendredi (UTC) | 20 | Fermeture avancée le vendredi : le marché ferme vers 21h UTC, après il n'y a plus de tick pour fermer avant la réouverture du dimanche |
| Dernière entrée (heures avant fermeture) | 1 | Pas de nouvelle entrée dans la dernière heure avant la fermeture |
| Durée max en position (heures) | 0 | Ferme une position qui n'a touché ni SL ni TP après ce délai (0 = désactivé) |
| Break-even après TP1 | true | Sécurise la position restante une fois TP1 atteint |
| Max pertes consécutives | 3 | Nombre de pertes d'affilée avant mise en pause du robot |
| Perte journalière max (%) | 5.0 | Coupe-circuit : suspend les entrées pour le reste de la journée (UTC) si dépassé |
| Max Spread (pips) | 50 | Sécurité anti-spread élevé |
| Filtre de session | true | Limite le trading aux sessions Londres/New York |

## Petit compte (ex. 200 €)

Pour chaque signal, le bot choisit dans cet ordre :

1. **Deux jambes TP1/TP2** avec le volume calculé au risque, si chaque moitié atteint le volume minimum (0,01 lot)
2. **Deux jambes au volume minimum**, si le risque total reste sous `Risque max au volume minimum (%)`
3. **Une seule position** (TP = TP2, stop remonté au break-even quand le niveau TP1 est atteint), au volume calculé ou au minimum si son risque reste sous le plafond
4. Sinon, **entrée ignorée** avec le risque qu'elle aurait pris affiché dans le log

Ordres de grandeur avec 200 € et un stop à 0,4 % (réglages 5 min du script d'origine) :

| Actif | Risque de 0,01 lot au stop | Résultat avec le plafond à 3 % |
|---|---|---|
| EURUSD | ≈ 4 € (≈ 2 %) | Une seule position à 0,01 lot |
| XAUUSD | ≈ 15 € (≈ 7 %) | Entrée ignorée — l'or n'est pas jouable avec ce capital et ce stop |

Au démarrage, le log affiche les caractéristiques du symbole (valeur du pip, volume minimum) pour vérifier ces calculs.

## Prochaines étapes possibles

- Ajouter des alertes cTrader (notifications) en plus des logs `Print`
- Reproduire l'histogramme de volume complet (boxes) sur le graphique
- Logger les trades dans un fichier pour analyse de performance

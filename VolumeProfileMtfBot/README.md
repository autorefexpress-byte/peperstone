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
- Le **tableau de bord** et l'**histogramme de volume** (boxes colorées) du script Pine ne sont pas reproduits à l'identique : seules les lignes POC/VAH/VAL et un texte de statut condensé (robot actif/en pause, niveaux, pertes consécutives) sont affichés sur le graphique cTrader.

La logique de signal (zones VAL/POC/VAH, bougie directionnelle, filtre de volume, filtres de tendance HTF, RSI, filtre de session, pause après pertes consécutives, break-even après TP1) est conservée fidèlement.

## Stratégie

- **Profil de volume** : calculé sur les N dernières bougies (`Barres`, 78 par défaut ≈ une session en 5 min), avec POC (Point of Control), VAH (Value Area High) et VAL (Value Area Low) sur une Value Area à 70% par défaut
- **Filtres multi-timeframe** : tendance haussière/baissière déterminée par EMA20 vs EMA50 sur un timeframe intermédiaire (15 min par défaut) et un timeframe haut (1H par défaut)
- **Signaux d'entrée** (5 conditions réunies) :
  - Niveau VP touché (rebond sur VAL/POC, ou cassure de VAH) selon le sens
  - Bougie directionnelle (corps > mèche opposée)
  - Volume supérieur à sa moyenne × un multiplicateur
  - Tendance HTF alignée
  - RSI pas déjà en zone opposée (optionnel)
- **Gestion du risque** :
  - Stop loss et deux niveaux de take profit (TP1/TP2) définis en % du prix
  - Taille de position en % de l'equity, répartie entre les deux jambes TP1/TP2
  - Break-even automatique sur la jambe TP2 restante une fois TP1 atteint (optionnel)
  - Pause automatique après un nombre configurable de pertes consécutives
- **Filtre de session** : trading limité aux sessions Londres/New York (UTC), avec option pour éviter les 30 premières minutes après l'ouverture de chaque session

Tous ces paramètres sont réglables dans l'interface cTrader (onglet Parameters du bot), regroupés par catégorie (Volume Profile, Multi-Timeframe, Signaux, Risk Management, Session).

## Installation dans cTrader

1. Ouvrez **cTrader**, connectez-vous à votre compte Pepperstone (démo ou réel)
2. Allez dans l'onglet **Automate**
3. Cliquez sur **Add** → **New cBot**
4. Copiez le contenu de [`VolumeProfileMtfBot.cs`](VolumeProfileMtfBot.cs) dans l'éditeur de code intégré
5. Cliquez sur **Build** (le bouton marteau) — ça doit compiler sans erreur
6. Glissez le cBot sur un graphique **5 min** (timeframe principal attendu par la stratégie), réglez les paramètres
7. Lancez d'abord en mode **backtest**, puis en **compte démo**

## Workflow avec Git / GitHub

Même principe que pour GoldTrendBot : ce dépôt reste la source de vérité versionnée. À chaque modification, recopiez le contenu mis à jour dans l'éditeur cTrader puis **Build** à nouveau. Git ne fait tourner aucun code — cTrader doit rester ouvert (ou tourner sur un VPS) pour que le bot exécute des trades.

## Paramètres clés

| Paramètre | Défaut | Rôle |
|---|---|---|
| Barres (fenêtre du profil) | 78 | Nombre de bougies utilisées pour calculer le profil de volume |
| Lignes (Row Size) | 24 | Résolution du profil (nombre de niveaux de prix) |
| Value Area % | 70 | % du volume total inclus dans VAH/VAL |
| HTF1 / HTF2 | 15 min / 1H | Timeframes des filtres de tendance |
| Tolérance zone (%) | 0.15 | Distance max au niveau VP pour considérer "proche" |
| Multiplicateur volume min | 1.2 | Volume requis vs sa moyenne pour valider un signal |
| RSI Overbought / Oversold | 65 / 35 | Bornes RSI empêchant un signal à contre-sens |
| Taille position (% equity) | 10 | Notionnel engagé par trade |
| Stop Loss / TP1 / TP2 (%) | 0.4 / 0.4 / 0.8 | Distances de sortie en % du prix |
| Break-even après TP1 | true | Sécurise la position restante une fois TP1 atteint |
| Max pertes consécutives | 3 | Nombre de pertes d'affilée avant mise en pause du robot |
| Filtre de session | true | Limite le trading aux sessions Londres/New York |

## Prochaines étapes possibles

- Ajouter des alertes cTrader (notifications) en plus des logs `Print`
- Reproduire l'histogramme de volume complet (boxes) sur le graphique
- Ajouter un filtre de spread comme dans GoldTrendBot
- Logger les trades dans un fichier pour analyse de performance

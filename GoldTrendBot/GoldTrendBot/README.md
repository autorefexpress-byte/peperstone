# GoldTrendBot — cBot cTrader (XAU/USD)

cBot de suivi de tendance pour cTrader, pensé pour XAU/USD, utilisable avec n'importe quel broker cTrader (dont Pepperstone).

## ⚠️ Avertissement important

**Aucune stratégie de trading n'est fiable à 100%.** Ce bot n'est pas une machine à profits garantis : c'est un point de départ raisonnable, avec une gestion du risque intégrée, que vous devez :

1. **Tester en backtest** dans cTrader (onglet *Backtesting*) sur plusieurs années de données
2. **Faire tourner en compte démo** pendant plusieurs semaines/mois avant de risquer de l'argent réel
3. **N'utiliser qu'avec de l'argent que vous pouvez vous permettre de perdre**, en commençant petit

Le trading sur marge (CFD, Forex, or) comporte un risque de perte important, y compris la perte totale du capital investi.

## Stratégie

- **Signal d'entrée** : croisement de deux moyennes mobiles (rapide/lente, EMA par défaut)
  - Croisement haussier (rapide au-dessus de la lente) → achat
  - Croisement baissier → vente
- **Filtre de volatilité (ATR)** : le signal n'est pris en compte que si l'ATR actuel est proche ou au-dessus de sa propre moyenne, pour éviter les faux signaux en marché plat/sans tendance
- **Stop loss initial** : basé sur un multiple de l'ATR (s'adapte automatiquement à la volatilité du moment)
- **Trailing stop** : basé sur l'ATR également, ne remonte/descend le stop que dans le sens favorable
- **Taille de position** : calculée automatiquement à partir d'un % du capital risqué par trade (pas un lot fixe)
- **Filtre de spread** : n'entre pas en position si le spread est anormalement large (ex: pendant une actu majeure)

Tous ces paramètres sont réglables directement dans l'interface cTrader (onglet Parameters du bot).

## Installation dans cTrader

1. Ouvrez **cTrader**, connectez-vous à votre compte Pepperstone (démo ou réel)
2. Allez dans l'onglet **Automate**
3. Cliquez sur **Add** → **New cBot** (ou **Import** si vous voulez importer directement le fichier)
4. Copiez le contenu de [`GoldTrendBot/GoldTrendBot.cs`](GoldTrendBot/GoldTrendBot.cs) dans l'éditeur de code intégré
5. Cliquez sur **Build** (le bouton marteau) — ça doit compiler sans erreur
6. Glissez le cBot sur un graphique **XAU/USD**, choisissez le timeframe (H1 recommandé pour commencer), réglez les paramètres
7. Lancez d'abord en mode **backtest**, puis en **compte démo**

## Workflow avec Git / GitHub

L'idée : garder ce dossier comme source de vérité versionnée, et copier le fichier `.cs` mis à jour dans cTrader chaque fois qu'il change.

```bash
# Première fois : créer un dépôt sur GitHub (via le site ou gh CLI), puis :
git remote add origin https://github.com/<votre-compte>/GoldTrendBot.git
git branch -M main
git push -u origin main
```

Pour chaque modification future :
1. Modifiez `GoldTrendBot.cs` (vous-même ou avec mon aide)
2. `git add -A && git commit -m "description du changement"`
3. `git push`
4. Recopiez le contenu mis à jour dans l'éditeur cTrader, puis **Build** à nouveau

Git ne fait tourner aucun code : cTrader doit rester ouvert (ou tourner sur un VPS Windows si vous voulez une exécution 24/7 sans garder votre PC allumé) pour que le bot exécute des trades.

## Paramètres clés

| Paramètre | Défaut | Rôle |
|---|---|---|
| Fast/Slow MA Period | 20 / 50 | Détection de tendance |
| ATR Period / Average Period | 14 / 50 | Mesure et lissage de la volatilité |
| ATR Filter Threshold (%) | 80 | Seuil minimum de volatilité pour trader |
| Stop Loss (x ATR) | 2.0 | Distance du stop initial |
| Trailing Stop (x ATR) | 2.0 | Distance du trailing stop |
| Risk per Trade (%) | 1.0 | % du capital risqué par trade |
| Max Spread (pips) | 50 | Sécurité anti-spread élevé |

## Prochaines étapes possibles

- Ajouter un filtre de tendance long-terme (ex: MA 200 sur timeframe supérieur)
- Ajouter des horaires de trading (éviter les sessions creuses)
- Ajouter un take-profit fixe optionnel en complément du trailing stop
- Logger les trades dans un fichier pour analyse de performance

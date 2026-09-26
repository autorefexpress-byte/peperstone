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
- **Take-profit fixe (optionnel)** : désactivé par défaut ; si activé, un take-profit basé sur un multiple de l'ATR est posé dès l'entrée, en complément du trailing stop
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
| Use Fixed Take Profit | false | Active un take-profit fixe posé à l'entrée |
| Take Profit (x ATR) | 4.0 | Distance du take-profit fixe (si activé) |
| Risk per Trade (%) | 1.0 | % du capital risqué par trade |
| Allow Min Volume Fallback | Oui | Petit compte : trade 0,01 lot si le volume calculé est trop petit |
| Max Risk at Min Volume (%) | 3.0 | Plafond du risque réel quand ce volume minimum est utilisé |
| Max Spread (pips) | 50 | Sécurité anti-spread élevé |

## Réglages pour un petit compte (~200 €)

Sur l'or, le volume minimum est 0,01 lot (≈ 0,088 € par pip chez Pepperstone). Avec 200 € et 1 % de risque (2 €), le volume calculé est toujours **inférieur** à 0,01 lot : sans `Allow Min Volume Fallback`, le bot ne prendrait **aucun** trade. Avec l'option, il trade 0,01 lot tant que le risque réel du stop reste sous `Max Risk at Min Volume (%)`, sinon il saute l'entrée (message dans le log).

Le stop étant en multiple d'ATR, son coût en euros dépend du timeframe. En H1, 2 × ATR vaut souvent 300 à 500 pips (26 à 44 €, soit 13 à 22 % de 200 €) : presque tous les signaux seraient sautés. Réglages de départ proposés :

| Paramètre | Valeur |
|---|---|
| Timeframe du graphique | M5 |
| Fast / Slow MA Period | 20 / 50 |
| ATR Period / Average Period | 14 / 50 |
| ATR Filter Threshold (%) | 80 |
| Stop Loss (x ATR) | 1.5 |
| Trailing Stop (x ATR) | 2.0 |
| Use Fixed Take Profit | Non |
| Risk per Trade (%) | 1.0 (sans effet tant que le volume minimum s'applique) |
| Allow Min Volume Fallback | Oui |
| Max Risk at Min Volume (%) | 3.0 (≈ 6 € max par trade) |
| Max Spread (pips) | 15 |

Une seule position à la fois, donc au plus ≈ 6 € de perte par trade. En dehors du cycle hebdomadaire ci-dessous, il n'y a pas d'arrêt d'urgence global.

## Cycle de 5 jours (lundi → vendredi)

Avec `Weekly Cycle (Mon-Fri)` activé (par défaut), chaque semaine est traitée comme une course séparée :

- Lundi 00h UTC : nouveau solde de départ de la semaine (ligne `New week … starting balance …`).
- **Objectif hebdo** (`Weekly Profit Target`, 10 %) : tout est fermé et le bot ne trade plus jusqu'à lundi.
- **Perte max hebdo** (`Weekly Max Loss`, 10 %) : pareil, la semaine s'arrête là.
- Pas de nouvelle entrée le vendredi après 16h UTC, ni le week-end ; positions fermées le vendredi à 20h UTC (pas de gap du week-end).
- À chaque changement de semaine (et à l'arrêt du bot) : ligne `WEEK SUMMARY … result … trades …`.

Pour juger la stratégie, lancez un backtest de plusieurs mois : chaque semaine y est indépendante, et les lignes `WEEK SUMMARY` donnent directement combien de semaines sont gagnantes ou perdantes.

## Prochaines étapes possibles

- Ajouter un filtre de tendance long-terme (ex: MA 200 sur timeframe supérieur)
- Ajouter des horaires de trading (éviter les sessions creuses)
- Logger les trades dans un fichier pour analyse de performance

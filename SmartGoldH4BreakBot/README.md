# SmartGoldH4BreakBot — cBot cTrader

cBot pour **XAUUSD** qui trade les **cassures de structure 4H** (Smart Money Concepts) avec une **entrée de précision en 1H**. Il est filtré par le volume (VSA), la volatilité (ATR/ADR) et protège le capital. Il est conçu pour être patient : il peut rester plusieurs jours sans trader.

C'est une implémentation personnelle écrite à partir d'une description de stratégie (« cassure 4H + SMC + VSA + ATR/ADR + trailing adaptatif »). Ce n'est pas le code d'un bot commercial.

## ⚠️ Avertissement

- Aucune stratégie ne gagne à tous les coups. **Backtest long en tick data** (plusieurs mois, plusieurs périodes), puis **compte démo**, avant le réel.
- Le bot prend peu de trades (3 par semaine maximum, souvent 0). Il faut **beaucoup de mois** de backtest pour juger le résultat.
- **Petit compte (~200 €)** : les stops sont calculés en 1H, donc larges (souvent 80 à 250 pips, soit 7 à 21 € à 0,01 lot). Avec le plafond de 3 %, beaucoup d'entrées seront ignorées (« Volume minimum = X % de risque… »). La stratégie est prévue pour un capital d'au moins 500 à 1000 €.

## Logique

1. **Structure 4H** : repère les swings (pivots) confirmés. Une bougie 4H qui **clôture** au-dessus du dernier swing high (ou sous le dernier swing low) est un **Break of Structure**. Seule la première clôture au-delà du niveau compte.
2. **Filtres sur la bougie de cassure 4H** :
   - **Pic de volume (VSA)** : volume tick ≥ 1,3 × moyenne des 20 bougies précédentes.
   - **Pas épuisée** : bougie ≤ 2 ATR 4H, clôture dans le haut de la bougie (≥ 50 %).
   - **ADR** : la journée n'a pas déjà consommé plus de 80 % de son range moyen.
   - **Tendance** : clôture du bon côté de l'EMA 50 4H.
   - Un BOS rejeté est affiché dans le log avec la raison, et le niveau n'est plus repris.
3. **Entrée 1H (mode Retest)** : attend que le prix revienne tester le niveau cassé, puis une bougie 1H qui clôture de nouveau au-delà dans le sens du trade.
   - **Fausse cassure** : une clôture 1H nettement de l'autre côté du niveau (0,5 ATR 1H) annule le setup.
   - Le setup **expire** après 48 h.
4. **Stop** sous le creux des 6 dernières bougies 1H (+ 0,3 ATR), borné entre 1 et 3 ATR 1H. **Take profit** à 2,5 R.
5. **Trailing adaptatif** : break-even (+5 pips) à 1 R, puis trailing à 2 ATR 1H à partir de 1,5 R.
6. **Protections** :
   - perte journalière max 5 % (plus d'entrée ce jour-là) ;
   - drawdown max 20 % depuis le plus haut du capital (ferme tout et **arrête le bot**) ;
   - pause de 24 h après une perte ;
   - 3 trades max par semaine ;
   - pas d'entrée le vendredi après 16 h UTC ;
   - filtre de spread.

## Installation dans cTrader

1. **Automate** → **New cBot**, nommez-le `SmartGoldH4BreakBot`.
2. Remplacez tout le code par le contenu de [`SmartGoldH4BreakBot.cs`](SmartGoldH4BreakBot.cs), puis **Build**.
3. Lancez-le sur **XAUUSD**, sur un graphique **M5, M15 ou H1**. L'analyse 4H et 1H est faite en interne ; n'utilisez pas un graphique 4H ou plus grand.
4. Backtest en **tick data** sur une longue période (par exemple janvier → août 2026), puis compte démo.

## Paramètres principaux

| Paramètre | Défaut | Rôle |
|---|---|---|
| Direction | Achat seulement | Achat, vente ou les deux |
| Force des swings | 2 | Bougies 4H de chaque côté pour confirmer un swing |
| Filtre tendance EMA 4H / période | Oui / 50 | Trade seulement dans le sens de l'EMA 4H |
| Pic de volume (x moyenne) | 1,3 | Confirmation VSA de la bougie de cassure |
| Bougie de cassure max (x ATR 4H) | 2,0 | Rejette les bougies trop étirées |
| Force de clôture min (%) | 50 | Clôture dans le haut (achat) ou le bas (vente) de la bougie |
| ADR déjà consommé max (%) | 80 | Évite de courir après une journée déjà très étendue |
| Mode d'entrée | Retest | `Retest` (patient) ou `Immediate` (dès la clôture 4H) |
| Validité du setup (heures) | 48 | Durée d'attente du retest |
| Stop min / max (x ATR 1H) | 1,0 / 3,0 | Bornes du stop |
| Take profit (x R) | 2,5 | Objectif en multiple du risque |
| Break-even à (R) / trailing à partir de (R) | 1,0 / 1,5 | Gestion de la position |
| Risque par trade (%) | 1,0 | Taille de position basée sur le risque |
| Risque max au volume minimum (%) | 3,0 | Plafond de risque réel sur petit compte |
| Réduire le stop pour petit compte | Oui | Raccourcit le stop jusqu'au plafond au lieu d'ignorer l'entrée |
| Stop réduit min (x ATR 1H) | 0,4 | Distance minimale du stop raccourci |
| Perte journalière max (%) | 5 | Coupe-circuit du jour |
| Drawdown max (%) | 20 | Arrêt complet du bot |
| Pause après une perte (heures) | 24 | Anti sur-trading |
| Trades max par semaine | 3 | Anti sur-trading |

## Lire le log

- `BOS haussier valide sur …` : cassure acceptée, attente du retest.
- `BOS … rejeté : …` : cassure refusée, avec la raison (volume, ADR, bougie étirée…).
- `Setup abandonné : …` : fausse cassure ou expiration.
- `Volume minimum = X % de risque …, entrée ignorée` : stop trop large pour la taille du compte.
- `Stop réduit de X à Y pips…` : stop raccourci pour respecter le plafond de risque (petit compte).
- `Position closed (…). Net: … Balance: …` : résultat de chaque trade.
- `===== Résumé =====` (à la fin du backtest) : combien de cassures détectées, rejetées par chaque filtre, setups expirés, entrées ignorées et pourquoi. C'est la première chose à regarder si le bot ne trade pas.

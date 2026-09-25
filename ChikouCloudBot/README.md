# ChikouCloudBot — cBot cTrader

cBot Ichimoku : il prend un signal quand la **Chikou Span sort du nuage** (vert ou rouge), puis n'entre que si d'autres éléments Ichimoku **confirment** dans les bougies qui suivent. Il fonctionne sur n'importe quel symbole (EURUSD, XAUUSD…) et suit un cycle de 5 jours, du lundi au vendredi.

## ⚠️ Avertissement

Aucune stratégie ne gagne à tous les coups. Avant le réel, faites un backtest en **tick data** sur **deux périodes** (par exemple janvier → mai et juin → septembre), puis un compte **démo**. Ne passez en réel que si les deux périodes sont positives.

## Logique

1. **Signal** : la Chikou Span (la clôture actuelle, tracée 26 bougies en arrière) **clôture au-dessus du nuage** de cette époque pour un achat, **en dessous** pour une vente. À la bougie précédente, elle était encore dans le nuage ou de l'autre côté. La couleur du nuage traversé n'a pas d'importance.
2. **Confirmation** : dans les 12 bougies suivantes, toutes les confirmations activées doivent être vraies en même temps :
   - le prix clôture **hors du nuage actuel**, du bon côté ;
   - **Tenkan au-dessus de Kijun** pour un achat (en dessous pour une vente) ;
   - la **Chikou est libre** : au-dessus du plus haut de la bougie d'il y a 26 périodes (sous le plus bas pour une vente) ;
   - option : **nuage futur** de la bonne couleur ;
   - option : **bougie de confirmation** qui clôture dans le sens du trade.

   Si la Chikou rentre dans le nuage avant la confirmation, le signal est **annulé**. S'il n'est pas confirmé à temps, il **expire**.
3. **Stop** sous la Kijun (+ 0,5 ATR), borné entre 1 et 3 ATR. Taille de position : **1 % de risque** par trade.
4. **Sortie** Ichimoku classique : clôture de l'autre côté de la **Kijun**. Break-even à 1R. Take profit en R optionnel (désactivé par défaut).
5. **Semaine** :
   - pas d'entrée le week-end, ni le vendredi après 16h UTC ;
   - tout est fermé le vendredi à 20h UTC ;
   - perte max de 4 % par jour et de 6 % par semaine ;
   - une ligne `WEEK SUMMARY` par semaine.

## Installation

1. Dans cTrader : **Automate** → **New cBot**, nommez-le `ChikouCloudBot`.
2. Collez le contenu de [`ChikouCloudBot.cs`](ChikouCloudBot.cs), puis cliquez sur **Build**.
3. Lancez-le sur **EURUSD en H1** (conseillé ; M15 possible).
4. Faites les backtests en tick data sur les deux périodes.

## Lire le log

| Ligne | Signification |
|---|---|
| `Chikou sortie au-dessus du nuage : attente de confirmation…` | Nouveau signal |
| `Signal expiré sans confirmation (manquait : …)` | Liste des confirmations qui manquaient |
| `Signal annulé : la Chikou est revenue dans le nuage.` | Fausse sortie de la Chikou |
| `Buy ouvert (signal confirmé)…` / `Position closed (…)` | Trades |
| `WEEK SUMMARY …` | Résultat de chaque semaine |
| `===== Résumé =====` (à la fin) | Nombre de signaux, confirmés, expirés, annulés, et de trades |

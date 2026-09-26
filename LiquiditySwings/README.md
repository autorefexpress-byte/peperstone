# LiquiditySwings — indicateur cTrader

Port en indicateur cTrader (C#) de l'indicateur TradingView **"Liquidity Swings [LuxAlgo]"** (Pine Script v5).

> **Licence** : l'original est © LuxAlgo, sous licence [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/). Ce port reste sous la même licence : **usage non commercial uniquement**, attribution à LuxAlgo conservée, et toute version modifiée doit être partagée sous la même licence. Ne le revendez pas et ne l'intégrez pas dans un produit payant.

C'est un **indicateur** (affichage uniquement) : il ne passe aucun ordre.

## Ce qu'il affiche

Pour chaque **pivot haut** (rouge) et **pivot bas** (sarcelle) :

- **Zone de liquidité** : la mèche du pivot (`Wick Extremity`) ou la bougie entière (`Full Range`)
- **Ligne de niveau** : prolongée vers la droite tant que le prix n'a pas clôturé au-delà ; elle passe en **pointillés** une fois cassée
- **Bloc** : sa largeur = nombre de bougies revenues dans la zone depuis le pivot (plus il est large, plus la zone a été « travaillée »)
- **Étiquette** : volume accumulé dans la zone (format 1.2K, 3.4M…)

## Installation dans cTrader

1. Onglet **Automate** → **Add** → **New Indicator** (pas « New cBot »)
2. Copiez le contenu de [`LiquiditySwings.cs`](LiquiditySwings.cs) dans l'éditeur
3. **Build** (bouton marteau)
4. Sur un graphique : **Indicators** → **Custom** → **LiquiditySwings**

## Paramètres

| Paramètre | Défaut | Rôle |
|---|---|---|
| Pivot Lookback | 14 | Bougies de chaque côté pour confirmer un pivot |
| Swing Area | WickExtremity | Zone = mèche du pivot, ou `FullRange` = bougie entière |
| Filter Areas By | Count | Filtrer les zones par nombre de retours (`Count`) ou par volume (`Volume`) |
| Filter Value | 0 | Une zone n'est affichée (ligne, bloc, étiquette) qu'au-delà de cette valeur |
| Swing High / Swing Low | true | Afficher les pivots hauts / bas |
| Swing High / Low Color | Red / Teal | Couleur par nom (`Red`, `Teal`, `Orange`…) ou en hexa (`#FF0000`) |
| Area Opacity (0-255) | 128 | Opacité des blocs |
| Labels Size | Tiny | Taille des étiquettes |

## Différences avec le script d'origine

- **Volume** : tick volume cTrader (le forex/CFD n'a pas de volume centralisé), donc les valeurs affichées ne sont pas comparables à celles de TradingView.
- **Intrabar Precision** non reprise : le volume de la bougie entière est utilisé (comportement par défaut du script d'origine).
- **Bougies clôturées uniquement** : la bougie en cours n'est pas prise en compte, les niveaux ne bougent donc pas en temps réel.
- Un pivot est confirmé `Pivot Lookback` bougies **après** qu'il s'est formé (comme dans le script Pine) : ce n'est pas un signal en temps réel.

// This work is licensed under a Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0) https://creativecommons.org/licenses/by-nc-sa/4.0/
// Original Pine Script indicator "Liquidity Swings [LuxAlgo]" (c) LuxAlgo.
// cTrader (C#) port - same license (CC BY-NC-SA 4.0), non-commercial use only.

using System;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Indicators
{
    // ============================================================================
    // LiquiditySwings
    // ----------------------------------------------------------------------------
    // Port cTrader de l'indicateur TradingView "Liquidity Swings [LuxAlgo]".
    //
    // Pour chaque pivot haut (swing high) et pivot bas (swing low) :
    //   - une zone de liquidite (meche du pivot, ou bougie entiere) ;
    //   - une ligne de niveau prolongee vers la droite tant que le prix ne l'a
    //     pas cassee en cloture (pointillee ensuite) ;
    //   - un bloc dont la largeur = nombre de bougies revenues dans la zone ;
    //   - une etiquette avec le volume accumule dans la zone.
    //
    // Differences assumees par rapport au script Pine :
    //   - Le volume est le tick volume cTrader (pas de volume centralise sur le
    //     forex/CFD).
    //   - L'option "Intrabar Precision" (volume lu sur un timeframe inferieur)
    //     n'est pas reprise : le volume de la bougie entiere est utilise, comme
    //     avec l'option desactivee dans le script d'origine (reglage par defaut).
    //   - Les calculs se font sur les bougies CLOTUREES uniquement (la bougie en
    //     cours n'est pas prise en compte), pour que les niveaux ne bougent pas en
    //     temps reel.
    //   - Couleurs saisies par nom ("Red", "Teal"...) ou en hexa ("#FF0000").
    // ============================================================================
    [Indicator(IsOverlay = true, TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class LiquiditySwings : Indicator
    {
        public enum SwingAreaMode
        {
            WickExtremity,
            FullRange
        }

        public enum FilterMode
        {
            Count,
            Volume
        }

        public enum LabelSizeMode
        {
            Tiny,
            Small,
            Normal
        }

        [Parameter("Pivot Lookback", DefaultValue = 14, MinValue = 1, MaxValue = 200)]
        public int Length { get; set; }

        [Parameter("Swing Area", DefaultValue = SwingAreaMode.WickExtremity)]
        public SwingAreaMode Area { get; set; }

        [Parameter("Filter Areas By", DefaultValue = FilterMode.Count)]
        public FilterMode FilterBy { get; set; }

        [Parameter("Filter Value", DefaultValue = 0.0, MinValue = 0)]
        public double FilterValue { get; set; }

        [Parameter("Swing High", Group = "Style", DefaultValue = true)]
        public bool ShowTop { get; set; }

        [Parameter("Swing High Color", Group = "Style", DefaultValue = "Red")]
        public string TopColorName { get; set; }

        [Parameter("Swing Low", Group = "Style", DefaultValue = true)]
        public bool ShowBottom { get; set; }

        [Parameter("Swing Low Color", Group = "Style", DefaultValue = "Teal")]
        public string BottomColorName { get; set; }

        [Parameter("Area Opacity (0-255)", Group = "Style", DefaultValue = 128, MinValue = 0, MaxValue = 255)]
        public int AreaAlpha { get; set; }

        [Parameter("Labels Size", Group = "Style", DefaultValue = LabelSizeMode.Tiny)]
        public LabelSizeMode LabelSize { get; set; }

        private const string Prefix = "LuxLiqSwings_";

        private sealed class SideState
        {
            public bool IsTop;
            public string Name;
            public Color LineColor;
            public Color ZoneColor;
            public Color AreaColor;

            public double Top = double.NaN;
            public double Bottom = double.NaN;
            public bool Crossed;
            public int X1;
            public int Count;
            public double Volume;
            public double PrevTarget;
            public int PivotId;

            public bool HasLevel;
            public int LevelId;
            public int LevelX1;
            public int LevelX2;
            public double LevelY;
            public bool LevelDashed;
            public bool LevelVisible;

            public bool HasZone;
            public int ZoneId;
            public int ZoneX;
            public double ZoneTop;
            public double ZoneBottom;

            public bool HasLabel;
            public int LabelId;
            public int LabelX;
            public double LabelY;
        }

        private SideState _top;
        private SideState _bottom;
        private int _lastProcessed = -1;

        protected override void Initialize()
        {
            var topColor = ParseColor(TopColorName, Color.Red);
            var bottomColor = ParseColor(BottomColorName, Color.Teal);

            _top = CreateSide(true, "top", topColor);
            _bottom = CreateSide(false, "btm", bottomColor);
        }

        public override void Calculate(int index)
        {
            // Uniquement les bougies cloturees : index - 1 est la derniere.
            var lastClosed = index - 1;
            if (lastClosed <= _lastProcessed)
                return;

            for (var n = _lastProcessed + 1; n <= lastClosed; n++)
                ProcessBar(n);

            _lastProcessed = lastClosed;
        }

        private SideState CreateSide(bool isTop, string name, Color color)
        {
            return new SideState
            {
                IsTop = isTop,
                Name = name,
                LineColor = color,
                ZoneColor = Color.FromArgb(AreaAlpha, color),
                // Zone "fantome" de la derniere liquidite, plus transparente
                // (equivalent de color.new(areaCss, 80) dans le script).
                AreaColor = Color.FromArgb(Math.Max(0, AreaAlpha / 5), color)
            };
        }

        private void ProcessBar(int n)
        {
            var pivotIndex = n - Length;
            var hasPivotWindow = pivotIndex - Length >= 0;

            var isPivotHigh = hasPivotWindow && IsPivotHigh(pivotIndex);
            var isPivotLow = hasPivotWindow && IsPivotLow(pivotIndex);

            ProcessSide(_top, n, isPivotHigh, ShowTop);
            ProcessSide(_bottom, n, isPivotLow, ShowBottom);
        }

        // Equivalent de ta.pivothigh(length, length) : le plus haut de la
        // bougie centrale depasse strictement les `length` bougies de gauche et
        // n'est depasse par aucune des `length` bougies de droite.
        private bool IsPivotHigh(int p)
        {
            var value = Bars.HighPrices[p];
            for (var i = 1; i <= Length; i++)
            {
                if (Bars.HighPrices[p - i] >= value || Bars.HighPrices[p + i] > value)
                    return false;
            }
            return true;
        }

        private bool IsPivotLow(int p)
        {
            var value = Bars.LowPrices[p];
            for (var i = 1; i <= Length; i++)
            {
                if (Bars.LowPrices[p - i] <= value || Bars.LowPrices[p + i] < value)
                    return false;
            }
            return true;
        }

        private void ProcessSide(SideState s, int n, bool isPivot, bool show)
        {
            var k = n - Length;

            // -- get_counts : bougies (decalees de `length`) revenues dans la zone --
            if (isPivot)
            {
                s.Count = 0;
                s.Volume = 0;
            }
            else if (k >= 0 && !double.IsNaN(s.Top))
            {
                if (Bars.LowPrices[k] < s.Top && Bars.HighPrices[k] > s.Bottom)
                {
                    s.Volume += Bars.TickVolumes[k];
                    s.Count++;
                }
            }

            // -- Mise a jour de la zone de liquidite --
            var prevCrossed = s.Crossed;
            if (isPivot && show)
            {
                var o = Bars.OpenPrices[k];
                var c = Bars.ClosePrices[k];
                if (s.IsTop)
                {
                    s.Top = Bars.HighPrices[k];
                    s.Bottom = Area == SwingAreaMode.WickExtremity ? Math.Max(c, o) : Bars.LowPrices[k];
                }
                else
                {
                    s.Top = Area == SwingAreaMode.WickExtremity ? Math.Min(c, o) : Bars.HighPrices[k];
                    s.Bottom = Bars.LowPrices[k];
                }

                s.X1 = k;
                s.Crossed = false;
                s.PivotId++;
            }
            else if (!double.IsNaN(s.Top))
            {
                var close = Bars.ClosePrices[n];
                if (s.IsTop ? close > s.Top : close < s.Bottom)
                    s.Crossed = true;
            }

            if (!show || double.IsNaN(s.Top))
                return;

            DrawArea(s, n);

            var target = FilterBy == FilterMode.Count ? s.Count : s.Volume;
            var crossover = target > FilterValue && s.PrevTarget <= FilterValue;
            var aboveFilter = target > FilterValue;

            UpdateZone(s, crossover, aboveFilter);
            UpdateLevel(s, n, isPivot, prevCrossed, aboveFilter);
            UpdateLabel(s, crossover, aboveFilter);

            s.PrevTarget = target;
        }

        // Zone "fantome" de la derniere liquidite : prolongee tant qu'elle n'est
        // pas cassee, retiree une fois cassee.
        private void DrawArea(SideState s, int n)
        {
            var name = Prefix + s.Name + "_area";
            if (s.Crossed)
            {
                Chart.RemoveObject(name);
                return;
            }

            var rect = Chart.DrawRectangle(name, s.X1, s.Top, n + 3, s.Bottom, s.AreaColor);
            rect.IsFilled = true;
        }

        // -- set_zone : bloc dont la largeur = nombre de retours dans la zone --
        private void UpdateZone(SideState s, bool crossover, bool aboveFilter)
        {
            if (crossover)
            {
                s.HasZone = true;
                s.ZoneId = s.PivotId;
                s.ZoneX = s.X1;
                s.ZoneTop = s.Top;
                s.ZoneBottom = s.Bottom;
            }

            if (!aboveFilter || !s.HasZone)
                return;

            var rect = Chart.DrawRectangle(Prefix + s.Name + "_zone_" + s.ZoneId,
                s.ZoneX, s.ZoneTop, s.ZoneX + s.Count, s.ZoneBottom, s.ZoneColor);
            rect.IsFilled = true;
        }

        // -- set_level : ligne du niveau, pointillee une fois cassee --
        private void UpdateLevel(SideState s, int n, bool isPivot, bool prevCrossed, bool aboveFilter)
        {
            if (isPivot)
            {
                if (s.HasLevel)
                {
                    if (s.PrevTarget < FilterValue)
                    {
                        Chart.RemoveObject(LevelName(s));
                    }
                    else if (!prevCrossed)
                    {
                        s.LevelX2 = n - Length;
                        DrawLevel(s);
                    }
                }

                s.HasLevel = true;
                s.LevelId = s.PivotId;
                s.LevelX1 = n - Length;
                s.LevelX2 = n;
                s.LevelY = s.IsTop ? s.Top : s.Bottom;
                s.LevelDashed = false;
                s.LevelVisible = false;
            }

            if (!s.HasLevel)
                return;

            if (!prevCrossed)
                s.LevelX2 = n + 3;

            if (s.Crossed && !prevCrossed)
            {
                s.LevelX2 = n;
                s.LevelDashed = true;
            }

            if (aboveFilter)
                s.LevelVisible = true;

            DrawLevel(s);
        }

        private void DrawLevel(SideState s)
        {
            if (!s.LevelVisible)
                return;

            Chart.DrawTrendLine(LevelName(s), s.LevelX1, s.LevelY, s.LevelX2, s.LevelY, s.LineColor, 1,
                s.LevelDashed ? LineStyle.Lines : LineStyle.Solid);
        }

        private string LevelName(SideState s)
        {
            return Prefix + s.Name + "_level_" + s.LevelId;
        }

        // -- set_label : volume accumule dans la zone --
        private void UpdateLabel(SideState s, bool crossover, bool aboveFilter)
        {
            if (crossover)
            {
                s.HasLabel = true;
                s.LabelId = s.PivotId;
                s.LabelX = s.X1;
                s.LabelY = s.IsTop ? s.Top : s.Bottom;
            }

            if (!aboveFilter || !s.HasLabel)
                return;

            var text = Chart.DrawText(Prefix + s.Name + "_label_" + s.LabelId, FormatVolume(s.Volume),
                s.LabelX, s.LabelY, s.LineColor);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = s.IsTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            text.FontSize = LabelSize == LabelSizeMode.Tiny ? 8 : LabelSize == LabelSizeMode.Small ? 10 : 12;
        }

        // Equivalent de format.volume : 1234 -> "1.23K", 1234567 -> "1.23M".
        private static string FormatVolume(double volume)
        {
            var abs = Math.Abs(volume);
            if (abs >= 1e9)
                return (volume / 1e9).ToString("0.##") + "B";
            if (abs >= 1e6)
                return (volume / 1e6).ToString("0.##") + "M";
            if (abs >= 1e3)
                return (volume / 1e3).ToString("0.##") + "K";
            return volume.ToString("0");
        }

        private static Color ParseColor(string value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            try
            {
                var trimmed = value.Trim();
                return trimmed.StartsWith("#") ? Color.FromHex(trimmed) : Color.FromName(trimmed);
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}

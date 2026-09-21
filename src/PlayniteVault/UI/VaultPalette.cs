using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using PlayniteVault.Services;

namespace PlayniteVault.UI
{
    /// <summary>
    /// 侧边栏页的配色令牌（design tokens）。
    ///
    /// <para><b>为什么要单独抽一层</b>：之前的写法是「每个卡片现场挑一个颜色」
    /// ——卡片 A 蓝、卡片 B 绿、卡片 C 黄、卡片 D 橙，五个并列就是一道彩虹。
    /// 那种配色的毛病不是「不够鲜艳」，而是<b>颜色没有含义</b>：
    /// 用户看久了也学不会「蓝色代表什么」，因为蓝色什么也不代表。</para>
    ///
    /// <para>现在只有三类颜色，各自职责固定：</para>
    /// <list type="bullet">
    /// <item><b>中性色</b>（Bg / Surface / Border / Text / TextMuted）——承担全部版面，
    /// 不携带含义，只区分层次。</item>
    /// <item><b>强调色</b>（Accent）——一页里只用来标「当前选中」和「主操作」。
    /// 一片版面上出现三次以上就说明用滥了。</item>
    /// <item><b>语义色</b>（Success / Warning / Danger / Info）——只有状态才配用它们，
    /// 所以看到黄色就一定意味着「需要注意」。</item>
    /// </list>
    ///
    /// <para><b>色板来源</b>：取 Catppuccin 的 Latte（浅）/ Mocha（深）两套中性色阶，
    /// 强调色沿用 Vault 自己的粉。选它是因为两套色阶的明度是配平过的 ——
    /// 同一个令牌在明暗两侧的对比度都落在 WCAG AA 区间，不用逐个肉眼调。</para>
    ///
    /// <para><b>几何风格不变</b>：仍然是「硬边 + 粗描边 + 无渐变无阴影」那一路，
    /// 保留原来一眼能认出来的块面感；这次动的是颜色，不是形状。</para>
    /// </summary>
    public sealed class VaultPalette
    {
        /// <summary>这套令牌最终落在深色侧还是浅色侧。</summary>
        public bool IsDark { get; private set; }

        /// <summary>页面底色。</summary>
        public Brush Bg { get; private set; }

        /// <summary>卡片 / 输入框底色（比 Bg 高一层）。</summary>
        public Brush Surface { get; private set; }

        /// <summary>次级底色：条形图轨道、表头、泳道底。</summary>
        public Brush SurfaceAlt { get; private set; }

        /// <summary>常规描边。</summary>
        public Brush Border { get; private set; }

        /// <summary>需要强调的描边（分隔线、聚焦框）。</summary>
        public Brush BorderStrong { get; private set; }

        /// <summary>主文字。</summary>
        public Brush Text { get; private set; }

        /// <summary>次要文字 / 说明。</summary>
        public Brush TextMuted { get; private set; }

        /// <summary>强调色：选中态与主操作专用。</summary>
        public Brush Accent { get; private set; }

        /// <summary>压在强调色上的文字色（保证对比度）。</summary>
        public Brush AccentInk { get; private set; }

        public Brush Success { get; private set; }
        public Brush Warning { get; private set; }
        public Brush Danger { get; private set; }
        public Brush Info { get; private set; }

        /// <summary>图表配色序列。顺序固定 = 同一个指标的颜色跨页面一致。</summary>
        public Brush[] Series { get; private set; }

        private VaultPalette()
        {
        }

        // ================================================================ 取用

        /// <summary>
        /// 按设置里的三档拿到一套令牌。
        /// auto 档去问 Playnite 当前是明是暗（失败就按深色算——桌面模式默认深色）。
        /// </summary>
        public static VaultPalette Resolve(VaultUiThemeMode mode)
        {
            switch (mode)
            {
                case VaultUiThemeMode.Light:
                    return BuildLight();
                case VaultUiThemeMode.Dark:
                    return BuildDark();
                default:
                    return DetectFromPlaynite() ? BuildDark() : BuildLight();
            }
        }

        /// <summary>把设置里的字符串转成三档枚举。</summary>
        public static VaultUiThemeMode ParseMode(string raw)
        {
            switch (VaultSettings.NormalizeUiTheme(raw))
            {
                case VaultSettings.UiThemeLight:
                    return VaultUiThemeMode.Light;
                case VaultSettings.UiThemeDark:
                    return VaultUiThemeMode.Dark;
                default:
                    return VaultUiThemeMode.Auto;
            }
        }

        /// <summary>
        /// 读 Playnite 的窗口底色，按感知亮度判明暗。
        ///
        /// 用相对亮度公式而不是简单平均：人眼对绿最敏感、对蓝最不敏感，
        /// 简单平均会把一些偏绿的浅色误判成深色。
        /// </summary>
        private static bool DetectFromPlaynite()
        {
            var probe = VaultPanelView.ThemedBrush("WindowBackgroundBrush", null)
                        ?? VaultPanelView.ThemedBrush("BackgroundBrush", null)
                        ?? VaultPanelView.ThemedBrush("ControlBackgroundBrush", null);

            var solid = probe as SolidColorBrush;
            if (solid == null)
            {
                return true;   // 问不到就按深色算
            }

            var c = solid.Color;
            var luminance = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            return luminance < 140;
        }

        // ================================================================ 两套色阶

        private static VaultPalette BuildLight()
        {
            var p = new VaultPalette { IsDark = false };

            p.Bg = Frozen("#EFF1F5");
            p.Surface = Frozen("#FFFFFF");
            p.SurfaceAlt = Frozen("#E6E9EF");
            p.Border = Frozen("#D3D7E0");
            p.BorderStrong = Frozen("#9CA0B0");
            p.Text = Frozen("#4C4F69");
            // #7C7F93 在浅色底上只有约 3.9:1，正文级的次要文字（字段说明、卡片副标题）
            // 压线看不清。压到 #5A5E72 后约 5.4:1，跨过 WCAG AA 的 4.5 线。
            p.TextMuted = Frozen("#5A5E72");

            p.Accent = Frozen("#EA76CB");
            p.AccentInk = Frozen("#1E1E2E");

            p.Success = Frozen("#40A02B");
            p.Warning = Frozen("#DF8E1D");
            p.Danger = Frozen("#D20F39");
            p.Info = Frozen("#1E66F5");

            p.Series = new[]
            {
                p.Accent,
                p.Info,
                p.Success,
                p.Warning,
                p.Danger,
                Frozen("#8839EF"),
                Frozen("#179299")
            };

            return p;
        }

        private static VaultPalette BuildDark()
        {
            var p = new VaultPalette { IsDark = true };

            p.Bg = Frozen("#11111B");
            p.Surface = Frozen("#1E1E2E");
            p.SurfaceAlt = Frozen("#181825");
            p.Border = Frozen("#313244");
            p.BorderStrong = Frozen("#45475A");
            p.Text = Frozen("#CDD6F4");
            p.TextMuted = Frozen("#9399B2");

            p.Accent = Frozen("#F5C2E7");
            p.AccentInk = Frozen("#11111B");

            p.Success = Frozen("#A6E3A1");
            p.Warning = Frozen("#F9E2AF");
            p.Danger = Frozen("#F38BA8");
            p.Info = Frozen("#89B4FA");

            p.Series = new[]
            {
                p.Accent,
                p.Info,
                p.Success,
                p.Warning,
                p.Danger,
                Frozen("#CBA6F7"),
                Frozen("#94E2D5")
            };

            return p;
        }

        // ================================================================ 工具

        private static Brush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 把一个画刷调成指定透明度（做轨道、底纹、状态点光晕用）。
        ///
        /// 四舍五入而不是截断：<c>(byte)</c> 直接强转会向下取整，
        /// 于是 <c>Alpha(0.5)</c> 得 127 而不是 128 —— 差 1 看不出来，
        /// 但「0.5 就是一半」这个预期会在逐级叠加时被放大。
        /// </summary>
        public static Brush Alpha(Brush source, double alpha)
        {
            var solid = source as SolidColorBrush;
            if (solid == null)
            {
                return source;
            }

            var clamped = Math.Max(0, Math.Min(1, alpha));
            var c = solid.Color;
            var copy = new SolidColorBrush(Color.FromArgb((byte)Math.Round(clamped * 255), c.R, c.G, c.B));
            copy.Freeze();
            return copy;
        }

        /// <summary>按序取图表色；序号超界就绕回来，保证同一个指标颜色稳定。</summary>
        public Brush SeriesAt(int index)
        {
            if (Series == null || Series.Length == 0)
            {
                return Accent;
            }

            var i = index % Series.Length;
            return i < 0 ? Series[0] : Series[i];
        }

        /// <summary>白色墨（压在深色块上的那一档）。</summary>
        private static readonly Brush WhiteInk = Frozen("#FFFFFF");

        /// <summary>
        /// 压在**语义色实心块**上的文字色（状态徽章用）。
        ///
        /// <para>为什么不能直接复用 <see cref="AccentInk"/>：强调色只有粉一种，深墨一定压得住；
        /// 但语义色有绿橙红蓝四种，明度差得很远 —— 浅色档的「绿」(#40A02B) 配深墨有 4.9:1，
        /// 而「红」(#D20F39) 配深墨只有 3.0:1，必须换白字才有 5.4:1。
        /// 所以这里**两个候选都算一遍、取对比度高的那个**，
        /// 而不是按固定阈值猜（猜错的那一档就是「看不清的字」）。</para>
        /// </summary>
        public Brush OnStatus(Brush status)
        {
            var solid = status as SolidColorBrush;
            if (solid == null)
            {
                return AccentInk;
            }

            var withDark = Contrast(solid.Color, ((SolidColorBrush)AccentInk).Color);
            var withWhite = Contrast(solid.Color, ((SolidColorBrush)WhiteInk).Color);
            return withDark >= withWhite ? AccentInk : WhiteInk;
        }

        /// <summary>
        /// WCAG 相对对比度。抽成公开静态是为了让自检能对着它断言 ——
        /// 「看着还行」和「算出来 2.9」之间的差距，只有算过才知道。
        /// </summary>
        public static double Contrast(Color a, Color b)
        {
            var la = RelativeLuminance(a);
            var lb = RelativeLuminance(b);
            var hi = Math.Max(la, lb);
            var lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }

        /// <summary>WCAG 相对亮度（先线性化再按人眼权重加权）。</summary>
        public static double RelativeLuminance(Color c)
        {
            return 0.2126 * Linearize(c.R) + 0.7152 * Linearize(c.G) + 0.0722 * Linearize(c.B);
        }

        private static double Linearize(byte channel)
        {
            var v = channel / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>明暗三档。跟设置里的字符串一一对应，映射只发生在 <see cref="VaultPalette.ParseMode"/> 一处。</summary>
    public enum VaultUiThemeMode
    {
        Auto = 0,
        Light = 1,
        Dark = 2
    }
}

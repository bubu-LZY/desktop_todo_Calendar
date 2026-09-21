namespace MicaAgenda.App.Models;

/// <summary>ARGB 颜色（0-255），与 UI 框架无关，便于在 Core 里计算与测试。</summary>
public readonly record struct RgbaColor(byte A, byte R, byte G, byte B)
{
    public static RgbaColor Rgb(byte r, byte g, byte b) => new(255, r, g, b);

    public static readonly RgbaColor White = Rgb(255, 255, 255);
    public static readonly RgbaColor Black = Rgb(0, 0, 0);
    public static readonly RgbaColor Transparent = new(0, 0, 0, 0);
}

/// <summary>一个主题的定义：身份由 <see cref="Base"/>（基色）+ <see cref="ShellMix"/>（浓度）决定。</summary>
/// <param name="TextureOpacity">
/// 纹理层浓度（0 = 无纹理）。非 0 时宿主会在底色之上再铺一层**平铺的细颗粒**，
/// 用来表现"纸感 / 布感"这类**不是纯色**的底色。
///
/// <para>它同时乘上用户的透明度设置 —— 纹理属于背景的一部分，
/// 背景变透明时纹理也要跟着淡掉，否则"拉低透明度"会留下满屏悬浮的噪点，比不透明还难看。</para>
/// </param>
public sealed record ThemeDefinition(
    CalendarBackgroundMode Mode,
    string Name,
    bool IsDark,
    RgbaColor Base,
    double ShellMix,
    double TextureOpacity = 0);

/// <summary>某个主题 + 某档不透明度下，整套界面用色。</summary>
public sealed record ThemeColors(
    RgbaColor Shell,
    RgbaColor PrimaryText,
    RgbaColor MutedText,
    RgbaColor CellBorder,
    RgbaColor TaskBorder,
    RgbaColor DayCell,
    RgbaColor DayCellOutMonth,
    RgbaColor YearMonth,
    RgbaColor TodayCell,
    RgbaColor TodayCellBorder,
    RgbaColor SelectedCell,
    RgbaColor SelectedCellBorder,
    RgbaColor TaskPill,
    RgbaColor Panel,
    RgbaColor PanelBorder,
    RgbaColor WeekGroup,
    RgbaColor WeekGroupHover,
    RgbaColor WeekRowHover,
    RgbaColor ImportantBg,
    RgbaColor ImportantBorder,
    RgbaColor ImportantText,
    RgbaColor HolidayBreak,
    RgbaColor HolidayWork,
    RgbaColor HolidayText,
    RgbaColor HolidayWorkText,
    RgbaColor DoneCheck,
    RgbaColor WindowEdge,
    RgbaColor ToolbarBackground,
    RgbaColor ToolbarBorder,
    RgbaColor ToolbarHover,
    RgbaColor ToolbarPressed,
    /// <summary>周期任务前缀【周期】的颜色（蓝）。明暗主题各取一档，保证压在各自底色上都看得清。</summary>
    RgbaColor RecurringBadge = default,
    /// <summary>纹理层的有效浓度（已乘上用户透明度）。0 表示该主题不铺纹理。</summary>
    double TextureOpacity = 0);

/// <summary>
/// 全部主题的唯一权威定义（两个宿主共用，并有单测覆盖）。
///
/// <para><b>为什么要集中到 Core</b>：早先 Avalonia 宿主与 WPF 宿主各写一份按模式 switch 的调色板，
/// 于是同一个主题在两边的观感可以不一样，也没法写测试。更糟的是，几个深色主题共用同一套格子色，
/// 只在窗口底色上差了几个 RGB —— 用户反馈「暗色磨砂和石墨深色几乎没区别」「不是白的就是黑的」，
/// 根因就在这里：<b>主题的身份没有被表达出来</b>。</para>
///
/// <para><b>设计规则</b>：每个主题只声明两件事 —— 基色 <see cref="ThemeDefinition.Base"/> 与
/// 上色浓度 <see cref="ThemeDefinition.ShellMix"/>，其余全部派生。于是"每个主题有自己的色调"
/// 成为结构性保证，而不是靠逐处 hand-tune 颜色。</para>
///
/// <para><b>对比度也是保证</b>：文字色按明暗两套给定，并由单测按 WCAG 相对亮度检查
/// （正文 ≥ 7:1、次要文字 ≥ 4.5:1），避免再出现「背景/透明度标签和星期几看不清」。</para>
/// </summary>
public static class ThemeCatalog
{
    /// <summary>无背景 / 描边变体：整窗透明，透出桌面。</summary>
    public static bool IsTransparent(CalendarBackgroundMode mode)
        => mode is CalendarBackgroundMode.None or CalendarBackgroundMode.ClearBorder;

    public static bool IsDark(CalendarBackgroundMode mode) => Get(mode).IsDark;

    /// <summary>
    /// 该主题是否靠**纹理层**表达个性（<see cref="ThemeDefinition.TextureOpacity"/> &gt; 0）。
    ///
    /// <para>存在的意义：这类主题的观感**不能只看基色** —— 真实纸感的底色本来就是"极浅的暖白"，
    /// 与「白雾玻璃」在 RGB 上必然接近。所以"主题之间必须拉开色距"的不变式对它们要换个判据
    /// （改判"有没有纹理"，见 ThemeCatalogTests）。</para>
    /// </summary>
    public static bool IsTextured(CalendarBackgroundMode mode) => Get(mode).TextureOpacity > 0;

    /// <summary>
    /// 可选主题清单（下拉列表按此顺序展示）。历史值 Glass / Transparent / Solid 不在其中 ——
    /// 它们加载时就会被 <c>MigrateLegacy</c> 迁到 FrostedWhite。
    /// </summary>
    public static IReadOnlyList<ThemeDefinition> All { get; } = new ThemeDefinition[]
    {
        // ===== 无背景 =====
        new(CalendarBackgroundMode.None,           "无背景",     false, RgbaColor.Rgb(120, 132, 150), 0.86),

        // ===== 浅色 · 中性 =====
        // 白雾玻璃：接近纯白但略冷，和「米杏」「纸感」拉开。
        new(CalendarBackgroundMode.FrostedWhite,   "白雾玻璃",   false, RgbaColor.Rgb(203, 213, 225), 0.80),
        // 灰雾玻璃：中性灰。浓度 0.50 让它明显比白雾玻璃"有颜色"。
        new(CalendarBackgroundMode.FrostedGray,    "灰雾玻璃",   false, RgbaColor.Rgb(140, 142, 150), 0.50),

        // ===== 浅色 · 暖色系（用户点名要的四类暖调，浅色侧三个）=====
        // 米杏奶油：偏粉的浅奶油。
        new(CalendarBackgroundMode.Cream,          "米杏奶油",   false, RgbaColor.Rgb(205, 120, 95),  0.66),
        // 纸感浅白：偏黄的书纸。
        new(CalendarBackgroundMode.PaperLight,     "纸感浅白",   false, RgbaColor.Rgb(169, 144, 63),  0.50),
        // 焦糖奶茶：三者里最深的暖棕。浓度不能更低 —— 再深下去顶栏的次要文字对比度会掉到 4.5:1 以下
        // （单测会拦住），这是"浅色主题必须是浅色"的硬约束。
        new(CalendarBackgroundMode.Caramel,        "焦糖奶茶",   false, RgbaColor.Rgb(169, 113, 63),  0.42),

        // ===== 浅色 · 彩色 =====
        // 蓝色亚克力：基色取饱和蓝，浓度 0.60 —— 老版本用 blue-100 级别"一点也不蓝"。
        new(CalendarBackgroundMode.AcrylicBlue,    "蓝色亚克力", false, RgbaColor.Rgb(59, 130, 246),  0.60),
        new(CalendarBackgroundMode.AcrylicMint,    "薄荷玻璃",   false, RgbaColor.Rgb(16, 185, 129),  0.60),
        new(CalendarBackgroundMode.AcrylicRose,    "樱粉玻璃",   false, RgbaColor.Rgb(219, 39, 119),  0.62),
        new(CalendarBackgroundMode.AcrylicViolet,  "紫罗兰",     false, RgbaColor.Rgb(139, 92, 246),  0.60),

        // ===== 深色 =====
        // 四个深色主题用四种不同色相的基色 —— 关键在于基色**色相不同**，
        // 而不是像老版本那样只在同一个深灰上差几个 RGB（那正是「暗色磨砂和石墨深色没区别」的根因）。
        new(CalendarBackgroundMode.FrostedDark,    "暗色磨砂",   true,  RgbaColor.Rgb(128, 132, 140), 0.62), // 纯中性深灰
        new(CalendarBackgroundMode.Graphite,       "石墨深色",   true,  RgbaColor.Rgb(75, 105, 170),  0.75), // 深蓝黑
        new(CalendarBackgroundMode.Walnut,         "暖褐木色",   true,  RgbaColor.Rgb(150, 110, 74),  0.70), // 暖褐（深色暖调）
        new(CalendarBackgroundMode.Terracotta,     "陶土赭石",   true,  RgbaColor.Rgb(178, 78, 54),   0.50),  // 土红（深色暖调）

        // ===== 带纹理 =====
        // 米色纹理：参考"米色再生纸"。
        //
        // 基色按**实测标定**：用户给的参考图与他桌面壁纸的纸面都是 (242,237,234) ——
        // 近白暖米，而不是灰米。这正是它和「白雾玻璃」在**颜色上**天然靠近的原因：
        // 真实纸感本来就是"极浅的暖白"，靠加灰去拉开色距只会越做越不像纸。
        // 所以它的辨识度交给**纹理层**（见 PaperTexture 与 TextureOpacity），
        // 单测的色距不变式对这种主题按"不算基色、算纹理"处理（见 ThemeCatalogTests）。
        new(CalendarBackgroundMode.BeigeTexture,   "米色纹理",   false, RgbaColor.Rgb(208, 196, 180), 0.70, 0.72)
    };

    private static readonly Dictionary<CalendarBackgroundMode, ThemeDefinition> ByMode =
        BuildIndex();

    private static Dictionary<CalendarBackgroundMode, ThemeDefinition> BuildIndex()
    {
        var map = All.ToDictionary(d => d.Mode);

        // 历史值不在可选清单里，但必须能取到定义（老配置 / 迁移途中）。
        map[CalendarBackgroundMode.ClearBorder] = map[CalendarBackgroundMode.None];
        foreach (var legacy in new[]
                 {
                     CalendarBackgroundMode.Glass,
                     CalendarBackgroundMode.Transparent,
                     CalendarBackgroundMode.Solid
                 })
        {
            map[legacy] = map[CalendarBackgroundMode.FrostedWhite];
        }

        return map;
    }

    public static ThemeDefinition Get(CalendarBackgroundMode mode)
        => ByMode.TryGetValue(mode, out var def) ? def : ByMode[CalendarBackgroundMode.FrostedWhite];

    /// <summary>下拉列表里显示的文案。</summary>
    public static string NameOf(CalendarBackgroundMode mode) => Get(mode).Name;

    /// <summary>
    /// 按主题 + 不透明度算出整套用色。
    ///
    /// <para><b>opacity 不再被逐模式截断</b>：老版本对多种模式写了
    /// <c>Math.Min(alpha, 210)</c> / <c>Math.Min(alpha, 150)</c>，于是透明度滑杆拉到头
    /// 也只能到 82% 甚至 58% —— 用户反馈"透明度的强度不高"，是代码里限死的。
    /// 现在滑杆直接 1:1 映射到 alpha，强度完全交给用户。</para>
    /// </summary>
    public static ThemeColors Build(CalendarBackgroundMode mode, double opacity)
    {
        var def = Get(mode);
        var alpha = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);

        var colors = IsTransparent(mode)
            ? BuildTransparent(def, alpha)
            : def.IsDark ? BuildDark(def, alpha) : BuildLight(def, alpha);

        // 纹理浓度乘上用户透明度，且**在这里统一算**（不在三个分支里各写一遍）：
        // 纹理是背景的一部分，背景被调透明时它必须跟着淡掉。
        return colors with { TextureOpacity = def.TextureOpacity * Math.Clamp(opacity, 0, 1) };
    }

    // ===== 派生工具 =====

    /// <summary>在两种颜色之间线性插值（t=0 取 from，t=1 取 to）。</summary>
    private static RgbaColor Mix(RgbaColor from, RgbaColor to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new RgbaColor(
            (byte)Math.Round(from.A + (to.A - from.A) * t),
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    private static RgbaColor WithAlpha(RgbaColor c, byte a) => c with { A = a };

    /// <summary>深色主题的"底色基准"——把基色压到接近黑，保留色相。</summary>
    private static readonly RgbaColor DeepBase = RgbaColor.Rgb(10, 12, 15);

    // ===== 三套派生 =====

    private static ThemeColors BuildTransparent(ThemeDefinition def, byte alpha)
    {
        // 无背景：窗口与格子全透明，只给面板 / 任务胶囊留一层薄磨砂，
        // 否则文字直接压在壁纸上会读不出来。这层薄底是刻意的，不是"发白"。
        var light = def.IsDark ? RgbaColor.White : RgbaColor.White;
        var panel = WithAlpha(Mix(def.Base, light, 0.88), 110);
        var pill = WithAlpha(Mix(def.Base, light, 0.70), 140);

        return Base(def, shell: RgbaColor.Transparent, alpha: alpha) with
        {
            DayCell = RgbaColor.Transparent,
            DayCellOutMonth = RgbaColor.Transparent,
            YearMonth = RgbaColor.Transparent,
            Panel = panel,
            TaskPill = pill,
            WeekGroup = WithAlpha(Mix(def.Base, light, 0.82), 120),
            WeekGroupHover = WithAlpha(Mix(def.Base, light, 0.70), 150),
            ToolbarBackground = WithAlpha(Mix(def.Base, light, 0.80), 120),
            TodayCell = WithAlpha(def.Base, 120),
            TodayCellBorder = WithAlpha(def.Base, 190),
            SelectedCell = WithAlpha(def.Base, 130),
            SelectedCellBorder = WithAlpha(def.Base, 210),
            PanelBorder = WithAlpha(RgbaColor.Black, 60),
            WindowEdge = WithAlpha(def.Base, 150)
        };
    }

    private static ThemeColors BuildLight(ThemeDefinition def, byte alpha)
    {
        // 浅色主题：面板永远比格子更亮一档（"浮起"的卡片），彩色主题的面板也保留色相。
        var shell = WithAlpha(Mix(def.Base, RgbaColor.White, def.ShellMix), alpha);

        return Base(def, shell, alpha) with
        {
            DayCell = WithAlpha(Mix(def.Base, RgbaColor.White, 0.78), 175),
            DayCellOutMonth = WithAlpha(RgbaColor.White, 111),
            YearMonth = WithAlpha(Mix(def.Base, RgbaColor.White, 0.78), 175),
            TodayCell = WithAlpha(Mix(def.Base, RgbaColor.White, 0.40), 205),
            TodayCellBorder = WithAlpha(def.Base, 205),
            SelectedCell = WithAlpha(def.Base, 150),
            SelectedCellBorder = WithAlpha(def.Base, 225),
            TaskPill = WithAlpha(Mix(def.Base, RgbaColor.White, 0.50), 195),
            Panel = WithAlpha(Mix(def.Base, RgbaColor.White, 0.90), 200),
            WeekGroup = WithAlpha(Mix(def.Base, RgbaColor.White, 0.72), 185),
            WeekGroupHover = WithAlpha(Mix(def.Base, RgbaColor.White, 0.58), 205),
            ToolbarBackground = WithAlpha(Mix(def.Base, RgbaColor.White, 0.80), 150),
            ToolbarHover = WithAlpha(RgbaColor.White, 185)
        };
    }

    private static ThemeColors BuildDark(ThemeDefinition def, byte alpha)
    {
        var shell = WithAlpha(Mix(def.Base, DeepBase, def.ShellMix), alpha);

        // 深色下的层次**相对 ShellMix** 往「更亮」推移（Mix 的 t 越小 = 越靠近基色 = 越亮）。
        // 用相对量而不是固定值，是因为各深色主题的 ShellMix 并不相同（0.50 ~ 0.75）——
        // 写死的话，浓度低的主题会出现"面板比窗口底色还暗"，也就是之前那个黑洞观感的成因。
        RgbaColor Surface(double lift, byte a)
            => WithAlpha(Mix(def.Base, DeepBase, def.ShellMix - lift), a);

        return Base(def, shell, alpha) with
        {
            DayCell = Surface(0.10, 130),
            DayCellOutMonth = Surface(0.05, 70),
            YearMonth = Surface(0.10, 130),
            TodayCell = Surface(0.34, 175),
            TodayCellBorder = WithAlpha(Mix(def.Base, RgbaColor.White, 0.45), 200),
            SelectedCell = Surface(0.30, 165),
            SelectedCellBorder = WithAlpha(Mix(def.Base, RgbaColor.White, 0.35), 215),
            TaskPill = Surface(0.26, 160),
            Panel = Surface(0.18, 170),
            WeekGroup = Surface(0.14, 140),
            WeekGroupHover = Surface(0.24, 170),
            ToolbarBackground = Surface(0.20, 150),
            ToolbarHover = Surface(0.30, 170),
            ToolbarPressed = Surface(0.36, 185),
            ToolbarBorder = WithAlpha(RgbaColor.White, 70),
            PanelBorder = WithAlpha(RgbaColor.White, 130),
            CellBorder = WithAlpha(RgbaColor.White, 70),
            TaskBorder = WithAlpha(RgbaColor.White, 90),
            WeekRowHover = WithAlpha(RgbaColor.White, 60),
            WindowEdge = WithAlpha(RgbaColor.White, 42)
        };
    }

    /// <summary>两个明暗分支共用的骨架：文字、边框、语义色（今日 / 重要 / 节假日 / 完成）。</summary>
    private static ThemeColors Base(ThemeDefinition def, RgbaColor shell, byte alpha)
    {
        if (def.IsDark)
        {
            return new ThemeColors(
                Shell: shell,
                // 文字掺一点主题色，避免所有深色主题的文字完全一样 —— 但幅度很小，以可读性优先。
                PrimaryText: RgbaColor.Rgb(243, 244, 246),
                // 老版本 MutedText 用的中灰压在彩色/深色底上对比不足（用户反馈标签看不清），这里加深。
                MutedText: RgbaColor.Rgb(203, 210, 220),
                CellBorder: WithAlpha(RgbaColor.White, 70),
                TaskBorder: WithAlpha(RgbaColor.White, 90),
                DayCell: RgbaColor.Transparent,
                DayCellOutMonth: RgbaColor.Transparent,
                YearMonth: RgbaColor.Transparent,
                TodayCell: RgbaColor.Transparent,
                TodayCellBorder: RgbaColor.Transparent,
                SelectedCell: RgbaColor.Transparent,
                SelectedCellBorder: RgbaColor.Transparent,
                TaskPill: RgbaColor.Transparent,
                Panel: RgbaColor.Transparent,
                PanelBorder: RgbaColor.Transparent,
                WeekGroup: RgbaColor.Transparent,
                WeekGroupHover: RgbaColor.Transparent,
                WeekRowHover: WithAlpha(RgbaColor.White, 60),
                ImportantBg: WithAlpha(RgbaColor.Rgb(127, 29, 29), 150),
                ImportantBorder: WithAlpha(RgbaColor.Rgb(248, 113, 113), 210),
                ImportantText: RgbaColor.Rgb(252, 165, 165),
                HolidayBreak: WithAlpha(RgbaColor.Rgb(127, 29, 29), 130),
                HolidayWork: WithAlpha(RgbaColor.Rgb(30, 64, 175), 130),
                HolidayText: RgbaColor.Rgb(254, 202, 202),
                HolidayWorkText: RgbaColor.Rgb(191, 219, 254),
                DoneCheck: RgbaColor.Rgb(134, 239, 172),
                WindowEdge: WithAlpha(RgbaColor.White, 42),
                ToolbarBackground: RgbaColor.Transparent,
                ToolbarBorder: WithAlpha(RgbaColor.White, 70),
                ToolbarHover: RgbaColor.Transparent,
                ToolbarPressed: RgbaColor.Transparent,
                // 深色底上用**浅**蓝，深蓝会糊在深色里看不出来（与"深色面板不能比底色更暗"是同一条规则）。
                RecurringBadge: RgbaColor.Rgb(147, 197, 253));
        }

        return new ThemeColors(
            Shell: shell,
            PrimaryText: RgbaColor.Rgb(17, 24, 39),
            // 比老版本的 (107,114,128) 明显更深。老值在彩色主题上只有 3.4:1；
            // 而且浅色主题里最深的「焦糖奶茶」底色偏中等亮度，次要文字必须压到这个深度
            // 才能保住顶栏那排小字（用户反馈"背景 / 透明度这两个字看不清"）。
            MutedText: RgbaColor.Rgb(48, 56, 70),
            CellBorder: WithAlpha(RgbaColor.Black, 26),
            TaskBorder: WithAlpha(RgbaColor.Black, 51),
            DayCell: RgbaColor.Transparent,
            DayCellOutMonth: RgbaColor.Transparent,
            YearMonth: RgbaColor.Transparent,
            TodayCell: RgbaColor.Transparent,
            TodayCellBorder: RgbaColor.Transparent,
            SelectedCell: RgbaColor.Transparent,
            SelectedCellBorder: RgbaColor.Transparent,
            TaskPill: RgbaColor.Transparent,
            Panel: RgbaColor.Transparent,
            PanelBorder: WithAlpha(RgbaColor.Black, 40),
            WeekGroup: RgbaColor.Transparent,
            WeekGroupHover: RgbaColor.Transparent,
            WeekRowHover: WithAlpha(RgbaColor.Black, 20),
            ImportantBg: RgbaColor.Rgb(254, 202, 202),
            ImportantBorder: RgbaColor.Rgb(239, 68, 68),
            ImportantText: RgbaColor.Rgb(185, 28, 28),
            HolidayBreak: RgbaColor.Rgb(254, 226, 226),
            HolidayWork: RgbaColor.Rgb(219, 234, 254),
            HolidayText: RgbaColor.Rgb(153, 27, 27),
            HolidayWorkText: RgbaColor.Rgb(29, 78, 216),
            DoneCheck: RgbaColor.Rgb(21, 115, 71),
            WindowEdge: WithAlpha(RgbaColor.Black, 45),
            ToolbarBackground: RgbaColor.Transparent,
            ToolbarBorder: WithAlpha(RgbaColor.Black, 42),
            ToolbarHover: RgbaColor.Transparent,
            ToolbarPressed: RgbaColor.Rgb(229, 231, 235),
            // 浅色底上用深蓝：对最"中灰"的浅色主题也保得住 4.5:1（单测会检查）。
            RecurringBadge: RgbaColor.Rgb(29, 78, 216));
    }

    // ===== 供测试与宿主使用的小工具 =====

    /// <summary>把带 alpha 的颜色合成到不透明背景上（对比度检查用）。</summary>
    public static RgbaColor Composite(RgbaColor top, RgbaColor bottom)
    {
        var a = top.A / 255.0;
        return new RgbaColor(
            255,
            (byte)Math.Round(top.R * a + bottom.R * (1 - a)),
            (byte)Math.Round(top.G * a + bottom.G * (1 - a)),
            (byte)Math.Round(top.B * a + bottom.B * (1 - a)));
    }

    /// <summary>WCAG 相对亮度。</summary>
    public static double RelativeLuminance(RgbaColor c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>WCAG 对比度（1.0 ~ 21.0）。</summary>
    public static double ContrastRatio(RgbaColor foreground, RgbaColor background)
    {
        var l1 = RelativeLuminance(foreground);
        var l2 = RelativeLuminance(background);
        var (hi, lo) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (hi + 0.05) / (lo + 0.05);
    }
}

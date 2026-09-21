using MicaAgenda.App.Models;

namespace MicaAgenda.Tests;

/// <summary>
/// 主题调色板。这一组测试守的是用户连着报的一串问题：
/// 「不是白的就是黑的」「暗色磨砂和石墨深色没区别」「蓝色亚克力一点也不蓝」
/// 「无背景界面还是白的」「透明度强度不高」「背景/透明度标签和星期几看不清」。
///
/// 这些问题逐个手调颜色是治不住的 —— 所以规则全部集中在 <see cref="ThemeCatalog"/>，
/// 并用这里的不变式把它钉住。
/// </summary>
public sealed class ThemeCatalogTests
{
    /// <summary>背景色差异（RGB 欧氏距离）。</summary>
    private static double Distance(RgbaColor a, RgbaColor b)
    {
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    /// <summary>不透明的主题（能比较底色、做对比度检查）。</summary>
    private static IEnumerable<ThemeDefinition> OpaqueThemes
        => ThemeCatalog.All.Where(d => !ThemeCatalog.IsTransparent(d.Mode));

    [Fact]
    public void All_ProvidesAtLeastTwelveSelectableThemes()
    {
        Assert.True(ThemeCatalog.All.Count >= 12, $"主题只有 {ThemeCatalog.All.Count} 个，用户要求 12 个以上");
    }

    [Fact]
    public void All_ExcludesLegacyAndTransparentVariants()
    {
        // 历史值不应该出现在下拉列表里（加载时会迁移掉），否则用户能选到一个"半成品"主题。
        var modes = ThemeCatalog.All.Select(d => d.Mode).ToList();
        Assert.DoesNotContain(CalendarBackgroundMode.Glass, modes);
        Assert.DoesNotContain(CalendarBackgroundMode.Transparent, modes);
        Assert.DoesNotContain(CalendarBackgroundMode.Solid, modes);
        Assert.DoesNotContain(CalendarBackgroundMode.ClearBorder, modes);
        Assert.Single(modes, m => m == CalendarBackgroundMode.None);
    }

    [Fact]
    public void All_HasUniqueNamesAndModes()
    {
        // 下拉列表按名字展示：重名的话用户根本分不清选的是哪个。
        var names = ThemeCatalog.All.Select(d => d.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

    [Fact]
    public void EveryEnumValue_ResolvesWithoutThrowing()
    {
        // 任何枚举值（含历史值）都必须能取到一套完整用色，否则运行时换肤会直接抛异常。
        foreach (var mode in Enum.GetValues<CalendarBackgroundMode>())
        {
            var def = ThemeCatalog.Get(mode);
            Assert.NotNull(def);

            var colors = ThemeCatalog.Build(mode, 0.86);
            Assert.NotNull(colors);
        }
    }

    [Fact]
    public void LegacyValues_MapToTheirDocumentedTargets()
    {
        Assert.Equal(CalendarBackgroundMode.None, ThemeCatalog.Get(CalendarBackgroundMode.ClearBorder).Mode);
        Assert.Equal(CalendarBackgroundMode.FrostedWhite, ThemeCatalog.Get(CalendarBackgroundMode.Glass).Mode);
        Assert.Equal(CalendarBackgroundMode.FrostedWhite, ThemeCatalog.Get(CalendarBackgroundMode.Transparent).Mode);
        Assert.Equal(CalendarBackgroundMode.FrostedWhite, ThemeCatalog.Get(CalendarBackgroundMode.Solid).Mode);
    }

    [Fact]
    public void TransparentTheme_IsActuallyTransparent()
    {
        // 「无背景」的整个诉求就是透出桌面。老版本因为 Win10 上仍请求亚克力材质，
        // 界面看上去是一层白雾（用户反馈"界面还是白色的"）—— 这里锁住"窗口与格子必须全透明"。
        var c = ThemeCatalog.Build(CalendarBackgroundMode.None, 1.0);

        Assert.True(ThemeCatalog.IsTransparent(CalendarBackgroundMode.None));
        Assert.Equal(0, c.Shell.A);
        Assert.Equal(0, c.DayCell.A);
        Assert.Equal(0, c.DayCellOutMonth.A);
        Assert.Equal(0, c.YearMonth.A);

        // 面板仍保留一层薄磨砂：否则文字直接压在壁纸上会读不出来。但必须是"薄"的。
        Assert.InRange(c.Panel.A, 1, 160);
    }

    [Fact]
    public void AcrylicBlue_IsActuallyBlue()
    {
        // 用户原话："蓝色亚克力的模式，这个主题蓝色也不太蓝"。
        // 老版本底色是 blue-100 级别（219,234,254），几乎就是白。要求蓝通道明显压过红通道。
        var c = ThemeCatalog.Build(CalendarBackgroundMode.AcrylicBlue, 1.0);

        Assert.True(c.Shell.B - c.Shell.R >= 40, $"蓝色不足：R={c.Shell.R} B={c.Shell.B}");
        Assert.True(c.Shell.B > c.Shell.G, "蓝通道应当是主色");
    }

    [Fact]
    public void WarmThemes_AreActuallyWarm()
    {
        // 用户要求补暖色调。暖 = 红通道压过蓝通道。
        foreach (var mode in new[]
                 {
                     CalendarBackgroundMode.Cream,
                     CalendarBackgroundMode.PaperLight,
                     CalendarBackgroundMode.Caramel,
                     CalendarBackgroundMode.Walnut,
                     CalendarBackgroundMode.Terracotta
                 })
        {
            var shell = ThemeCatalog.Build(mode, 1.0).Shell;
            Assert.True(shell.R > shell.B, $"{ThemeCatalog.NameOf(mode)} 不是暖色：R={shell.R} B={shell.B}");
        }
    }

    [Fact]
    public void DarkThemes_AreVisuallyDistinctFromEachOther()
    {
        // 用户原话："石墨深色和暗色磨砂这两个主题的区别不大"。
        // 老版本所有深色主题共用同一套格子色，只在窗口底色上差几个 RGB（28,31,36 vs 17,24,39）。
        var darks = OpaqueThemes.Where(d => d.IsDark).ToList();
        Assert.True(darks.Count >= 3, "深色主题应当有多个可选项");

        for (var i = 0; i < darks.Count; i++)
        {
            for (var j = i + 1; j < darks.Count; j++)
            {
                var a = ThemeCatalog.Build(darks[i].Mode, 1.0).Shell;
                var b = ThemeCatalog.Build(darks[j].Mode, 1.0).Shell;
                var distance = Distance(a, b);

                Assert.True(distance >= 30,
                    $"{darks[i].Name} 与 {darks[j].Name} 的底色太接近（Δ={distance:F1}）：" +
                    $"{a.R},{a.G},{a.B} vs {b.R},{b.G},{b.B}");
            }
        }
    }

    [Fact]
    public void AllThemes_AreVisuallyDistinctFromEachOther()
    {
        // 用户原话："不是白的，就是黑的"、"每个主题都有它自己的特色"。
        // 任意两个主题的窗口底色都必须拉得开。
        var light = OpaqueThemes.Where(d => !d.IsDark).ToList();
        var dark = OpaqueThemes.Where(d => d.IsDark).ToList();

        // 组内比较（跨明暗组天然差得远，不必比）
        foreach (var group in new[] { light, dark })
        {
            for (var i = 0; i < group.Count; i++)
            {
                for (var j = i + 1; j < group.Count; j++)
                {
                    var a = ThemeCatalog.Build(group[i].Mode, 1.0).Shell;
                    var b = ThemeCatalog.Build(group[j].Mode, 1.0).Shell;
                    var distance = Distance(a, b);

                    // 带纹理的主题走**另一条判据**（见下）。
                    // 理由：真实纸感的底色本来就是"极浅的暖白"—— 用户给的参考图实测是
                    // (242,237,234)，与「白雾玻璃」在 RGB 上必然只差十几。
                    // 硬要它拉开 28 就只能加灰/加深，而那样就不再像纸了（第一版就是这么做的，
                    // 结果用户看截图说"对不上"）。它的辨识度来自**那层颗粒**，
                    // 而颗粒是叠在底色之上的另一层，这个"比底色"的测试看不到它。
                    var textured = ThemeCatalog.IsTextured(group[i].Mode)
                                   || ThemeCatalog.IsTextured(group[j].Mode);

                    var threshold = textured ? TexturedThemeMinShellDistance : 28;
                    Assert.True(distance >= threshold,
                        $"{group[i].Name} 与 {group[j].Name} 的底色太接近（Δ={distance:F1}，" +
                        $"阈值 {threshold}）");
                }
            }
        }

        // 豁免是有代价的，所以补两条"不许滥用豁免"的约束：
        // ① 带纹理的主题必须**真的有纹理**（否则它就是个和别的主题重复的纯色主题）；
        // ② 它相对同组其它主题仍须拉开一个不至于"看起来一样"的最小距离。
        var texturedThemes = OpaqueThemes.Where(d => ThemeCatalog.IsTextured(d.Mode)).ToList();
        Assert.NotEmpty(texturedThemes);

        foreach (var def in texturedThemes)
        {
            Assert.True(ThemeCatalog.Get(def.Mode).TextureOpacity > 0,
                $"{def.Name} 声明为纹理主题，但 TextureOpacity 为 0 —— 它其实只是个纯色主题");

            var shell = ThemeCatalog.Build(def.Mode, 1.0).Shell;
            foreach (var other in light.Where(d => d.Mode != def.Mode))
            {
                var distance = Distance(shell, ThemeCatalog.Build(other.Mode, 1.0).Shell);
                Assert.True(distance >= TexturedThemeMinShellDistance,
                    $"{def.Name} 与 {other.Name} 的底色几乎重合（Δ={distance:F1}）");
            }
        }
    }

    /// <summary>
    /// 带纹理的主题所需的"最小可区分底色距离"。
    ///
    /// <para>比纯色主题的 28 低，但不是没有底线：8 已经足以避免"两个主题是同一个颜色"，
    /// 而真正的区分度由纹理层提供（并由上面那条"必须真的有纹理"的断言保证存在）。</para>
    /// </summary>
    private const double TexturedThemeMinShellDistance = 8;

    [Fact]
    public void EveryTheme_HasReadableTextOnItsOwnSurfaces()
    {
        // 用户反馈："背景 / 透明度这两个字，以及格子里的星期几，都不太看得清"。
        // 正文要求 ≥ 7:1、次要文字 ≥ 4.5:1（WCAG AA/AAA 之间），
        // 在「未知壁纸」的中性灰底上按默认不透明度合成后再比 —— 这是实际使用的场景。
        const double defaultOpacity = 0.85;
        var wallpaper = RgbaColor.Rgb(128, 128, 128);

        foreach (var def in OpaqueThemes)
        {
            var c = ThemeCatalog.Build(def.Mode, defaultOpacity);

            var windowBackground = ThemeCatalog.Composite(c.Shell, wallpaper);
            var cellBackground = ThemeCatalog.Composite(c.DayCell, windowBackground);

            var primary = ThemeCatalog.ContrastRatio(c.PrimaryText, cellBackground);
            var muted = ThemeCatalog.ContrastRatio(c.MutedText, cellBackground);

            Assert.True(primary >= 7.0, $"{def.Name} 正文对比度只有 {primary:F2}:1");
            Assert.True(muted >= 4.5, $"{def.Name} 次要文字对比度只有 {muted:F2}:1");

            // 表头（顶栏、面板标题）直接压在窗口底色上，而不是格子底色上，这一层也要够。
            var onShell = ThemeCatalog.ContrastRatio(c.MutedText, windowBackground);
            Assert.True(onShell >= 4.5, $"{def.Name} 顶栏次要文字对比度只有 {onShell:F2}:1");
        }
    }

    [Fact]
    public void DarkThemes_PanelIsBrighterThanTheWindowBackdrop()
    {
        // 另一条踩过的坑：深色下容器比窗口底色更暗，观感是一块黑洞（用户反馈"面板特别黑"）。
        // 深色主题的面板必须**更亮**；浅色主题同理（浅色下也是往更亮走）。
        foreach (var def in OpaqueThemes)
        {
            var c = ThemeCatalog.Build(def.Mode, 1.0);
            var shellLuma = ThemeCatalog.RelativeLuminance(c.Shell);
            var panelLuma = ThemeCatalog.RelativeLuminance(c.Panel);

            Assert.True(panelLuma > shellLuma,
                $"{def.Name} 的面板比窗口底色更暗（面板 {panelLuma:F3} vs 窗口 {shellLuma:F3}）");
        }
    }

    [Fact]
    public void OpacitySlider_ReachesFullStrength()
    {
        // 用户原话："包括这个透明度的样式，我觉得强度不高"。
        // 老版本对多种模式写了 Math.Min(alpha, 210) / Math.Min(alpha, 150)，
        // 滑杆拉到头也只有 82% / 58% —— 强度是代码里限死的。现在必须能到满。
        foreach (var def in OpaqueThemes)
        {
            var full = ThemeCatalog.Build(def.Mode, 1.0);
            Assert.Equal(255, full.Shell.A);

            // 有底色下限的"材质型"主题**刻意**不适用下面这条 —— 它不该在 0 处消失，
            // 由 TexturedTheme_KeepsItsPaperFill... 单独覆盖。
            if (def.MinShellOpacity > 0)
            {
                continue;
            }

            // 其余不透明主题在 0 不透明度下应当几乎全透。
            var zero = ThemeCatalog.Build(def.Mode, 0.0);
            Assert.True(zero.Shell.A <= 8, $"{def.Name} 在 0 不透明度下仍有 {zero.Shell.A} 的不透明度");
        }
    }

    [Fact]
    public void TexturedTheme_KeepsItsPaperFillWhereverTheSliderIs()
    {
        // 用户原话："它四周都是透明的，但是我想要那种浅一点的米色纹理填充进去。"
        //
        // 根因：底色浓度原本 = 滑块值。他停在 0.22，于是整窗只剩 22% 的米色、
        // 纹理浓度又被乘到 0.72×0.22 ≈ 0.16 —— 看上去既没有颜色也没有纹理。
        // 现在这类主题声明了 MinShellOpacity，滑块只在下限**之上**调浓淡。
        var textured = ThemeCatalog.All.Where(d => d.MinShellOpacity > 0).ToList();
        Assert.NotEmpty(textured);

        foreach (var def in textured)
        {
            var floor = (byte)Math.Round(def.MinShellOpacity * 255);
            var textureFloor = def.TextureOpacity * def.MinShellOpacity;

            foreach (var opacity in new[] { 0.0, 0.22, 0.5, def.MinShellOpacity, 1.0 })
            {
                var c = ThemeCatalog.Build(def.Mode, opacity);

                Assert.True(c.Shell.A >= floor - 1,
                    $"{def.Name} 在滑块 {opacity:0.##} 时底色只剩 {c.Shell.A}（下限 {floor}）—— 纸感会被抹掉");

                // 纹理浓度必须跟着**同一个**有效浓度走：两边各算各的会出现
                // "底色很实、纹理却淡得看不见"这种自相矛盾的效果。
                Assert.True(c.TextureOpacity >= textureFloor - 0.01,
                    $"{def.Name} 在滑块 {opacity:0.##} 时纹理浓度只有 {c.TextureOpacity:0.###}（下限 {textureFloor:0.###}）");
            }

            // 下限之上仍须能调浓淡 —— 否则下限就成了"唯一值"，滑块形同虚设。
            Assert.True(ThemeCatalog.Build(def.Mode, 1.0).Shell.A
                        > ThemeCatalog.Build(def.Mode, 0.0).Shell.A,
                $"{def.Name} 在下限之上无法再调浓淡");
        }
    }

    [Fact]
    public void OpacitySlider_IsMonotonic()
    {
        foreach (var def in OpaqueThemes)
        {
            var low = ThemeCatalog.Build(def.Mode, 0.2).Shell.A;
            var mid = ThemeCatalog.Build(def.Mode, 0.5).Shell.A;
            var high = ThemeCatalog.Build(def.Mode, 0.9).Shell.A;

            // 用"不递减"而不是"严格递增"：材质型主题在**下限以下**是一段平台
            // （0.2 和 0.5 都取下限），但整体必须随滑块向上，绝不允许反向。
            Assert.True(low <= mid && mid <= high,
                $"{def.Name} 的不透明度随滑杆不单调：{low}/{mid}/{high}");
            Assert.True(low < high,
                $"{def.Name} 的不透明度在整段区间上完全没有变化：{low}/{high}");
        }
    }

    [Fact]
    public void TransparentTheme_IgnoresOpacityForTheShell()
    {
        // 「无背景」要的是全透明，滑杆不该把它变成一层白。
        foreach (var opacity in new[] { 0.0, 0.5, 1.0 })
        {
            Assert.Equal(0, ThemeCatalog.Build(CalendarBackgroundMode.None, opacity).Shell.A);
        }
    }

    [Fact]
    public void Composite_And_Contrast_MatchKnownValues()
    {
        // 工具函数本身也要是对的，否则上面所有对比度断言都是假绿。
        Assert.Equal(1.0, ThemeCatalog.ContrastRatio(RgbaColor.White, RgbaColor.White), 3);
        Assert.Equal(21.0, ThemeCatalog.ContrastRatio(RgbaColor.White, RgbaColor.Black), 1);

        // 50% 中灰叠在白上 = 中灰
        var half = new RgbaColor(128, 0, 0, 0);
        var result = ThemeCatalog.Composite(half, RgbaColor.White);
        Assert.InRange(result.R, 126, 130);
    }

    [Fact]
    public void PaperTexture_MatchesTheMeasuredReference()
    {
        // 参数是按参考图**实测标定**后、再按用户反馈加重的（见 MaxAlpha 的注释）。
        var pixels = PaperTexture.Create();
        Assert.Equal(PaperTexture.Size * PaperTexture.Size, pixels.Length);

        var speckles = pixels.Where(p => p.A > 0).ToList();
        var density = (double)speckles.Count / pixels.Length;
        Assert.InRange(density, PaperTexture.MinDensity, PaperTexture.MaxDensity);

        Assert.All(speckles, p =>
        {
            // 三层颗粒（细颗粒 / 纤维 / 2×2 粗颗粒）各有自己的区间，取并集检查。
            Assert.InRange(p.A, PaperTexture.MinFiberAlpha, PaperTexture.MaxCoarseAlpha);

            // 颗粒必须是**暖色**（R>G>B）且比纸底明显更深 ——
            // 做成比底色更亮就成了"撒了糖霜"，那是另一种材质。
            // 这一条抓到过真 bug：三通道各取一路独立随机数时，会撞出 R=196,G=207 的偏绿点。
            Assert.True(p.R > p.G && p.G > p.B, $"颗粒不是暖色：{p.R},{p.G},{p.B}");
            Assert.True(p.B < 210, $"颗粒不够深，压在浅色纸上会看不见：B={p.B}");
        });

        // 粗颗粒是"能看见"的那一层：必须有，且必须是**成块**的（2×2）。
        // 只有 1×1 细噪点时，125%/150% 缩放下会被插值糊成一片均匀的灰。
        var coarse = speckles.Count(p => p.A >= PaperTexture.MinCoarseAlpha);
        Assert.True(coarse >= 200, $"粗颗粒太少（{coarse} 个像素），纹理在缩放屏幕上会被糊掉");

        // 确定性：两个宿主必须生成同一张图，否则同一个主题在两端的观感会不一样。
        Assert.Equal(pixels, PaperTexture.Create());
    }

    [Fact]
    public void BeigeTexture_GrainIsVisibleWithoutBecomingSandpaper()
    {
        const double defaultOpacity = 0.86;
        var c = ThemeCatalog.Build(CalendarBackgroundMode.BeigeTexture, defaultOpacity);

        Assert.True(ThemeCatalog.IsTextured(CalendarBackgroundMode.BeigeTexture));
        Assert.True(c.TextureOpacity > 0, "纹理浓度必须随用户透明度一起算出来");

        // 底色要贴近实测的纸面 (242,237,234)：近白暖米，R>G>B。
        // 这里直接看 Shell 本身（未与壁纸合成）—— 那才是"这张纸"的颜色。
        var shell = c.Shell;
        Assert.True(shell.R > shell.G && shell.G > shell.B, $"纸底不是暖色：{shell.R},{shell.G},{shell.B}");
        Assert.InRange(shell.R, 232, 252);
        Assert.InRange(shell.B, 222, 245);

        // ===== 这条判据被**刻意改过**，理由留在这里 =====
        // 上一版按参考照片实测标定，要求最深颗粒合成后亮度落差只有 1~12 级
        // （照片实测 ±2~3）。用户装上后的反馈是"感觉纹理还不够"。
        // 原因很清楚：照片本身经过拍摄与压缩，振幅天然偏小；而且照片有自己的物理尺度，
        // 屏幕上按同一振幅铺就是看不见。所以判据改成——
        // **必须看得见**（≥4 级），同时不许变成砂纸（≤30 级）。
        var deepest = _texturePixels.Value.OrderByDescending(p => p.A).First();
        var alpha = (byte)Math.Round(deepest.A * c.TextureOpacity);
        var over = ThemeCatalog.Composite(
            new RgbaColor(alpha, deepest.R, deepest.G, deepest.B), shell);

        var delta = Math.Max(
            Math.Abs(over.R - shell.R),
            Math.Max(Math.Abs(over.G - shell.G), Math.Abs(over.B - shell.B)));

        Assert.InRange(delta, 4, 30);
    }

    /// <summary>纹理像素是常量图案，测试内缓存一份，避免每个断言都重算。</summary>
    private static readonly Lazy<RgbaColor[]> _texturePixels = new(PaperTexture.Create);
}

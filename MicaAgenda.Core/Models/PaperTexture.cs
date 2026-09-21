namespace MicaAgenda.App.Models;

/// <summary>
/// 「米色纹理」主题用的**纸感颗粒**图案。
///
/// <para><b>参数是按实测标定的</b>，不是拍脑袋：用户给的参考图（米色纸照片）与他的桌面壁纸
/// 纸面部分，量出来是同一档 ——底色 <c>(242, 237, 234)</c>（近白暖米），
/// 高通颗粒振幅 <b>sd ≈ 0.7</b>，只有约 1.3% 的像素比邻域暗 2 级以上、几乎没有暗 5 级以上的点。
/// 也就是说这是**极细、极低对比**的颗粒；凭感觉调很容易做重 3~5 倍，
/// 那样看起来就不是"纸"而是"砂纸"了。</para>
///
/// <para><b>为什么是纯算法 + 自己实现随机数</b>：这段代码要在两个宿主（Avalonia / WPF）里
/// 各生成一次位图，还必须长得一模一样；单测也要能稳定复现。所以：
/// ① 不依赖任何 UI 框架（只产出 ARGB 像素数组）；
/// ② 自己写 LCG 而**不用 <c>System.Random</c>** —— 带种子的 <c>System.Random</c>
/// 算法在 .NET 6 时被换过一次，不能指望跨版本/跨宿主产出同一串数。</para>
///
/// <para><b>为什么是「随机噪点」而不是画纹理图案</b>：这张图会被 <c>TileMode=Tile</c> 平铺，
/// 每 128px 重复一次。随机颗粒没有大尺度结构，平铺接缝看不出来；任何有图案的纹理
/// 一旦平铺就会露出明显的方格重复。</para>
///
/// <para><b>色相约定</b>：颗粒一律是**暖色**（R &gt; G &gt; B），且比底色**更深**。
/// 参考图是"米色再生纸"，颗粒的观感来自"纸上更深的小杂点"；
/// 若做成比底色更亮就成了"撒了糖霜"，那是另一种材质，不是这张图。</para>
/// </summary>
public static class PaperTexture
{
    /// <summary>平铺图块的边长（像素）。128 足够大到看不出重复，又足够小到随手就能生成。</summary>
    public const int Size = 128;

    /// <summary>
    /// 颗粒覆盖率（约 7%）。太高会变成"砂纸"，太低则完全看不出纹理。
    /// 单测按这个区间校验：实际覆盖率必须落在 <see cref="MinDensity"/> ~ <see cref="MaxDensity"/> 之间。
    /// </summary>
    public const double Density = 0.07;

    public const double MinDensity = 0.03;
    public const double MaxDensity = 0.12;

    /// <summary>颗粒不透明度的下限 —— 低于这个值在浅色底上等于没有，纯属白算。</summary>
    public const byte MinAlpha = 6;

    /// <summary>
    /// 颗粒不透明度的上限。**这个值直接对应观感强弱**：颗粒色约 (210,193,170)、
    /// 底色约 (242,237,234) 时，合成后的亮度落差 ≈ <c>(底色 - 颗粒) × alpha / 255</c>；
    /// 按参考图量到的 ±2~3 级，alpha 均值落在 20 附近。
    /// 调到 80 那种量级会变成一颗颗清晰的黑点，失去"纸感"。
    /// </summary>
    public const byte MaxAlpha = 40;

    /// <summary>
    /// 次要的"纤维"层（<see cref="FiberChance"/> 那部分像素）的不透明度区间。
    /// 它刻意比主颗粒更淡，作用只是把颗粒之间的空隙填得**不那么均匀**，本身不该被看成杂点。
    /// </summary>
    public const byte MinFiberAlpha = 3;

    public const byte MaxFiberAlpha = 12;

    /// <summary>纤维层占比（在主颗粒之外额外叠的那一层）。</summary>
    private const double FiberChance = 0.025;

    /// <summary>
    /// 主颗粒的色域端点。取值受一条硬约束：**必须 R &gt; G &gt; B**（暖色 + 比底色深）。
    ///
    /// <para>这里用"一个亮度参数 <c>t</c> 同时推导三通道"，而<b>不是三个独立随机数</b> ——
    /// 后者会撞出 <c>R=196, G=207</c> 这种 G 反超 R 的偏绿点（单测实测抓到过）。
    /// 用同一个 t 插值，只要两端都满足 R&gt;G&gt;B，中间任何取值也必然满足，
    /// 这才让"颗粒是暖色"成为结构性保证而不是统计上的大概率。</para>
    /// </summary>
    private const int LightR = 225;
    private const int LightG = 207;
    private const int LightB = 183;
    private const int DarkR = 196;
    private const int DarkG = 180;
    private const int DarkB = 158;

    /// <summary>
    /// 生成 <see cref="Size"/>×<see cref="Size"/> 的 ARGB 像素（行优先）。
    /// 底色位置一律返回**全透明** —— 底色由主题的 Shell 画刷负责，
    /// 纹理层只负责叠上去的那点颗粒，这样它才能跟着透明度一起淡出（见 <see cref="ThemeColors.TextureOpacity"/>）。
    /// </summary>
    public static RgbaColor[] Create()
    {
        var pixels = new RgbaColor[Size * Size];
        var rng = new Lcg(20260921);

        for (var i = 0; i < pixels.Length; i++)
        {
            var roll = rng.NextUnit();

            if (roll < Density)
            {
                // 主颗粒：一个亮度参数 t 同时决定 alpha 与三通道，保证 R>G>B。
                var alpha = (byte)(MinAlpha + rng.NextInt(MaxAlpha - MinAlpha + 1));
                var t = rng.NextUnit();
                pixels[i] = new RgbaColor(
                    alpha,
                    (byte)Math.Round(LightR - (LightR - DarkR) * t),
                    (byte)Math.Round(LightG - (LightG - DarkG) * t),
                    (byte)Math.Round(LightB - (LightB - DarkB) * t));
                continue;
            }

            if (roll < Density + FiberChance)
            {
                // 更淡的"纤维"：颜色固定，只让不透明度轻微浮动。
                pixels[i] = new RgbaColor(
                    (byte)(MinFiberAlpha + rng.NextInt(MaxFiberAlpha - MinFiberAlpha + 1)),
                    214,
                    200,
                    180);
            }
        }

        return pixels;
    }

    /// <summary>
    /// 线性同余发生器。跨进程 / 跨宿主 / 跨 .NET 版本都产出同一串数，
    /// 这是"两个宿主纹理必须一致"和"单测可复现"的前提。
    /// </summary>
    private struct Lcg(uint seed)
    {
        private uint _state = seed == 0 ? 1u : seed;

        /// <summary>下一个 32 位无符号数（Numerical Recipes 的参数）。</summary>
        private uint Next()
        {
            _state = unchecked(_state * 1664525u + 1013904223u);
            return _state;
        }

        /// <summary>[0,1) 上的浮点数。</summary>
        public double NextUnit() => (Next() >> 8) / (double)(1 << 24);

        /// <summary>[0, maxExclusive) 上的整数。</summary>
        public int NextInt(int maxExclusive)
            => maxExclusive <= 0 ? 0 : (int)(Next() % (uint)maxExclusive);
    }
}

namespace MicaAgenda.App.Models;

/// <summary>
/// 「米色纹理」主题用的**纸感颗粒**图案。
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
    /// 颗粒覆盖率（约 5.5%）。太高会变成"砂纸"，太低则完全看不出纹理。
    /// 单测按这个区间校验：实际覆盖率必须落在 <see cref="MinDensity"/> ~ <see cref="MaxDensity"/> 之间。
    /// </summary>
    public const double Density = 0.055;

    public const double MinDensity = 0.03;
    public const double MaxDensity = 0.10;

    /// <summary>颗粒不透明度的下限 —— 低于这个值在浅色底上等于没有，纯属白算。</summary>
    public const byte MinAlpha = 8;

    /// <summary>颗粒不透明度的上限 —— 超过就会变成一颗颗清晰的黑点，失去"纸感"。</summary>
    public const byte MaxAlpha = 80;

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
                // 主颗粒：暖棕，深浅随机（同一片纸上的杂点本来就不是同一个深度）
                var alpha = (byte)(MinAlpha + rng.NextInt(MaxAlpha - MinAlpha + 1));
                pixels[i] = new RgbaColor(
                    alpha,
                    (byte)(148 + rng.NextInt(44)),   // R 148..191
                    (byte)(118 + rng.NextInt(38)),   // G 118..155
                    (byte)(96 + rng.NextInt(36)));   // B  96..131
                continue;
            }

            if (roll < Density + 0.02)
            {
                // 极淡的"纤维"：把颗粒之间的空隙填得不那么均匀，
                // 只加一点存在感，不构成可见图案。
                pixels[i] = new RgbaColor((byte)(6 + rng.NextInt(8)), 172, 152, 132);
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

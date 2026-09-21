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
    /// 细颗粒覆盖率（约 10%）。太高会变成"砂纸"，太低则完全看不出纹理。
    /// 单测按 <see cref="MinDensity"/> ~ <see cref="MaxDensity"/> 校验**总**覆盖率（含粗颗粒与纤维）。
    /// </summary>
    public const double Density = 0.10;

    public const double MinDensity = 0.06;
    public const double MaxDensity = 0.24;

    /// <summary>颗粒不透明度的下限 —— 低于这个值在浅色底上等于没有，纯属白算。</summary>
    public const byte MinAlpha = 6;

    /// <summary>
    /// 细颗粒不透明度的上限。
    ///
    /// <para>参考图实测的振幅只有 ±2~3 级，但那是**一张照片**：拍摄与压缩本身就把颗粒糊平了，
    /// 而且照片有自己的物理尺度。屏幕上按同样的振幅铺，用户的原话是"感觉纹理还不够"。
    /// 所以这里刻意做得比照片**明显更重**，目标是在 100% 缩放下就能一眼看出纸感，
    /// 而不是要一张数据上和照片一致、看起来却什么都没有的图。</para>
    /// </summary>
    public const byte MaxAlpha = 84;

    /// <summary>
    /// 次要的"纤维"层（<see cref="FiberChance"/> 那部分像素）的不透明度区间。
    /// 它刻意比主颗粒更淡，作用只是把颗粒之间的空隙填得**不那么均匀**，本身不该被看成杂点。
    /// </summary>
    public const byte MinFiberAlpha = 5;

    public const byte MaxFiberAlpha = 26;

    /// <summary>纤维层占比（在主颗粒之外额外叠的那一层）。</summary>
    private const double FiberChance = 0.05;

    /// <summary>
    /// 粗颗粒（2×2 像素）的落点密度 —— 按 2 像素网格算，即约 4.5% 的格子会被填上。
    ///
    /// <para><b>为什么必须有这么一层</b>：只有 1×1 的细噪点时，纹理在 125%/150% 缩放的屏幕上
    /// 会被放大插值糊成一片均匀的灰 —— 用户看到的就还是"没有纹理"。
    /// 2×2 的颗粒在缩放后仍然是一个有边界的斑点，这才是"能看见"的那一层。
    /// 它同时让质感更接近再生纸上的**纤维碎屑**，而不只是均匀噪点。</para>
    /// </summary>
    private const double CoarseDensity = 0.045;

    /// <summary>粗颗粒的不透明度区间 —— 它要的就是"看得见"，所以整体比细颗粒更实。</summary>
    public const byte MinCoarseAlpha = 30;

    public const byte MaxCoarseAlpha = 96;

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

    /// <summary>粗颗粒专用色域：整体更深一档，压在纸上才看得出是"碎屑"而不是"被糊掉的噪点"。</summary>
    private const int CoarseLightR = 200;
    private const int CoarseLightG = 184;
    private const int CoarseLightB = 161;
    private const int CoarseDarkR = 172;
    private const int CoarseDarkG = 155;
    private const int CoarseDarkB = 131;

    /// <summary>
    /// 生成 <see cref="Size"/>×<see cref="Size"/> 的 ARGB 像素（行优先）。
    /// 底色位置一律返回**全透明** —— 底色由主题的 Shell 画刷负责，
    /// 纹理层只负责叠上去的那点颗粒，这样它才能跟着透明度一起淡出（见 <see cref="ThemeColors.TextureOpacity"/>）。
    /// </summary>
    public static RgbaColor[] Create()
    {
        var pixels = new RgbaColor[Size * Size];
        var rng = new Lcg(20260921);

        // ① 细颗粒 + 纤维：逐像素。
        for (var i = 0; i < pixels.Length; i++)
        {
            var roll = rng.NextUnit();

            if (roll < Density)
            {
                // 主颗粒：一个亮度参数 t 同时决定 alpha 与三通道，保证 R>G>B。
                var alpha = (byte)(MinAlpha + rng.NextInt(MaxAlpha - MinAlpha + 1));
                pixels[i] = Grain(ref rng, alpha,
                    LightR, LightG, LightB, DarkR, DarkG, DarkB, 0.0);
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

        // ② 粗颗粒：按 2 像素网格铺。
        //    奇偶行错开半个格（x 起点轮流从 0 / 1 开始），否则会看到规则的方阵。
        for (var y = 0; y < Size - 1; y += 2)
        {
            var xStart = (y / 2) % 2;

            for (var x = xStart; x < Size - 1; x += 2)
            {
                if (rng.NextUnit() >= CoarseDensity)
                {
                    continue;
                }

                var alpha = (byte)(MinCoarseAlpha +
                                   rng.NextInt(MaxCoarseAlpha - MinCoarseAlpha + 1));
                // 粗颗粒只取色域偏深的那半段（t ≥ 0.4），保证它在纸上真的看得出来。
                var grain = Grain(ref rng, alpha,
                    CoarseLightR, CoarseLightG, CoarseLightB,
                    CoarseDarkR, CoarseDarkG, CoarseDarkB, 0.4);

                pixels[y * Size + x] = grain;
                pixels[y * Size + x + 1] = grain;
                pixels[(y + 1) * Size + x] = grain;
                pixels[(y + 1) * Size + x + 1] = grain;
            }
        }

        return pixels;
    }

    /// <summary>
    /// 按一个亮度参数推导出颗粒颜色，保证三通道始终 R &gt; G &gt; B。
    ///
    /// <para><b><paramref name="rng"/> 必须按引用传</b>：<see cref="Lcg"/> 是<b>结构体</b>，
    /// 按值传的话每次调用都会拿到一份状态副本，内部推进不会回写 ——
    /// 结果是每一颗颗粒都用同一个随机值，整张纸变成同一种颜色的重复图案。</para>
    ///
    /// <para><paramref name="minT"/> 是色域下界的起点：细颗粒取 0（全色域），
    /// 粗颗粒取 0.4（只用偏深的那半段），这样小碎屑看得见、大斑点也不会深得发黑。</para>
    /// </summary>
    private static RgbaColor Grain(
        ref Lcg rng,
        byte alpha,
        int lightR, int lightG, int lightB,
        int darkR, int darkG, int darkB,
        double minT)
    {
        var t = minT + rng.NextUnit() * (1 - minT);

        return new RgbaColor(
            alpha,
            (byte)Math.Round(lightR - (lightR - darkR) * t),
            (byte)Math.Round(lightG - (lightG - darkG) * t),
            (byte)Math.Round(lightB - (lightB - darkB) * t));
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

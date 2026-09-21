using System.Runtime.InteropServices;
using MicaAgenda.App.Services;

namespace MicaAgenda.Tests;

/// <summary>
/// 更新检查里的纯逻辑：版本号解析/规范化、按平台挑安装包。
/// 这几条决定了「会不会给用户推错包」，所以单独钉住。
/// </summary>
public sealed class UpdateServiceTests
{
    private static readonly UpdateAsset WinSetup =
        new("desktop_todo_Calendar-Setup-4.2.0.exe", "https://example.com/setup.exe", 52_015_020);
    private static readonly UpdateAsset SourceZip =
        new("desktop_todo_Calendar-Source-4.2.0.zip", "https://example.com/source.zip", 444_571);
    private static readonly UpdateAsset WinZip =
        new("desktop_todo_Calendar-4.2.0-x64.zip", "https://example.com/x64.zip", 47_109_313);
    private static readonly UpdateAsset MacArmDmg =
        new("desktop_todo_Calendar-4.2.0-arm64.dmg", "https://example.com/arm64.dmg", 51_187_112);
    private static readonly UpdateAsset MacX64Dmg =
        new("desktop_todo_Calendar-4.2.0-x64.dmg", "https://example.com/x64.dmg", 52_776_035);
    private static readonly UpdateAsset LinuxAppImage =
        new("desktop_todo_Calendar-4.2.0-x64.AppImage", "https://example.com/x64.AppImage", 41_683_448);
    private static readonly UpdateAsset LinuxDeb =
        new("desktop_todo_Calendar_4.2.0_amd64.deb", "https://example.com/amd64.deb", 35_718_426);

    private static readonly UpdateAsset[] All =
        [WinSetup, SourceZip, WinZip, MacArmDmg, MacX64Dmg, LinuxAppImage, LinuxDeb];

    [Theory]
    [InlineData("v4.2.0", "4.2.0")]
    [InlineData("4.2.0", "4.2.0")]
    [InlineData("V4.2.0", "4.2.0")]
    [InlineData("v4.2.0-beta.1", "4.2.0")]
    [InlineData(" v4.1.1 ", "4.1.1")]
    public void ParseTagVersion_AcceptsCommonTagShapes(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateService.ParseTagVersion(tag));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("vNext")]
    public void ParseTagVersion_ReturnsNullForGarbage(string tag)
    {
        Assert.Null(UpdateService.ParseTagVersion(tag));
    }

    [Fact]
    public void Normalize_PadsMissingSegments()
    {
        Assert.Equal("4.2.0", UpdateService.Normalize(new Version(4, 2)));
        Assert.Equal("4.2.0", UpdateService.Normalize(new Version(4, 2, 0)));
        Assert.Equal("4.2.1", UpdateService.Normalize(new Version(4, 2, 1, 7)));
    }

    [Fact]
    public void SelectAsset_Windows_PicksSetupExeNotZipOrSource()
    {
        var asset = UpdateService.SelectAsset(All, OSPlatform.Windows, Architecture.X64);

        Assert.NotNull(asset);
        Assert.Equal(WinSetup.Name, asset!.Name);
    }

    [Fact]
    public void SelectAsset_Windows_ReturnsNullWhenOnlyArchivesExist()
    {
        var asset = UpdateService.SelectAsset([SourceZip, WinZip], OSPlatform.Windows, Architecture.X64);

        Assert.Null(asset);
    }

    [Fact]
    public void SelectAsset_MacOS_PicksDmgMatchingArchitecture()
    {
        Assert.Equal(
            MacArmDmg.Name,
            UpdateService.SelectAsset(All, OSPlatform.OSX, Architecture.Arm64)!.Name);
        Assert.Equal(
            MacX64Dmg.Name,
            UpdateService.SelectAsset(All, OSPlatform.OSX, Architecture.X64)!.Name);
    }

    [Fact]
    public void SelectAsset_Linux_PrefersAppImageOverDeb()
    {
        var asset = UpdateService.SelectAsset(All, OSPlatform.Linux, Architecture.X64);

        Assert.NotNull(asset);
        Assert.Equal(LinuxAppImage.Name, asset!.Name);
    }

    [Fact]
    public void SelectAsset_EmptyList_ReturnsNull()
    {
        Assert.Null(UpdateService.SelectAsset([], OSPlatform.Windows, Architecture.X64));
    }

    [Fact]
    public void CurrentAppVersion_ComesFromTheAssemblyVersionNotAHardcodedLiteral()
    {
        // 单一版本号来源是 Directory.Build.props；这里只钉住「能读到、且不是 0.0.0」，
        // 免得将来有人把 AssemblyInformationalVersion 去掉后就静默降级成 0.0.0，
        // 那会让「有新版本」的条件永远成立。
        var version = UpdateService.CurrentAppVersion();

        Assert.True(version.Major >= 1, $"版本号看起来不对：{version}");
    }

    // ===== 分段下载 =====

    [Fact]
    public void PlanSegments_CoversTheWholeFileExactlyOnce()
    {
        // 分段下载最容易出的错是"拼起来不是原来那个文件"：
        // 漏一段、重叠一段、或者最后一段没吃掉余数，都会做出一个坏安装包。
        // 所以边界必须钉死：首段从 0 开始、段与段无缝相邻、总长正好等于文件大小。
        foreach (var total in new long[]
                 {
                     0, 1, 1024,
                     UpdateService.ParallelThresholdBytes,                 // 8MB
                     UpdateService.ParallelThresholdBytes + 1,
                     52_015_020,                                           // 真实安装包大小
                     300L * 1024 * 1024                                    // 远超上限
                 })
        {
            var segments = UpdateService.PlanSegments(total);

            if (total <= 0)
            {
                Assert.Empty(segments);
                continue;
            }

            Assert.InRange(segments.Count, 1, UpdateService.MaxSegments);
            Assert.Equal(0, segments[0].Start);

            long cursor = 0;
            long sum = 0;
            foreach (var segment in segments)
            {
                Assert.True(segment.Length > 0, $"出现了空分段（total={total}）");
                Assert.Equal(cursor, segment.Start);      // 无缝：下一段的起点就是上一段的终点
                cursor = segment.EndExclusive;
                sum += segment.Length;
            }

            Assert.Equal(total, sum);
        }
    }

    [Fact]
    public void PlanSegments_SplitsTheRealInstallerIntoSeveralParts()
    {
        // 52MB 的安装包必须真的被切开 —— 否则这次改动等于没做。
        var segments = UpdateService.PlanSegments(WinSetup.SizeBytes);

        Assert.True(segments.Count > 1,
            $"52MB 的安装包只切出 {segments.Count} 段，分段下载没有生效");
    }

    [Fact]
    public void PlanSegments_DoesNotSplitTinyFiles()
    {
        // 小文件分段只会多几次握手，纯亏。
        Assert.Single(UpdateService.PlanSegments(1_000_000));
        Assert.Single(UpdateService.PlanSegments(UpdateService.ParallelThresholdBytes - 1));
    }

    // ===== 加速前缀 =====

    [Theory]
    [InlineData(null, "https://github.com/a/b.exe", "https://github.com/a/b.exe")]
    [InlineData("", "https://github.com/a/b.exe", "https://github.com/a/b.exe")]
    [InlineData("   ", "https://github.com/a/b.exe", "https://github.com/a/b.exe")]
    // 带结尾斜杠
    [InlineData("https://mirror.example/", "https://github.com/a/b.exe",
        "https://mirror.example/https://github.com/a/b.exe")]
    // 不带结尾斜杠：自动补一个，别拼出 "mirror.examplehttps://..."
    [InlineData("https://mirror.example", "https://github.com/a/b.exe",
        "https://mirror.example/https://github.com/a/b.exe")]
    // http 也认
    [InlineData("http://127.0.0.1:8080/", "https://github.com/a/b.exe",
        "http://127.0.0.1:8080/https://github.com/a/b.exe")]
    public void BuildDownloadUrl_PrependsTheMirrorPrefix(string? prefix, string original, string expected)
        => Assert.Equal(expected, UpdateService.BuildDownloadUrl(original, prefix));

    [Theory]
    [InlineData("mirror.example")]          // 没有协议头
    [InlineData("mirror.example/")]
    [InlineData("/local/path")]
    [InlineData("ftp://mirror.example/")]
    public void BuildDownloadUrl_IgnoresAnUnusablePrefix(string prefix)
    {
        // 用户填错了前缀时，宁可**不加速**也不能拼出一个必然失败的地址 ——
        // 那会让"下载更新"直接变成死路，比慢严重得多。
        const string original = "https://github.com/a/b.exe";
        Assert.Equal(original, UpdateService.BuildDownloadUrl(original, prefix));
    }
}

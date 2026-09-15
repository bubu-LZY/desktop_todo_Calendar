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
}

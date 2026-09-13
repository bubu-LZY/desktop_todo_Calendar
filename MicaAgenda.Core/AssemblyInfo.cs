using System.Runtime.CompilerServices;

// 跨平台单测会直接断言内部类型：同步比对/仲裁的 ReviewSyncPlan、ReviewSyncEntry
// 等都是 internal。它们原先位于 MicaAgenda.App（测试通过 App 的
// InternalsVisibleTo 看得到），搬进 Core 后需要在这里声明。
[assembly: InternalsVisibleTo("MicaAgenda.Tests")]

// Windows 测试工程同理：AutoStartService / HighPriorityStartupService 搬进 Core 后，
// 它们拼命令行、查注册表的内部方法要靠这里才能被测到。
[assembly: InternalsVisibleTo("MicaAgenda.Tests.Windows")]
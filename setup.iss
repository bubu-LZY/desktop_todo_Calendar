; MicaAgenda 桌面日历 - 安装脚本
; 编译命令: ISCC.exe setup.iss

#define MyAppName "desktop_todo_Calendar"
; 允许 CI 用 /DMyAppVersion=... 覆盖；本地直接编译时用下面的兜底值
#ifndef MyAppVersion
  #define MyAppVersion "3.1.8"
#endif
#define MyAppPublisher "MicaAgenda"
#define MyAppExeName "MicaAgenda.App.exe"

[Setup]
AppId={{B8A7F2E1-9C4D-4A2B-8E5F-3D6C1A9B7E20}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\desktop_todo_Calendar
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=installer
OutputBaseFilename=desktop_todo_Calendar-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 中文语言
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64

[Languages]
; 中文语言文件：优先用编译器自带的；CI 里若编译器不带中文，会通过 /DLangIsl= 指到随仓库携带的副本
#ifndef LangIsl
  #define LangIsl "compiler:Languages\ChineseSimplified.isl"
#endif
Name: "chinesesimplified"; MessagesFile: "{#LangIsl}"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"
Name: "autostart"; Description: "开机自启动"; GroupDescription: "附加任务:"

[Files]
; 复制整个 release 目录：单文件自包含 exe 仍需要旁边的 WPF 原生依赖 DLL
; (wpfgfx_cor3 / D3DCompiler_47_cor3 / PenImc_cor3 / PresentationNative_cor3 / vcruntime140_cor3)
; 否则在干净机器上 WPF 启动即崩溃，表现为"安装后打不开"。
Source: "installer\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "desktop_todo_Calendar"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// 判断某个文件是否属于"本程序一定不会用到的其它宿主产物"。
// 用前缀匹配（大小写不敏感）而不是枚举完整文件名，避免漏掉版本号/平台后缀带来的变体。
const
  ForeignPrefixes = 'micaagenda.desktop,micaagenda.core,avalonia,libskiasharp,libharfbuzzsharp,harfbuzzsharp';
  ForeignExactNames = 'skiasharp.dll,microcom.runtime.dll,tmds.dbus.protocol.dll,av_libglesv2.dll';

function IsForeignFile(const FileName: String): Boolean;
var
  Lower: String;
begin
  Lower := Lowercase(FileName);

  // 前后补逗号后做整词匹配：避免 'avalonia' 误伤 'av_libglesv2' 这类看着像、其实没关系的名字
  if Pos(',' + Lower + ',', ',' + ForeignPrefixes + ',') > 0 then
  begin
    Result := True;
    Exit;
  end;

  if Pos(',' + Lower + ',', ',' + ForeignExactNames + ',') > 0 then
  begin
    Result := True;
    Exit;
  end;

  Result := False;
end;

// 安装前：先关掉正在运行的旧进程，再清掉"上一个安装包遗留、本版本用不到"的文件。
//
// 为什么需要清理：[Files] 是整目录覆盖，同名文件会被替换，但旧版本多出来的文件会一直留着。
// 历史上有一版安装包把 Avalonia 跨平台宿主也一起装进了同一个目录（MicaAgenda.Desktop /
// MicaAgenda.Core / Avalonia*.dll / Skia / HarfBuzz 等），本程序（WPF 宿主）完全用不到，
// 留着既白占空间，也容易让人搞不清自己装的到底是哪个版本。
//
// ⚠️ 只清理上面名单里写死的那几个名字，绝不遍历删除其他文件：自包含发布有 300+ 个运行库文件，
//    任何"保留白名单"式的清理都可能把必需的 DLL 删掉，导致装完打不开
//    （这正是本脚本早先那版写法的隐患）。
// ⚠️ 更不能碰用户数据目录（%LOCALAPPDATA%\MicaAgenda 与 %APPDATA%\MicaAgenda），
//    那里存着任务数据 / 节假日缓存 / 配置文件，删掉会造成永久数据丢失。
//    本脚本只在 {app}（安装目录，默认 %LOCALAPPDATA%\Programs\desktop_todo_Calendar）里动手。
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  AppDir: String;
  FindRec: TFindRec;
  FileName: String;
  LowerName: String;
begin
  if CurStep <> ssInstall then
    Exit;

  // 旧进程占用文件会导致安装失败，先关掉（同时兼容从 Avalonia 版本装回来的情况）
  Exec('taskkill', '/F /IM MicaAgenda.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill', '/F /IM MicaAgenda.Desktop.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  AppDir := ExpandConstant('{app}');
  if not DirExists(AppDir) then
    Exit;

  if FindFirst(AppDir + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
        begin
          FileName := FindRec.Name;
          LowerName := Lowercase(FileName);

          // 前缀命中（如 MicaAgenda.Desktop.exe / Avalonia.Base.dll / libSkiaSharp.pdb）
          if (Pos(',' + LowerName + ',', ',' + ForeignPrefixes + ',') > 0)
             or (Copy(LowerName, 1, 18) = 'micaagenda.desktop')
             or (Copy(LowerName, 1, 15) = 'micaagenda.core')
             or (Copy(LowerName, 1, 8) = 'avalonia')
             or (Copy(LowerName, 1, 12) = 'libskiasharp')
             or (Copy(LowerName, 1, 15) = 'libharfbuzzsharp')
             or (Copy(LowerName, 1, 12) = 'harfbuzzsharp')
             // 精确命中（如 SkiaSharp.dll / av_libglesv2.dll）
             or (Pos(',' + LowerName + ',', ',' + ForeignExactNames + ',') > 0) then
          begin
            if DeleteFile(AppDir + '\' + FileName) then
              Log('Removed stale non-WPF file: ' + FileName)
            else
              Log('Failed to remove stale file: ' + FileName);
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;
; MicaAgenda 桌面日历 - 安装脚本
; 编译命令: ISCC.exe setup.iss

#define MyAppName "desktop_todo_Calendar"
; 允许 CI 用 /DMyAppVersion=... 覆盖；本地直接编译时用兜底值
#ifndef MyAppVersion
  #define MyAppVersion "3.3.3"
#endif
#define MyAppPublisher "MicaAgenda"
#define MyAppExeName "MicaAgenda.Desktop.exe"

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
; 中文语言文件随仓库携带（choco 安装的 Inno Setup 不含中文，用相对脚本目录的路径）
Name: "chinesesimplified"; MessagesFile: "tools\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"
Name: "autostart"; Description: "开机自启动"; GroupDescription: "附加任务:"

[Files]
; 复制整个 pkg 目录（dotnet publish -o pkg 的产物）：自包含发布仍需要旁边的原生依赖 DLL
; (Avalonia 的 libSkiaSharp / libHarfBuzzSharp 等)，否则干净机器上启动即崩溃。
Source: "pkg\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; GPL-3.0 合规：安装目录必须随附许可证
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "desktop_todo_Calendar"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// 安装前自动关闭正在运行的 MicaAgenda 进程 + 清理旧版本残留文件。
// ⚠️ 绝对不能触碰用户数据目录（%LOCALAPPDATA%\MicaAgenda 与 %APPDATA%\MicaAgenda），
//    它们保存任务数据 / 节假日缓存 / 配置文件，清掉会导致用户数据永久丢失。
//    本脚本只清理安装目录（即 {app}，默认 %LOCALAPPDATA%\Programs\desktop_todo_Calendar）下的文件。
// 策略是「先删后铺」：删掉旧版本残留（WPF 宿主的 MicaAgenda.App.exe / MicaAgenda.App.dll 与
// PresentationFramework*.dll / *_cor3.dll 等 WPF 专属依赖），再由 [Files] 重新铺一遍本次产物；
// 只保留卸载器必需的 unins000.*，否则「应用和功能」里会卸载不掉。
// 注意：只清顶层文件，语言子目录（cs / de / zh-Hans 等）里的旧资源 DLL 不处理 —— 新版不会加载它们。
procedure RemoveStaleAppFiles;
var
  AppDir: String;
  Keep: TStringList;
  FindRec: TFindRec;
  FilePath, FileName: String;
begin
  AppDir := ExpandConstant('{app}');
  // 只认「本程序的安装目录」：最后一级目录名必须是 desktop_todo_Calendar。
  // 安装包免管理员（PrivilegesRequired=lowest），{app} 默认落在 %LOCALAPPDATA%\Programs\desktop_todo_Calendar，
  // 所以不能按「路径里含 AppData 就跳过」来兜底 —— 那会让清理永远不执行。改成认目录名：
  // 即使用户把安装目录指到别处（比如用户数据目录），也不会误删。
  if CompareText(ExtractFileName(AppDir), 'desktop_todo_Calendar') <> 0 then
  begin
    Log('ABORT: Refusing to clean unexpected directory: ' + AppDir);
    Exit;
  end;
  if not DirExists(AppDir) then Exit;
  Keep := TStringList.Create;
  try
    Keep.Sorted := True;
    Keep.Duplicates := dupIgnore;
    Keep.Add('unins000.exe');
    Keep.Add('unins000.dat');
    Keep.Add('unins000.msg');
    if FindFirst(AppDir + '\\*', FindRec) then
    begin
      try
        repeat
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
          begin
            FileName := FindRec.Name;
            if Keep.IndexOf(FileName) < 0 then
            begin
              FilePath := AppDir + '\\' + FileName;
              if DeleteFile(FilePath) then
                Log('Cleaned stale file: ' + FilePath)
              else
                Log('Failed to delete: ' + FilePath);
            end;
          end;
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
  finally
    Keep.Free;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    Exec('taskkill', '/F /IM MicaAgenda.Desktop.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // 兼容从旧版（WPF 宿主）升级：旧进程也要关掉，否则占用文件导致安装失败
    Exec('taskkill', '/F /IM MicaAgenda.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // 进程关掉之后再清理：正在运行的 exe / 已加载的原生 dll 删不掉
    RemoveStaleAppFiles;
  end;
end;

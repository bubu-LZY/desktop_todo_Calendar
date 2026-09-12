; MicaAgenda 桌面日历 - 安装脚本
; 编译命令: ISCC.exe setup.iss

#define MyAppName "desktop_todo_Calendar"
; 允许 CI 用 /DMyAppVersion=... 覆盖；本地直接编译时用兜底值
#ifndef MyAppVersion
  #define MyAppVersion "3.2.0"
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
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"
Name: "autostart"; Description: "开机自启动"; GroupDescription: "附加任务:"

[Files]
; 复制整个 publish 目录：自包含发布仍需要旁边的原生依赖 DLL
; (Avalonia 的 libSkiaSharp / libHarfBuzzSharp 等)，否则干净机器上启动即崩溃。
Source: "installer\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
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
// 安装前自动关闭正在运行的 MicaAgenda 进程 + 清理旧版本残留文件
// 保留单文件版本必需的 7 个文件，删除其他全部（多文件时代残留的 MicaAgenda.App.dll
// / .deps.json / System.*.dll / Microsoft.*.dll 等），避免 DLL 冲突导致 WPF 启动异常。
// ⚠️ 绝对不能触碰用户数据目录（%LOCALAPPDATA%\MicaAgenda 与 %APPDATA%\MicaAgenda），
//    它们保存任务数据 / 节假日缓存 / 配置文件，清掉会导致用户数据永久丢失。
// 本脚本只清理安装目录（即 {app}，默认 C:\Program Files\MicaAgenda）下的文件。
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    Exec('taskkill', '/F /IM MicaAgenda.Desktop.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // 兼容从旧版（WPF 宿主）升级：旧进程也要关掉，否则占用文件导致安装失败
    Exec('taskkill', '/F /IM MicaAgenda.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurInstallBeforeInstall;
var
  AppDir: String;
  Keep: TStringList;
  FindRec: TFindRec;
  FilePath, FileName: String;
  IsSafePath: Boolean;
begin
  // {app} 是安装目录（C:\Program Files\MicaAgenda），不是用户数据目录。
  // 多重护栏：如果路径包含 'AppData' / 'Roaming' / 'Local'，拒绝执行清理。
  AppDir := ExpandConstant('{app}');
  IsSafePath := (Pos('AppData', AppDir) = 0)
            and (Pos('Appdata', AppDir) = 0)
            and (Pos('appdata', AppDir) = 0)
            and (Pos('Roaming', AppDir) = 0)
            and (Pos('roaming', AppDir) = 0)
            and (Pos('Local\\', AppDir) = 0)
            and (Pos('local\\', AppDir) = 0);
  if not IsSafePath then
  begin
    Log('ABORT: Refusing to clean path that contains AppData/Roaming/Local: ' + AppDir);
    Exit;
  end;
  if not DirExists(AppDir) then Exit;
  Keep := TStringList.Create;
  try
    Keep.Sorted := True;
    Keep.Duplicates := dupIgnore;
    Keep.Add('MicaAgenda.App.exe');
    Keep.Add('MicaAgenda.App.pdb');
    Keep.Add('D3DCompiler_47_cor3.dll');
    Keep.Add('PenImc_cor3.dll');
    Keep.Add('PresentationNative_cor3.dll');
    Keep.Add('vcruntime140_cor3.dll');
    Keep.Add('wpfgfx_cor3.dll');
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

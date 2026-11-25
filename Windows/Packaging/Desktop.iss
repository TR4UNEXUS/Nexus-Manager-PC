#define AppVersion GetVersionNumbersString(PayloadDir + "\NexusManagerPC.exe")
[Setup]
AppId=NexusManagerPCDesktop
AppName=Nexus Manager for PC
AppVersion={#AppVersion}
AppPublisher=TR4UNEXUS
AppPublisherURL=https://github.com/TR4UNEXUS/Nexus-Manager-PC
AppUpdatesURL=https://github.com/TR4UNEXUS/Nexus-Manager-PC/releases/latest
DefaultDirName={localappdata}\Programs\Nexus Manager for PC
DefaultGroupName=Nexus Manager for PC
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupMutex=NexusManagerPCDesktop-Setup
SetupIconFile=..\nexus.ico
UninstallDisplayIcon={app}\NexusManagerPC.exe
Uninstallable=not Portable
CreateUninstallRegKey=not Portable
OutputBaseFilename=Nexus-Manager-PC-win-x64-Setup
OutputDir={#Destination}
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=no
RestartApplications=no
DisableReadyPage=yes

[Files]
Source: "{#PayloadDir}\NexusManagerPC.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Compression\NexusCompressionHost.exe"; DestDir: "{app}\Compression"; Flags: ignoreversion
Source: "{#PayloadDir}\Compression\LICENSE"; DestDir: "{app}\Compression"; Flags: ignoreversion
Source: "{#PayloadDir}\Compression\CompressionHost-source.zip"; DestDir: "{app}\Compression"; Flags: ignoreversion
Source: "..\..\README.md"; DestDir: "{app}\NexusManagerPC-docs"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}\NexusManagerPC-docs"; Flags: ignoreversion
Source: "..\..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}\NexusManagerPC-docs"; Flags: ignoreversion
Source: "notices\*.txt"; DestDir: "{app}\NexusManagerPC-docs\notices"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Nexus Manager for PC"; Filename: "{app}\NexusManagerPC.exe"; Check: not Portable

[Run]
Filename: "{app}\NexusManagerPC.exe"; Description: "Open Nexus Manager for PC"; Flags: postinstall nowait skipifsilent; Check: not Updating
Filename: "{app}\NexusManagerPC.exe"; Flags: nowait; Check: Updating

[Code]
function Portable: Boolean;
begin
  Result := ExpandConstant('{param:NEXUSPORTABLE|0}') = '1';
end;

function Updating: Boolean;
begin
  Result := ExpandConstant('{param:NEXUSUPDATE|0}') = '1';
end;

function OpenProcess(Access: LongWord; Inherit: Boolean; Pid: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Timeout: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Pid: Integer; Parent: THandle;
begin
  Result := '';
  if Updating then begin
    if not FileExists(ExpandConstant('{app}\NexusManagerPC.exe')) then begin
      Result := 'Could not find Nexus Manager in the update folder.';
      exit;
    end;
    Pid := StrToIntDef(ExpandConstant('{param:NEXUSPARENT|0}'),0);
    if Pid > 0 then begin
      Parent := OpenProcess($00100000,False,Pid);
      if Parent <> 0 then begin
        if WaitForSingleObject(Parent,60000) <> 0 then
          Result := 'Nexus Manager is still busy. Finish the current operation and try again.';
        CloseHandle(Parent);
      end;
    end;
  end;
  if CheckForMutexes('Local\NexusManagerPCDesktop-Running') then
    Result := 'Close all open copies of Nexus Manager for PC, then retry the installation.';
end;

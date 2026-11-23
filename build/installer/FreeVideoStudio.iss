#if Ver < EncodeVer(6, 3, 0)
  #error Inno Setup 6.3 or newer is required (winget install JRSoftware.InnoSetup).
#endif
#ifndef AppVersion
  #error AppVersion is missing. Build the installer with dev_build.cmd / Build.cmd (FvsBuild), not by hand.
#endif
#ifndef StagingDir
  #error StagingDir is missing. Build the installer with dev_build.cmd / Build.cmd (FvsBuild), not by hand.
#endif
#ifndef SignedListDir
  #error SignedListDir is missing. Build the installer with dev_build.cmd / Build.cmd (FvsBuild), not by hand.
#endif
#ifndef IconFile
  #error IconFile is missing. Build the installer with dev_build.cmd / Build.cmd (FvsBuild), not by hand.
#endif
#ifndef OutputDir
  #error OutputDir is missing. Build the installer with dev_build.cmd / Build.cmd (FvsBuild), not by hand.
#endif

[Setup]
AppName=Free Video Studio
AppVersion={#AppVersion}
AppVerName=Free Video Studio {#AppVersion}
AppPublisher=Alon Reich
AppPublisherURL=https://github.com/alonreich/Free_Video_Studio
AppSupportURL=https://github.com/alonreich/Free_Video_Studio/issues
AppCopyright=Copyright © 2026 Alon Reich. Licensed under the GNU GPL v3.
VersionInfoVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoCompany=Alon Reich
VersionInfoDescription=Free Video Studio Setup
VersionInfoProductName=Free Video Studio
VersionInfoCopyright=Copyright © 2026 Alon Reich. Licensed under the GNU GPL v3.
VersionInfoOriginalFileName=FreeVideoStudio.exe
OutputDir={#OutputDir}
OutputBaseFilename=FreeVideoStudio
SetupIconFile={#IconFile}
PrivilegesRequired=lowest
CreateAppDir=no
Uninstallable=no
DisableWelcomePage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
DisableProgramGroupPage=yes
DisableStartupPrompt=yes
ShowLanguageDialog=no
WizardStyle=modern
ArchitecturesAllowed=x64compatible
MinVersion=10.0
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
CloseApplications=no
RestartIfNeededByRun=no
SetupLogging=no

[Messages]
StatusExtractFiles=Unpacking Free Video Studio…

[Files]
Source: "{#StagingDir}\*"; DestDir: "{tmp}\fvs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SignedListDir}\update.json"; DestDir: "{tmp}\fvs"; Flags: ignoreversion
Source: "{#SignedListDir}\update.sig.json"; DestDir: "{tmp}\fvs"; Flags: ignoreversion

[Run]
Filename: "{tmp}\fvs\FreeVideoStudio.exe"; Parameters: "--install --smart-update ""{tmp}\fvs""{code:ForwardedArguments}"; WorkingDir: "{tmp}\fvs"; StatusMsg: "Starting the Free Video Studio installer…"; Flags: waituntilterminated runasoriginaluser

[Code]
function IsForwardedSwitch(const S: String): Boolean;
begin
  Result := (CompareText(S, '--auto-update') = 0) or (CompareText(S, '--source-current') = 0) or
            (CompareText(S, '--no-launch') = 0) or (CompareText(S, '--quiet') = 0);
end;

function ForwardedArguments(Param: String): String;
var
  I: Integer;
begin
  Result := '';
  for I := 1 to ParamCount do
  begin
    if IsForwardedSwitch(ParamStr(I)) then
      Result := Result + ' ' + LowerCase(ParamStr(I))
    else if (CompareText(ParamStr(I), '--wait-pid') = 0) and (I < ParamCount) and (StrToIntDef(ParamStr(I + 1), 0) > 0) then
      Result := Result + ' --wait-pid ' + IntToStr(StrToIntDef(ParamStr(I + 1), 0));
  end;
end;

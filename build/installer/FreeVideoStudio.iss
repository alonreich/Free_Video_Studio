; ════════════════════════════════════════════════════════════════════════════════════════════════
; NODROPPER_01 — THE FREE VIDEO STUDIO INSTALLER (Inno Setup 6.3 or newer)
;
; Spec: docs/09_DISTRIBUTION_AND_RELEASE.md #DIST-INSTALLER. Compiled by build\FvsBuild\InnoInstaller.cs;
; never edit the output by hand and never compile this file without the defines FvsBuild passes.
;
; WHY THIS EXISTS. The release used to be ONE NativeAOT program that carried FFmpeg, libmpv, its own
; copy and the starter media as a deflate-compressed zip inside its managed resources, unpacked them
; on first run and started them. "An executable whose resources hold compressed executables that it
; writes to disk and runs" is the definition of a dropper, and Defender's machine-learning detections
; (Trojan:Win32/Wacatac and friends) scored it that way on every new release.
;
; An Inno Setup package is a container format every antivirus engine unpacks natively: each file in it
; is scanned and reputation-checked on its own, with its real name, and the Inno engine itself is one
; of the most widely seen installers on Windows. It is still ONE download.
;
; WHAT IT DOES — AND DELIBERATELY DOES NOT DO.
;   * Runs WITHOUT administrator rights (PrivilegesRequired=lowest). It writes nothing to the machine:
;     no program folder, no registry, no uninstaller of its own (Uninstallable=no).
;   * Unpacks the release into Inno's own private temporary folder ({tmp}, removed by Inno when it
;     exits) with its real layout, beside the SIGNED file list (update.json + update.sig.json, DIST-SMART).
;   * Starts the unpacked FreeVideoStudio.exe with --install --smart-update <that folder>, as the user
;     who started Setup. From there the existing transactional installer does everything (SYS-UPGRADE):
;     the broker window, the one Windows permission prompt, the elevated worker, which re-verifies the
;     signed list ITSELF and hashes every byte it copies (InstallPayload.ExtractFromSmartUpdate), the
;     shortcuts, the Apps & features entry, the first-launch health check and the rollback.
;   * Forwards only the arguments the in-app updater and older updaters use (see ForwardedArguments).
; ════════════════════════════════════════════════════════════════════════════════════════════════

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
AppCopyright=Copyright © 2026 Alon Reich. All rights reserved.
; PEIDENTITY_01 — the installer's version resource names its own file and the same publisher as the program.
VersionInfoVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoCompany=Alon Reich
VersionInfoDescription=Free Video Studio Setup
VersionInfoProductName=Free Video Studio
VersionInfoCopyright=Copyright © 2026 Alon Reich. All rights reserved.
VersionInfoOriginalFileName=FreeVideoStudio-Setup.exe
OutputDir={#OutputDir}
OutputBaseFilename=FreeVideoStudio-Setup
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
{ NODROPPER_01 — passes on ONLY the switches the in-app updater (and updaters released before this
  installer existed, which start the full installer with --install --auto-update --source-current
  --wait-pid <id>) use. Everything else is ignored, so a command line can never steer the unpacked
  program into another mode. }
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

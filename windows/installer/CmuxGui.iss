#ifndef AppVersion
  #error AppVersion must be supplied by windows\scripts\installer.ps1
#endif
#ifndef PackageVersion
  #error PackageVersion must be supplied by windows\scripts\installer.ps1
#endif
#ifndef SourceDir
  #error SourceDir must be supplied by windows\scripts\installer.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by windows\scripts\installer.ps1
#endif
#ifndef ShellCertificateThumbprint
  #error ShellCertificateThumbprint must be supplied by windows\scripts\installer.ps1
#endif

[Setup]
AppId={{31F22F12-850E-4F56-82D5-3235233B3ABE}
AppName=cmux for Windows
AppVersion={#AppVersion}
AppVerName=cmux for Windows {#AppVersion}
AppPublisher=sweetcornna
AppPublisherURL=https://github.com/sweetcornna/cmux-for-windows
AppSupportURL=https://github.com/sweetcornna/cmux-for-windows/issues
AppUpdatesURL=https://github.com/sweetcornna/cmux-for-windows/releases
VersionInfoVersion={#PackageVersion}
VersionInfoProductName=cmux for Windows
VersionInfoProductVersion={#AppVersion}
SetupIconFile=..\CmuxGui\Assets\AppIcon.ico
LicenseFile={#SourceDir}\LICENSE
DefaultDirName={localappdata}\Programs\cmux
DefaultGroupName=cmux
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UninstallDisplayName=cmux for Windows
UninstallDisplayIcon={app}\Assets\AppIcon.ico
CloseApplications=yes
RestartApplications=no
OutputDir={#OutputDir}
OutputBaseFilename=cmux-windows-v{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\cmux"; Filename: "{app}\CmuxGui.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\AppIcon.ico"
Name: "{autodesktop}\cmux"; Filename: "{app}\CmuxGui.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\AppIcon.ico"; Tasks: desktopicon

[Run]
Filename: "{sys}\certutil.exe"; Parameters: "-user -addstore TrustedPeople ""{app}\CmuxShellIntegration.cer"""; Flags: runhidden waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\shell-package.ps1"" -Action Install -PackagePath ""{app}\CmuxShellIntegration.msix"" -ExternalLocation ""{app}"""; WorkingDir: "{app}"; Flags: runhidden waituntilterminated
Filename: "{app}\CmuxGui.exe"; Parameters: "--repair-shell"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated
Filename: "{app}\CmuxGui.exe"; Description: "{cm:LaunchProgram,cmux}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\CmuxGui.exe"; Parameters: "--unregister-shell"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterShellIntegration"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\shell-package.ps1"" -Action Uninstall"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveShellIntegrationPackage"
Filename: "{sys}\certutil.exe"; Parameters: "-user -delstore TrustedPeople {#ShellCertificateThumbprint}"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveShellIntegrationCertificate"

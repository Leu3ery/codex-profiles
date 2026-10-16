param([ValidateSet('x64', 'arm64')][string]$Architecture = 'x64', [string]$Version = '2.0.1')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root "dist/windows-$Architecture"
New-Item -ItemType Directory -Force $output | Out-Null
dotnet publish "$root/Windows/CodexProfiles.Windows" -c Release -r "win-$Architecture" --self-contained true -p:Version=$Version -o $output
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }
Move-Item "$output/Codex-Profiles.exe" "$output/Codex-Profiles-windows-$Architecture.exe" -Force
$compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$compilerPath = if ($compiler) { $compiler.Source } else { 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' }
if (-not (Test-Path $compilerPath)) { throw 'Install Inno Setup 6 to build the Windows installer.' }
& $compilerPath "/DAppVersion=$Version" "/DAppArch=$Architecture" "$root/Windows/installer.iss"
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
Compress-Archive -Path "$output/Codex-Profiles-windows-$Architecture.exe", "$root/Windows/README.md" -DestinationPath "$output/Codex-Profiles-windows-$Architecture.zip" -Force
Get-ChildItem $output -File | Where-Object Extension -in '.exe', '.zip' | ForEach-Object {
  "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content "$output/SHA256SUMS-windows-$Architecture.txt" -Encoding ascii

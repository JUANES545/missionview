[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$CompilerPath
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$app = [IO.Path]::GetFullPath($PublishDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Packaging requires a new output directory' }
if ($output.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase) -or $output -eq $app) {
    throw 'Package output must be outside the published application'
}
$info = Get-Content -LiteralPath (Join-Path $app 'build-info.json') -Raw | ConvertFrom-Json
if ($info.version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or
    $info.sourceSha -notmatch '^[a-f0-9]{40}$' -or $info.runtime -ne 'win-x64' -or
    $info.selfContained -ne $true) { throw 'Invalid publish manifest' }
foreach ($file in @('MissionView.exe', 'MissionView.dll', 'MissionView.deps.json',
        'MissionView.runtimeconfig.json', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'coreclr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $file) -PathType Leaf)) { throw "Missing $file" }
}
$version = $info.version
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $app 'MissionView.exe')).FileVersion -ne "$version.0") {
    throw 'EXE version does not match the publish manifest'
}
if (Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object {
    $_.Name -eq 'settings.json' -or $_.Extension -eq '.pdb' -or
    $_.Name -like 'WindowScatter.*' -or $_.Name -match 'diagnostic.*\.json$'
}) { throw 'Publish directory contains local state or debug files' }
if (-not $CompilerPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) { $CompilerPath = $compiler.Source }
    elseif (Test-Path -LiteralPath "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") {
        $CompilerPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    } else { throw 'Inno Setup 6.3+ is required. Install it through an approved process, then rerun.' }
}
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'ISCC.exe was not found' }
New-Item -ItemType Directory -Path $output | Out-Null
& $CompilerPath "/DAppVersion=$version" "/DPublishDir=$app" "/DOutputDir=$output" `
    (Join-Path $root 'installer\MissionView.iss')
if ($LASTEXITCODE) { throw "Inno Setup compilation failed ($LASTEXITCODE)" }
$setup = Join-Path $output "MissionView-$version-Setup.exe"
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Compiler did not produce the expected installer' }
$setupInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($setup)
$setupVersion = @($setupInfo.FileMajorPart, $setupInfo.FileMinorPart,
    $setupInfo.FileBuildPart, $setupInfo.FilePrivatePart) -join '.'
if ($setupVersion -ne "$version.0") {
    throw 'Installer version does not match the app'
}
Compress-Archive -Path (Join-Path $app '*') -DestinationPath (Join-Path $output "MissionView-$version-win-x64.zip")
Copy-Item -LiteralPath (Join-Path $app 'build-info.json') -Destination (Join-Path $output 'release-manifest.json')
$lines = Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
}
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), [string[]]$lines)
& node -e "const m=JSON.parse(require('node:fs').readFileSync(process.argv[3])); require(process.argv[1]).validateAssets(process.argv[2], {...m, sha:m.sourceSha})" `
    (Join-Path $PSScriptRoot 'release.cjs') $output (Join-Path $output 'release-manifest.json')
if ($LASTEXITCODE) { throw 'Release asset verification failed' }
Write-Output "Packaged and checksummed release: $output"

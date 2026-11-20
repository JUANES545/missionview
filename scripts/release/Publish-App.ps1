[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceSha,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (($Version.Split('.') | Where-Object { [long]$_ -gt 65534 }).Count) {
    throw 'Version exceeds Windows file-version limits'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Publish requires a new output directory' }
$project = Join-Path $root 'WindowScatter.csproj'
& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:EnableWindowsTargeting=true -p:EnumerationDiagnostics=false `
    "-p:Version=$Version" "-p:AssemblyVersion=$Version.0" "-p:FileVersion=$Version.0" `
    "-p:InformationalVersion=$Version" -p:IncludeSourceRevisionInInformationalVersion=false `
    -p:PublishTrimmed=false -p:PublishSingleFile=false -o $output --nologo
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
foreach ($file in @('MissionView.exe', 'MissionView.dll', 'MissionView.deps.json',
        'MissionView.runtimeconfig.json', 'coreclr.dll',
        'hostfxr.dll', 'PresentationFramework.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $file) -PathType Leaf)) {
        throw "Self-contained output is missing $file"
    }
}
$exeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $output 'MissionView.exe')).FileVersion
if ($exeVersion -ne "$Version.0") { throw "Unexpected EXE version: $exeVersion" }
if (Get-ChildItem -LiteralPath $output -Filter 'WindowScatter.*' -File) {
    throw 'Publish contains obsolete WindowScatter application binaries'
}
Get-ChildItem -LiteralPath $output -Filter '*.pdb' | Remove-Item
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $output

$assets = Get-Content -LiteralPath (Join-Path $root 'obj\project.assets.json') -Raw | ConvertFrom-Json
$packages = @($assets.libraries.PSObject.Properties | ForEach-Object { $_.Value.path })
$framework = $assets.project.frameworks.PSObject.Properties.Value
$runtimePacks = @($framework.downloadDependencies | Where-Object {
    $_.name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$'
})
if ($runtimePacks.Count -ne 2) { throw 'Expected both .NET and Windows Desktop runtime packs' }
$packages += $runtimePacks | ForEach-Object {
    $_.name.ToLowerInvariant() + '/' + $_.version.Trim('[', ']').Split(',')[0].Trim()
}
$notices = [Collections.Generic.List[string]]::new()
$notices.Add("MissionView includes these NuGet packages and self-contained runtimes.`n")
foreach ($package in ($packages | Sort-Object -Unique)) {
    $directory = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder $package.Replace('/', '\')
        if (Test-Path -LiteralPath $candidate -PathType Container) { $directory = $candidate; break }
    }
    if (-not $directory) { throw "Restored package is missing: $package" }
    $nuspec = @(Get-ChildItem -LiteralPath $directory -Filter '*.nuspec')
    if ($nuspec.Count -ne 1) { throw "Expected one NuGet manifest for $package" }
    [xml]$xml = Get-Content -LiteralPath $nuspec[0].FullName -Raw
    $metadata = $xml.package.metadata
    if (-not $metadata.license) { throw "Missing license metadata: $package" }
    $legal = @(Get-ChildItem -LiteralPath $directory -File | Where-Object {
        $_.Name -match '^(LICENSE(\..*)?|THIRD.PARTY.NOTICES(\..*)?)$'
    })
    if ($metadata.license.type -eq 'expression') {
        if ($metadata.license.InnerText -ne 'MIT') { throw "Review license before packaging: $package" }
    } elseif ($metadata.license.type -ne 'file' -or
        $metadata.license.InnerText -notin $legal.Name) {
        throw "Unresolved license file: $package"
    }
    $notices.Add("$($metadata.id) $($metadata.version)`n$($metadata.copyright)`nLicense: $($metadata.license.InnerText)`n")
    $destination = Join-Path $output ('notices\' + $package.Replace('/', '\'))
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($file in $legal) { Copy-Item -LiteralPath $file.FullName -Destination $destination }
}
$license = Get-Content -LiteralPath (Join-Path $root 'LICENSE') -Raw
$notices.Add("MIT license terms for the MIT packages listed above:`n" +
    $license.Substring($license.IndexOf('Permission is hereby granted')))
[IO.File]::WriteAllText((Join-Path $output 'THIRD-PARTY-NOTICES.txt'), ($notices -join "`n"))
if (Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object {
    $_.Name -eq 'settings.json' -or $_.Name -match 'diagnostic.*\.json$'
}) { throw 'Application state or diagnostics must not ship' }
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
@{
    version = $Version
    sourceSha = $SourceSha
    runtime = 'win-x64'
    selfContained = $true
    targetFramework = [string]$projectXml.Project.PropertyGroup.TargetFramework
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build-info.json') -Encoding utf8
Write-Output "Published self-contained app: $output"

<#
.SYNOPSIS
  Builds the Chinese OFD-enabled "格式转换器" distribution (Setup.exe + Portable.zip) from the fork's
  installer/FormatConverter.iss. A thin variant of build/build-installer.ps1 (kept in sync with it):
  different installer script, output names, and the license/notices/quick-start files added to the
  package so the distribution satisfies the MIT license of the upstream project.

.EXAMPLE
  pwsh build/build-installer-cn.ps1 -Version 1.12.0.1 -Portable
#>
[CmdletBinding()]
param(
    # Defaults to <Version> in Directory.Build.props.
    [string]$Version,
    # A finished publish folder (Filee.exe, FileeExplorerMenu.dll/.msix, engines\); skips publishing.
    [string]$PublishDir,
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\Releases'),
    [switch]$Portable,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cache = Join-Path $PSScriptRoot '.cache'

# Inno Setup 7.1.0, 64-bit edition (same pin as build-installer.ps1).
$innoVersion = '7.1.0'
$innoSha256 = '0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F'
$innoSize = 14304168
$innoUrl = "https://github.com/jrsoftware/issrc/releases/download/is-$($innoVersion.Replace('.', '_'))/innosetup-$($innoVersion)-x64.exe"

function Get-Iscc {
    $folder = Join-Path $cache "innosetup-$innoVersion"
    $iscc = Join-Path $folder 'ISCC.exe'
    if (Test-Path $iscc) { return $iscc }

    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $setup = Join-Path $cache "innosetup-$($innoVersion)-x64.exe"
    if (-not ((Test-Path $setup) -and (Get-FileHash $setup -Algorithm SHA256).Hash -eq $innoSha256)) {
        Write-Host "get  $innoUrl"
        & curl.exe --fail --location --silent --show-error --connect-timeout 20 --retry 3 --max-time 300 --output $setup $innoUrl
        if ($LASTEXITCODE -ne 0) { throw "curl exit code $LASTEXITCODE" }
        $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
        if ($hash -ne $innoSha256) {
            Remove-Item $setup
            throw "Inno Setup checksum mismatch (got $hash, size $((Get-Item $setup).Length), expected $innoSize bytes)"
        }
    }

    Write-Host "unpack Inno Setup $innoVersion (portable)"
    $partial = "$folder.partial"
    Remove-Item $partial -Recurse -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $setup -Wait -PassThru -ArgumentList @(
        '/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS',
        "/DIR=`"$partial`"", "/LOG=`"$cache\innosetup-$innoVersion.log`"")
    if ($process.ExitCode -ne 0 -or -not (Test-Path (Join-Path $partial 'ISCC.exe'))) {
        throw "Inno Setup setup failed with exit code $($process.ExitCode) (see $cache\innosetup-$innoVersion.log)"
    }
    Move-Item $partial $folder
    return $iscc
}

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE" }
}

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

if ($CheckOnly) {
    $PublishDir = Join-Path $cache 'installer-check\app'
    New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
    Set-Content (Join-Path $PublishDir 'Filee.exe') 'stub' -NoNewline
    $OutputDir = Join-Path $cache 'installer-check'
}
elseif (-not $PublishDir) {
    $PublishDir = Join-Path $root 'publish\win-x64'
    Remove-Item $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
    Invoke-Checked 'dotnet publish' {
        dotnet publish (Join-Path $root 'src\Filee.App\Filee.App.csproj') -c Release -r win-x64 --self-contained `
            "-p:Version=$Version" -p:PublishReadyToRun=true -p:FileeRelease=true -o $PublishDir
    }
    Invoke-Checked 'dotnet publish (cli)' {
        dotnet publish (Join-Path $root 'src\Filee.Cli\Filee.Cli.csproj') -c Release -r win-x64 --self-contained `
            "-p:Version=$Version" -p:PublishReadyToRun=true -p:FileeRelease=true -o $PublishDir
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $PublishDir 'cli') | Out-Null
    Set-Content (Join-Path $PublishDir 'cli\filee.cmd') "@`"%~dp0..\filee-cli.exe`" %*" -Encoding ascii
    & (Join-Path $PSScriptRoot 'build-explorer-menu.ps1') -Destination $PublishDir -Version $Version
    & (Join-Path $PSScriptRoot 'fetch-engines.ps1') -Only rhwp, 7zip -Destination (Join-Path $PublishDir 'engines')

    # MIT license of the upstream project travels with the distribution, plus a quick-start note.
    Copy-Item (Join-Path $root 'LICENSE') $PublishDir -Force
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $PublishDir -Force
    Copy-Item (Join-Path $PSScriptRoot 'quick-start-cn.txt') $PublishDir -Force
    # The distribution's trial window: edit installer/distribution.json before packaging to change it.
    Copy-Item (Join-Path $root 'installer\distribution.json') $PublishDir -Force

    # Debug symbols (~100 MB of .pdb, of which 100 MB is libSkia/libHarfBuzz) are useless to users.
    Remove-Item (Join-Path $PublishDir '*.pdb') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $PublishDir 'cli\*.pdb') -Force -ErrorAction SilentlyContinue
}
$PublishDir = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $PublishDir 'Filee.exe'))) { throw "Filee.exe is missing in $PublishDir" }

$iscc = Get-Iscc
Push-Location $root
try {
    Invoke-Checked 'make-installer-engines' { dotnet run build/tools/make-installer-engines.cs -- installer/obj/engines.iss }
}
finally {
    Pop-Location
}

Write-Host "compile installer $Version"
$options = @('/Q', "/DAppVersion=$Version", "/DPublishDir=$PublishDir", "/O$OutputDir")
if ($CheckOnly) { $options += '--no-compression' }
Invoke-Checked 'ISCC' { & $iscc @options (Join-Path $root 'installer\FormatConverter.iss') }
$setupExe = Join-Path $OutputDir "FormatConverter-$Version-win-Setup.exe"
if (-not (Test-Path $setupExe)) { throw "ISCC did not write $setupExe" }
if ($CheckOnly) {
    Write-Host "installer/FormatConverter.iss compiles"
    return
}

$files = @($setupExe)
if ($Portable) {
    $zip = Join-Path $OutputDir "FormatConverter-$Version-win-Portable.zip"
    Remove-Item $zip -ErrorAction SilentlyContinue
    Write-Host "zip  $zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($PublishDir, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $files += $zip
}

$sums = Join-Path $OutputDir "FormatConverter-$Version-SHA256SUMS.txt"
$files | ForEach-Object { "$((Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $_ -Leaf)" } |
    Set-Content $sums -Encoding ascii
Get-Item ($files + $sums) | Format-Table Name, Length

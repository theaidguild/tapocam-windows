#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args[0]) failed (exit code $LASTEXITCODE)."
    }
}

function Assert-Sha256([string] $Path, [string] $Expected) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ne $Expected) {
        throw "SHA-256 mismatch for ${Path}: expected $Expected, got $actual."
    }
}

function Get-VerifiedArchive([string] $Url, [string] $Path, [string] $Sha256) {
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $Url -OutFile $Path
    Assert-Sha256 $Path $Sha256
}

function Copy-ZipEntry([string] $Archive, [string] $Entry, [string] $Destination) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $file = $zip.GetEntry($Entry)
        if ($null -eq $file -or $file.Length -eq 0) {
            throw "Expected file '$Entry' is missing or empty in '$Archive'."
        }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($file, $Destination)
    } finally {
        $zip.Dispose()
    }
}

function Copy-PackageNotices([string] $AssetsFile, [string] $DepsFile, [string] $Destination) {
    $assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json -AsHashtable
    $deps = Get-Content -LiteralPath $DepsFile -Raw | ConvertFrom-Json -AsHashtable
    foreach ($library in $deps.libraries.GetEnumerator()) {
        if ($library.Value.type -eq 'package') {
            $packagePath = $library.Value.path
        } elseif ($library.Value.type -eq 'runtimepack') {
            $packagePath = $library.Key.Substring('runtimepack.'.Length).ToLowerInvariant()
        } else {
            continue
        }
        foreach ($root in $assets.packageFolders.Keys) {
            $package = Join-Path $root $packagePath
            if (-not (Test-Path -LiteralPath $package -PathType Container)) { continue }
            $files = @(Get-ChildItem -LiteralPath $package -File | Where-Object {
                $_.Name -match '^(LICENSE|COPYING|NOTICE|THIRD[-_. ]?PARTY[-_. ]?NOTICES)(\..+)?$'
            })
            if ($files.Count -gt 0) {
                $folder = Join-Path $Destination $packagePath
                New-Item -Path $folder -ItemType Directory -Force | Out-Null
                Copy-Item -LiteralPath $files.FullName -Destination $folder
            }
            break
        }
    }
}

function Find-InnoSetupCompiler {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($root in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if (-not $root) { continue }
        $candidate = Join-Path $root 'Inno Setup 6/ISCC.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup'
}

$project = Join-Path $PSScriptRoot 'src/TapoCam.App/TapoCam.App.csproj'
$stage = Join-Path $PSScriptRoot "obj/package-$([guid]::NewGuid().ToString('N'))"
Push-Location -LiteralPath $PSScriptRoot
try {
    if (-not $Version) {
        $Version = (Invoke-Dotnet msbuild $project '-nologo' '-getProperty:Version' '-p:Configuration=Release' '-p:RuntimeIdentifier=win-x64').Trim()
    }
    if ($Version -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?\z') {
        throw "Invalid package version '$Version'. Use X.Y.Z or X.Y.Z-prerelease (without 'v')."
    }
    $iscc = Find-InnoSetupCompiler

    Write-Host '==> Running tests'
    Invoke-Dotnet test (Join-Path $PSScriptRoot 'TapoCam.sln') --configuration Release
    Write-Host '==> Publishing the app'
    $publish = Join-Path $stage 'publish'
    Invoke-Dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
        '-p:PublishSingleFile=false' "-p:Version=$Version" --output $publish
    if (-not (Test-Path -LiteralPath (Join-Path $publish 'TapoCam.App.exe') -PathType Leaf)) {
        throw 'Publish did not produce TapoCam.App.exe.'
    }

    $tools = Join-Path $publish 'tools'
    $licenses = Join-Path $publish 'licenses'
    New-Item -Path $tools, $licenses -ItemType Directory -Force | Out-Null
    $relay = Join-Path $stage 'go2rtc.zip'

    Write-Host '==> Downloading go2rtc'
    # GitHub release asset digest, independently checked against the downloaded ZIP.
    Get-VerifiedArchive 'https://github.com/AlexxIT/go2rtc/releases/download/v1.9.15/go2rtc_win64.zip' $relay `
        '5b448fc90f15c925406d8da0984301985e385050222c1c2bbdf0a76fe4b1e9cb'
    Copy-ZipEntry $relay 'go2rtc.exe' (Join-Path $tools 'go2rtc.exe')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $licenses
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'packaging/licenses') -File | Copy-Item -Destination $licenses
    $assetsFile = (Invoke-Dotnet msbuild $project '-nologo' '-getProperty:ProjectAssetsFile' `
        '-p:Configuration=Release' '-p:RuntimeIdentifier=win-x64' '-p:SelfContained=true').Trim()
    Copy-PackageNotices $assetsFile (Join-Path $publish 'TapoCam.App.deps.json') (Join-Path $licenses 'packages')

    $dist = Join-Path $PSScriptRoot 'dist'
    $zipName = "TapoCam-$Version-win-x64.zip"
    $stagedZip = Join-Path $stage $zipName
    Write-Host '==> Creating portable ZIP'
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $stagedZip)
    New-Item -Path $dist -ItemType Directory -Force | Out-Null
    $destination = Join-Path $dist $zipName
    Move-Item -LiteralPath $stagedZip -Destination $destination -Force
    Write-Host "Created $destination"

    Write-Host '==> Compiling installer'
    $installerOutput = Join-Path $stage 'installer'
    & $iscc '/Q' "/DAppVersion=$Version" "/DNumericVersion=$($Version.Split('-')[0])" `
        "/DPublishDir=$publish" "/DOutputDir=$installerOutput" (Join-Path $PSScriptRoot 'packaging/installer/TapoCam.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit code $LASTEXITCODE)." }
    $setupName = "TapoCam-$Version-win-x64-setup.exe"
    $setup = Join-Path $dist $setupName
    Move-Item -LiteralPath (Join-Path $installerOutput $setupName) -Destination $setup -Force
    Write-Host "Created $setup"
} finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
    Pop-Location
}

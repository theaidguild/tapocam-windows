#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Version,
    [string] $FfmpegSourceArchive,
    [string] $FfmpegSourceSha256
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

if ($FfmpegSourceArchive -or $FfmpegSourceSha256) {
    if (-not $FfmpegSourceArchive -or $FfmpegSourceSha256 -notmatch '\A[0-9a-f]{64}\z') {
        throw 'Supply both -FfmpegSourceArchive (ZIP) and -FfmpegSourceSha256 (64 hex digits).'
    }
    $FfmpegSourceArchive = (Get-Item -LiteralPath $FfmpegSourceArchive).FullName
    if ([IO.Path]::GetExtension($FfmpegSourceArchive) -ne '.zip') {
        throw 'The FFmpeg corresponding-source archive must be a ZIP.'
    }
    Assert-Sha256 $FfmpegSourceArchive $FfmpegSourceSha256
    $sourceZip = [IO.Compression.ZipFile]::OpenRead($FfmpegSourceArchive)
    try {
        if ($sourceZip.Entries.Count -eq 0) { throw 'The FFmpeg corresponding-source ZIP is empty.' }
    } finally {
        $sourceZip.Dispose()
    }
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

    Invoke-Dotnet test (Join-Path $PSScriptRoot 'TapoCam.sln') --configuration Release
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
    $ffmpeg = Join-Path $stage 'ffmpeg.zip'
    $ffmpegDir = 'ffmpeg-9.0.2-essentials_build'

    # GitHub release asset digests, independently checked against the downloaded ZIPs.
    Get-VerifiedArchive 'https://github.com/AlexxIT/go2rtc/releases/download/v1.9.15/go2rtc_win64.zip' $relay `
        '5b448fc90f15c925406d8da0984301985e385050222c1c2bbdf0a76fe4b1e9cb'
    Get-VerifiedArchive "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/$ffmpegDir.zip" $ffmpeg `
        '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
    Copy-ZipEntry $relay 'go2rtc.exe' (Join-Path $tools 'go2rtc.exe')
    Copy-ZipEntry $ffmpeg "$ffmpegDir/bin/ffmpeg.exe" (Join-Path $tools 'ffmpeg.exe')
    Copy-ZipEntry $ffmpeg "$ffmpegDir/LICENSE" (Join-Path $licenses 'FFmpeg-GPL-3.0.txt')
    Copy-ZipEntry $ffmpeg "$ffmpegDir/README.txt" (Join-Path $licenses 'FFmpeg-upstream-README.txt')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $licenses
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'packaging/licenses') -File | Copy-Item -Destination $licenses
    $assetsFile = (Invoke-Dotnet msbuild $project '-nologo' '-getProperty:ProjectAssetsFile' `
        '-p:Configuration=Release' '-p:RuntimeIdentifier=win-x64' '-p:SelfContained=true').Trim()
    Copy-PackageNotices $assetsFile (Join-Path $publish 'TapoCam.App.deps.json') (Join-Path $licenses 'packages')

    $dist = Join-Path $PSScriptRoot 'dist'
    $zipName = "TapoCam-$Version-win-x64.zip"
    $stagedZip = Join-Path $stage $zipName
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $stagedZip)
    New-Item -Path $dist -ItemType Directory -Force | Out-Null
    if ($FfmpegSourceArchive) {
        Copy-Item -LiteralPath $FfmpegSourceArchive -Destination (Join-Path $dist 'ffmpeg-9.0.2-essentials-corresponding-source.zip')
    } else {
        Write-Warning 'Private-use build only: do not redistribute without the matching FFmpeg Corresponding Source.'
    }
    $destination = Join-Path $dist $zipName
    Move-Item -LiteralPath $stagedZip -Destination $destination -Force
    Write-Host "Created $destination"
} finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
    Pop-Location
}

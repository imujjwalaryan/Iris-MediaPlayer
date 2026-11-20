<#
.SYNOPSIS
    Builds the Iris installer (IrisSetup) and update packages with Velopack.

.DESCRIPTION
    1. Publishes Iris in Release as a self-contained desktop app (its own .NET and Windows App SDK
       runtimes, so the target PC needs nothing else).
    2. Packs it with Velopack (the vpk tool, restored from dotnet-tools.json): a one-click setup
       with the Iris splash, a portable zip, and the packages installed copies update from.

    Everything lands in artifacts\releases. Upload all of it to the Iris-1 folder of the R2 bucket
    behind ryujininteractive.com (or pass -Publish to do that for you) and installed copies of Iris
    offer the update on their next start.

    The version comes from <Version> in Iris-MediaPlayer\Iris-MediaPlayer.csproj; raise it for
    every release.

.PARAMETER Platform
    x64 (default) or ARM64. Each platform has its own update channel.

.PARAMETER Publish
    Also upload the release to the Cloudflare R2 bucket. Needs an R2 API token (Access Key ID and
    Secret Access Key, with Object Read & Write on the bucket), the account ID and the bucket name,
    from environment variables, r2.local.json next to this script, or typed in when asked.
    See README for the details.

.PARAMETER UploadOnly
    Skip building and upload what is already in artifacts\releases (for a release you built
    earlier). Implies -Publish.

.PARAMETER FromGitHub
    Upload a release that GitHub Actions built (see .github/workflows/release.yml): downloads the
    files of that GitHub release (a tag such as v1.3.1, drafts included) into
    artifacts\github\<tag> with the GitHub CLI, then uploads them to R2 from this PC.
    Implies -UploadOnly. The R2 keys never leave this PC.

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -Platform ARM64
    .\build-installer.ps1 -Publish
    .\build-installer.ps1 -UploadOnly
    .\build-installer.ps1 -FromGitHub v1.3.1
#>
param(
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',
    [switch] $Publish,
    [switch] $UploadOnly,
    [string] $FromGitHub,
    # Don't fetch the published release first: no delta update, but the current version can be
    # packed again (test builds). Never upload a build made this way.
    [switch] $SkipPrevious
)
if ($SkipPrevious -and $Publish) { throw '-SkipPrevious builds are for testing; they can''t be published.' }
if ($FromGitHub) { $UploadOnly = $true }
if ($UploadOnly) { $Publish = $true }

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

$appProject = Join-Path $root 'Iris-MediaPlayer\Iris-MediaPlayer.csproj'
# Installed copies of Iris check this folder for updates (see Services/Installation.cs).
$updatesUrl = 'https://www.ryujininteractive.com/Iris-1/'
$r2Prefix = 'Iris-1'
$runtime = "win-$($Platform.ToLowerInvariant())"
$channel = if ($Platform -eq 'x64') { 'win' } else { $runtime }
$publishDir = Join-Path $root "artifacts\publish\$runtime\"
$releasesDir = Join-Path $root 'artifacts\releases'
if ($FromGitHub) { $releasesDir = Join-Path $root "artifacts\github\$FromGitHub" }

if (-not $UploadOnly) {
    # MSBuild from Visual Studio builds the WinUI app (the dotnet CLI can't compile its XAML).
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuild = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'MSBuild not found. Install Visual Studio with the WinUI application development workload.' }
}

[xml] $csproj = Get-Content $appProject
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'No <Version> found in Iris-MediaPlayer.csproj.' }

if ($FromGitHub) {
    # A release GitHub Actions built: fetch its files (drafts too) with the GitHub CLI.
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI (gh) is needed for -FromGitHub: https://cli.github.com' }
    $version = $FromGitHub.TrimStart('v')
    if (Test-Path $releasesDir) { Remove-Item $releasesDir -Recurse -Force }
    New-Item -ItemType Directory -Force $releasesDir | Out-Null
    Write-Host "Downloading GitHub release $FromGitHub..." -ForegroundColor Cyan
    gh release download $FromGitHub --dir $releasesDir --clobber
    if ($LASTEXITCODE -ne 0) { throw "Couldn't download GitHub release $FromGitHub." }
    dotnet tool restore | Out-Null
}

if (-not $UploadOnly) {
# ---------- 1. Publish ----------
Write-Host "Publishing Iris $version for $Platform..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
# Not trimmed: trimming removes types WinUI only reaches at runtime, and the trimmed app crashes
# on start. ReadyToRun stays on (from the project) for fast startup.
& $msbuild $appProject -restore -t:Publish -v:minimal -nologo `
    -p:Configuration=Release -p:Platform=$Platform -p:RuntimeIdentifier=$runtime `
    -p:PublishTrimmed=false "-p:PublishDir=$publishDir"
if ($LASTEXITCODE -ne 0) { throw 'Publishing Iris failed.' }

# Unpackaged WinUI publishing leaves out the compiled XAML (.xbf) files; copy them from the build output.
$buildDir = Join-Path $root "Iris-MediaPlayer\bin\$Platform\Release\net8.0-windows10.0.19041.0\$runtime"
Get-ChildItem $buildDir -Recurse -Filter *.xbf |
    Where-Object { $_.FullName -notmatch '\\(AppX|publish)\\' } |
    ForEach-Object {
        $target = Join-Path $publishDir $_.FullName.Substring($buildDir.Length + 1)
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item $_.FullName $target -Force
    }
if (-not (Get-ChildItem $publishDir -Recurse -Filter MainWindow.xbf)) { throw 'Compiled XAML missing from the published app.' }

# ---------- 2. Pack with Velopack ----------
dotnet tool restore | Out-Null

# Fetch the latest published release so Velopack can make a small delta update from it.
# (Nothing to fetch before the first release; that's fine.)
New-Item -ItemType Directory -Force $releasesDir | Out-Null
if (-not $SkipPrevious) {
    dotnet vpk download http --url $updatesUrl --channel $channel --outputDir $releasesDir --allowEmptyChannel 2>&1 | Out-Null
}

Write-Host "Packing IrisSetup..." -ForegroundColor Cyan
# packId decides the install folder (%LocalAppData%\IrisMediaPlayer); it must stay the same forever,
# and must not be "Iris", which is where Iris keeps the library data.
dotnet vpk pack `
    --packId IrisMediaPlayer --packVersion $version --packTitle Iris --packAuthors 'Ryujin Interactive' `
    --packDir $publishDir --mainExe Iris-MediaPlayer.exe --runtime $runtime --channel $channel `
    --icon (Join-Path $root 'Iris-MediaPlayer\Assets\Iris.ico') `
    --splashImage (Join-Path $root 'installer\Splash.png') --splashProgressColor '#8B93FF' `
    --exclude '\.pdb$' --outputDir $releasesDir
if ($LASTEXITCODE -ne 0) { throw 'Packing with Velopack failed.' }

}

$setup = Get-ChildItem $releasesDir -Filter "*$channel-Setup.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $setup -or -not (Test-Path (Join-Path $releasesDir "releases.$channel.json"))) {
    throw "Nothing to upload in $releasesDir. Build first (without -UploadOnly)."
}
# A copy with the version in its name, for linking to a specific release.
if (-not $FromGitHub) {
    $versionedSetup = Join-Path $releasesDir ($setup.BaseName + "-v$version.exe")
    Copy-Item $setup.FullName $versionedSetup -Force
}
Write-Host "Done: $($setup.FullName)" -ForegroundColor Green

# ---------- 3. Optional: upload to Cloudflare R2 ----------
if ($Publish) {
    # Settings come from environment variables, then r2.local.json (never committed), then a prompt.
    $localFile = Join-Path $root 'r2.local.json'
    $local = if (Test-Path $localFile) { Get-Content $localFile -Raw | ConvertFrom-Json } else { $null }
    function Get-R2Setting([string] $envName, [string] $jsonName, [string] $prompt, [switch] $Secret) {
        $value = [Environment]::GetEnvironmentVariable($envName)
        if (-not $value -and $local) { $value = $local.$jsonName }
        if (-not $value) {
            if ($Secret) {
                $secure = Read-Host $prompt -AsSecureString
                $value = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
            } else {
                $value = Read-Host $prompt
            }
        }
        if (-not $value) { throw "$prompt is needed to publish." }
        return $value
    }

    $accountId = Get-R2Setting 'IRIS_R2_ACCOUNT_ID' 'accountId' 'Cloudflare account ID'
    # Just the name: "iris-downloads", not "iris-downloads/" or a path.
    $bucket = (Get-R2Setting 'IRIS_R2_BUCKET' 'bucket' 'R2 bucket name').Trim().Trim('/')
    $keyId = Get-R2Setting 'IRIS_R2_ACCESS_KEY_ID' 'accessKeyId' 'R2 Access Key ID'
    $secret = Get-R2Setting 'IRIS_R2_SECRET_ACCESS_KEY' 'secretAccessKey' 'R2 Secret Access Key' -Secret

    Write-Host "Uploading Iris $version to R2 ($bucket/$r2Prefix)..." -ForegroundColor Cyan
    dotnet vpk upload s3 --outputDir $releasesDir --channel $channel `
        --endpoint "https://$accountId.r2.cloudflarestorage.com" --region auto `
        --bucket $bucket --prefix $r2Prefix --keyId $keyId --secret $secret `
        --disableChecksumValidation
    if ($LASTEXITCODE -ne 0) { throw 'Uploading to R2 failed.' }

    Write-Host "Published. Download link: $($updatesUrl)$($setup.Name)" -ForegroundColor Green
}

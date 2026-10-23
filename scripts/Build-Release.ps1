param(
    [string]$Version = '0.3.1',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$') { throw 'Invalid version' }
$repo = Split-Path $PSScriptRoot -Parent
$releaseRoot = Join-Path $repo 'artifacts'
New-Item $releaseRoot -ItemType Directory -Force | Out-Null
$work = Join-Path $releaseRoot ("build-$Version-" + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $work 'source'
$packageName = "Qiban-$Version-Windows-x64"
$package = Join-Path $work $packageName
New-Item $source -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $repo 'CHANGELOG.md') $source
$projectSource = Join-Path $source '一键启动器WinUI3'
New-Item $projectSource -ItemType Directory | Out-Null
# Copy reviewed source types only; never reuse a user's live publish directory.
Get-ChildItem (Join-Path $repo '一键启动器WinUI3') -File | Where-Object {
    $_.Extension -in '.cs', '.xaml', '.csproj', '.manifest'
} | Copy-Item -Destination $projectSource
$assets = Join-Path $projectSource 'Assets'
New-Item $assets -ItemType Directory | Out-Null
foreach ($name in 'app.ico','app.png','klee-logo-v1.png','klee-v1.ico') {
    Copy-Item (Join-Path $repo "一键启动器WinUI3/Assets/$name") $assets
}
$project = Join-Path $projectSource 'QuickLauncherWinUI3.csproj'
$numericVersion = ($Version -split '-')[0]
& $Dotnet restore $project -r win-x64 -p:Platform=x64 -p:SelfContained=true -p:NuGetAudit=false --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
& $Dotnet publish $project -c Release -r win-x64 -p:Platform=x64 --self-contained true --no-restore `
    -p:Version=$Version -p:AssemblyVersion="$numericVersion.0" -p:FileVersion="$numericVersion.0" `
    -p:DebugType=None -p:DebugSymbols=false -p:PathMap="$source=/_/src" -o $package
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
foreach ($required in 'QuickLauncherWinUI3.exe','QuickLauncherWinUI3.dll','coreclr.dll','hostfxr.dll','Microsoft.ui.xaml.dll','resources.pri','Assets/klee-logo-v1.png') {
    if (-not (Test-Path (Join-Path $package $required))) { throw "Missing runtime file: $required" }
}
$runtime = Get-Content (Join-Path $package 'QuickLauncherWinUI3.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks) { throw 'Framework-dependent output' }
$forbidden = Get-ChildItem $package -Recurse -Force | Where-Object {
    $_.Name -match '^(launcher_config.*|icon_cache.*|logs|Backgrounds)$' -or $_.Extension -in '.pdb','.user','.pfx','.key'
}
if ($forbidden) { throw 'Unexpected personal or debug data in release' }
Copy-Item (Join-Path $repo 'docs/使用说明.txt') $package
Copy-Item (Join-Path $repo 'docs/已知限制.md') $package
Copy-Item (Join-Path $repo 'docs/第三方说明.md') $package
$licenses = Join-Path $package 'ThirdPartyNotices'
New-Item $licenses -ItemType Directory | Out-Null
# Preserve notices shipped with the exact restored packages.
$assetData = Get-Content (Join-Path $projectSource 'obj/project.assets.json') -Raw | ConvertFrom-Json
foreach ($library in $assetData.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    foreach ($folder in $assetData.packageFolders.PSObject.Properties.Name) {
        $location = Join-Path $folder $library.Value.path
        if (-not (Test-Path $location)) { continue }
        $noticeFiles = Get-ChildItem $location -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD.PARTY)' }
        $target = Join-Path $licenses ($library.Name -replace '/', '-')
        New-Item $target -ItemType Directory -Force | Out-Null
        Get-ChildItem $location -Filter '*.nuspec' -File | Copy-Item -Destination $target
        foreach ($notice in $noticeFiles) {
            $target = Join-Path $licenses ($library.Name -replace '/', '-')
            New-Item $target -ItemType Directory -Force | Out-Null
            Copy-Item $notice.FullName $target
        }
        break
    }
}
foreach ($folder in $assetData.packageFolders.PSObject.Properties.Name) {
    $runtimePack = Join-Path $folder ("microsoft.netcore.app.runtime.win-x64/" + $runtime.runtimeOptions.includedFrameworks[0].version)
    if (Test-Path $runtimePack) {
        $target = Join-Path $licenses 'Microsoft.NETCore.App.Runtime.win-x64'
        New-Item $target -ItemType Directory -Force | Out-Null
        Get-ChildItem $runtimePack -File | Where-Object { $_.Name -match '^(LICENSE|THIRD.PARTY)' } | Copy-Item -Destination $target
        break
    }
}
Get-ChildItem $package -File -Recurse | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\','/')
    "$( (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() )  $relative"
} | Set-Content (Join-Path $package '文件校验-SHA256.txt') -Encoding utf8
$zip = Join-Path $releaseRoot "$packageName.zip"
if (Test-Path $zip) { throw "Archive already exists: $zip" }
Compress-Archive -LiteralPath $package -DestinationPath $zip -CompressionLevel Optimal
"$((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant())  $packageName.zip" |
    Set-Content "$zip.sha256" -Encoding utf8
Write-Output "PACKAGE=$package"
Write-Output "ARCHIVE=$zip"

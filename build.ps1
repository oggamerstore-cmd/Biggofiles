param([switch]$Test, [switch]$Package, [switch]$GitHub)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$githubOutput = Join-Path $projectRoot 'github'
if ($GitHub -and (Test-Path -LiteralPath $githubOutput)) { throw 'The github folder already exists. Preserve it or move it aside before exporting a fresh copy.' }
$assemblyText = [IO.File]::ReadAllText((Join-Path $projectRoot 'src\AssemblyInfo.cs'))
$versionMatch = [regex]::Match($assemblyText, 'AssemblyInformationalVersion\("(v[0-9]+\.[0-9]+)"\)')
if (-not $versionMatch.Success) { throw 'Expected a two-part release version in AssemblyInfo.cs.' }
$releaseVersion = $versionMatch.Groups[1].Value
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Install .NET Framework 4.8 on 64-bit Windows before building.' }
$output = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
& (Join-Path $projectRoot 'tools\make-icon.ps1')
$references = @('System.dll','System.Core.dll','System.Xaml.dll','WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $framework 'WPF') $_) }
# WindowsBase may be installed in the WPF directory rather than alongside csc.
$references = $references | ForEach-Object { if ($_ -like '*WindowsBase.dll' -and -not (Test-Path -LiteralPath $_.Substring(11))) { '/reference:' + (Join-Path $framework 'WPF\WindowsBase.dll') } else { $_ } }
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName
$common = @('/nologo','/optimize+','/platform:x64','/utf8output','/warn:4','/warnaserror+')
$common += '/win32manifest:' + (Join-Path $projectRoot 'app.manifest')
$common += '/win32icon:' + (Join-Path $projectRoot 'assets\App.ico')
$common += '/resource:' + (Join-Path $projectRoot 'src\MainWindow.xaml') + ',Biggofiles.MainWindow.xaml'
$common += '/resource:' + (Join-Path $projectRoot 'assets\App.ico') + ',Biggofiles.App.ico'
& $compiler @common @references '/target:winexe' ('/out:' + (Join-Path $output 'Biggofiles.exe')) @sources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'Biggofiles.exe.config') -Destination $output
if ($Test) {
    $testOutput = Join-Path $projectRoot 'tests\bin'
    New-Item -ItemType Directory -Force -Path $testOutput | Out-Null
    $testSources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' | ForEach-Object FullName
    & $compiler @common @references '/target:exe' '/main:Biggofiles.Tests' ('/out:' + (Join-Path $testOutput 'Biggofiles.Tests.exe')) @sources @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Biggofiles.exe.config') -Destination (Join-Path $testOutput 'Biggofiles.Tests.exe.config')
    & (Join-Path $testOutput 'Biggofiles.Tests.exe') $projectRoot
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
if ($Package -or $GitHub) {
    foreach ($name in @('QUICK_START.txt','LICENSE','PRIVACY.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $output }
    $release = Join-Path $projectRoot 'release'
    New-Item -ItemType Directory -Force -Path $release | Out-Null
    $archive = Join-Path $release ('Biggofiles-' + $releaseVersion + '-win-x64.zip')
    $portableFiles = @('Biggofiles.exe','Biggofiles.exe.config','QUICK_START.txt','LICENSE','PRIVACY.md') | ForEach-Object { Join-Path $output $_ }
    Compress-Archive -LiteralPath $portableFiles -DestinationPath $archive -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sourceArchive = Join-Path $release ('Biggofiles-' + $releaseVersion + '-source.zip')
    # Overwrite only this generated archive; do not stage or recursively copy build output.
    $sourceStream = [System.IO.File]::Create($sourceArchive)
    $sourceZip = [System.IO.Compression.ZipArchive]::new($sourceStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $sourcePaths = @('.gitignore','.gitattributes','app.manifest','Biggofiles.exe.config','build.ps1','README.md','CONTRIBUTING.md','CHANGELOG.md','LICENSE','PRIVACY.md','QUICK_START.txt') | ForEach-Object { Get-Item -LiteralPath (Join-Path $projectRoot $_) -Force }
        foreach ($folder in @('src','assets','tools','docs','.github')) { $sourcePaths += Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -File -Recurse -Force }
        $sourcePaths += Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' -File
        foreach ($sourceFile in $sourcePaths) {
            if ($sourceFile.Name -in @('minimum-window.png','about.png','size-explanation.png')) { continue }
            $entryName = $sourceFile.FullName.Substring($projectRoot.Length + 1).Replace('\','/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($sourceZip,$sourceFile.FullName,$entryName,[System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $sourceZip.Dispose(); $sourceStream.Dispose() }
    @($archive,$sourceArchive) | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [System.IO.Path]::GetFileName($_) } | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
    if ($GitHub) {
        New-Item -ItemType Directory -Path $githubOutput | Out-Null
        foreach ($sourceFile in $sourcePaths) {
            if ($sourceFile.Name -in @('minimum-window.png','about.png','size-explanation.png')) { continue }
            $relativeName = $sourceFile.FullName.Substring($projectRoot.Length + 1)
            $destination = Join-Path $githubOutput $relativeName
            New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
            Copy-Item -LiteralPath $sourceFile.FullName -Destination $destination
        }
        $downloadOutput = Join-Path $githubOutput 'downloads'
        New-Item -ItemType Directory -Path $downloadOutput | Out-Null
        Copy-Item -LiteralPath $archive -Destination $downloadOutput
        ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($archive)) | Set-Content -LiteralPath (Join-Path $downloadOutput 'SHA256SUMS.txt') -Encoding ascii
    }
}
Get-Item -LiteralPath (Join-Path $output 'Biggofiles.exe') | Select-Object FullName,Length


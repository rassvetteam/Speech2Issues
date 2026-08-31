# Build Speech2Issues into a self-contained single-file exe for Windows x64.
# Result: publish\Speech2Issues.exe (runs on any Windows 10/11, no .NET runtime needed).

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sln = Join-Path $root 'Speech2Issues.sln'
$app = Join-Path $root 'src\Speech2Issues.App\Speech2Issues.App.csproj'
$out = Join-Path $root 'publish'

Write-Host '==> restore' -ForegroundColor Cyan
dotnet restore $sln
if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

Write-Host '==> test (Release)' -ForegroundColor Cyan
dotnet test $sln -c Release --no-restore --filter 'Category!=Live'
if ($LASTEXITCODE -ne 0) { throw 'tests failed' }

Write-Host '==> publish single-file self-contained' -ForegroundColor Cyan
dotnet publish $app -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$exe = Join-Path $out 'Speech2Issues.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'published exe is missing' }
$exeSize = (Get-Item -LiteralPath $exe).Length
$limit = 100MB
if ($exeSize -gt $limit) {
    throw "published exe is too large: $([math]::Round($exeSize / 1MB, 2)) MB (limit: 100 MB)"
}
if (Test-Path -LiteralPath (Join-Path $out 'runtimes')) {
    throw 'Whisper runtimes must not be bundled in publish output'
}
Get-ChildItem -LiteralPath $out -Filter '*.pdb' -File | Remove-Item -Force

Write-Host ''
Write-Host "Done: $exe ($([math]::Round($exeSize / 1MB, 2)) MB)" -ForegroundColor Green


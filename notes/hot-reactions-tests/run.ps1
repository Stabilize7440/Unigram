param([switch]$Benchmark)
$ErrorActionPreference = 'Stop'
$msbuild = 'F:\Dev\Microsoft Visual Studio\community\MSBuild\Current\Bin\amd64\MSBuild.exe'
& $msbuild (Join-Path $PSScriptRoot 'HotReactions.Tests.csproj') /restore /p:Configuration=Release /m /verbosity:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$program = Join-Path $PSScriptRoot 'bin\Release\net10.0\HotReactions.Tests.dll'
if ($Benchmark) { & dotnet $program --benchmark } else { & dotnet $program }
exit $LASTEXITCODE

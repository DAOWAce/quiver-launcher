# Build using Roslyn's managed compiler through dotnet.exe.
# The installed SDK remains untouched; only a temporary compiler copy is changed.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$compilerCopy = $null
Push-Location $repoRoot
try {
    $dotnet = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
    $sdkVersion = (& $dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') {
        throw "The selected SDK must be .NET 10. Selected: $sdkVersion"
    }
    $sdkList = @(& $dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0) { throw 'Could not list installed SDKs.' }
    $sdkRoot = $null
    foreach ($line in $sdkList) {
        if ($line -match '^([^\s]+)\s+\[(.+)\]$' -and $Matches[1] -eq $sdkVersion) {
            $sdkRoot = Join-Path $Matches[2] $sdkVersion
            break
        }
    }
    if (-not $sdkRoot) { throw "Cannot locate SDK $sdkVersion." }
    $source = Join-Path $sdkRoot 'Roslyn'
    $compilerCopy = Join-Path ([IO.Path]::GetTempPath()) ('Quiver-Roslyn-' + [Guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $source -Destination $compilerCopy -Recurse
    foreach ($name in @('csc.exe', 'vbc.exe', 'VBCSCompiler.exe')) {
        $apphost = Join-Path $compilerCopy "bincore\$name"
        if (Test-Path -LiteralPath $apphost) {
            Remove-Item -LiteralPath $apphost
        }
    }
    $taskAssembly = Join-Path $compilerCopy 'Microsoft.Build.Tasks.CodeAnalysis.dll'
    if (-not (Test-Path -LiteralPath $taskAssembly)) { throw 'Compiler task assembly is missing.' }
    if (-not (Test-Path -LiteralPath (Join-Path $compilerCopy 'bincore\csc.dll'))) {
        throw 'Managed C# compiler is missing.'
    }
    Write-Host "Using SDK $sdkVersion with the managed compiler (no compiler apphost)."
    & $dotnet publish 'QuiverLauncher.Desktop/QuiverLauncher.Desktop.csproj' -c Release -r win-x64 --self-contained true '-p:PublishTrimmed=false' '-p:CETCompat=false' '-p:UseSharedCompilation=false' "-p:RoslynTargetsPath=$compilerCopy" "-p:RoslynTasksAssembly=$taskAssembly" '-nr:false' -o '.\artifacts\win10-2004'
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
    Write-Host "Published to $repoRoot\artifacts\win10-2004"
}
finally {
    Pop-Location
    if ($compilerCopy -and (Test-Path -LiteralPath $compilerCopy)) {
        Remove-Item -LiteralPath $compilerCopy -Recurse -Force -ErrorAction SilentlyContinue
    }
}

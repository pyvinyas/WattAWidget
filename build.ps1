# Builds WattWidget.exe using the built-in .NET Framework C# compiler (no SDK needed).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pkgDir = Join-Path $root 'packages'
$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $pkgDir | Out-Null
New-Item -ItemType Directory -Force $bin | Out-Null

function Get-Pkg([string]$id, [string]$ver) {
    $dir = Join-Path $pkgDir "$id.$ver"
    if (-not (Test-Path $dir)) {
        $zip = Join-Path $pkgDir "$id.$ver.zip"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Write-Host "Downloading $id $ver from nuget.org..."
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$id/$ver/$id.$ver.nupkg" -OutFile $zip -UseBasicParsing
        Expand-Archive -Path $zip -DestinationPath $dir
        Remove-Item $zip
    }
    return $dir
}

function Find-Lib([string]$pkgRoot, [string]$dllName, [string[]]$tfms) {
    foreach ($tfm in $tfms) {
        $p = Join-Path $pkgRoot "lib\$tfm\$dllName"
        if (Test-Path $p) { return $p }
    }
    throw "$dllName not found in $pkgRoot (tried: $($tfms -join ', '))"
}

$lhmRoot = Get-Pkg 'librehardwaremonitorlib' '0.9.4'
$hidRoot = Get-Pkg 'hidsharp' '2.1.0'
$lhmDll = Find-Lib $lhmRoot 'LibreHardwareMonitorLib.dll' @('net472','net47','netstandard2.0')
$hidDll = Find-Lib $hidRoot 'HidSharp.dll' @('net472','net47','net35','netstandard2.0')
Write-Host "Using LHM: $lhmDll"
Write-Host "Using HidSharp: $hidDll"

# Stop a running instance so the exe can be replaced; wait for file handles to release
$proc = @(Get-Process WattAWidget -ErrorAction SilentlyContinue) + @(Get-Process WattWidget -ErrorAction SilentlyContinue)
if ($proc) {
    $proc | Stop-Process -Force
    foreach ($p in $proc) { try { $p.WaitForExit(5000) } catch {} }
    Start-Sleep -Milliseconds 500
}

$refs = @(
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll', '/r:System.Management.dll', '/r:Microsoft.CSharp.dll',
    '/r:Microsoft.VisualBasic.dll',
    "/r:$lhmDll",
    "/res:$lhmDll,LibreHardwareMonitorLib.dll",
    "/res:$hidDll,HidSharp.dll"
)

# If the LHM build is netstandard2.0, csc needs the netstandard facade reference
if ($lhmDll -match 'netstandard2\.0') {
    $facades = @(
        "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades\netstandard.dll",
        "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\Facades\netstandard.dll",
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\Facades\netstandard.dll"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $facades) {
        $gac = Get-ChildItem "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\netstandard" -Recurse -Filter netstandard.dll -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($gac) { $facades = $gac.FullName }
    }
    if (-not $facades) { throw "netstandard.dll facade not found" }
    $refs += "/r:$facades"
}

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /optimize+ `
    /win32manifest:"$root\src\app.manifest" `
    /win32icon:"$root\src\app.ico" `
    /out:"$bin\WattAWidget.exe" `
    @refs `
    "$root\src\WattWidget.cs"
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)" }
# DLLs are embedded in the exe now; remove leftovers from older builds
Remove-Item (Join-Path $bin 'LibreHardwareMonitorLib.dll'), (Join-Path $bin 'HidSharp.dll') -ErrorAction SilentlyContinue
# standalone ico beside the exe (shortcuts reference it to dodge stale icon caches)
Copy-Item "$root\src\app.ico" (Join-Path $bin 'WattAWidget.ico') -Force
Write-Host "Built: $bin\WattAWidget.exe (single file)"

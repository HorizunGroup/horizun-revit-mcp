#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('hz-instructions-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $renderer = Join-Path $root 'fake-renderer.exe'
    $source = @'
using System;
using System.IO;
public class FakeRenderer {
    public static int Main(string[] args) {
        Console.Error.WriteLine("renderer diagnostic on stderr");
        string mode = Environment.GetEnvironmentVariable("HZ_TEST_RENDERER_MODE");
        foreach (string arg in args) {
            if (arg.StartsWith("--print-to-pdf=") && mode != "missing")
                File.WriteAllBytes(arg.Substring(15), new byte[110000]);
        }
        return mode == "fail" ? 7 : 0;
    }
}
'@
    # PowerShell 7 cannot emit an executable with Add-Type; use the installed
    # Windows PowerShell compiler for the fixture, then test the caller's shell.
    if ($PSVersionTable.PSEdition -eq 'Core') {
        $sourcePath = Join-Path $root 'fake.cs'
        [IO.File]::WriteAllText($sourcePath, $source)
        $compiler = Join-Path $root 'compile.ps1'
        [IO.File]::WriteAllText($compiler, 'param($src,$out) Add-Type -Path $src -OutputAssembly $out -OutputType ConsoleApplication')
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $compiler $sourcePath $renderer
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $renderer)) { throw 'could not compile test renderer' }
    }
    else { Add-Type -OutputAssembly $renderer -OutputType ConsoleApplication -TypeDefinition $source }
    $builder = Join-Path $PSScriptRoot 'build-instructions.ps1'
    $out = Join-Path $root 'out'
    $env:HZ_TEST_RENDERER_MODE = 'success'
    & $builder -OutputDir $out -Version '4.1.0-test' -RendererPath $renderer | Out-Null
    if (@(Get-ChildItem $out -Filter '*.pdf').Count -ne 2) { throw 'stderr with exit 0 did not produce both PDFs' }
    foreach ($mode in 'fail', 'missing') {
        $env:HZ_TEST_RENDERER_MODE = $mode
        $failed = $false
        try { & $builder -OutputDir $out -Version '4.1.0-test' -RendererPath $renderer | Out-Null }
        catch { $failed = $true }
        if (-not $failed) { throw "renderer mode $mode was incorrectly accepted" }
    }
    Write-Host 'PASS: stderr with exit 0, nonzero exit, and stale/missing PDF'
}
finally {
    Remove-Item Env:HZ_TEST_RENDERER_MODE -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

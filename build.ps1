param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'AutoHello.exe'),
    [switch]$Test
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "The Windows .NET Framework x64 compiler was not found at $compiler."
}
$OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$references = @('System.dll', 'System.Core.dll', 'System.Xml.dll', 'Microsoft.CSharp.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Security.dll', (Join-Path $framework 'WPF\UIAutomationClient.dll'), (Join-Path $framework 'WPF\UIAutomationTypes.dll'), (Join-Path $framework 'WPF\WindowsBase.dll'))
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', ('/out:' + $OutputPath))
$arguments += $references | ForEach-Object { '/reference:' + $_ }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src'), (Join-Path $PSScriptRoot 'tests') -Filter '*.cs' -File | Sort-Object FullName)
if ($sources.Count -eq 0) { throw 'No C# source files found.' }
$arguments += $sources.FullName
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output "Built $OutputPath"
if ($Test) {
    $testDirectory = Join-Path $PSScriptRoot 'bin\test-results'
    New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
    $stdout = Join-Path $testDirectory 'self-test.out.txt'
    $stderr = Join-Path $testDirectory 'self-test.err.txt'
    $process = Start-Process -FilePath $OutputPath -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Get-Content -LiteralPath $stdout, $stderr
    if ($process.ExitCode -ne 0) { throw "Self-tests failed with exit code $($process.ExitCode). See $stderr" }
    Remove-Item -LiteralPath $stdout, $stderr
    if (@(Get-ChildItem -LiteralPath $testDirectory -Force).Count -eq 0) {
        Remove-Item -LiteralPath $testDirectory
    }
    $buildDirectory = Split-Path -Parent $testDirectory
    if (@(Get-ChildItem -LiteralPath $buildDirectory -Force).Count -eq 0) {
        Remove-Item -LiteralPath $buildDirectory
    }
    Write-Output 'Self-tests passed.'
}

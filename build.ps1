param([string]$OutputPath = (Join-Path $PSScriptRoot 'AutoHello.exe'))
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$references = @('System.dll', 'System.Core.dll', 'System.Xml.dll', 'Microsoft.CSharp.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Security.dll', (Join-Path $framework 'WPF\UIAutomationClient.dll'), (Join-Path $framework 'WPF\UIAutomationTypes.dll'), (Join-Path $framework 'WPF\WindowsBase.dll'))
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', ('/out:' + $OutputPath))
$arguments += $references | ForEach-Object { '/reference:' + $_ }
$arguments += Join-Path $PSScriptRoot 'AutoHello.cs'
$arguments += Join-Path $PSScriptRoot 'Automation.cs'
$arguments += Join-Path $PSScriptRoot 'SelfTests.cs'
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output "Built $OutputPath"

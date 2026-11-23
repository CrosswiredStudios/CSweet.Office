#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '../windows/Install-CSweetOfficeRuntimeHost.ps1'
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$errors)
if ($errors) { throw $errors[0] }
# Load the installed service-command path without registering or changing any service.
foreach ($name in 'Invoke-Sc', 'Out-OfficeSetupStatus') {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $false)
    . ([scriptblock]::Create($definition.Extent.Text))
}
$script:consoleWrites = 0
function Out-Host {
    param([Parameter(ValueFromPipeline = $true)] $Line)
    process {
        if ($NonInteractive) { throw 'Unattended setup attempted a console write.' }
        $script:consoleWrites++
    }
}
$NonInteractive = $true
Invoke-Sc @('showsid', 'CSweet.Office.RuntimeHost')
try {
    Invoke-Sc @('query', 'CSweet.Missing.HeadlessSetupRegression')
    throw 'Setup accepted a failed service query.'
} catch {
    if ($_.Exception.Message -notmatch 'exit code 1060') { throw }
}
$NonInteractive = $false
Invoke-Sc @('showsid', 'CSweet.Office.RuntimeHost')
if ($script:consoleWrites -eq 0) { throw 'Interactive setup lost the native service status.' }

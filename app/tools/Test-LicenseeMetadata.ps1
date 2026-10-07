<#
    Checks the LicenseeName / LicenseId assembly metadata of a built ScheduledRestart.exe against the
    expected values, which are passed through environment variables (never on the command line) so
    Hebrew and quote characters arrive intact.
    Usage: powershell -File Test-LicenseeMetadata.ps1 -Exe <path>   (expects $env:LicenseeName / $env:LicenseId; empty = unlicensed)
#>
param([Parameter(Mandatory = $true)][string]$Exe)
$ErrorActionPreference = 'Stop'
$assembly = [System.Reflection.Assembly]::LoadFile((Resolve-Path $Exe).Path)
$meta = @{}
foreach ($attr in $assembly.GetCustomAttributesData()) {
    if ($attr.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute') {
        $meta[[string]$attr.ConstructorArguments[0].Value] = [string]$attr.ConstructorArguments[1].Value
    }
}
foreach ($key in 'LicenseeName', 'LicenseId') {
    $expected = [string][Environment]::GetEnvironmentVariable($key)
    $actual = [string]$meta[$key]
    Write-Host ("{0}: [{1}] (expected [{2}])" -f $key, $actual, $expected)
    if ($actual -cne $expected) { throw "$key metadata does not match" }
}
exit 0

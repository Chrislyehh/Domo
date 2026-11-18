$ErrorActionPreference = "Stop"

$sourcePath = Join-Path $PSScriptRoot "JoanlabReader.cs"
$outputPath = Join-Path $PSScriptRoot "JOANLAB_Balanza.exe"

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Force
}

$source = [System.IO.File]::ReadAllText(
    $sourcePath,
    [System.Text.Encoding]::UTF8
)

Add-Type `
    -TypeDefinition $source `
    -Language CSharp `
    -OutputAssembly $outputPath `
    -OutputType WindowsApplication `
    -ReferencedAssemblies @(
        "System.dll",
        "System.Core.dll",
        "System.Drawing.dll",
        "System.Windows.Forms.dll"
    )

Write-Host "Aplicacion compilada correctamente:"
Write-Host $outputPath

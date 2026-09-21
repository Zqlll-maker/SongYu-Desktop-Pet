$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectDir 'build'
$sourcePath = Join-Path $projectDir 'Program.cs'
$exePath = Join-Path $outputDir 'SongYuPetV3.exe'

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Xaml
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$assemblies = @(
    [System.Windows.Application].Assembly.Location,
    [System.Windows.Controls.Grid].Assembly.Location,
    [System.Windows.Media.Brushes].Assembly.Location,
    [System.Windows.Threading.DispatcherTimer].Assembly.Location,
    [System.Xaml.XamlReader].Assembly.Location,
    [System.Windows.Forms.NotifyIcon].Assembly.Location,
    [System.Drawing.Icon].Assembly.Location
) | Select-Object -Unique

$source = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8
Add-Type -TypeDefinition $source -Language CSharp -OutputAssembly $exePath -OutputType WindowsApplication -ReferencedAssemblies $assemblies

Write-Host "Built: $exePath"

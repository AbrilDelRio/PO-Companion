$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot "DC.CopyProyectFromTemplate.csproj"
$publishDirectory = Join-Path $projectRoot "bin\Release\net10.0\publish"
$packageDirectory = Join-Path $projectRoot "deployment"
$packagePath = Join-Path $packageDirectory "DC.CopyProyectFromTemplate-deploy.zip"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK was not found. Install the .NET 10 SDK and reopen the terminal."
}

if (Test-Path $publishDirectory) {
    Remove-Item $publishDirectory -Recurse -Force
}

if (-not (Test-Path $packageDirectory)) {
    New-Item $packageDirectory -ItemType Directory | Out-Null
}

if (Test-Path $packagePath) {
    Remove-Item $packagePath -Force
}

Write-Host "Publishing the Azure Function..."
dotnet publish $projectFile `
    --configuration Release `
    --output $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$azureFunctionsDirectory = Join-Path $publishDirectory ".azurefunctions"
$hostJson = Join-Path $publishDirectory "host.json"
$workerConfig = Join-Path $publishDirectory "worker.config.json"
$functionsMetadata = Join-Path $publishDirectory "functions.metadata"

foreach ($requiredPath in @(
    $azureFunctionsDirectory,
    $hostJson,
    $workerConfig,
    $functionsMetadata
)) {
    if (-not (Test-Path $requiredPath)) {
        throw "The publish output is incomplete. Required path not found: $requiredPath"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishDirectory,
    $packagePath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)

Write-Host ""
Write-Host "Deployment package created successfully:" -ForegroundColor Green
Write-Host $packagePath
Write-Host ""
Write-Host "The ZIP root contains .azurefunctions, host.json, worker.config.json and functions.metadata."

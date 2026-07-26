# Seeds one fully-populated demo spaza shop into the database.
#
#   .\seed-db.ps1              # SQLite dev database (spazahub-dev.db)
#   .\seed-db.ps1 -SqlServer   # SQL Server (Spazahub, from appsettings.json)
#
# Idempotent: re-running does nothing if the demo shop already exists.

param(
    [switch]$SqlServer
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\SpazaHub.Api"

if ($SqlServer) {
    $env:ASPNETCORE_ENVIRONMENT = "Production"   # base appsettings.json -> SqlServer provider
    Write-Host "Seeding SQL Server (Spazahub)..." -ForegroundColor Cyan
} else {
    $env:ASPNETCORE_ENVIRONMENT = "Development"   # appsettings.Development.json -> Sqlite provider
    Write-Host "Seeding SQLite dev database (spazahub-dev.db)..." -ForegroundColor Cyan
}

dotnet run --project $project -- seed

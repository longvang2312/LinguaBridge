<#
.SYNOPSIS
    Stops the whole AppInHouse stack started by docker-all-up.ps1.

.DESCRIPTION
    By default only the containers and the network are removed: the databases, the Data Protection keys and the STS
    certificates stay in their volumes, so the next start picks up where this one stopped.

.PARAMETER RemoveData
    Also delete the volumes. This destroys the Identity database (users, clients, tokens), the LinguaBridge database and the keys, and cannot be undone.

.PARAMETER Force
    Do not ask for confirmation before deleting the volumes.

.EXAMPLE
    ./scripts/docker-all-down.ps1

.EXAMPLE
    ./scripts/docker-all-down.ps1 -RemoveData
#>
[CmdletBinding()]
param(
    [switch] $RemoveData,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $root 'docker-compose.yml'

# docker writes progress to stderr; under 'Stop' Windows PowerShell 5.1 would treat that as a failure whenever output is
# redirected. Success is decided by the exit code instead.
function Invoke-Docker {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & docker @args }
    finally { $ErrorActionPreference = $previous }
}

Invoke-Docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is not running. Start Docker Desktop and try again.' }

if ($RemoveData -and -not $Force) {
    Write-Warning 'This deletes the Identity and LinguaBridge databases and the Data Protection keys and STS certificates (docker volumes).'
    if ((Read-Host 'Type YES to continue') -ne 'YES') {
        Write-Host 'Cancelled; nothing was removed.'
        return
    }
}

$arguments = @('compose', '-f', $composeFile, 'down', '--remove-orphans')
if ($RemoveData) { $arguments += '--volumes' }
Write-Host "docker $($arguments -join ' ')" -ForegroundColor Cyan
Invoke-Docker @arguments
if ($LASTEXITCODE -ne 0) { throw "docker compose down failed (exit code $LASTEXITCODE)." }

if ($RemoveData) {
    Write-Host 'Stopped; data volumes deleted.' -ForegroundColor Green
}
else {
    Write-Host 'Stopped; data volumes kept (use -RemoveData to delete them).' -ForegroundColor Green
}

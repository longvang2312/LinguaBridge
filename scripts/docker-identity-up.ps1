<#
.SYNOPSIS
    Starts the Identity platform (SQL Server, STS, Admin API, Admin Web) from docker-compose.identity.yml.

.DESCRIPTION
    Builds the images, starts the stack and waits until every service reports healthy. If the host's SQL Server port
    (default 1433) is already taken, for example by a SQL Server installed on the machine, the database is published on
    another port instead of failing; services talk to it inside the Docker network and are not affected.

.PARAMETER NoBuild
    Skip the image build and use the images already built.

.PARAMETER DbPort
    Host port for SQL Server. Defaults to 1433, or 14330 when 1433 is busy. IDENTITY_DB_PORT in the environment also works.

.PARAMETER TimeoutSeconds
    How long to wait for the services to become healthy.

.EXAMPLE
    ./scripts/docker-identity-up.ps1
#>
[CmdletBinding()]
param(
    [switch] $NoBuild,
    [int] $DbPort = 0,
    [int] $TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $root 'docker-compose.identity.yml'

# docker writes progress to stderr; under 'Stop' Windows PowerShell 5.1 would treat that as a failure whenever output is
# redirected. Success is decided by the exit code instead.
function Invoke-Docker {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & docker @args }
    finally { $ErrorActionPreference = $previous }
}

function Test-PortInUse([int] $port) {
    $null -ne (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
}

Invoke-Docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is not running. Start Docker Desktop and try again.' }

$previousDbPort = $env:IDENTITY_DB_PORT

if ($DbPort -gt 0) {
    $env:IDENTITY_DB_PORT = "$DbPort"
}
elseif (-not $env:IDENTITY_DB_PORT) {
    if (Test-PortInUse 1433) {
        $env:IDENTITY_DB_PORT = '14330'
        Write-Warning 'Port 1433 is in use on this machine (a local SQL Server?). Publishing the container database on 14330 instead.'
    }
    else {
        $env:IDENTITY_DB_PORT = '1433'
    }
}

foreach ($port in 5000, 5001, 5002) {
    $owner = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($owner) {
        $running = docker ps --filter "publish=$port" --filter 'name=identity-' --format '{{.Names}}'
        if (-not $running) { throw "Port $port is already in use by process $($owner.OwningProcess); free it first." }
    }
}

$arguments = @('compose', '-f', $composeFile, 'up', '-d')
if (-not $NoBuild) { $arguments += '--build' }
Write-Host "docker $($arguments -join ' ')" -ForegroundColor Cyan
try {
    Invoke-Docker @arguments
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed (exit code $LASTEXITCODE)." }
}
finally {
    # Do not leave the choice behind in the caller's session: it would silently apply to the next compose command.
    $publishedDbPort = $env:IDENTITY_DB_PORT
    $env:IDENTITY_DB_PORT = $previousDbPort
}

$services = 'identity-db', 'identity-sts', 'identity-admin-api', 'identity-admin-web'
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
    $states = $services | ForEach-Object { [pscustomobject]@{ Name = $_; Health = (docker inspect -f '{{.State.Health.Status}}' $_ 2>$null) } }
    $pending = $states | Where-Object { $_.Health -ne 'healthy' }
    if (-not $pending) { break }
    Start-Sleep -Seconds 3
} while ((Get-Date) -lt $deadline)

if ($pending) {
    docker compose -f $composeFile ps
    throw "Not healthy after $TimeoutSeconds s: $(($pending | ForEach-Object { "$($_.Name)=$($_.Health)" }) -join ', '). See: docker compose -f docker-compose.identity.yml logs"
}

Write-Host ''
Write-Host 'Identity platform is up:' -ForegroundColor Green
Write-Host '  Admin Web   http://localhost:5000'
Write-Host '  STS         http://host.docker.internal:5001/.well-known/openid-configuration'
Write-Host '  Admin API   http://localhost:5002/swagger'
Write-Host "  SQL Server  localhost,$publishedDbPort  (user sa, database AppInHouse_Identity)"
Write-Host ''
Write-Host 'Always use http://host.docker.internal:5001 for the STS (browser, Swagger, Postman): a token is only accepted by the Admin API'
Write-Host 'when it was issued through that address, a token obtained via localhost:5001 is rejected with 401.'
Write-Host 'SQL Server, Admin API and Admin Web listen on this machine only; the STS (5001) is reachable from the network.'
Write-Host 'Stop with: ./scripts/docker-identity-down.ps1'

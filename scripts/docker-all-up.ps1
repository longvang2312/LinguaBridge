<#
.SYNOPSIS
    Starts the whole AppInHouse stack (Identity: SQL Server, STS, Admin API, Admin Web; LinguaBridge: PostgreSQL and API)
    from docker-compose.yml.

.DESCRIPTION
    Builds the images, starts the stack and waits until every service reports healthy. Host ports are checked first: a port
    owned by something else stops the script with the process id instead of a Docker error halfway through. The SQL Server
    (default host port 14330) and PostgreSQL (default 5432) ports can be moved; services talk to the databases inside the
    Docker network and are not affected.

.PARAMETER NoBuild
    Skip the image build and use the images already built.

.PARAMETER DbPort
    Host port for SQL Server. Defaults to 14330. IDENTITY_DB_PORT in the environment also works.

.PARAMETER PostgresPort
    Host port for PostgreSQL. Defaults to 5432, or 54320 when 5432 is busy. LINGUABRIDGE_DB_PORT in the environment also works.

.PARAMETER NoSeed
    Do not load the sample courses. By default, once every service is healthy, scripts/seed-sample-courses.ps1 runs: 3 courses,
    5 lessons, 35 questions, and 25 due flashcards for the seeded admin account. It is safe to run again (nothing is duplicated
    and review progress is kept), so repeating this script never harms existing data.

.PARAMETER TimeoutSeconds
    How long to wait for the services to become healthy.

.EXAMPLE
    ./scripts/docker-all-up.ps1
#>
[CmdletBinding()]
param(
    [switch] $NoBuild,
    [switch] $NoSeed,
    [int] $DbPort = 0,
    [int] $PostgresPort = 0,
    [int] $TimeoutSeconds = 300
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

# True when something other than this stack listens on the port. A port published by this stack's own containers (a re-run
# of this script) is not a conflict: counting it as one would move the port, and recreate the container, on every run.
function Test-PortTakenByOthers([int] $port) {
    if (-not (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)) { return $false }
    $ours = docker ps --filter "publish=$port" --filter 'label=com.docker.compose.project=appinhouse' --format '{{.Names}}'
    return -not $ours
}

Invoke-Docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is not running. Start Docker Desktop and try again.' }

# Choices made here are passed to compose through the environment and must not outlive this script.
$previousDbPort = $env:IDENTITY_DB_PORT
$previousPostgresPort = $env:LINGUABRIDGE_DB_PORT

if ($DbPort -gt 0) { $env:IDENTITY_DB_PORT = "$DbPort" }
elseif (-not $env:IDENTITY_DB_PORT) { $env:IDENTITY_DB_PORT = '14330' }

if ($PostgresPort -gt 0) {
    $env:LINGUABRIDGE_DB_PORT = "$PostgresPort"
}
elseif (-not $env:LINGUABRIDGE_DB_PORT) {
    if (Test-PortTakenByOthers 5432) {
        $env:LINGUABRIDGE_DB_PORT = '54320'
        Write-Warning 'Port 5432 is in use on this machine (a local PostgreSQL?). Publishing the container database on 54320 instead.'
    }
    else {
        $env:LINGUABRIDGE_DB_PORT = '5432'
    }
}

# Any other owner of a port is a conflict.
$ports = @(
    @{ Port = 5000; Name = 'Admin Web' }, @{ Port = 5001; Name = 'STS' }, @{ Port = 5002; Name = 'Admin API' },
    @{ Port = 5020; Name = 'LinguaBridge API' }, @{ Port = [int]$env:IDENTITY_DB_PORT; Name = 'SQL Server' },
    @{ Port = [int]$env:LINGUABRIDGE_DB_PORT; Name = 'PostgreSQL' })
foreach ($entry in $ports) {
    if (Test-PortTakenByOthers $entry.Port) {
        $owner = Get-NetTCPConnection -State Listen -LocalPort $entry.Port | Select-Object -First 1
        $process = Get-Process -Id $owner.OwningProcess -ErrorAction SilentlyContinue
        throw "Port $($entry.Port) ($($entry.Name)) is already in use by process $($owner.OwningProcess) ($($process.ProcessName)); free it first."
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
    $publishedPostgresPort = $env:LINGUABRIDGE_DB_PORT
    $env:IDENTITY_DB_PORT = $previousDbPort
    $env:LINGUABRIDGE_DB_PORT = $previousPostgresPort
}

$services = 'identity-db', 'identity-sts', 'identity-admin-api', 'identity-admin-web', 'linguabridge-db', 'linguabridge-api'
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
    $states = $services | ForEach-Object { [pscustomobject]@{ Name = $_; Health = (docker inspect -f '{{.State.Health.Status}}' $_ 2>$null) } }
    $pending = $states | Where-Object { $_.Health -ne 'healthy' }
    if (-not $pending) { break }
    Start-Sleep -Seconds 3
} while ((Get-Date) -lt $deadline)

if ($pending) {
    docker compose -f $composeFile ps
    throw "Not healthy after $TimeoutSeconds s: $(($pending | ForEach-Object { "$($_.Name)=$($_.Health)" }) -join ', '). See: docker compose logs"
}

# The API has migrated the LinguaBridge database by the time it is healthy, so the sample content can go in. A failure here
# does not undo a stack that is up: it is reported, with the command to retry, and the rest of the summary still prints.
$seedNote = $null
if ($NoSeed) {
    $seedNote = 'Sample courses skipped (-NoSeed). Load them later with: ./scripts/seed-sample-courses.ps1'
}
else {
    Write-Host ''
    Write-Host 'Loading sample content (use -NoSeed to skip) ...' -ForegroundColor Cyan
    try {
        & (Join-Path $PSScriptRoot 'seed-sample-courses.ps1')
        $seedNote = 'Sample content is loaded: 3 courses, 5 lessons, 35 questions, flashcards for the admin account.'
    }
    catch {
        Write-Warning "The sample content could not be loaded: $($_.Exception.Message)"
        $seedNote = 'The stack is up but the sample content is NOT loaded. Retry with: ./scripts/seed-sample-courses.ps1'
    }
}

Write-Host ''
Write-Host 'AppInHouse is up:' -ForegroundColor Green
Write-Host '  Admin Web         http://localhost:5000'
Write-Host '  STS               http://host.docker.internal:5001/.well-known/openid-configuration'
Write-Host '  Admin API         http://localhost:5002/swagger'
Write-Host '  LinguaBridge API  http://localhost:5020/swagger   (health: http://localhost:5020/health)'
Write-Host "  SQL Server        localhost,$publishedDbPort  (user sa, database AppInHouse_Identity)"
Write-Host "  PostgreSQL        localhost:$publishedPostgresPort  (user postgres, database LinguaBridge)"
Write-Host ''
Write-Host 'Always use http://host.docker.internal:5001 for the STS (browser, Swagger, Postman): a token is only accepted by the APIs'
Write-Host 'when it was issued through that address, a token obtained via localhost:5001 is rejected with 401.'
Write-Host 'The databases, Admin API and Admin Web listen on this machine only; the STS (5001) and LinguaBridge API (5020) are reachable from the network.'
Write-Host $seedNote
Write-Host 'Check the integration with: ./scripts/verify-linguabridge-identity.ps1'
Write-Host 'Stop with: ./scripts/docker-all-down.ps1'

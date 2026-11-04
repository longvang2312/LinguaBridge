<#
.SYNOPSIS
    Loads the sample courses (English A1 and A2, Chinese HSK 1) into the LinguaBridge database running in Docker, and gives a
    learner due flashcards.

.DESCRIPTION
    Runs scripts/seed-sample-courses.sql inside the linguabridge-db container.

      * Content: 3 published courses, 5 lessons, 35 questions with Vietnamese and English translations.
      * Flashcards: one SRS card per vocabulary question (25), already due, for the learner given by -UserEmail (looked up in
        the Identity database) or -UserId. They get cards for /flashcards.html straight away.

    Safe to run again: rows have fixed ids and are only inserted when missing, so nothing is duplicated and review progress
    on existing cards is kept. The tables must exist: the LinguaBridge API creates them when it starts (docker-all-up.ps1).

.PARAMETER UserEmail
    Learner who receives the flashcards. Default admin@appinhouse.local, the account seeded in the STS.

.PARAMETER UserId
    The learner's id (the "sub" of their token) when it is known; skips the lookup in the Identity database.

.PARAMETER ContentOnly
    Create the courses only, no flashcards.

.PARAMETER Container
    Name of the PostgreSQL container. Default linguabridge-db.

.EXAMPLE
    ./scripts/seed-sample-courses.ps1

.EXAMPLE
    ./scripts/seed-sample-courses.ps1 -UserEmail someone@example.com
#>
[CmdletBinding()]
param(
    [string] $UserEmail = $(if ($env:IDENTITY_ADMIN_EMAIL) { $env:IDENTITY_ADMIN_EMAIL } else { 'admin@appinhouse.local' }),
    [string] $UserId,
    [switch] $ContentOnly,
    [string] $Container = 'linguabridge-db'
)

$ErrorActionPreference = 'Stop'
$sqlFile = Join-Path $PSScriptRoot 'seed-sample-courses.sql'
if (-not (Test-Path $sqlFile)) { throw "Missing $sqlFile." }

# docker writes progress to stderr; under 'Stop' Windows PowerShell 5.1 would treat that as a failure. Exit codes decide.
function Invoke-Docker {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & docker @args 2>&1 }
    finally { $ErrorActionPreference = $previous }
}

Invoke-Docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is not running. Start Docker Desktop and try again.' }

# A missing container is an expected answer here, not an error to show (stderr dropped; 'Continue' so 5.1 does not turn it into one).
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$health = (& docker inspect -f '{{.State.Health.Status}}' $Container 2>$null | Out-String).Trim()
$inspectFailed = $LASTEXITCODE -ne 0
$ErrorActionPreference = $previousPreference
if ($inspectFailed) { $health = 'not found' }
if ($health -ne 'healthy') {
    throw "Container '$Container' is not running and healthy (state: $health). Start the stack first: ./scripts/docker-all-up.ps1"
}

$postgresUser = (Invoke-Docker exec $Container printenv POSTGRES_USER | Out-String).Trim()
$postgresDb = (Invoke-Docker exec $Container printenv POSTGRES_DB | Out-String).Trim()

$migrated = (Invoke-Docker exec $Container psql -U $postgresUser -d $postgresDb -tAc "select to_regclass('content.courses') is not null and to_regclass('srs.user_vocabulary_progress') is not null" | Out-String).Trim()
if ($migrated -ne 't') {
    throw "The LinguaBridge tables do not exist in '$postgresDb' yet. Start the API once (it migrates the database): ./scripts/docker-all-up.ps1"
}

# ---- The learner who gets the flashcards ---------------------------------------------------------------------------------

$learnerId = $null
if (-not $ContentOnly) {
    if ($UserId) {
        if ($UserId -notmatch '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') { throw "-UserId '$UserId' is not a GUID." }
        $learnerId = $UserId.ToLowerInvariant()
    }
    else {
        # Look the account up in the Identity database (SQL Server) by e-mail. The e-mail only ever travels as data inside the
        # query, so refuse anything that is not a plain address.
        if ($UserEmail -notmatch '^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+$') { throw "-UserEmail '$UserEmail' does not look like an e-mail address." }
        $query = "SET NOCOUNT ON; SELECT LOWER(CAST(Id AS nvarchar(64))) FROM AspNetUsers WHERE NormalizedEmail = UPPER('$UserEmail')"
        # The query goes to sqlcmd as one argument with no double quotes in it, so PowerShell passes it through intact.
        $saPassword = (Invoke-Docker exec identity-db printenv MSSQL_SA_PASSWORD | Out-String).Trim()
        $found = (Invoke-Docker exec identity-db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $saPassword -C -d AppInHouse_Identity -h -1 -W -Q $query | Out-String).Trim()
        if ($LASTEXITCODE -eq 0 -and $found -match '^[0-9a-f]{8}-([0-9a-f]{4}-){3}[0-9a-f]{12}$') {
            $learnerId = $found
        }
        else {
            Write-Warning "Could not find a learner with e-mail '$UserEmail' in the Identity database (is identity-db running?). Creating the courses without flashcards; pass -UserId <guid> to add them."
        }
    }
}

# ---- Run ---------------------------------------------------------------------------------------------------------------------

# The file is copied into the container instead of being piped in: PowerShell would re-encode piped text (the lessons are
# Vietnamese and Chinese) with the console code page.
$remote = '/tmp/seed-sample-courses.sql'
Invoke-Docker cp $sqlFile "${Container}:$remote" *> $null
if ($LASTEXITCODE -ne 0) { throw 'Could not copy the SQL file into the container.' }

$arguments = @('exec', $Container, 'psql', '-U', $postgresUser, '-d', $postgresDb, '-v', 'ON_ERROR_STOP=1')
if ($learnerId) { $arguments += @('-v', "user_id=$learnerId") }
$arguments += @('-f', $remote)

Write-Host "Loading sample courses into $postgresDb on $Container ..." -ForegroundColor Cyan
$output = Invoke-Docker @arguments
$exit = $LASTEXITCODE
Invoke-Docker exec $Container rm -f $remote *> $null
if ($exit -ne 0) {
    $output | ForEach-Object { Write-Host $_ }
    throw "The seed script failed (exit code $exit); nothing was changed (it runs in one transaction)."
}

$output | Where-Object { $_ -match '\S' -and $_ -notmatch '^(BEGIN|COMMIT|CREATE|INSERT|SET|SELECT \d)' } | ForEach-Object { Write-Host $_ }

if ($learnerId) {
    $cards = (Invoke-Docker exec $Container psql -U $postgresUser -d $postgresDb -tAc "select count(*) from srs.user_vocabulary_progress where user_id = '$learnerId' and next_review_date <= current_date" | Out-String).Trim()
    Write-Host ''
    Write-Host "Done. $cards flashcard(s) are due for learner $learnerId." -ForegroundColor Green
}
else {
    Write-Host ''
    Write-Host 'Done (content only, no flashcards).' -ForegroundColor Green
}
Write-Host 'Open http://localhost:3000/courses.html and /flashcards.html (start the web client with ./scripts/start-web.ps1).'

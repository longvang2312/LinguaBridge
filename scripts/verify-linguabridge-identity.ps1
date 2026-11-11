<#
.SYNOPSIS
    Checks that the LinguaBridge API and the Identity STS (in Docker) work together: tokens issued by the STS are
    accepted by the API through introspection, and the right ones are refused.

.DESCRIPTION
    Needs the Identity stack and the LinguaBridge database running (./scripts/docker-identity-up.ps1) with the database
    migrated, and the LinguaBridge API running with Development settings on http://localhost:5020. The API does not have to be
    started by hand: add -StartApi and the script runs it for the duration of the check.

      1. Preconditions: STS discovery, API health, and a database-backed endpoint (the database is reachable and migrated).
      2. Refused: no token, a garbage token, and a genuine STS token meant for another API (identity_admin_api) -> 401.
      3. Service token: client_credentials for scope linguabridge_api -> GET /api/v1/me is 200, authenticated, the client
         is internal-service and there is no user; learner-only endpoints say 403.
      4. User token: the administrator signs in at the STS and the learners' client (linguabridge-app, authorization code +
         PKCE) gets a token -> /api/v1/me is 200 and its userId is the very user the STS signed in; database-backed learner
         endpoints answer 200.
      5. The admin portal client may not obtain the linguabridge_api scope.

    Exits with 0 when every check passed, 1 otherwise.

.PARAMETER StsUrl
    The STS as the browser and the API know it. It must be this address: the API only accepts tokens issued through the
    same address it was configured with (Authentication:Authority in appsettings.Development.json).

.PARAMETER StartApi
    Start LinguaBridge.Api (dotnet run, Development) if it is not already answering, and stop it afterwards.

.EXAMPLE
    ./scripts/verify-linguabridge-identity.ps1 -StartApi
#>
[CmdletBinding()]
param(
    [string] $StsUrl = 'http://localhost:5001',
    [string] $ApiUrl = 'http://localhost:5020',
    [string] $AdminEmail = $(if ($env:IDENTITY_ADMIN_EMAIL) { $env:IDENTITY_ADMIN_EMAIL } else { 'admin@appinhouse.local' }),
    [string] $AdminPassword = $(if ($env:IDENTITY_ADMIN_PASSWORD) { $env:IDENTITY_ADMIN_PASSWORD } else { 'Admin@AppInHouse123!' }),
    [string] $InternalServiceSecret = $(if ($env:IDENTITY_INTERNAL_SERVICE_SECRET) { $env:IDENTITY_INTERNAL_SERVICE_SECRET } else { 'dev-internal-service-secret-change-me' }),
    [string] $LearnerRedirectUri = 'http://localhost:3000/callback',
    [switch] $StartApi,
    [int] $TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$StsUrl = $StsUrl.TrimEnd('/')
$ApiUrl = $ApiUrl.TrimEnd('/')
$script:results = New-Object System.Collections.Generic.List[object]
$script:apiProcess = $null

# ---- Plumbing (same approach as verify-identity-e2e.ps1) -----------------------------------------------------------

function New-Client {
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false           # every redirect is part of what is being verified
    $handler.UseCookies = $true
    $handler.CookieContainer = New-Object System.Net.CookieContainer
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    $client | Add-Member -NotePropertyName Jar -NotePropertyValue $handler.CookieContainer
    return $client
}

# ASP.NET marks some cookies "secure" even on plain HTTP; browsers still send them to http://localhost but .NET's
# CookieContainer drops them. Keeping them is what a browser would do.
function Save-SecureCookie($Client, [string] $Url, [string] $SetCookie) {
    if ($SetCookie -notmatch ';\s*secure(;|$)') { return }

    $uri = New-Object Uri($Url)
    $parts = $SetCookie -split ';\s*'
    $pair = $parts[0].Split('=', 2)
    $path = '/'
    foreach ($attribute in $parts | Select-Object -Skip 1) { if ($attribute -match '^path=(.*)$') { $path = $Matches[1] } }
    $Client.Jar.Add((New-Object System.Net.Cookie($pair[0], $pair[1], $path, $uri.Host)))
}

function Send-Request {
    param($Client, [string] $Method, [string] $Url, [hashtable] $Form, [string] $Bearer)

    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($Method), $Url)
    if ($Bearer) { $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $Bearer) }
    if ($Form) {
        $encoded = ($Form.Keys | ForEach-Object { '{0}={1}' -f [Uri]::EscapeDataString($_), [Uri]::EscapeDataString([string] $Form[$_]) }) -join '&'
        $request.Content = New-Object System.Net.Http.StringContent($encoded, [Text.Encoding]::UTF8, 'application/x-www-form-urlencoded')
    }

    $response = $Client.SendAsync($request).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ($response.Headers.Contains('Set-Cookie')) {
        foreach ($header in $response.Headers.GetValues('Set-Cookie')) { Save-SecureCookie $Client $Url $header }
    }
    $location = $null
    if ($response.Headers.Location) { $location = (New-Object Uri((New-Object Uri($Url)), $response.Headers.Location)).AbsoluteUri }

    return [pscustomobject]@{ Status = [int] $response.StatusCode; Location = $location; Body = $body }
}

function Add-Result([string] $Name, [bool] $Passed, [string] $Detail) {
    $script:results.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    $mark = if ($Passed) { 'PASS' } else { 'FAIL' }
    $colour = if ($Passed) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}{2}" -f $mark, $Name, $(if ($Detail) { " - $Detail" } else { '' })) -ForegroundColor $colour
}

# Runs a check; an exception counts as a failure of that check, never as the end of the run.
function Test-Step([string] $Name, [scriptblock] $Check) {
    try {
        $outcome = & $Check
        if ($outcome -is [bool]) { Add-Result $Name $outcome '' }
        else { Add-Result $Name ([bool] $outcome.Passed) ([string] $outcome.Detail) }
    }
    catch { Add-Result $Name $false $_.Exception.Message }
}

function Get-QueryValue([string] $Url, [string] $Name) {
    $match = [regex]::Match($Url, "[?&]$Name=([^&#]*)")
    if ($match.Success) { return [System.Uri]::UnescapeDataString($match.Groups[1].Value) }
    return $null
}

function ConvertFrom-Base64Url([string] $Text) {
    $padded = $Text.Replace('-', '+').Replace('_', '/')
    switch ($padded.Length % 4) { 2 { $padded += '==' } 3 { $padded += '=' } }
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($padded))
}

function New-Pkce {
    $bytes = New-Object byte[] 48
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $verifier = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $challenge = [Convert]::ToBase64String([System.Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    return [pscustomobject]@{ Verifier = $verifier; Challenge = $challenge }
}

function Get-AuthorizeUrl([string] $ClientId, [string] $RedirectUri, [string] $Scope, [string] $Challenge) {
    return "$StsUrl/connect/authorize?client_id=$ClientId&response_type=code&scope=" + [uri]::EscapeDataString($Scope) +
        '&redirect_uri=' + [uri]::EscapeDataString($RedirectUri) + "&code_challenge=$Challenge&code_challenge_method=S256&state=lb"
}

function Get-ServiceToken([string] $Scope) {
    $response = Send-Request (New-Client) 'POST' "$StsUrl/connect/token" @{
        grant_type = 'client_credentials'; client_id = 'internal-service'; client_secret = $InternalServiceSecret; scope = $Scope }
    return ($response.Body | ConvertFrom-Json).access_token
}

function Wait-ForApi([int] $Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try { if ((Send-Request (New-Client) 'GET' "$ApiUrl/health").Status -eq 200) { return $true } } catch { }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Stop-StartedApi {
    if ($script:apiProcess -and -not $script:apiProcess.HasExited) {
        & taskkill.exe /PID $script:apiProcess.Id /T /F *> $null    # dotnet run starts the API as a child process
    }
}

try {
    Write-Host "LinguaBridge <-> Identity check`n  STS $StsUrl | LinguaBridge API $ApiUrl`n"

    # ---- 1. Preconditions ---------------------------------------------------------------------------------------------

    Write-Host '1. Preconditions'
    $alreadyRunning = $false
    try { $alreadyRunning = ((Send-Request (New-Client) 'GET' "$ApiUrl/health").Status -eq 200) } catch { }
    if (-not $alreadyRunning -and $StartApi) {
        $project = Join-Path (Split-Path -Parent $PSScriptRoot) 'repos\linguabridge\src\Presentation\LinguaBridge.Api'
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $env:ASPNETCORE_URLS = $ApiUrl
        Write-Host '  starting LinguaBridge.Api (dotnet run) ...'
        $script:apiProcess = Start-Process -FilePath 'dotnet' -ArgumentList @('run', '--project', "`"$project`"", '--no-launch-profile') -PassThru -WindowStyle Hidden
    }

    Test-Step 'STS discovery is served and its issuer is the address in use' {
        $response = Send-Request (New-Client) 'GET' "$StsUrl/.well-known/openid-configuration"
        $issuer = ($response.Body | ConvertFrom-Json).issuer
        [pscustomobject]@{ Passed = ($response.Status -eq 200 -and $issuer.TrimEnd('/') -eq $StsUrl); Detail = "status $($response.Status), issuer $issuer" }
    }
    Test-Step 'LinguaBridge API is up (/health)' {
        $up = Wait-ForApi $(if ($script:apiProcess) { 120 } else { 5 })
        [pscustomobject]@{ Passed = $up; Detail = $(if ($up) { '' } else { "no answer at $ApiUrl; start it (dotnet run, Development) or pass -StartApi" }) }
    }
    Test-Step 'Database is reachable and migrated (anonymous GET /api/v1/billing/plans)' {
        $response = Send-Request (New-Client) 'GET' "$ApiUrl/api/v1/billing/plans"
        [pscustomobject]@{ Passed = ($response.Status -eq 200); Detail = "status $($response.Status)$(if ($response.Status -ne 200) { ' - is linguabridge-db up and migrated (dotnet ef database update --connection ...)?' })" }
    }

    # ---- 2. Tokens that must be refused ---------------------------------------------------------------------------------

    Write-Host "`n2. Tokens the API must refuse"
    Test-Step 'No token -> 401' { (Send-Request (New-Client) 'GET' "$ApiUrl/api/v1/me").Status -eq 401 }
    Test-Step 'Garbage token -> 401' { (Send-Request (New-Client) 'GET' "$ApiUrl/api/v1/me" -Bearer 'not-a-token').Status -eq 401 }
    Test-Step 'A genuine STS token meant for the Identity Admin API (another audience) -> 401' {
        $token = Get-ServiceToken 'identity_admin_api'
        $status = (Send-Request (New-Client) 'GET' "$ApiUrl/api/v1/me" -Bearer $token).Status
        [pscustomobject]@{ Passed = ($status -eq 401 -and $token); Detail = "status $status" }
    }

    # ---- 3. Service token -----------------------------------------------------------------------------------------------

    Write-Host "`n3. Service token (client_credentials, scope linguabridge_api)"
    $serviceToken = $null
    Test-Step 'internal-service gets a token for linguabridge_api' {
        $script:serviceToken = Get-ServiceToken 'linguabridge_api'
        [pscustomobject]@{ Passed = [bool] $script:serviceToken; Detail = $(if ($script:serviceToken) { '' } else { 'the client lacks the scope: restart the STS so the seeder grants it, or reset the data (docker-identity-down.ps1 -RemoveData)' }) }
    }
    Test-Step 'GET /api/v1/me -> 200, authenticated, caller is the internal-service client with no user' {
        $response = Send-Request (New-Client) 'GET' "$ApiUrl/api/v1/me" -Bearer $script:serviceToken
        $me = $response.Body | ConvertFrom-Json
        [pscustomobject]@{ Passed = ($response.Status -eq 200 -and $me.isAuthenticated -eq $true -and $me.clientId -eq 'internal-service' -and $null -eq $me.userId); Detail = "status $($response.Status), $($response.Body)" }
    }
    Test-Step 'A learner-only endpoint refuses a service token (403 learner.required)' {
        $response = Send-Request (New-Client) 'GET' "$ApiUrl/api/v1/billing/entitlement" -Bearer $script:serviceToken
        [pscustomobject]@{ Passed = ($response.Status -eq 403 -and $response.Body -match 'learner.required'); Detail = "status $($response.Status)" }
    }

    # ---- 4. User token --------------------------------------------------------------------------------------------------

    Write-Host "`n4. User token (learners' client linguabridge-app, authorization code + PKCE)"
    $userToken = $null
    $idTokenSubject = $null
    $refreshToken = $null
    Test-Step 'The STS signs the administrator in and issues linguabridge-app a token' {
        $browser = New-Client
        $pkce = New-Pkce
        $authorize = Get-AuthorizeUrl 'linguabridge-app' $LearnerRedirectUri 'openid profile email offline_access linguabridge_api' $pkce.Challenge

        $toLogin = Send-Request $browser 'GET' $authorize
        if (-not $toLogin.Location -or $toLogin.Location -notmatch '/Account/Login') { return [pscustomobject]@{ Passed = $false; Detail = "expected the login page, got status $($toLogin.Status) $($toLogin.Location) (is linguabridge-app seeded?)" } }
        $page = Send-Request $browser 'GET' $toLogin.Location
        $tag = [regex]::Match($page.Body, '<input[^>]*name="__RequestVerificationToken"[^>]*>').Value
        $antiforgery = [regex]::Match($tag, 'value="([^"]*)"').Groups[1].Value
        $login = Send-Request $browser 'POST' $toLogin.Location @{
            Email = $AdminEmail; Password = $AdminPassword; ReturnUrl = (Get-QueryValue $toLogin.Location 'ReturnUrl'); RememberMe = 'false'; __RequestVerificationToken = $antiforgery }
        if ($login.Status -ne 302) { return [pscustomobject]@{ Passed = $false; Detail = "sign-in answered $($login.Status), expected 302" } }

        $back = Send-Request $browser 'GET' $authorize
        $code = if ($back.Location) { Get-QueryValue $back.Location 'code' } else { $null }
        if (-not $code) { return [pscustomobject]@{ Passed = $false; Detail = "no code (status $($back.Status), location $($back.Location))" } }

        $token = Send-Request (New-Client) 'POST' "$StsUrl/connect/token" @{
            grant_type = 'authorization_code'; client_id = 'linguabridge-app'; code = $code; redirect_uri = $LearnerRedirectUri; code_verifier = $pkce.Verifier }
        $json = $token.Body | ConvertFrom-Json
        $script:userToken = $json.access_token
        $script:refreshToken = $json.refresh_token
        if ($json.id_token) { $script:idTokenSubject = (ConvertFrom-Base64Url $json.id_token.Split('.')[1] | ConvertFrom-Json).sub }
        [pscustomobject]@{ Passed = ($token.Status -eq 200 -and $script:userToken); Detail = "token endpoint status $($token.Status)" }
    }
    Test-Step 'The token response includes a refresh token (offline_access)' { [bool] $script:refreshToken }
    Test-Step 'GET /api/v1/me -> 200 and userId is the user the STS signed in' {
        $response = Send-Request (New-Client) 'GET' "$ApiUrl/api/v1/me" -Bearer $script:userToken
        $me = $response.Body | ConvertFrom-Json
        $isGuid = $me.userId -and [guid]::TryParse([string] $me.userId, [ref] ([guid]::Empty))
        [pscustomobject]@{
            Passed = ($response.Status -eq 200 -and $me.isAuthenticated -eq $true -and $isGuid -and $me.userId -eq $script:idTokenSubject -and $me.clientId -eq 'linguabridge-app')
            Detail = "status $($response.Status), userId $($me.userId), id_token sub $($script:idTokenSubject), client $($me.clientId)"
        }
    }
    Test-Step 'Learner endpoints backed by the database answer 200 (entitlement, AI quota)' {
        $entitlement = Send-Request (New-Client) 'GET' "$ApiUrl/api/v1/billing/entitlement" -Bearer $script:userToken
        $quota = Send-Request (New-Client) 'GET' "$ApiUrl/api/v1/ai/quota" -Bearer $script:userToken
        [pscustomobject]@{ Passed = ($entitlement.Status -eq 200 -and $quota.Status -eq 200); Detail = "entitlement $($entitlement.Status), ai quota $($quota.Status)" }
    }

    # ---- 5. Scope boundaries --------------------------------------------------------------------------------------------

    Write-Host "`n5. Scope boundaries"
    Test-Step 'The admin portal client cannot obtain the linguabridge_api scope' {
        $pkce = New-Pkce
        $response = Send-Request (New-Client) 'GET' (Get-AuthorizeUrl 'admin-portal' 'http://localhost:5000/signin-oidc' 'openid linguabridge_api' $pkce.Challenge)
        [pscustomobject]@{ Passed = ($response.Status -ge 400 -or ($response.Location -and $response.Location -match 'error=')); Detail = "status $($response.Status)" }
    }
}
finally {
    Stop-StartedApi
}

# ---- Summary --------------------------------------------------------------------------------------------------------

$failed = @($script:results | Where-Object { -not $_.Passed })
Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host "All $($script:results.Count) checks passed." -ForegroundColor Green
    exit 0
}

Write-Host "$($failed.Count) of $($script:results.Count) checks FAILED:" -ForegroundColor Red
$failed | ForEach-Object { Write-Host "  - $($_.Name): $($_.Detail)" -ForegroundColor Red }
exit 1

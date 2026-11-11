<#
.SYNOPSIS
    End-to-end smoke test of the running Identity platform (STS, Admin API, Admin Web).

.DESCRIPTION
    Start the stack first (./scripts/docker-identity-up.ps1). No browser is needed: the script plays the browser's part
    of the OpenID Connect sign-in over HTTP, with a cookie jar and without following redirects automatically, so every
    hop can be checked.

      1. Discovery document and signing keys (JWKS) of the STS.
      2. Service token (client_credentials) against Admin API: no token -> 401, bad token -> 401, service token -> 403
         (authenticated, but the API wants a global administrator).
      3. Admin Web sign-in: Admin Web -> STS login -> /signin-oidc -> dashboard, then Users, Roles, Clients pages.
         Also: a wrong password does not sign in, and an anonymous request is sent to the STS.
      4. A real user token (authorization code + PKCE for the admin-portal client) against Admin API -> 200.

    Exits with 0 when every check passed, 1 otherwise. The defaults are the development values the stack seeds; they
    are read from the environment when set (IDENTITY_ADMIN_EMAIL, IDENTITY_ADMIN_PASSWORD, IDENTITY_INTERNAL_SERVICE_SECRET).

.PARAMETER StsUrl
    The STS as the browser and the containers know it. It must be this address: the Admin API only accepts tokens issued
    through the same address it validates against (the token issuer is taken from the request's host name).

.EXAMPLE
    ./scripts/verify-identity-e2e.ps1
#>
[CmdletBinding()]
param(
    [string] $StsUrl = 'http://localhost:5001',
    [string] $AdminWebUrl = 'http://localhost:5000',
    [string] $AdminApiUrl = 'http://localhost:5002',
    [string] $AdminEmail = $(if ($env:IDENTITY_ADMIN_EMAIL) { $env:IDENTITY_ADMIN_EMAIL } else { 'admin@appinhouse.local' }),
    [string] $AdminPassword = $(if ($env:IDENTITY_ADMIN_PASSWORD) { $env:IDENTITY_ADMIN_PASSWORD } else { 'Admin@AppInHouse123!' }),
    [string] $InternalServiceSecret = $(if ($env:IDENTITY_INTERNAL_SERVICE_SECRET) { $env:IDENTITY_INTERNAL_SERVICE_SECRET } else { 'dev-internal-service-secret-change-me' }),
    [int] $TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$StsUrl = $StsUrl.TrimEnd('/')
$AdminWebUrl = $AdminWebUrl.TrimEnd('/')
$AdminApiUrl = $AdminApiUrl.TrimEnd('/')
$script:results = New-Object System.Collections.Generic.List[object]

# ---- Plumbing -------------------------------------------------------------------------------------------------------

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

# ASP.NET marks the OpenID Connect correlation and nonce cookies "secure; samesite=none" even when the site runs on plain
# HTTP. Browsers (and curl) still send them to http://localhost, but .NET's CookieContainer drops Secure cookies on
# http:// addresses, which would make every sign-in fail with "Correlation failed". This keeps them, as a browser would.
function Save-SecureCookie($Client, [string] $Url, [string] $SetCookie) {
    if ($SetCookie -notmatch ';\s*secure(;|$)') { return }

    $uri = New-Object Uri($Url)
    $parts = $SetCookie -split ';\s*'
    $pair = $parts[0].Split('=', 2)
    $path = '/'
    $expired = [string]::IsNullOrEmpty($pair[1])
    foreach ($attribute in $parts | Select-Object -Skip 1) {
        if ($attribute -match '^path=(.*)$') { $path = $Matches[1] }
        if ($attribute -match '^expires=(.*)$') {
            $when = [DateTime]::MinValue
            if ([DateTime]::TryParse($Matches[1], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal, [ref] $when) -and $when -lt [DateTime]::UtcNow) { $expired = $true }
        }
    }

    $cookie = New-Object System.Net.Cookie($pair[0], $pair[1], $path, $uri.Host)
    $cookie.Expired = $expired
    $Client.Jar.Add($cookie)
}

function Send-Request {
    param($Client, [string] $Method, [string] $Url, [hashtable] $Form, [string] $Bearer)

    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($Method), $Url)
    if ($Bearer) { $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $Bearer) }
    if ($Form) {
        # Encoded by hand: handing a list to FormUrlEncodedContent is unrolled by PowerShell into separate arguments.
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

function Get-FormToken([string] $Html) {
    $tag = [regex]::Match($Html, '<input[^>]*name="__RequestVerificationToken"[^>]*>').Value
    return [regex]::Match($tag, 'value="([^"]*)"').Groups[1].Value
}

function Get-HiddenFields([string] $Html) {
    $fields = @{}
    foreach ($tag in [regex]::Matches($Html, '<input[^>]*>')) {
        $name = [regex]::Match($tag.Value, 'name="([^"]+)"').Groups[1].Value
        if ($name) { $fields[$name] = [System.Net.WebUtility]::HtmlDecode([regex]::Match($tag.Value, 'value="([^"]*)"').Groups[1].Value) }
    }
    return $fields
}

function Get-QueryValue([string] $Url, [string] $Name) {
    $match = [regex]::Match($Url, "[?&]$Name=([^&#]*)")
    if ($match.Success) { return [System.Uri]::UnescapeDataString($match.Groups[1].Value) }
    return $null
}

# Plays the browser through the sign-in. Returns the last response of each hop, and the client holding the cookies.
function Invoke-Login {
    param([string] $Password)

    $client = New-Client
    $hops = [ordered]@{}

    $hops.AdminWeb = Send-Request $client 'GET' "$AdminWebUrl/"
    $hops.Authorize = Send-Request $client 'GET' $hops.AdminWeb.Location
    $hops.LoginPage = Send-Request $client 'GET' $hops.Authorize.Location
    $returnUrl = Get-QueryValue $hops.Authorize.Location 'ReturnUrl'

    $hops.Login = Send-Request $client 'POST' $hops.Authorize.Location @{
        Email = $AdminEmail; Password = $Password; ReturnUrl = $returnUrl; RememberMe = 'false'
        __RequestVerificationToken = (Get-FormToken $hops.LoginPage.Body)
    }
    $hops.Client = $client
    $hops.Fields = $null
    if ($hops.Login.Status -ne 302) { return $hops }

    # The STS answers the authorization request with a page that posts the code to Admin Web (response_mode=form_post).
    $hops.Callback = Send-Request $client 'GET' (New-Object Uri((New-Object Uri($hops.Authorize.Location)), $hops.Login.Location)).AbsoluteUri
    $form = [regex]::Match($hops.Callback.Body, '<form[^>]*action="([^"]*)"')
    if ($form.Success) {
        $action = [System.Net.WebUtility]::HtmlDecode($form.Groups[1].Value)
        $hops.Fields = Get-HiddenFields $hops.Callback.Body
        $hops.SignIn = Send-Request $client 'POST' $action $hops.Fields
    }
    return $hops
}

Write-Host "Identity end-to-end check`n  STS $StsUrl | Admin Web $AdminWebUrl | Admin API $AdminApiUrl`n"

# ---- 1. Discovery and keys ------------------------------------------------------------------------------------------

Write-Host '1. STS discovery and signing keys'
$discovery = $null
Test-Step 'Discovery document is served (200, JSON)' {
    $script:discovery = Send-Request (New-Client) 'GET' "$StsUrl/.well-known/openid-configuration"
    $json = $script:discovery.Body | ConvertFrom-Json
    [pscustomobject]@{ Passed = ($script:discovery.Status -eq 200 -and $json.issuer); Detail = "status $($script:discovery.Status), issuer $($json.issuer)" }
}
Test-Step 'Issuer is the address everything uses' {
    $json = $script:discovery.Body | ConvertFrom-Json
    [pscustomobject]@{ Passed = ($json.issuer.TrimEnd('/') -eq $StsUrl); Detail = "issuer '$($json.issuer)' vs '$StsUrl'" }
}
Test-Step 'JWKS publishes at least one signing key' {
    $json = $script:discovery.Body | ConvertFrom-Json
    $jwks = Send-Request (New-Client) 'GET' $json.jwks_uri
    $keys = @(($jwks.Body | ConvertFrom-Json).keys)
    [pscustomobject]@{ Passed = ($jwks.Status -eq 200 -and $keys.Count -ge 1 -and $keys[0].kid); Detail = "$($keys.Count) key(s), kid $($keys[0].kid)" }
}

# ---- 2. Service token against Admin API -----------------------------------------------------------------------------

Write-Host "`n2. Admin API with a service token"
$serviceToken = $null
Test-Step 'client_credentials token is issued' {
    $response = Send-Request (New-Client) 'POST' "$StsUrl/connect/token" @{
        grant_type = 'client_credentials'; client_id = 'internal-service'; client_secret = $InternalServiceSecret; scope = 'identity_admin_api' }
    $script:serviceToken = ($response.Body | ConvertFrom-Json).access_token
    [pscustomobject]@{ Passed = ($response.Status -eq 200 -and $script:serviceToken); Detail = "status $($response.Status)" }
}
Test-Step 'No token -> 401' { (Send-Request (New-Client) 'GET' "$AdminApiUrl/api/v1/users").Status -eq 401 }
Test-Step 'Invalid token -> 401' { (Send-Request (New-Client) 'GET' "$AdminApiUrl/api/v1/users" -Bearer 'not-a-token').Status -eq 401 }
Test-Step 'Service token is authenticated but not an administrator -> 403' {
    $status = (Send-Request (New-Client) 'GET' "$AdminApiUrl/api/v1/users" -Bearer $script:serviceToken).Status
    [pscustomobject]@{ Passed = ($status -eq 403); Detail = "status $status (401 would mean the token was not accepted)" }
}

# ---- 3. Sign in through Admin Web -----------------------------------------------------------------------------------

Write-Host "`n3. Admin Web sign-in (the browser's part, over HTTP)"
$login = $null
Test-Step 'Anonymous request to Admin Web is sent to the STS authorize endpoint' {
    $hop = Send-Request (New-Client) 'GET' "$AdminWebUrl/"
    [pscustomobject]@{ Passed = ($hop.Status -eq 302 -and $hop.Location.StartsWith("$StsUrl/connect/authorize")); Detail = "status $($hop.Status) -> $($hop.Location.Split('?')[0])" }
}
Test-Step 'STS shows its login page' {
    $script:login = Invoke-Login -Password $AdminPassword
    [pscustomobject]@{ Passed = ($script:login.LoginPage.Status -eq 200 -and $script:login.LoginPage.Location -eq $null -and (Get-FormToken $script:login.LoginPage.Body)); Detail = "login page status $($script:login.LoginPage.Status)" }
}
Test-Step 'Admin credentials are accepted and the STS posts the code back to Admin Web' {
    $callback = $script:login.Callback
    $okFields = $script:login.Fields -and $script:login.Fields.ContainsKey('code') -and $script:login.Fields.ContainsKey('state')
    [pscustomobject]@{ Passed = ($script:login.Login.Status -eq 302 -and $callback.Status -eq 200 -and $okFields); Detail = "login $($script:login.Login.Status), callback page $($callback.Status)" }
}
Test-Step 'Admin Web completes sign-in at /signin-oidc and redirects to the dashboard' {
    $signIn = $script:login.SignIn
    [pscustomobject]@{ Passed = ($signIn.Status -eq 302 -and $signIn.Location.TrimEnd('/') -eq $AdminWebUrl); Detail = "status $($signIn.Status) -> $($signIn.Location)" }
}
Test-Step 'Dashboard is shown to the signed-in administrator' {
    $page = Send-Request $script:login.Client 'GET' "$AdminWebUrl/"
    [pscustomobject]@{ Passed = ($page.Status -eq 200 -and $page.Body -match 'Dashboard'); Detail = "status $($page.Status)" }
}
Test-Step 'Users page lists the seeded administrator' {
    $page = Send-Request $script:login.Client 'GET' "$AdminWebUrl/Users"
    [pscustomobject]@{ Passed = ($page.Status -eq 200 -and $page.Body.Contains($AdminEmail)); Detail = "status $($page.Status)" }
}
Test-Step 'Roles page lists the roles' {
    $page = Send-Request $script:login.Client 'GET' "$AdminWebUrl/Roles"
    [pscustomobject]@{ Passed = ($page.Status -eq 200 -and $page.Body.Contains('GlobalAdmin')); Detail = "status $($page.Status)" }
}
Test-Step 'Clients page lists the seeded OpenIddict clients' {
    $page = Send-Request $script:login.Client 'GET' "$AdminWebUrl/Clients"
    $missing = @('admin-portal', 'internal-service', 'identity_admin_api', 'linguabridge_api') | Where-Object { -not $page.Body.Contains($_) }
    [pscustomobject]@{ Passed = ($page.Status -eq 200 -and -not $missing); Detail = "status $($page.Status)$(if ($missing) { ", missing: $($missing -join ', ')" })" }
}
Test-Step 'A wrong password does not sign in' {
    $bad = Invoke-Login -Password "$AdminPassword-wrong"
    [pscustomobject]@{ Passed = ($bad.Login.Status -eq 200); Detail = "login answered $($bad.Login.Status) (302 would mean it signed in)" }
}
Test-Step 'Without the session cookie, Users is not served' {
    $status = (Send-Request (New-Client) 'GET' "$AdminWebUrl/Users").Status
    [pscustomobject]@{ Passed = ($status -eq 302); Detail = "status $status" }
}

# ---- 4. A real user token against Admin API -------------------------------------------------------------------------

Write-Host "`n4. Admin API with the administrator's own token (authorization code + PKCE)"
$userToken = $null
Test-Step 'The STS issues an access token for the signed-in administrator' {
    $bytes = New-Object byte[] 48
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $verifier = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $challenge = [Convert]::ToBase64String([System.Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $redirect = "$AdminWebUrl/signin-oidc"

    $authorize = "$StsUrl/connect/authorize?client_id=admin-portal&response_type=code&scope=" + [uri]::EscapeDataString('openid profile email identity_admin_api') +
        "&redirect_uri=" + [uri]::EscapeDataString($redirect) + "&code_challenge=$challenge&code_challenge_method=S256&state=e2e"
    # Reuses the STS session cookie from the sign-in above, as a second request from the same browser would.
    $hop = Send-Request $script:login.Client 'GET' $authorize
    $code = if ($hop.Location) { Get-QueryValue $hop.Location 'code' } else { $null }
    if (-not $code) { return [pscustomobject]@{ Passed = $false; Detail = "no code (status $($hop.Status), location $($hop.Location))" } }

    $token = Send-Request (New-Client) 'POST' "$StsUrl/connect/token" @{
        grant_type = 'authorization_code'; client_id = 'admin-portal'; code = $code; redirect_uri = $redirect; code_verifier = $verifier }
    $script:userToken = ($token.Body | ConvertFrom-Json).access_token
    [pscustomobject]@{ Passed = ($token.Status -eq 200 -and $script:userToken); Detail = "token endpoint status $($token.Status)" }
}
foreach ($resource in 'users', 'roles', 'clients', 'permissions') {
    $script:resource = $resource
    Test-Step "Admin API GET /api/v1/$resource -> 200" {
        $response = Send-Request (New-Client) 'GET' "$AdminApiUrl/api/v1/$($script:resource)" -Bearer $script:userToken
        [pscustomobject]@{ Passed = ($response.Status -eq 200); Detail = "status $($response.Status)" }
    }
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

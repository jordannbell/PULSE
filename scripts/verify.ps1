<#
.SYNOPSIS
  End-to-end verification of Pulse as a multi-customer service: starts the real API, the real web site and a
  misbehaving target, then plays real scenarios against them (sign-up, isolation between customers, plan
  limits, security, incidents, per-customer alerts, restart, rate limits).

.DESCRIPTION
  Run from the repository root:   pwsh ./scripts/verify.ps1      (or powershell -File scripts/verify.ps1)
  Needs the .NET SDK. Uses ports 5180-5184, 5189, 5190, 9190 and 2590 on 127.0.0.1, and temporary databases.
  Takes about 5 minutes. Exit code = number of failed checks (0 = everything works).
  Checks that need the internet (real DNS, a real public site) are skipped with -SkipInternet.
#>
[CmdletBinding()]
param(
    [string]$Dotnet = "dotnet",
    [switch]$SkipBuild,
    [switch]$SkipInternet
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$flakyPort = 9190   # not 9100: that port is used for manual tests
$smtpPort = 2590    # the fake mail server (2525 is used for manual tests)
$password = "correct horse battery"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("pulse-verify-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromSeconds(30)
$script:passed = 0
$script:failed = 0
$script:skipped = 0
$script:procs = New-Object System.Collections.Generic.List[object]
$encryptionKey = [Convert]::ToBase64String([byte[]](1..32 | ForEach-Object { Get-Random -Minimum 0 -Maximum 256 }))

function Write-Section($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }

function Check($name, [scriptblock]$test) {
    try {
        # Only the last value the block outputs counts: helper calls inside it may print other things.
        $out = @(& $test)
        $ok = $out.Count -gt 0 -and $out[$out.Count - 1]
        if ($ok) { $script:passed++; Write-Host "[PASS] $name" -ForegroundColor Green }
        else { $script:failed++; Write-Host "[FAIL] $name" -ForegroundColor Red }
    }
    catch { $script:failed++; Write-Host "[FAIL] $name -- $($_.Exception.Message)" -ForegroundColor Red }
}

function Skip($name, $why) { $script:skipped++; Write-Host "[SKIP] $name ($why)" -ForegroundColor Yellow }

function Call($Method, $Url, $Body = $null, $Token = $null, $RawBody = $null, $ContentType = "application/json") {
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($Method)), $Url)
    if ($Token) { $req.Headers.Add("Authorization", "Bearer $Token") }
    $payload = $RawBody
    if ($null -ne $Body) { $payload = ConvertTo-Json -InputObject $Body -Compress }
    if ($null -ne $payload) { $req.Content = New-Object System.Net.Http.StringContent($payload, [Text.Encoding]::UTF8, $ContentType) }
    $resp = $http.SendAsync($req).GetAwaiter().GetResult()
    $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $json = $null
    try { if ($text) { $json = $text | ConvertFrom-Json } } catch { }
    $headers = @{}
    foreach ($h in $resp.Headers) { $headers[$h.Key] = ($h.Value -join ",") }
    foreach ($h in $resp.Content.Headers) { $headers[$h.Key] = ($h.Value -join ",") }
    [pscustomobject]@{ Status = [int]$resp.StatusCode; Text = $text; Json = $json; Headers = $headers }
}

function Wait-Until([scriptblock]$condition, $timeoutSeconds, $everySeconds = 1) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try { if (& $condition) { return $true } } catch { }
        Start-Sleep -Seconds $everySeconds
    }
    return $false
}

function Start-Dll($name, $dll, $urls, $envVars, $arguments = @()) {
    $old = @{}
    foreach ($k in $envVars.Keys) { $old[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $envVars[$k]) }
    try {
        $out = Join-Path $tmp "$name.out.log"; $err = Join-Path $tmp "$name.err.log"
        $argList = @("`"$dll`"") + $arguments
        if ($urls) { $argList += @("--urls", $urls) }
        $p = Start-Process -FilePath $Dotnet -ArgumentList $argList -WorkingDirectory (Split-Path $dll) -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err
    }
    finally { foreach ($k in $old.Keys) { [Environment]::SetEnvironmentVariable($k, $old[$k]) } }
    $null = $p.Handle   # without this, PowerShell cannot read the exit code afterwards
    $script:procs.Add($p)
    [pscustomobject]@{ Name = $name; Process = $p; Out = $out; Err = $err }
}

function Stop-App($app) {
    if ($app -and -not $app.Process.HasExited) { Stop-Process -Id $app.Process.Id -Force -ErrorAction SilentlyContinue; $app.Process.WaitForExit(5000) | Out-Null }
}

function Log-Text($app) { ((Get-Content $app.Out -Raw -ErrorAction SilentlyContinue) + "`n" + (Get-Content $app.Err -Raw -ErrorAction SilentlyContinue)) }

function Count-Of($x) { if ($null -eq $x) { 0 } else { @($x).Count } }

function Get-Signature($secret, $timestamp, $body) {
    $hmac = New-Object System.Security.Cryptography.HMACSHA256
    $hmac.Key = [Text.Encoding]::UTF8.GetBytes($secret)
    $bytes = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes("$timestamp.$body"))
    "sha256=" + (($bytes | ForEach-Object { $_.ToString("x2") }) -join "")
}

$apiDll = Join-Path $root "src/Pulse.Api/bin/Release/net10.0/Pulse.Api.dll"
$webDll = Join-Path $root "src/Pulse.Web/bin/Release/net10.0/Pulse.Web.dll"
$flakyDll = Join-Path $root "tools/FlakyTarget/bin/Release/net10.0/FlakyTarget.dll"

function New-ApiEnv($db, $extra = @{}) {
    $e = @{
        ASPNETCORE_ENVIRONMENT = "Production"
        Pulse__Accounts__EncryptionKey = $encryptionKey
        ConnectionStrings__Pulse = "Data Source=$db"
        Pulse__SchedulerTickSeconds = "1"
        Pulse__AuthRateLimitPerMinute = "100000"
    }
    foreach ($k in $extra.Keys) { $e[$k] = $extra[$k] }
    $e
}

function Start-Api($name, $port, $db, $extra = @{}) {
    $app = Start-Dll $name $apiDll "http://127.0.0.1:$port" (New-ApiEnv $db $extra)
    $ready = Wait-Until { (Call GET "http://127.0.0.1:$port/health").Status -eq 200 } 25
    if (-not $ready) { throw "API '$name' did not start. Log: $(Log-Text $app)" }
    $app
}

# Signs a new customer up and returns what later calls need.
function New-Customer($base, $email, $slug, $name = "Organisation $slug") {
    $r = Call POST "$base/api/v1/auth/signup" -Body @{ email = $email; password = $password; organizationName = $name; slug = $slug }
    [pscustomobject]@{ Status = $r.Status; Token = $r.Json.token; Email = $email; Slug = $slug; Json = $r.Json; Raw = $r }
}

try {
    if (-not $SkipBuild) {
        Write-Section "Build (Release)"
        & $Dotnet build (Join-Path $root "Pulse.sln") -c Release --nologo -v quiet | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    }

    $api = "http://127.0.0.1:5180"

    # ------------------------------------------------------------------ A
    Write-Section "A. The API refuses to start with an unsafe configuration"
    $base = @{ ASPNETCORE_ENVIRONMENT = "Production"; Pulse__Accounts__EncryptionKey = $encryptionKey }
    function With($extra, $db) { $e = $base.Clone(); foreach ($k in $extra.Keys) { $e[$k] = $extra[$k] }; $e["ConnectionStrings__Pulse"] = "Data Source=$tmp\$db.db"; $e }
    foreach ($case in @(
            @{ Name = "no encryption key"; Env = @{ ASPNETCORE_ENVIRONMENT = "Production"; ConnectionStrings__Pulse = "Data Source=$tmp\a1.db" } },
            @{ Name = "an encryption key of 16 bytes"; Env = (With @{ Pulse__Accounts__EncryptionKey = [Convert]::ToBase64String([byte[]](1..16)) } "a2") },
            @{ Name = "an encryption key that is not base64"; Env = (With @{ Pulse__Accounts__EncryptionKey = "not base64 at all !" } "a3") },
            @{ Name = "a web address that is not a URL"; Env = (With @{ Pulse__Accounts__WebBaseUrl = "pas une adresse" } "a4") },
            @{ Name = "a mail server without a valid sender"; Env = (With @{ Pulse__Mail__Host = "127.0.0.1"; Pulse__Mail__From = "nope" } "a5") },
            @{ Name = "a failure threshold of 0"; Env = (With @{ Pulse__FailureThreshold = "0" } "a6") })) {
        $once = Start-Dll "startup-$($case.Name -replace '\W','')" $apiDll "http://127.0.0.1:5189" $case.Env
        $exited = $once.Process.WaitForExit(15000)
        Check "Startup stops fast, with a clear message, with $($case.Name)" { $exited -and $once.Process.ExitCode -ne 0 -and (Log-Text $once) -match "Invalid configuration" }
        if (-not $exited) { Stop-App $once }
    }

    # ------------------------------------------------------------------ B
    Write-Section "B. Real API in production mode (strict: private targets forbidden, e-mail links written to the log)"
    $strictDb = Join-Path $tmp "strict.db"
    $strictEnv = @{ Pulse__Accounts__LogEmailsToConsole = "true" }
    $strict = Start-Api "strict" 5180 $strictDb $strictEnv
    Check "GET /health answers 200" { (Call GET "$api/health").Status -eq 200 }
    Check "Responses carry nosniff and no-store headers" {
        $h = (Call GET "$api/health").Headers
        $h["X-Content-Type-Options"] -eq "nosniff" -and $h["Cache-Control"] -eq "no-store"
    }

    Write-Host "-- sign-up validation"
    $bad = @(
        @{ Name = "a short password"; Body = @{ email = "v1@example.com"; password = "short"; organizationName = "V"; slug = "v-one" }; Field = "password" },
        @{ Name = "a password equal to the e-mail"; Body = @{ email = "same.address@example.com"; password = "same.address@example.com"; organizationName = "V"; slug = "v-two" }; Field = "password" },
        @{ Name = "a bad e-mail address"; Body = @{ email = "nope"; password = $password; organizationName = "V"; slug = "v-three" }; Field = "email" },
        @{ Name = "a slug with capitals and spaces"; Body = @{ email = "v4@example.com"; password = $password; organizationName = "V"; slug = "Bad Slug" }; Field = "slug" },
        @{ Name = "a reserved slug (admin)"; Body = @{ email = "v5@example.com"; password = $password; organizationName = "V"; slug = "admin" }; Field = "slug" },
        @{ Name = "the demo slug"; Body = @{ email = "v6@example.com"; password = $password; organizationName = "V"; slug = "demo" }; Field = "slug" },
        @{ Name = "an empty organization name"; Body = @{ email = "v7@example.com"; password = $password; organizationName = " "; slug = "v-seven" }; Field = "organizationName" })
    foreach ($case in $bad) {
        Check "Sign-up refused with $($case.Name)" {
            $r = Call POST "$api/api/v1/auth/signup" -Body $case.Body
            $r.Status -eq 400 -and $null -ne $r.Json.errors.($case.Field)
        }
    }
    Check "Malformed JSON -> 400 (not 500)" { (Call POST "$api/api/v1/auth/signup" -RawBody "{ not json").Status -eq 400 }
    Check "Wrong content type -> 415" { (Call POST "$api/api/v1/auth/signup" -RawBody "a=b" -ContentType "text/plain").Status -eq 415 }

    Write-Host "-- two customers"
    $ana = New-Customer $api "ana@example.com" "ana-sarl" "Ana SARL"
    $bob = New-Customer $api "bob@example.com" "bob-corp" "Bob Corp"
    Check "Both sign-ups answer 201 with a session token" { $ana.Status -eq 201 -and $bob.Status -eq 201 -and $ana.Token.Length -ge 40 -and $ana.Token -ne $bob.Token }
    Check "The new account starts on the free plan, with its address not yet confirmed" {
        $a = $ana.Json.account
        $a.plan -eq "Free" -and -not $a.emailVerified -and $a.slug -eq "ana-sarl"
    }
    Check "Signing up twice with the same e-mail is refused (case does not matter)" {
        $r = Call POST "$api/api/v1/auth/signup" -Body @{ email = "ANA@example.com"; password = $password; organizationName = "Copy"; slug = "ana-copy" }
        $r.Status -eq 400 -and $null -ne $r.Json.errors.email
    }
    Check "Taking an address name that exists is refused" {
        $r = Call POST "$api/api/v1/auth/signup" -Body @{ email = "carl@example.com"; password = $password; organizationName = "Copy"; slug = "ana-sarl" }
        $r.Status -eq 400 -and $null -ne $r.Json.errors.slug
    }
    Check "The password hash never leaves the server" { $ana.Raw.Text -notmatch "hash|salt|PBKDF" -and $ana.Raw.Text -notmatch [regex]::Escape($password) }

    Write-Host "-- authentication"
    Check "No token -> 401 on the service list" { (Call GET "$api/api/v1/services").Status -eq 401 }
    Check "A made-up token -> 401" { (Call GET "$api/api/v1/services" -Token ("x" * 43)).Status -eq 401 }
    Check "The right token minus one character -> 401" { (Call GET "$api/api/v1/services" -Token $ana.Token.Substring(0, $ana.Token.Length - 1)).Status -eq 401 }
    Check "No token -> 401 on create, delete, account, webhooks and alerts" {
        (Call POST "$api/api/v1/services" -Body @{ name = "x"; url = "https://example.com/" }).Status -eq 401 -and
        (Call DELETE "$api/api/v1/services/$([guid]::NewGuid())").Status -eq 401 -and
        (Call GET "$api/api/v1/account").Status -eq 401 -and
        (Call GET "$api/api/v1/webhooks").Status -eq 401 -and
        (Call POST "$api/api/v1/alerts/test").Status -eq 401
    }
    Check "The token opens the account" { (Call GET "$api/api/v1/account" -Token $ana.Token).Json.email -eq "ana@example.com" }
    Check "Login with the right password gives a second, different token" {
        $r = Call POST "$api/api/v1/auth/login" -Body @{ email = "ANA@example.com"; password = $password }
        $r.Status -eq 200 -and $r.Json.token -ne $ana.Token
    }
    Check "Wrong password and unknown e-mail get the same 401 answer" {
        $a = Call POST "$api/api/v1/auth/login" -Body @{ email = "ana@example.com"; password = "wrong password here" }
        $b = Call POST "$api/api/v1/auth/login" -Body @{ email = "nobody@example.com"; password = "wrong password here" }
        $a.Status -eq 401 -and $b.Status -eq 401 -and $a.Json.detail -eq $b.Json.detail
    }

    Write-Host "-- e-mail confirmation (the link is read from the log, as an owner without a mail server would)"
    Check "The confirmation link is written to the log" { Wait-Until { (Log-Text $strict) -match "verify-email\?token=[\w-]+" } 5 }
    $verifyToken = [regex]::Match((Log-Text $strict), "ana@example.com[^\r\n]*verify-email\?token=([\w-]+)").Groups[1].Value
    Check "A wrong confirmation token is refused" { (Call POST "$api/api/v1/auth/verify-email" -Body @{ token = "nope" }).Status -eq 400 }
    Check "The right token confirms the address" {
        (Call POST "$api/api/v1/auth/verify-email" -Body @{ token = $verifyToken }).Status -in 200, 204 -and (Call GET "$api/api/v1/account" -Token $ana.Token).Json.emailVerified
    }
    Check "The same token cannot be used twice" { (Call POST "$api/api/v1/auth/verify-email" -Body @{ token = $verifyToken }).Status -eq 400 }

    Write-Host "-- password reset"
    Check "Forgot-password answers 202 for a known and an unknown address alike" {
        (Call POST "$api/api/v1/auth/forgot-password" -Body @{ email = "bob@example.com" }).Status -eq 202 -and (Call POST "$api/api/v1/auth/forgot-password" -Body @{ email = "ghost@example.com" }).Status -eq 202
    }
    $resetToken = [regex]::Match((Log-Text $strict), "bob@example.com[^\r\n]*reset-password\?token=([\w-]+)").Groups[1].Value
    Check "Only the known address got a reset link in the log" { $resetToken.Length -gt 20 -and (Log-Text $strict) -notmatch "ghost@example.com" }
    Check "A too short new password is refused and the link stays valid" {
        (Call POST "$api/api/v1/auth/reset-password" -Body @{ token = $resetToken; newPassword = "short" }).Status -eq 400
    }
    Check "The reset works once, closes every session and the old password stops working" {
        $r = Call POST "$api/api/v1/auth/reset-password" -Body @{ token = $resetToken; newPassword = "a brand new passphrase" }
        $r.Status -in 200, 204 -and (Call GET "$api/api/v1/account" -Token $bob.Token).Status -eq 401 -and
        (Call POST "$api/api/v1/auth/login" -Body @{ email = "bob@example.com"; password = $password }).Status -eq 401 -and
        (Call POST "$api/api/v1/auth/login" -Body @{ email = "bob@example.com"; password = "a brand new passphrase" }).Status -eq 200
    }
    Check "The reset link cannot be used twice" { (Call POST "$api/api/v1/auth/reset-password" -Body @{ token = $resetToken; newPassword = "yet another passphrase" }).Status -eq 400 }
    $bob = [pscustomobject]@{ Token = (Call POST "$api/api/v1/auth/login" -Body @{ email = "bob@example.com"; password = "a brand new passphrase" }).Json.token; Email = "bob@example.com"; Slug = "bob-corp" }

    Write-Host "-- logout and password change"
    Check "Logout closes only that session" {
        $second = (Call POST "$api/api/v1/auth/login" -Body @{ email = "ana@example.com"; password = $password }).Json.token
        (Call POST "$api/api/v1/auth/logout" -Token $second).Status -eq 204 -and (Call GET "$api/api/v1/account" -Token $second).Status -eq 401 -and (Call GET "$api/api/v1/account" -Token $ana.Token).Status -eq 200
    }
    Check "Changing the password needs the current one" {
        (Call POST "$api/api/v1/account/password" -Token $ana.Token -Body @{ currentPassword = "not the password"; newPassword = "another long passphrase" }).Status -eq 400
    }

    Write-Host "-- SSRF: every address pointing inside the network must be refused"
    $blocked = @(
        "http://127.0.0.1/", "http://localhost/", "http://169.254.169.254/latest/meta-data/", "http://0x7f.1/", "http://2130706433/",
        "http://[::1]/", "http://[::ffff:127.0.0.1]/", "http://10.0.0.1/", "http://192.168.1.1/", "http://172.16.0.1/", "http://100.64.0.1/",
        "http://0.0.0.0/", "https://example.com:22/", "https://example.com:6379/", "ftp://example.com/", "file:///etc/passwd",
        "https://user:secret@example.com/", "javascript:alert(1)", "not a url")
    if (-not $SkipInternet) { $blocked += "http://localtest.me/" }
    foreach ($u in $blocked) {
        Check "Refused for a service: $u" {
            $r = Call POST "$api/api/v1/services" -Token $ana.Token -Body @{ name = "ssrf"; url = $u; intervalSeconds = 300; timeoutSeconds = 5; expectedStatusCode = 200 }
            $r.Status -eq 400 -and $null -ne $r.Json.errors.url
        }
    }
    foreach ($u in @("http://127.0.0.1:9190/hook", "http://169.254.169.254/", "http://localhost/hook", "http://10.0.0.1/")) {
        Check "Refused for a webhook: $u" {
            $r = Call POST "$api/api/v1/webhooks" -Token $ana.Token -Body @{ url = $u }
            $r.Status -eq 400 -and $null -ne $r.Json.errors.url
        }
    }
    Check "None of the refused addresses was stored" { (Call GET "$api/api/v1/services" -Token $ana.Token).Json.Count -eq 0 -and (Call GET "$api/api/v1/webhooks" -Token $ana.Token).Json.Count -eq 0 }

    Write-Host "-- input validation"
    Check "Bad fields -> 400 with one error per field" {
        $r = Call POST "$api/api/v1/services" -Token $ana.Token -Body @{ name = " "; url = "https://example.com/"; intervalSeconds = 5; timeoutSeconds = 99; expectedStatusCode = 42 }
        $e = $r.Json.errors
        $r.Status -eq 400 -and $e.name -and $e.intervalSeconds -and $e.timeoutSeconds -and $e.expectedStatusCode
    }
    Check "A 2 MB name -> 400 or 413 (not 500)" {
        (Call POST "$api/api/v1/services" -Token $ana.Token -Body @{ name = ("x" * 2000000); url = "https://example.com/" }).Status -in 400, 413
    }
    Check "Unknown id -> 404 on read, update, delete and checks" {
        $id = [guid]::NewGuid()
        (Call GET "$api/api/v1/services/$id" -Token $ana.Token).Status -eq 404 -and (Call DELETE "$api/api/v1/services/$id" -Token $ana.Token).Status -eq 404 -and
        (Call GET "$api/api/v1/services/$id/checks" -Token $ana.Token).Status -eq 404 -and
        (Call PUT "$api/api/v1/services/$id" -Token $ana.Token -Body @{ name = "x"; url = "https://example.com/"; intervalSeconds = 300; timeoutSeconds = 5; expectedStatusCode = 200; isPublic = $true }).Status -eq 404
    }
    Check "A non-GUID id -> 404 (not 500)" { (Call GET "$api/api/v1/services/abc" -Token $ana.Token).Status -eq 404 }

    Write-Host "-- plan limits (free plan: 5 services, one check every 5 minutes at most)"
    Check "A check every 60 seconds is refused on the free plan" {
        $r = Call POST "$api/api/v1/services" -Token $ana.Token -Body @{ name = "Fast"; url = "https://example.com/"; intervalSeconds = 60; timeoutSeconds = 5; expectedStatusCode = 200 }
        $r.Status -eq 400 -and $null -ne $r.Json.errors.intervalSeconds
    }
    $ids = @()
    foreach ($i in 1..5) {
        $r = Call POST "$api/api/v1/services" -Token $ana.Token -Body @{ name = "Service $i"; url = "https://example.com/p$i"; intervalSeconds = 300; timeoutSeconds = 5; expectedStatusCode = 200; isPublic = ($i -ne 5) }
        $ids += $r.Json.id
    }
    Check "Five services are accepted" { @($ids | Where-Object { $_ }).Count -eq 5 }
    Check "The sixth is refused with a message about the plan" {
        $r = Call POST "$api/api/v1/services" -Token $ana.Token -Body @{ name = "Sixth"; url = "https://example.com/p6"; intervalSeconds = 300; timeoutSeconds = 5; expectedStatusCode = 200 }
        $r.Status -eq 400 -and $r.Text -match "Free|plan"
    }
    Check "The account shows 5 of 5 services used" { $u = (Call GET "$api/api/v1/account" -Token $ana.Token).Json.usage; $u.servicesUsed -eq 5 -and $u.servicesLimit -eq 5 }
    Check "Switching to Pro lifts the limits, and back to Free is refused while 5+ services exist only if over the limit" {
        $up = Call PUT "$api/api/v1/account/plan" -Token $ana.Token -Body @{ plan = "Pro" }
        $up.Status -eq 200 -and $up.Json.plan -eq "Pro" -and (Call POST "$api/api/v1/services" -Token $ana.Token -Body @{ name = "Sixth"; url = "https://example.com/p6"; intervalSeconds = 60; timeoutSeconds = 5; expectedStatusCode = 200 }).Status -eq 201
    }
    Check "Going back to Free with 6 services is refused" { (Call PUT "$api/api/v1/account/plan" -Token $ana.Token -Body @{ plan = "Free" }).Status -eq 400 }

    Write-Host "-- isolation: one customer must never see or touch another's data"
    $anaServices = (Call GET "$api/api/v1/services" -Token $ana.Token).Json
    $anaId = $anaServices[0].id
    $bobService = (Call POST "$api/api/v1/services" -Token $bob.Token -Body @{ name = "Bob only"; url = "https://example.org/"; intervalSeconds = 300; timeoutSeconds = 5; expectedStatusCode = 200 }).Json
    Check "Bob's list holds only Bob's service" { $l = @((Call GET "$api/api/v1/services" -Token $bob.Token).Json); $l.Count -eq 1 -and $l[0].name -eq "Bob only" }
    Check "Bob cannot read Ana's service (404, not 403: its existence stays secret)" { (Call GET "$api/api/v1/services/$anaId" -Token $bob.Token).Status -eq 404 }
    Check "Bob cannot read Ana's checks" { (Call GET "$api/api/v1/services/$anaId/checks" -Token $bob.Token).Status -eq 404 }
    Check "Bob cannot change Ana's service" {
        (Call PUT "$api/api/v1/services/$anaId" -Token $bob.Token -Body @{ name = "Hacked"; url = "https://example.org/"; intervalSeconds = 300; timeoutSeconds = 5; expectedStatusCode = 200; isPublic = $true }).Status -eq 404 -and
        (Call GET "$api/api/v1/services/$anaId" -Token $ana.Token).Json.name -ne "Hacked"
    }
    Check "Bob cannot delete Ana's service" { (Call DELETE "$api/api/v1/services/$anaId" -Token $bob.Token).Status -eq 404 -and (Call GET "$api/api/v1/services/$anaId" -Token $ana.Token).Status -eq 200 }
    Check "Ana cannot touch Bob's service either" { (Call DELETE "$api/api/v1/services/$($bobService.id)" -Token $ana.Token).Status -eq 404 -and (Call GET "$api/api/v1/services/$($bobService.id)" -Token $bob.Token).Status -eq 200 }
    $anaHook = Call POST "$api/api/v1/webhooks" -Token $ana.Token -Body @{ url = "https://example.org/hooks/ana" }
    Check "A webhook is created with a one-time secret" { $anaHook.Status -eq 201 -and $anaHook.Json.secret -match "^whsec_[0-9a-f]{48}$" }
    Check "The secret is never shown again, in any list" {
        $l = Call GET "$api/api/v1/webhooks" -Token $ana.Token
        $l.Status -eq 200 -and @($l.Json).Count -eq 1 -and $l.Text -notmatch "whsec_|secret"
    }
    Check "Bob sees no webhook of Ana's and cannot delete it" {
        @((Call GET "$api/api/v1/webhooks" -Token $bob.Token).Json).Count -eq 0 -and (Call DELETE "$api/api/v1/webhooks/$($anaHook.Json.webhook.id)" -Token $bob.Token).Status -eq 404
    }
    Check "The secret is encrypted in the database file (the plain text is not in it)" {
        # The API holds the file open: read it (and its write-ahead log) with sharing allowed.
        $text = ""
        foreach ($f in @($strictDb, "$strictDb-wal")) {
            if (-not (Test-Path $f)) { continue }
            $stream = New-Object IO.FileStream($f, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            try { $buffer = New-Object byte[] $stream.Length; $null = $stream.Read($buffer, 0, $buffer.Length); $text += [Text.Encoding]::Latin1.GetString($buffer) } finally { $stream.Dispose() }
        }
        $text.Length -gt 0 -and -not $text.Contains($anaHook.Json.secret) -and -not $text.Contains($password)
    }
    Check "Each page lists only its own services" {
        $pa = Call GET "$api/api/v1/public/ana-sarl/status"
        $pb = Call GET "$api/api/v1/public/bob-corp/status"
        $pa.Status -eq 200 -and $pb.Status -eq 200 -and $pa.Text -match "Service 1" -and $pa.Text -notmatch "Bob only" -and $pb.Text -match "Bob only" -and $pb.Text -notmatch "Service 1"
    }
    Check "A service hidden from the public page does not appear on it" { (Call GET "$api/api/v1/public/ana-sarl/status").Text -notmatch "Service 5" }
    Check "A public page never reveals a monitored address or an e-mail" {
        $t = (Call GET "$api/api/v1/public/ana-sarl/status").Text
        $t -notmatch "example\.com" -and $t -notmatch "ana@"
    }
    Check "An unknown public page -> 404" { (Call GET "$api/api/v1/public/does-not-exist/status").Status -eq 404 }
    Check "A page of the wrong shape -> 404 (not 500)" { (Call GET "$api/api/v1/public/%00/status").Status -in 400, 404 }

    if ($SkipInternet) { Skip "Real public site is monitored" "-SkipInternet" }
    else {
        Write-Host "-- a real public site, real DNS, real TLS"
        $created = Call POST "$api/api/v1/services" -Token $bob.Token -Body @{ name = "Example"; url = "https://example.com/"; intervalSeconds = 300; timeoutSeconds = 10; expectedStatusCode = 200 }
        Check "Public address accepted (201)" { $created.Status -eq 201 -and $created.Json.status -eq "Unknown" }
        $exampleId = $created.Json.id
        Check "Becomes Up after the first automatic check" { Wait-Until { ((Call GET "$api/api/v1/services/$exampleId" -Token $bob.Token).Json.status) -eq "Up" } 25 }
        Check "The check recorded status 200 and a positive latency" {
            $c = (Call GET "$api/api/v1/services/$exampleId/checks" -Token $bob.Token).Json
            @($c).Count -ge 1 -and $c[0].success -and $c[0].statusCode -eq 200 -and $c[0].latencyMs -gt 0
        }
        Check "The public page shows 100 percent uptime" {
            $svc = (Call GET "$api/api/v1/public/bob-corp/status").Json.services | Where-Object { $_.name -eq "Example" }
            $svc.status -eq "Up" -and $svc.uptimePercent -eq 100
        }
    }

    Write-Host "-- persistence across a restart"
    Stop-App $strict
    $strict = Start-Api "strict2" 5180 $strictDb $strictEnv
    Check "After restart the sessions and the services are still there" {
        (Call GET "$api/api/v1/account" -Token $ana.Token).Status -eq 200 -and @((Call GET "$api/api/v1/services" -Token $ana.Token).Json).Count -eq 6
    }
    Check "The webhook secret still decrypts after a restart (a test alert reaches the channel list)" {
        $l = @((Call GET "$api/api/v1/alerts" -Token $ana.Token).Json | ForEach-Object { $_.name })
        ($l -join ",") -match "Webhook"
    }

    Write-Host "-- lockout (5 wrong passwords lock the account for a while, even with the right password)"
    $victim = New-Customer $api "victim@example.com" "victim-co"
    1..5 | ForEach-Object { Call POST "$api/api/v1/auth/login" -Body @{ email = "victim@example.com"; password = "wrong wrong wrong" } | Out-Null }
    Check "The right password is refused while the account is locked, with the same message" {
        $r = Call POST "$api/api/v1/auth/login" -Body @{ email = "victim@example.com"; password = $password }
        $r.Status -eq 401 -and $r.Json.detail -match "locked"
    }

    Write-Host "-- deleting an account"
    $leaver = New-Customer $api "leaver@example.com" "leaver-co"
    Call POST "$api/api/v1/services" -Token $leaver.Token -Body @{ name = "Gone soon"; url = "https://example.com/gone"; intervalSeconds = 300; timeoutSeconds = 5; expectedStatusCode = 200 } | Out-Null
    Check "Deleting needs the password" { (Call POST "$api/api/v1/account/delete" -Token $leaver.Token -Body @{ password = "not my password" }).Status -eq 400 }
    Check "Deleting works, ends every session and removes the public page" {
        (Call POST "$api/api/v1/account/delete" -Token $leaver.Token -Body @{ password = $password }).Status -in 200, 204 -and
        (Call GET "$api/api/v1/account" -Token $leaver.Token).Status -eq 401 -and (Call GET "$api/api/v1/public/leaver-co/status").Status -eq 404 -and
        (Call POST "$api/api/v1/auth/login" -Body @{ email = "leaver@example.com"; password = $password }).Status -eq 401
    }
    Check "The address name and the e-mail can be used again afterwards" { (New-Customer $api "leaver@example.com" "leaver-co").Status -eq 201 }
    Check "The other customers are untouched" { @((Call GET "$api/api/v1/services" -Token $ana.Token).Json).Count -eq 6 }
    Check "The strict log has no unhandled error and no secret" {
        $log = Log-Text $strict
        $log -notmatch "Unhandled|crit:" -and $log -notmatch "\sat Pulse\." -and $log -notmatch [regex]::Escape($anaHook.Json.secret) -and $log -notmatch [regex]::Escape($password)
    }

    # ------------------------------------------------------------------ D
    Write-Section "D. Real web site"
    $web = Start-Dll "web" $webDll "http://127.0.0.1:5190" @{ ASPNETCORE_ENVIRONMENT = "Production"; Api__BaseUrl = $api }
    if (-not (Wait-Until { (Call GET "http://127.0.0.1:5190/").Status -eq 200 } 25)) { throw "Web did not start. $(Log-Text $web)" }
    $w = "http://127.0.0.1:5190"
    Check "Home page renders (200) and offers sign-up and login" {
        $r = Call GET "$w/"
        $r.Status -eq 200 -and $r.Text -match 'href="/signup"' -and $r.Text -match 'href="/login"' -and $r.Text -match "<h1>"
    }
    Check "Sign-up and login pages render with their form" {
        $s = Call GET "$w/signup"; $l = Call GET "$w/login"
        $s.Status -eq 200 -and $s.Text -match 'type="password"' -and $l.Status -eq 200 -and $l.Text -match 'type="password"'
    }
    Check "Forgot-password page renders" { (Call GET "$w/forgot-password").Status -eq 200 }
    Check "The privacy page renders" { $r = Call GET "$w/confidentialite"; $r.Status -eq 200 -and $r.Text -match "<h1>" }
    Check "The dashboard asks for a login instead of showing anything" {
        $r = Call GET "$w/app"
        $r.Status -in 200, 302, 401 -and $r.Text -notmatch "Ana SARL|Service 1"
    }
    Check "The account and alerts pages are protected too" {
        (Call GET "$w/app/account").Text -notmatch "ana@example.com" -and (Call GET "$w/app/alerts").Text -notmatch "whsec_"
    }
    Check "The public page of a customer renders with its services, without the monitored addresses" {
        $r = Call GET "$w/s/ana-sarl"
        $r.Status -eq 200 -and $r.Text -match "Ana SARL" -and $r.Text -match "Service 1" -and $r.Text -notmatch "example\.com/p1" -and $r.Text -notmatch "Service 5"
    }
    Check "The public page of another customer does not leak into it" { (Call GET "$w/s/bob-corp").Text -notmatch "Service 1" }
    Check "An unknown public page answers 404 with a helpful message" { $r = Call GET "$w/s/nobody-here"; $r.Status -eq 404 -and $r.Text -match "n'existe pas" }
    Check "Pages set a Content-Security-Policy and nosniff" {
        $h = (Call GET "$w/s/ana-sarl").Headers
        $h["Content-Security-Policy"] -match "default-src 'self'" -and $h["X-Content-Type-Options"] -eq "nosniff"
    }
    Check "No page contains a session token or a password" {
        $all = (Call GET "$w/").Text + (Call GET "$w/login").Text + (Call GET "$w/s/ana-sarl").Text
        $all -notmatch [regex]::Escape($ana.Token) -and $all -notmatch [regex]::Escape($password)
    }
    Check "Unknown page -> 404" { (Call GET "$w/does-not-exist").Status -eq 404 }
    Stop-App $strict
    Check "With the API down, a public page stays up and says so (no stack trace)" {
        $r = Call GET "$w/s/ana-sarl"
        $r.Status -eq 200 -and $r.Text -match "pas disponible" -and $r.Text -notmatch "Exception|at Pulse"
    }
    Check "With the API down, the home page still renders" { (Call GET "$w/").Status -eq 200 }
    Stop-App $web

    # ------------------------------------------------------------------ rate limits
    Write-Section "E. Rate limiting (global: 20 requests a minute; login door: 4 a minute)"
    $limited = Start-Api "limited" 5181 (Join-Path $tmp "limited.db") @{ Pulse__RateLimitPerMinute = "20" }
    $codes = 1..40 | ForEach-Object { (Call GET "http://127.0.0.1:5181/health").Status }
    $tooMany = @($codes | Where-Object { $_ -eq 429 }).Count
    Check "Requests above the limit get 429" { $tooMany -ge 15 }
    Check "The first 429 comes right after the 20th request (the start-up probe counts too)" { $codes.IndexOf(429) -ge 16 -and $codes.IndexOf(429) -le 21 }
    Stop-App $limited

    $guarded = Start-Api "guarded" 5183 (Join-Path $tmp "guarded.db") @{ Pulse__AuthRateLimitPerMinute = "4" }
    $attempts = 1..8 | ForEach-Object { (Call POST "http://127.0.0.1:5183/api/v1/auth/login" -Body @{ email = "guess@example.com"; password = "guess number $_ here" }).Status }
    Check "Password guessing is cut off with 429 after a few tries" { @($attempts | Where-Object { $_ -eq 429 }).Count -ge 3 -and $attempts[0] -eq 401 }
    Check "The rest of the API is not slowed by the login limit" { (Call GET "http://127.0.0.1:5183/health").Status -eq 200 }
    Stop-App $guarded

    # ------------------------------------------------------------------ C
    Write-Section "C. Incidents and alerts per customer (lab: private targets allowed, 2 failures = down, mail sink on port $smtpPort)"
    $flaky = Start-Dll "flaky" $flakyDll $null @{ ASPNETCORE_ENVIRONMENT = "Production" } @("$flakyPort", "$smtpPort")
    if (-not (Wait-Until { (Call GET "http://127.0.0.1:$flakyPort/ok").Status -eq 200 } 25)) { throw "Flaky target did not start. $(Log-Text $flaky)" }
    $lab = Start-Api "lab" 5182 (Join-Path $tmp "lab.db") @{
        Pulse__AllowPrivateTargets = "true"; Pulse__FailureThreshold = "2"; Pulse__RateLimitPerMinute = "100000"
        Pulse__Accounts__AutoVerifyEmail = "true"
        Pulse__Mail__Host = "127.0.0.1"; Pulse__Mail__Port = "$smtpPort"; Pulse__Mail__Security = "None"; Pulse__Mail__From = "pulse@example.com"
        Pulse__Alerts__RetryDelaySeconds = "2"
    }
    $L = "http://127.0.0.1:5182"
    function Add-Service($token, $name, $url, $timeout = 5) {
        (Call POST "$L/api/v1/services" -Token $token -Body @{ name = $name; url = $url; intervalSeconds = 30; timeoutSeconds = $timeout; expectedStatusCode = 200 }).Json
    }
    function Get-Service($token, $id) { (Call GET "$L/api/v1/services/$id" -Token $token).Json }
    function Get-Checks($token, $id) { (Call GET "$L/api/v1/services/$id/checks" -Token $token).Json }
    function Get-Hooks { $r = (Call GET "http://127.0.0.1:$flakyPort/hook/received").Json; if ($null -ne $r) { @($r) } }
    function Get-Mails { $r = (Call GET "http://127.0.0.1:$flakyPort/smtp/received").Json; if ($null -ne $r) { @($r) } }

    $eva = New-Customer $L "eva@example.com" "eva-studio" "Eva Studio"
    $finn = New-Customer $L "finn@example.com" "finn-labs" "Finn Labs"
    Call PUT "$L/api/v1/account/plan" -Token $eva.Token -Body @{ plan = "Pro" } | Out-Null
    Call PUT "$L/api/v1/account/plan" -Token $finn.Token -Body @{ plan = "Pro" } | Out-Null
    Check "With the mail server on and auto-confirmation, both accounts are confirmed and can receive alerts" {
        (Call GET "$L/api/v1/account" -Token $eva.Token).Json.emailVerified -and (Call GET "$L/api/v1/account" -Token $eva.Token).Json.mailAvailable
    }

    Write-Host "-- alert channels: Eva has a webhook and e-mail, Finn has e-mail only"
    $evaHook = Call POST "$L/api/v1/webhooks" -Token $eva.Token -Body @{ url = "http://127.0.0.1:$flakyPort/hook" }
    $secret = $evaHook.Json.secret
    Check "Eva's webhook is created and signed with its own secret" { $evaHook.Status -eq 201 -and $secret -match "^whsec_" }
    Check "Eva sees two channels, Finn sees one" {
        $e = @((Call GET "$L/api/v1/alerts" -Token $eva.Token).Json | ForEach-Object { $_.name })
        $f = @((Call GET "$L/api/v1/alerts" -Token $finn.Token).Json | ForEach-Object { $_.name })
        $e.Count -eq 2 -and $f.Count -eq 1 -and $f[0] -eq "E-mail"
    }
    $evaTest = (Call POST "$L/api/v1/alerts/test" -Token $eva.Token).Json
    Check "Eva's test alert reaches both channels" { @($evaTest | Where-Object { $_.success }).Count -eq 2 }
    Check "The webhook received a signed test event" {
        $h = @(Get-Hooks)
        $h.Count -eq 1 -and $h[0].event -eq "test" -and $h[0].signature -eq (Get-Signature $secret $h[0].timestamp $h[0].body)
    }
    Check "The mail server received the test message for Eva only" {
        $m = @(Get-Mails)
        $m.Count -eq 1 -and $m[0].to -contains "eva@example.com" -and $m[0].from -eq "pulse@example.com"
    }
    Check "Turning off e-mail alerts leaves Finn with no channel, and the test says nothing instead of failing" {
        (Call PUT "$L/api/v1/account/notifications" -Token $finn.Token -Body @{ emailAlerts = $false }).Status -eq 200 -and
        (Count-Of (Call GET "$L/api/v1/alerts" -Token $finn.Token).Json) -eq 0 -and (Call POST "$L/api/v1/alerts/test" -Token $finn.Token).Status -eq 200
    }
    Call PUT "$L/api/v1/account/notifications" -Token $finn.Token -Body @{ emailAlerts = $true } | Out-Null
    Call DELETE "http://127.0.0.1:$flakyPort/hook/received" | Out-Null
    Call DELETE "http://127.0.0.1:$flakyPort/smtp/received" | Out-Null

    $evaSvc = Add-Service $eva.Token "Eva Flaky" "http://127.0.0.1:$flakyPort/flaky"
    $finnSvc = Add-Service $finn.Token "Finn Flaky" "http://127.0.0.1:$flakyPort/flaky"
    Check "Both targets become Up" { Wait-Until { (Get-Service $eva.Token $evaSvc.id).status -eq "Up" -and (Get-Service $finn.Token $finnSvc.id).status -eq "Up" } 25 }
    Check "No alert is sent while everything works" { @(Get-Hooks).Count -eq 0 -and @(Get-Mails).Count -eq 0 }
    Write-Host "-- the target starts failing (500); waiting for 2 failed checks, about 30 to 65 seconds"
    Call POST "http://127.0.0.1:$flakyPort/hook/fail/1" | Out-Null   # the first webhook call will fail: Pulse must retry
    Call POST "http://127.0.0.1:$flakyPort/control/500" | Out-Null
    Check "After 2 failures in a row both services are Down" { Wait-Until { (Get-Service $eva.Token $evaSvc.id).status -eq "Down" -and (Get-Service $finn.Token $finnSvc.id).status -eq "Down" } 120 3 }
    Check "Each public page shows its own open incident, with the cause, and not the other's" {
        $e = (Call GET "$L/api/v1/public/eva-studio/status").Json
        $f = (Call GET "$L/api/v1/public/finn-labs/status").Json
        $ei = @($e.incidents | Where-Object { -not $_.resolvedAt }); $fi = @($f.incidents | Where-Object { -not $_.resolvedAt })
        $ei.Count -eq 1 -and $ei[0].serviceName -eq "Eva Flaky" -and $ei[0].cause -match "500" -and $fi.Count -eq 1 -and $fi[0].serviceName -eq "Finn Flaky" -and $e.overall -eq "MajorOutage"
    }
    Check "Eva's outage was announced by webhook once, despite one failed delivery (retry), and Finn's never was" {
        $null = Wait-Until { @(Get-Hooks | Where-Object { $_.event -eq "incident.opened" }).Count -ge 1 } 25
        Start-Sleep -Seconds 3
        $opened = @(Get-Hooks | Where-Object { $_.event -eq "incident.opened" })
        $opened.Count -eq 1 -and (($opened[0].body | ConvertFrom-Json).service.name) -eq "Eva Flaky"
    }
    Check "The webhook signature is valid and the body carries the service and the cause, never the secret" {
        $h = Get-Hooks | Where-Object { $_.event -eq "incident.opened" } | Select-Object -First 1
        $b = $h.body | ConvertFrom-Json
        $h.signature -eq (Get-Signature $secret $h.timestamp $h.body) -and $b.incident.cause -match "500" -and $h.body -notmatch $secret
    }
    Check "A tampered body would not match its signature" {
        $h = Get-Hooks | Where-Object { $_.event -eq "incident.opened" } | Select-Object -First 1
        $h.signature -ne (Get-Signature $secret $h.timestamp ($h.body + " "))
    }
    Check "Each owner got an e-mail about their own service only" {
        $null = Wait-Until { @(Get-Mails).Count -ge 2 } 25
        $m = @(Get-Mails)
        $toEva = @($m | Where-Object { $_.to -contains "eva@example.com" }); $toFinn = @($m | Where-Object { $_.to -contains "finn@example.com" })
        $m.Count -eq 2 -and $toEva.Count -eq 1 -and $toFinn.Count -eq 1 -and $toEva[0].data -match "Eva Flaky" -and $toEva[0].data -notmatch "Finn Flaky" -and $toFinn[0].data -match "Finn Flaky" -and $toFinn[0].data -notmatch "Eva Flaky"
    }
    Write-Host "-- the target recovers (200); waiting for the next check, up to 35 seconds"
    Call POST "http://127.0.0.1:$flakyPort/control/200" | Out-Null
    Check "One success brings both services back Up" { Wait-Until { (Get-Service $eva.Token $evaSvc.id).status -eq "Up" -and (Get-Service $finn.Token $finnSvc.id).status -eq "Up" } 60 3 }
    Check "Recoveries were announced to the right people, with no duplicate of the first alerts" {
        $null = Wait-Until { @(Get-Hooks | Where-Object { $_.event -eq "incident.resolved" }).Count -ge 1 -and @(Get-Mails).Count -ge 4 } 40 2
        $events = @(Get-Hooks | ForEach-Object { $_.event }) | Sort-Object
        ($events -join ",") -eq "incident.opened,incident.resolved" -and @(Get-Mails).Count -eq 4 -and @(Get-Mails | Where-Object { $_.data -match "fonctionne de nouveau" }).Count -eq 2
    }
    Check "Each incident is resolved, with an end time after its start, and exactly one per outage" {
        $inc = @((Call GET "$L/api/v1/public/eva-studio/status").Json.incidents)
        $inc.Count -eq 1 -and $inc[0].resolvedAt -and ([datetime]$inc[0].resolvedAt) -ge ([datetime]$inc[0].startedAt)
    }
    Check "Uptime is below 100 percent after the outage" { ((Call GET "$L/api/v1/public/eva-studio/status").Json.services | Where-Object { $_.name -eq "Eva Flaky" }).uptimePercent -lt 100 }

    Write-Host "-- timeout and redirect"
    $slow = Add-Service $eva.Token "Slow" "http://127.0.0.1:$flakyPort/slow?ms=8000" 1
    $redirect = Add-Service $eva.Token "Redirect" "http://127.0.0.1:$flakyPort/redirect"
    Check "A slow target fails with a timeout message" {
        $null = Wait-Until { (Get-Service $eva.Token $slow.id).lastCheckedAt } 15
        $c = (Get-Checks $eva.Token $slow.id)[0]
        -not $c.success -and $c.error -match "No answer within 1 s" -and $c.latencyMs -lt 4000
    }
    Check "A redirect is reported as 302 and NOT followed" {
        $null = Wait-Until { (Get-Service $eva.Token $redirect.id).lastCheckedAt } 15
        $c = (Get-Checks $eva.Token $redirect.id)[0]
        -not $c.success -and $c.statusCode -eq 302 -and $c.error -match "got 302"
    }

    Write-Host "-- concurrency: 15 services each taking 2 seconds"
    $started = Get-Date
    $load = 1..15 | ForEach-Object { Add-Service $finn.Token "Load $_" "http://127.0.0.1:$flakyPort/slow?ms=2000" 10 }
    $allChecked = Wait-Until { @($load | Where-Object { -not (Get-Service $finn.Token $_.id).lastCheckedAt }).Count -eq 0 } 40 1
    $elapsed = ((Get-Date) - $started).TotalSeconds
    Write-Host ("   all 15 checked after {0:N1} s (one by one it would take at least 30 s)" -f $elapsed)
    Check "All 15 services were checked" { $allChecked }
    Check "They ran in parallel (well under 30 s)" { $allChecked -and $elapsed -lt 20 }
    Check "Every load check succeeded" { @($load | Where-Object { -not (Get-Checks $finn.Token $_.id)[0].success }).Count -eq 0 }

    Write-Host "-- deleting a service while its check is running"
    $doomed = Add-Service $eva.Token "Doomed" "http://127.0.0.1:$flakyPort/slow?ms=6000" 10
    $beforeLoad = (Get-Service $finn.Token $load[0].id).lastCheckedAt
    Start-Sleep -Seconds 2
    $del = (Call DELETE "$L/api/v1/services/$($doomed.id)" -Token $eva.Token).Status
    Start-Sleep -Seconds 9
    Check "Delete answers 204" { $del -eq 204 }
    Check "The API is still healthy afterwards" { (Call GET "$L/health").Status -eq 200 }
    Write-Host "   waiting for the next round of checks (up to 45 s)"
    Check "The scheduler survived: other services keep being checked" { Wait-Until { (Get-Service $finn.Token $load[0].id).lastCheckedAt -ne $beforeLoad } 45 3 }

    Write-Host "-- deleting an account while its service is being checked"
    $bye = New-Customer $L "bye@example.com" "bye-co"
    Call PUT "$L/api/v1/account/plan" -Token $bye.Token -Body @{ plan = "Pro" } | Out-Null
    Add-Service $bye.Token "Slow goodbye" "http://127.0.0.1:$flakyPort/slow?ms=5000" 10 | Out-Null
    Start-Sleep -Seconds 3
    Check "Account deletion during a running check answers fine and the API stays healthy" {
        (Call POST "$L/api/v1/account/delete" -Token $bye.Token -Body @{ password = $password }).Status -in 200, 204 -and (Call GET "$L/health").Status -eq 200
    }
    Start-Sleep -Seconds 7
    Check "The lab log has no unhandled error" { (Log-Text $lab) -notmatch "Unhandled|Scheduler round failed|crit:" }
    Check "The lab log has no stack trace" { (Log-Text $lab) -notmatch "\sat Pulse\." }
    Check "The lab log holds neither a password nor a webhook secret" { $log = Log-Text $lab; $log -notmatch [regex]::Escape($password) -and $log -notmatch [regex]::Escape($secret) }
    Stop-App $lab
    Stop-App $flaky
}
catch {
    $script:failed++
    Write-Host "[FAIL] The run was interrupted -- $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    foreach ($p in $script:procs) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
    $http.Dispose()
    Start-Sleep -Milliseconds 500
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host ("Result: {0} passed, {1} failed, {2} skipped" -f $script:passed, $script:failed, $script:skipped) -ForegroundColor $(if ($script:failed -eq 0) { "Green" } else { "Red" })
exit $script:failed

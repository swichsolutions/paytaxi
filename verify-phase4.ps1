# Quick verification of park-admin scope + login lockout.
# Run from any PowerShell window: .\verify-phase4.ps1
$env:PGPASSWORD = "postgres"

Write-Host ""
Write-Host "=== 1. Park-admin scope ===" -ForegroundColor Cyan
$loginBody = '{"email":"manager@tbilisi-auto-park-3.local","password":"park2!"}'
$park = Invoke-RestMethod -Uri 'http://localhost:5196/api/admin/auth/login' -Method Post -ContentType 'application/json' -Body $loginBody
Write-Host "Logged in as $($park.admin.email) (role=$($park.admin.role))"
$headers = @{ Authorization = "Bearer $($park.token)" }
$parks = Invoke-RestMethod -Uri 'http://localhost:5196/api/admin/parks' -Headers $headers
Write-Host "Park-admin sees $($parks.Count) park(s): $($parks.name -join ', ')"
Write-Host "Expected: 1 park (Tbilisi Auto Park #3)" -ForegroundColor Yellow

Write-Host ""
Write-Host "=== 2. Login lockout ===" -ForegroundColor Cyan
$badBody = '{"email":"ops@swich.dev","password":"wrong"}'
1..6 | ForEach-Object {
    try {
        Invoke-RestMethod -Uri 'http://localhost:5196/api/admin/auth/login' -Method Post -ContentType 'application/json' -Body $badBody | Out-Null
        Write-Host ("Attempt {0}: unexpectedly succeeded" -f $_)
    } catch {
        Write-Host ("Attempt {0} -> {1}" -f $_, $_.Exception.Response.StatusCode)
    }
}
Write-Host "Expected: 1-5 Unauthorized, 6 Locked" -ForegroundColor Yellow

Write-Host ""
Write-Host "=== Unlocking ops account so future logins work ===" -ForegroundColor Cyan
& "C:\Program Files\PostgreSQL\17\bin\psql.exe" -U postgres -d paytaxi_dev -c 'UPDATE "AdminUsers" SET "LockedUntil"=NULL, "FailedLoginAttempts"=0 WHERE "Email"=''ops@swich.dev'';'

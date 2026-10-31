<#
.SYNOPSIS
Runs ApexPost with fictional demo data and working demo logins.

.DESCRIPTION
Applies the EF Core migrations to the database named in user-secrets
(ConnectionStrings:ApexPostDb, see scripts/setup-database.ps1), then uses the
"demo" launch profile (Development, DemoData:Initialize=true). The demo staff
password is read from user-secrets (DemoData:StaffPassword); if none is set, a
random one is generated and stored there. No password is ever written to the
repository.

.PARAMETER Password
Use this demo password instead of the stored or generated one.

.PARAMETER Reset
Revert all migrations first, which DELETES ALL DATA in that database, so the
seed and demo logins are recreated (needed for a changed password to take
effect).

.EXAMPLE
./scripts/run-demo.ps1
./scripts/run-demo.ps1 -Reset -Password 'my-local-demo-password'
#>
[CmdletBinding()]
param(
	[string]$Password,
	[switch]$Reset
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\src\ApexPost.Web')).Path
$infrastructure = (Resolve-Path (Join-Path $PSScriptRoot '..\src\ApexPost.Infrastructure')).Path
$secretKey = 'DemoData:StaffPassword'
$connectionKey = 'ConnectionStrings:ApexPostDb'

function New-DemoPassword {
	$alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
	$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
	$buffer = New-Object byte[] 1
	$chars = New-Object System.Collections.Generic.List[char]
	while ($chars.Count -lt 20) {
		$rng.GetBytes($buffer)
		# Rejection sampling keeps every character equally likely.
		if ($buffer[0] -lt (256 - (256 % $alphabet.Length))) {
			$chars.Add($alphabet[$buffer[0] % $alphabet.Length])
		}
	}
	-join $chars
}

function Get-StoredSecret([string]$key) {
	$prefix = "$key = "
	$line = dotnet user-secrets list --project $project |
		Where-Object { $_.StartsWith($prefix) } |
		Select-Object -First 1
	if ($line) { $line.Substring($prefix.Length) } else { $null }
}

function Update-Database([string]$target) {
	$arguments = @('ef', 'database', 'update')
	if ($target) { $arguments += $target }
	dotnet @arguments --connection $connection --project $infrastructure --startup-project $project --context ApexPostDbContext
	if ($LASTEXITCODE -ne 0) { throw 'Applying the database migrations failed.' }
}

$connection = Get-StoredSecret $connectionKey
if (-not $connection) {
	throw "User-secret $connectionKey is not set. Run ./scripts/setup-database.ps1 first."
}

if ($Reset) {
	Write-Host 'Reverting all migrations: every row in the database will be deleted.'
	Update-Database '0'
}
Update-Database

$stored = Get-StoredSecret $secretKey
if ($Password) {
	if ($stored -and $stored -ne $Password -and -not $Reset) {
		Write-Warning 'Existing demo logins keep their old password. Add -Reset to recreate them with the new one.'
	}
	dotnet user-secrets set $secretKey $Password --project $project | Out-Null
}
elseif (-not $stored) {
	$Password = New-DemoPassword
	dotnet user-secrets set $secretKey $Password --project $project | Out-Null
	Write-Host 'Generated a demo staff password and stored it in user-secrets.'
}
else {
	$Password = $stored
}

Write-Host ''
Write-Host 'ApexPost demo logins (branch DM01), all with the same password:'
Write-Host '  demo-staff-01  Counter Clerk'
Write-Host '  demo-staff-02  Branch Manager'
Write-Host '  demo-staff-03  Auditor'
Write-Host '  demo-staff-04  Administrator'
Write-Host "  Password: $Password"
Write-Host '  (demo-staff-05 to demo-staff-20 repeat the roles for branches DM02-DM05.)'
Write-Host ''
Write-Host 'Starting https://localhost:7034 - press Ctrl+C to stop.'

dotnet run --project $project --launch-profile demo

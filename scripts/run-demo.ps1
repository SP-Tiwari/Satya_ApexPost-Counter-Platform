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

With -ConnectionString the script runs against a disposable database instead,
for verification. It leaves user-secrets alone, and takes the password from the
DemoData__StaffPassword environment variable without displaying it.

DemoData__StaffPassword takes precedence over user-secrets on every path, because
the application reads environment variables first. When it is set, the script
says so, does not show it, and does not read or change the stored password.

.PARAMETER Password
Use this demo password instead of the stored or generated one.

.PARAMETER Reset
Revert all migrations first, which DELETES ALL DATA in that database, so the
seed and demo logins are recreated. It needs -ConnectionString, so it can only
reach a disposable database.

.PARAMETER ConnectionString
Use this existing database for the migrations and the application. Its name must
start with ApexPost_Test_, it must use Windows authentication, and its data and
log files must be directly in E:\SqlData\ApexPost\Tests. Create it there first:
the script checks this through master, and stops before migrating if the database
is missing or elsewhere, because EF Core would otherwise create a missing database
in the instance's default location.

.EXAMPLE
./scripts/run-demo.ps1
./scripts/run-demo.ps1 -Password 'my-local-demo-password'
$env:DemoData__StaffPassword = '<generated password>'
./scripts/run-demo.ps1 -ConnectionString 'Server=.;Database=ApexPost_Test_Demo;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
#>
[CmdletBinding()]
param(
	[string]$Password,
	[switch]$Reset,
	[string]$ConnectionString
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\src\ApexPost.Web')).Path
$infrastructure = (Resolve-Path (Join-Path $PSScriptRoot '..\src\ApexPost.Infrastructure')).Path
$secretKey = 'DemoData:StaffPassword'
$connectionKey = 'ConnectionStrings:ApexPostDb'
$disposablePrefix = 'ApexPost_Test_'
# The port of the demo launch profile.
$demoPort = 7034
$approvedDirectory = 'E:\SqlData\ApexPost\Tests'

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

function Get-ConnectionValue([string]$value, [string[]]$keys) {
	$builder = New-Object System.Data.Common.DbConnectionStringBuilder
	try { $builder.set_ConnectionString($value) }
	catch { throw 'The connection string is not valid.' }

	foreach ($key in $keys) {
		if ($builder.ContainsKey($key)) { [string]$builder[$key] }
	}
}

# Messages never repeat the connection string, which may hold a credential.
function Assert-DisposableDatabase([string]$value) {
	foreach ($key in 'AttachDbFilename', 'Extended Properties', 'Initial File Name') {
		if (@(Get-ConnectionValue $value $key).Count -gt 0) {
			throw "$key is not accepted, because it can select another database."
		}
	}

	$names = @(Get-ConnectionValue $value 'Database', 'Initial Catalog')
	if ($names.Count -eq 0) { throw 'The connection string must name a database.' }

	foreach ($name in $names) {
		if ($name -ieq 'ApexPostDb') {
			throw 'ApexPostDb is the development database, and -ConnectionString refuses it.'
		}
		if ($name -cnotmatch "^$disposablePrefix[A-Za-z0-9_]+$") {
			throw "The database name must start with $disposablePrefix."
		}
	}

	$integrated = @(Get-ConnectionValue $value 'Integrated Security', 'Trusted_Connection')
	if (-not ($integrated | Where-Object { $_ -match '^(true|yes|sspi)$' })) {
		throw '-ConnectionString needs Windows authentication, because the location check uses it.'
	}
}

# Reads only master, so it never opens the target database. The name is safe to
# place in the query because Assert-DisposableDatabase has limited its characters.
function Assert-DatabaseLocation([string]$value) {
	$name = @(Get-ConnectionValue $value 'Database', 'Initial Catalog')[0]
	$server = @(Get-ConnectionValue $value 'Server', 'Data Source', 'Address', 'Addr', 'Network Address')[0]
	if (-not $server) { throw 'The connection string must name a server.' }

	$query = "SET NOCOUNT ON; SELECT f.physical_name FROM sys.databases AS d JOIN sys.master_files AS f ON f.database_id = d.database_id WHERE d.name = N'$name';"
	$lines = sqlcmd -S $server -E -C -b -d master -h -1 -W -Q $query
	if ($LASTEXITCODE -ne 0) { throw 'The database location could not be checked.' }

	$files = @($lines | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
	if ($files.Count -eq 0) {
		throw "Database $name was not found, and this script never creates one. Create it in $approvedDirectory first."
	}
	foreach ($file in $files) {
		$directory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($file))
		if ($directory -ine $approvedDirectory) {
			throw "Database $name has a file outside $approvedDirectory, so nothing was migrated."
		}
	}
}

function Assert-PortFree([int]$port) {
	$listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
	if ($listeners | Where-Object { $_.Port -eq $port }) {
		throw "Port $port is already in use. Stop the application using it, then run this script again."
	}
}

# Nothing below this block may run until these checks pass.
$environmentPassword = $env:DemoData__StaffPassword
if ($Reset -and -not $ConnectionString) {
	throw "-Reset deletes all data, so it needs -ConnectionString naming a $disposablePrefix database."
}
if ($Password -and $environmentPassword) {
	throw 'Use either -Password or DemoData__StaffPassword, not both.'
}
if ($ConnectionString) {
	Assert-DisposableDatabase $ConnectionString
	if (-not $environmentPassword) {
		throw '-ConnectionString needs the password in DemoData__StaffPassword, so user-secrets stay unchanged.'
	}
	$connection = $ConnectionString
}
else {
	$connection = Get-StoredSecret $connectionKey
	if (-not $connection) {
		throw "User-secret $connectionKey is not set. Run ./scripts/setup-database.ps1 first."
	}
}

Assert-PortFree $demoPort
if ($ConnectionString) { Assert-DatabaseLocation $ConnectionString }

if ($Reset) {
	Write-Host 'Reverting all migrations: every row in the database will be deleted.'
	Update-Database '0'
}
Update-Database

if ($environmentPassword) {
	$passwordLine = 'Password: from DemoData__StaffPassword, not shown'
}
else {
	$stored = Get-StoredSecret $secretKey
	if ($Password) {
		if ($stored -and $stored -ne $Password) {
			Write-Warning 'Existing demo logins keep their old password. A new password needs a new empty database.'
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
	$passwordLine = "Password: $Password"
}

Write-Host ''
Write-Host 'ApexPost demo logins (branch DM01), all with the same password:'
Write-Host '  demo-staff-01  Counter Clerk'
Write-Host '  demo-staff-02  Branch Manager'
Write-Host '  demo-staff-03  Auditor'
Write-Host '  demo-staff-04  Administrator'
Write-Host "  $passwordLine"
Write-Host '  (demo-staff-05 to demo-staff-20 repeat the roles for branches DM02-DM05.)'
Write-Host ''
Write-Host "Starting https://localhost:$demoPort - press Ctrl+C to stop."

# An environment variable outranks user-secrets, so the application uses the
# same database as the migrations.
$previousConnection = $env:ConnectionStrings__ApexPostDb
if ($ConnectionString) { $env:ConnectionStrings__ApexPostDb = $ConnectionString }
try {
	dotnet run --project $project --launch-profile demo
}
finally {
	$env:ConnectionStrings__ApexPostDb = $previousConnection
}

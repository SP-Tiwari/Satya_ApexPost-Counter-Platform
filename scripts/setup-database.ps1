<#
.SYNOPSIS
Creates the empty ApexPostDb development database and stores its connection string.

.DESCRIPTION
Safe to run again: an existing database is left untouched, and an existing
connection string is kept. Tables are created later by EF Core migrations.

The connection string goes to user-secrets as ConnectionStrings:ApexPostDb,
never to the repository. It uses Windows authentication, and
TrustServerCertificate accepts the local instance's self-signed certificate,
so it is for local development only.

.PARAMETER Server
SQL Server instance. The default instance accepts local connections as ".".

.PARAMETER DataDirectory
Folder for the database files. It defaults to E: because the instance default
is on the system drive.

.PARAMETER ServiceAccount
Windows account that runs SQL Server. It is granted access to DataDirectory.

.EXAMPLE
./scripts/setup-database.ps1
./scripts/setup-database.ps1 -DataDirectory 'D:\SqlData\ApexPost'
#>
[CmdletBinding()]
param(
	[string]$Server = '.',
	[string]$DataDirectory = 'E:\SqlData\ApexPost',
	[string]$ServiceAccount = 'NT Service\MSSQLSERVER'
)

$ErrorActionPreference = 'Stop'
$databaseName = 'ApexPostDb'
$secretKey = 'ConnectionStrings:ApexPostDb'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src\ApexPost.Web'
$sqlScript = Join-Path $root 'sql\setup\create-database.sql'

# sqlcmd substitutes this value into the script, so it is checked first.
if ($DataDirectory -notmatch '^[A-Za-z]:\\[A-Za-z0-9_\\ -]+$') {
	throw 'DataDirectory must be an absolute local path of letters, digits, spaces, hyphens and underscores.'
}
$DataDirectory = $DataDirectory.TrimEnd('\')

New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
icacls $DataDirectory /grant "${ServiceAccount}:(OI)(CI)M" /Q | Out-Null
if ($LASTEXITCODE -ne 0) {
	throw "Could not grant $ServiceAccount access to $DataDirectory."
}

# sqlcmd reads scripting variables from the environment; passing a path
# through -v is misparsed because of its colon.
$env:DatabaseName = $databaseName
$env:DataDirectory = $DataDirectory
sqlcmd -S $Server -E -C -b -i $sqlScript
if ($LASTEXITCODE -ne 0) {
	throw "Creating $databaseName failed."
}

$stored = dotnet user-secrets list --project $project |
	Where-Object { $_.StartsWith("$secretKey = ") }
if ($stored) {
	Write-Host "Keeping the existing $secretKey user-secret."
}
else {
	$connectionString = "Server=$Server;Database=$databaseName;Integrated Security=True;Encrypt=True;TrustServerCertificate=True"
	dotnet user-secrets set $secretKey $connectionString --project $project | Out-Null
	if ($LASTEXITCODE -ne 0) {
		throw "Could not store the $secretKey user-secret."
	}
	Write-Host "Stored the $secretKey user-secret."
}

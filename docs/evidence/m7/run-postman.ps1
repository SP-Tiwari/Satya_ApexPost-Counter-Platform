<#
.SYNOPSIS
Runs the Postman collection against a disposable copy of ApexPost.

.DESCRIPTION
Creates a uniquely named database in E:\SqlData\ApexPost\Tests from the
idempotent schema script, starts the application on its own port with fictional
settings and demo data, runs the collection with the Postman CLI, then stops that
process and drops that database. The API key and staff password are generated for
the run and never saved. It does not read user-secrets values for these or use
ApexPostDb, and it does not touch any other running copy of the application.

.PARAMETER Port
The local HTTP port for the disposable application.

.PARAMETER Output
The file that receives the results. Secrets are removed from it.

.EXAMPLE
./docs/evidence/m7/run-postman.ps1
#>
[CmdletBinding()]
param(
	[int]$Port = 5199,
	[string]$Output
)

$ErrorActionPreference = 'Stop'
if (-not $Output) { $Output = Join-Path $PSScriptRoot 'postman-run.txt' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$collection = Join-Path $PSScriptRoot 'ApexPost-week3.postman_collection.json'
$build = Join-Path $root 'out\postman'
$dataDirectory = 'E:\SqlData\ApexPost\Tests'
$database = "ApexPost_Postman_$([guid]::NewGuid().ToString('N'))"
$baseUrl = "http://127.0.0.1:$Port"
$apiKey = [guid]::NewGuid().ToString('N')
$password = [guid]::NewGuid().ToString('N')

function Invoke-Sql([string]$query, [string]$target = 'master') {
	$lines = sqlcmd -S . -E -C -b -W -d $target -Q $query
	if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $lines" }
	$lines
}

if (-not (Test-Path 'E:\')) { throw 'Drive E: is unavailable. This run never falls back to C:.' }
New-Item -ItemType Directory -Force $dataDirectory | Out-Null

$env:UseArtifactsOutput = 'true'
$env:ArtifactsPath = $build
dotnet build (Join-Path $root 'src\ApexPost.Web') --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
Remove-Item Env:UseArtifactsOutput, Env:ArtifactsPath

$process = $null
$standardOutput = Join-Path $env:TEMP "$database.out.log"
$standardError = Join-Path $env:TEMP "$database.err.log"
try {
	Invoke-Sql "CREATE DATABASE [$database] ON PRIMARY (NAME = [${database}_data], FILENAME = '$dataDirectory\$database.mdf') LOG ON (NAME = [${database}_log], FILENAME = '$dataDirectory\${database}_log.ldf');" | Out-Null
	sqlcmd -S . -E -C -b -I -d $database -i (Join-Path $root 'sql\schema\ApexPostDb-idempotent.sql') | Out-Null
	if ($LASTEXITCODE -ne 0) { throw 'The idempotent script failed.' }

	$settings = @{
		ASPNETCORE_ENVIRONMENT = 'Development'
		ASPNETCORE_URLS = $baseUrl
		ConnectionStrings__ApexPostDb = "Server=.;Database=$database;Integrated Security=True;Encrypt=True;TrustServerCertificate=True"
		DemoData__Initialize = 'true'
		DemoData__StaffPassword = $password
		ApiSecurity__ApiKey = $apiKey
		ApiSecurity__PartnerRateLimit__PermitLimit = '100'
		ApiSecurity__PartnerRateLimit__WindowSeconds = '60'
		Authentication__SignInRateLimit__PermitLimit = '50'
		Reports__LowStockThreshold = '5'
		Transactions__TaxRates__Standard = '0.18'
		Transactions__TaxRates__ZeroRated = '0'
		Transactions__PaymentMethods__0 = 'Cash'
		Shipping__TaxCategory = 'Standard'
		Shipping__MaximumParcelWeightKilograms__Standard = '30'
		MoneyOrders__HighValueThreshold__Amount = '10000'
		MoneyOrders__HighValueThreshold__Currency = 'INR'
	}
	foreach ($setting in $settings.GetEnumerator()) {
		Set-Item "Env:$($setting.Key)" $setting.Value
	}
	$application = Join-Path $build 'bin\ApexPost.Web\debug'
	$process = Start-Process dotnet -ArgumentList 'ApexPost.Web.dll' -WorkingDirectory $application -PassThru -WindowStyle Hidden -RedirectStandardOutput $standardOutput -RedirectStandardError $standardError
	foreach ($setting in $settings.GetEnumerator()) {
		Remove-Item "Env:$($setting.Key)"
	}

	$ready = $false
	foreach ($attempt in 1..60) {
		if ($process.HasExited) { throw 'The application stopped during startup.' }
		try {
			Invoke-WebRequest "$baseUrl/health" -UseBasicParsing -TimeoutSec 2 | Out-Null
			$ready = $true
			break
		}
		catch { Start-Sleep -Seconds 1 }
	}
	if (-not $ready) { throw 'The application did not become healthy.' }

	$cli = postman collection run $collection --env-var "baseUrl=$baseUrl" --env-var "apiKey=$apiKey" --env-var "password=$password" --color off 2>&1 | ForEach-Object { "$_" }
	$cliExit = $LASTEXITCODE

	$state = Invoke-Sql @'
SET NOCOUNT ON;
SELECT Status, COUNT(*) AS Transactions FROM CounterTransactions GROUP BY Status;
SELECT Action, StatusCode, Outcome, COUNT(*) AS Entries
FROM AuditEntries
WHERE Action IN ('Void', 'Refund', 'Adjust', 'CreateBranch', 'CreateStaff', 'CreateProduct', 'UpdateStaff', 'SetBranchStatus')
GROUP BY Action, StatusCode, Outcome
ORDER BY Action, StatusCode;
SELECT Action, StatusCode, RouteValues AS AuditedRoute FROM AuditEntries WHERE Action IN ('Void', 'Refund') ORDER BY AuditEntryId;
SELECT EntryType, COUNT(*) AS Entries FROM Payments GROUP BY EntryType;
SELECT paid.TransactionId, paid.Amount AS Paid, refunded.Amount AS Refunded
FROM Payments AS paid
INNER JOIN Payments AS refunded ON refunded.TransactionId = paid.TransactionId AND refunded.EntryType = 'Refund'
WHERE paid.EntryType = 'Payment';
SELECT COUNT(*) AS IdentityRows, SUM(CASE WHEN MaskedDocumentIdentifier LIKE '[*][*][*][*][0-9][0-9][0-9][0-9]' THEN 1 ELSE 0 END) AS MaskedRows FROM IdentityVerifications;
SELECT (SELECT COUNT(*) FROM Shipments) AS Shipments, (SELECT COUNT(*) FROM MoneyOrders) AS MoneyOrders;
SELECT Quantity AS StockOfDemoSku002AfterSaleAndVoid FROM BranchStock WHERE BranchCode = 'DM01' AND Sku = 'DEMO-SKU-002';
'@ $database

	$report = @(
		"Postman CLI $(postman --version)"
		"Run at $([DateTime]::UtcNow.ToString('u')) against an application built from the working tree."
		''
		$cli
		''
		'Database state after the run:'
		$state
	) -join [Environment]::NewLine
	$report.Replace($apiKey, '<api-key>').Replace($password, '<password>') |
		Set-Content -Path $Output -Encoding utf8
	if ($cliExit -ne 0) { Write-Warning "The Postman CLI exited with $cliExit. See $Output." }
}
finally {
	if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
	if ($process) { $process.WaitForExit(10000) | Out-Null }
	Remove-Item $standardOutput, $standardError -ErrorAction SilentlyContinue
	Invoke-Sql "IF DB_ID('$database') IS NOT NULL BEGIN ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]; END;" | Out-Null
	Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
}

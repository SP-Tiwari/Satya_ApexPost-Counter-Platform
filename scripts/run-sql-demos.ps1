<#
.SYNOPSIS
Runs the T-SQL deliverables on a disposable database and saves their output.

.DESCRIPTION
Creates an ApexPost_Test_* database in E:\SqlData\ApexPost\Tests, applies the
idempotent schema script twice, then runs the reports, procedures, atomic sale,
blocking demo and deadlock demo. Output goes to docs/evidence/m4. The database
is dropped at the end. ApexPostDb is never used.

.EXAMPLE
./scripts/run-sql-demos.ps1
#>
[CmdletBinding()]
param(
	[string]$Server = '.',
	[string]$DataDirectory = 'E:\SqlData\ApexPost\Tests'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$evidence = Join-Path $root 'docs\evidence\m4'
$database = 'ApexPost_Test_SqlDemo_' + [guid]::NewGuid().ToString('N').Substring(0, 8)

function Invoke-Sql {
	param([string]$Query, [string]$File, [string]$Target = $database, [switch]$AllowError, [switch]$FullWidth)

	$arguments = @('-S', $Server, '-E', '-C', '-I', '-d', $Target)
	if ($FullWidth) { $arguments += @('-y', '0') } else { $arguments += '-W' }
	if ($Query) { $arguments += @('-Q', $Query) } else { $arguments += @('-i', (Join-Path $root $File)) }
	if (-not $AllowError) { $arguments += '-b' }

	$previous = $ErrorActionPreference
	$ErrorActionPreference = 'Continue'
	$output = & sqlcmd @arguments 2>&1 | ForEach-Object { "$_" -replace ', Server [^,]+', '' }
	$exitCode = $LASTEXITCODE
	$ErrorActionPreference = $previous
	if ($exitCode -ne 0 -and -not $AllowError) {
		throw "sqlcmd failed: $($File + $Query)`n$output"
	}

	$output
}

function Start-Session {
	param([string]$File, [string]$OutputName)

	Start-Process sqlcmd -PassThru -NoNewWindow `
		-RedirectStandardOutput (Join-Path $evidence $OutputName) `
		-ArgumentList @('-S', $Server, '-E', '-C', '-I', '-W', '-d', $database, '-i', "`"$(Join-Path $root $File)`"")
}

function Write-Evidence {
	param([string]$Name, [string]$Title, [object[]]$Lines)

	Add-Content -Path (Join-Path $evidence $Name) -Encoding UTF8 -Value (@("## $Title", '') + $Lines + '')
}

New-Item -ItemType Directory -Force -Path $evidence, $DataDirectory | Out-Null
Get-ChildItem $evidence -Filter '*.txt' | Remove-Item

Invoke-Sql -Target 'master' -Query @"
CREATE DATABASE [$database]
ON PRIMARY (NAME = N'$database', FILENAME = N'$DataDirectory\$database.mdf', SIZE = 32MB)
LOG ON (NAME = N'${database}_log', FILENAME = N'$DataDirectory\${database}_log.ldf', SIZE = 16MB)
COLLATE SQL_Latin1_General_CP1_CI_AS;
"@ | Out-Null

try {
	Invoke-Sql -File 'sql\schema\ApexPostDb-idempotent.sql' | Out-Null
	Invoke-Sql -File 'sql\schema\ApexPostDb-idempotent.sql' | Out-Null
	Write-Evidence 'schema.txt' 'Idempotent script applied twice' (Invoke-Sql -Query @"
SET NOCOUNT ON;
SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId;
SELECT name AS ProcedureName FROM sys.procedures ORDER BY name;
"@)

	Invoke-Sql -File 'sql\demos\demo-fixture.sql' | Out-Null

	foreach ($report in Get-ChildItem (Join-Path $root 'sql\reports') -Filter '*.sql' | Sort-Object Name) {
		Write-Evidence 'reports.txt' $report.Name (Invoke-Sql -File "sql\reports\$($report.Name)")
	}

	$today = (Invoke-Sql -Query "SET NOCOUNT ON; SELECT CONVERT(char(8), SYSUTCDATETIME(), 112);")[-1].Trim()
	foreach ($call in @(
		"EXEC dbo.usp_DailyBranchSales @BranchCode = 'DM01', @Date = '$today';",
		"EXEC dbo.usp_DailyBranchSales @BranchCode = 'NOPE', @Date = '$today';",
		"EXEC dbo.usp_LowStockReport @BranchCode = 'DM02', @Threshold = 5;",
		"EXEC dbo.usp_LowStockReport @Threshold = 40;",
		"EXEC dbo.usp_LowStockReport @Threshold = -1;",
		"EXEC dbo.usp_AdjustStock @BranchCode = 'DM01', @Sku = 'DEMO-SKU-001', @Delta = -5, @Reason = N'DEMO damaged stock', @ChangedBy = 'demo-staff-02';",
		"EXEC dbo.usp_AdjustStock @BranchCode = 'DM01', @Sku = 'DEMO-SKU-001', @Delta = -500, @Reason = N'DEMO count', @ChangedBy = 'demo-staff-02';",
		"EXEC dbo.usp_AdjustStock @BranchCode = 'DM01', @Sku = 'DEMO-SKU-001', @Delta = 1, @Reason = N'DEMO count', @ChangedBy = 'demo-staff-01';"
	)) {
		Write-Evidence 'procedures.txt' $call (Invoke-Sql -Query $call -AllowError)
	}

	foreach ($branch in @("'DM01'", 'NULL')) {
		$plan = Invoke-Sql -FullWidth -Query "SET STATISTICS XML ON; EXEC dbo.usp_LowStockReport @BranchCode = $branch, @Threshold = 5;"
		$operators = [regex]::Matches(($plan -join ' '), 'PhysicalOp="([^"]+)"') |
			ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -match 'Clustered' } | Select-Object -Unique
		Write-Evidence 'procedures.txt' "OPTION (RECOMPILE) plan for @BranchCode = $branch" @($operators)
	}

	$stockQuery = "SET NOCOUNT ON; SELECT Sku, Quantity FROM dbo.BranchStock WHERE BranchCode = 'DM01' ORDER BY Sku;"
	Write-Evidence 'atomic-sale.txt' 'Stock before' (Invoke-Sql -Query $stockQuery)
	Write-Evidence 'atomic-sale.txt' 'Successful sale' (Invoke-Sql -File 'sql\transactions\atomic-sale.sql')
	Write-Evidence 'atomic-sale.txt' 'Stock after' (Invoke-Sql -Query $stockQuery)
	Invoke-Sql -Query "UPDATE dbo.BranchStock SET Quantity = 0 WHERE BranchCode = 'DM01' AND Sku = 'DEMO-SKU-002';" | Out-Null
	Write-Evidence 'atomic-sale.txt' 'Second sale with DEMO-SKU-002 set to 0' (Invoke-Sql -File 'sql\transactions\atomic-sale.sql' -AllowError)
	Write-Evidence 'atomic-sale.txt' 'Stock after the failed sale: DEMO-SKU-001 unchanged' (Invoke-Sql -Query $stockQuery)
	Invoke-Sql -Query "UPDATE dbo.BranchStock SET Quantity = 40 WHERE BranchCode = 'DM01';" | Out-Null

	$writer = Start-Session 'sql\demos\blocking-session-1-writer.sql' 'blocking-session-1-writer.txt'
	Start-Sleep -Seconds 2
	$reader = Start-Session 'sql\demos\blocking-session-2-reader.sql' 'blocking-session-2-reader.txt'
	Start-Sleep -Seconds 3
	Write-Evidence 'blocking-session-3-monitor.txt' 'Monitor, while the reader waits' (Invoke-Sql -File 'sql\demos\blocking-session-3-monitor.sql')
	$writer.WaitForExit()
	$reader.WaitForExit()

	$first = Start-Session 'sql\demos\deadlock-session-a.sql' 'deadlock-opposite-order-session-a.txt'
	$second = Start-Session 'sql\demos\deadlock-session-b-opposite-order.sql' 'deadlock-opposite-order-session-b.txt'
	$first.WaitForExit()
	$second.WaitForExit()
	Write-Evidence 'deadlock-summary.txt' 'After opposite order, from 40 each' (Invoke-Sql -Query $stockQuery)

	Invoke-Sql -Query "UPDATE dbo.BranchStock SET Quantity = 40 WHERE BranchCode = 'DM01';" | Out-Null
	$first = Start-Session 'sql\demos\deadlock-session-a.sql' 'deadlock-same-order-session-1.txt'
	$second = Start-Session 'sql\demos\deadlock-session-a.sql' 'deadlock-same-order-session-2.txt'
	$first.WaitForExit()
	$second.WaitForExit()
	Write-Evidence 'deadlock-summary.txt' 'After same order, from 40 each' (Invoke-Sql -Query $stockQuery)
}
finally {
	Invoke-Sql -Target 'master' -Query @"
IF DB_ID(N'$database') IS NOT NULL
BEGIN
	ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
	DROP DATABASE [$database];
END;
"@ | Out-Null
}

Write-Host "Evidence saved to $evidence"

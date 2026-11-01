<#
.SYNOPSIS
Builds ApexPostDb_Perf, measures the Part C workloads and saves the evidence.

.DESCRIPTION
Creates a disposable ApexPostDb_Perf database in E:\SqlData\ApexPost\Perf,
loads millions of fictional rows, and measures each workload before and after
the candidate indexes. Results go to docs/evidence/m6. ApexPostDb is never used.

Steps can run on their own, which saves reloading the data:
  Load      create the database, apply the schema and load the data
  Baseline  measure with the indexes as they were before AddPerformanceIndexes
  Indexes   build the candidate indexes, recording build time and size
  After     measure again with the candidate indexes
  Studies   SARGability, implicit conversion, estimates and fragmentation
  Export    save the Query Store history
  Drop      drop the database

.EXAMPLE
./scripts/run-performance.ps1
./scripts/run-performance.ps1 -Steps After,Export
#>
[CmdletBinding()]
param(
	[string]$Server = '.',
	[string]$DataDirectory = 'E:\SqlData\ApexPost\Perf',
	[ValidateSet('Load', 'Baseline', 'Indexes', 'After', 'Studies', 'Export', 'Drop')]
	[string[]]$Steps = @('Load', 'Baseline', 'Indexes', 'After', 'Studies', 'Export', 'Drop')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$evidence = Join-Path $root 'docs\evidence\m6'
$plans = Join-Path $evidence 'plans'
$database = 'ApexPostDb_Perf'
$connectionString = "Server=$Server;Database=$database;Integrated Security=True;TrustServerCertificate=True;Encrypt=True"
$measureRuns = 5
$busyBranch = 'P0001'
$smallBranch = 'P0400'
$businessDate = [datetime]'2026-06-15'

New-Item -ItemType Directory -Force -Path $plans | Out-Null

function Invoke-File {
	param([string]$File, [string]$Target = $database)

	$output = & sqlcmd -S $Server -E -C -I -b -d $Target -i (Join-Path $root $File) 2>&1 | ForEach-Object { "$_" }
	if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed for ${File}: $output" }
	$output
}

function Invoke-Text {
	param([string]$Query, [string]$Target = $database)

	$output = & sqlcmd -S $Server -E -C -I -b -W -d $Target -Q $Query 2>&1 | ForEach-Object { "$_" }
	if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $Query`n$output" }
	$output
}

function Open-Connection {
	$connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
	$connection.Open()
	$connection
}

function New-Command {
	param($Connection, $Workload)

	$command = $Connection.CreateCommand()
	$command.CommandTimeout = 0
	$command.CommandText = $Workload.Sql
	if ($Workload.Procedure) { $command.CommandType = [System.Data.CommandType]::StoredProcedure }
	foreach ($parameter in $Workload.Parameters) {
		$item = $command.Parameters.Add($parameter[0], [System.Data.SqlDbType]$parameter[1])
		if ($parameter[2] -gt 0) { $item.Size = $parameter[2] }
		$item.Value = if ($null -eq $parameter[3]) { [DBNull]::Value } else { $parameter[3] }
	}
	$command
}

function Set-Session {
	param($Connection, [string]$Setting)

	$command = $Connection.CreateCommand()
	$command.CommandText = $Setting
	[void]$command.ExecuteNonQuery()
}

# Reads every result set, as a client would, and returns the data row count.
function Read-Rows {
	param($Command)

	$rows = 0
	$reader = $Command.ExecuteReader()
	do {
		while ($reader.Read()) { $rows++ }
	} while ($reader.NextResult())
	$reader.Close()
	$rows
}

function Get-Median {
	param([double[]]$Values)

	($Values | Sort-Object)[[math]::Floor($Values.Count / 2)]
}

# One execution with STATISTICS IO and TIME on, parsed from the info messages.
function Measure-Once {
	param($Workload)

	$connection = Open-Connection
	$script:messages = New-Object System.Collections.ArrayList
	$handler = [System.Data.SqlClient.SqlInfoMessageEventHandler]{
		param($sender, $event)
		foreach ($error in $event.Errors) { [void]$script:messages.Add($error.Message) }
	}
	$connection.add_InfoMessage($handler)
	Set-Session $connection 'SET STATISTICS IO, TIME ON;'
	$rows = Read-Rows (New-Command $connection $Workload)
	$connection.Close()

	$reads = 0
	$tables = @{}
	$cpu = 0
	$elapsed = 0
	foreach ($message in $script:messages) {
		foreach ($match in [regex]::Matches($message, "Table '([^']+)'\. Scan count \d+, logical reads (\d+)")) {
			$tables[$match.Groups[1].Value] = [long]$match.Groups[2].Value + [long]$tables[$match.Groups[1].Value]
			$reads += [long]$match.Groups[2].Value
		}
		foreach ($match in [regex]::Matches($message, 'SQL Server Execution Times:\s+CPU time = (\d+) ms,\s+elapsed time = (\d+) ms')) {
			# The last figure in a procedure or sp_executesql call is the whole call.
			if ([int]$match.Groups[2].Value -ge $elapsed) {
				$cpu = [int]$match.Groups[1].Value
				$elapsed = [int]$match.Groups[2].Value
			}
		}
	}

	[pscustomobject]@{ Rows = $rows; Reads = $reads; Cpu = $cpu; Elapsed = $elapsed; Tables = $tables }
}

# Runs with STATISTICS XML on and saves the actual plan as a .sqlplan file.
function Save-Plan {
	param($Workload, [string]$FileName)

	$connection = Open-Connection
	Set-Session $connection 'SET STATISTICS XML ON;'
	$reader = (New-Command $connection $Workload).ExecuteReader()
	$xml = @()
	do {
		if ($reader.FieldCount -eq 1 -and $reader.GetName(0) -like 'Microsoft SQL Server*XML Showplan') {
			while ($reader.Read()) { $xml += $reader.GetString(0) }
		}
		else {
			while ($reader.Read()) { }
		}
	} while ($reader.NextResult())
	$reader.Close()
	$connection.Close()

	# A procedure returns one plan per statement; keep the most expensive.
	$plan = $xml | Sort-Object { [double][regex]::Match($_, 'StatementSubTreeCost="([^"]+)"').Groups[1].Value } | Select-Object -Last 1
	[System.IO.File]::WriteAllText((Join-Path $plans $FileName), $plan, (New-Object System.Text.UTF8Encoding $false))
	$plan
}

function Get-PlanShape {
	param([string]$Plan)

	$document = New-Object System.Xml.XmlDocument
	$document.LoadXml($Plan)
	$namespace = New-Object System.Xml.XmlNamespaceManager $document.NameTable
	$namespace.AddNamespace('s', 'http://schemas.microsoft.com/sqlserver/2004/07/showplan')
	$parts = foreach ($relOp in $document.SelectNodes('//s:RelOp', $namespace)) {
		$object = $relOp.SelectSingleNode('s:IndexScan/s:Object | s:TableScan/s:Object', $namespace)
		$lookup = $relOp.SelectSingleNode('s:IndexScan[@Lookup="1"]', $namespace)
		if ($object) {
			$index = $object.GetAttribute('Index').Trim('[', ']')
			$label = if ($lookup) { 'Key Lookup' } else { $relOp.GetAttribute('PhysicalOp') }
			"$label $index ($($relOp.GetAttribute('EstimatedExecutionMode')))"
		}
		elseif ($relOp.GetAttribute('PhysicalOp') -match 'Hash Match|Sort|Nested Loops|Merge Join|Stream Aggregate|Parallelism') {
			$relOp.GetAttribute('PhysicalOp')
		}
	}
	($parts | Select-Object -Unique) -join '; '
}

function Get-Estimates {
	param([string]$Plan)

	$document = New-Object System.Xml.XmlDocument
	$document.LoadXml($Plan)
	$namespace = New-Object System.Xml.XmlNamespaceManager $document.NameTable
	$namespace.AddNamespace('s', 'http://schemas.microsoft.com/sqlserver/2004/07/showplan')
	$relOp = $document.SelectSingleNode('(//s:RelOp[s:IndexScan/s:Object[@Table="[CounterTransactions]"]])[1]', $namespace)
	if (-not $relOp) { $relOp = $document.SelectSingleNode('(//s:RelOp[s:IndexScan])[1]', $namespace) }
	$actual = 0
	foreach ($thread in $relOp.SelectNodes('s:RunTimeInformation/s:RunTimeCountersPerThread', $namespace)) {
		$actual += [long]$thread.GetAttribute('ActualRows')
	}
	"Estimated rows: $($relOp.GetAttribute('EstimateRows')); actual rows: $actual; operator: $($relOp.GetAttribute('PhysicalOp'))"
}

function Measure-Workload {
	param($Workload, [string]$Phase)

	# The first run warms the cache and is not counted.
	[void](Measure-Once $Workload)
	$runs = 1..$measureRuns | ForEach-Object { Measure-Once $Workload }
	$plan = Save-Plan $Workload "$($Workload.Name)-$Phase.sqlplan"
	$tables = $runs[-1].Tables
	[pscustomobject]@{
		Workload = $Workload.Name
		Phase = $Phase
		Rows = $runs[-1].Rows
		LogicalReads = $runs[-1].Reads
		CpuMs = Get-Median ($runs | ForEach-Object { $_.Cpu })
		ElapsedMs = Get-Median ($runs | ForEach-Object { $_.Elapsed })
		TableReads = (($tables.Keys | Sort-Object | ForEach-Object { "$_=$($tables[$_])" }) -join ' ')
		PlanShape = Get-PlanShape $plan
	}
}

$paidSaleLines = @'
SELECT [c].[TransactionId], [c].[Currency], [t].[LineType], [t].[UnitPrice], [t].[Quantity], (
    SELECT TOP(1) [t0].[Rate]
    FROM [TransactionTaxRates] AS [t0]
    WHERE [c].[TransactionId] = [t0].[TransactionId] AND [t0].[TaxCategory] = [t].[TaxCategory]), [t].[ShipmentId]
FROM [CounterTransactions] AS [c]
INNER JOIN [TransactionLines] AS [t] ON [c].[TransactionId] = [t].[TransactionId]
WHERE [c].[BranchCode] = @branchCode AND [c].[Status] = 'Paid' AND [c].[OccurredAtUtc] >= @start AND [c].[OccurredAtUtc] < @end
ORDER BY [c].[OccurredAtUtc] DESC, [c].[TransactionId], [t].[TransactionLineId]
'@

$moneyOrdersList = @'
SELECT [m].[Amount], [m].[Currency], [m].[Sender], [m].[Recipient], [m].[Fee], [i].[DocumentType], [i].[MaskedDocumentIdentifier], [i].[IsSuccessful], [m].[Status]
FROM [MoneyOrders] AS [m]
LEFT JOIN [IdentityVerifications] AS [i] ON [m].[MoneyOrderId] = [i].[MoneyOrderId]
WHERE [m].[ProcessingBranchCode] = @branchCode
ORDER BY [m].[MoneyOrderId]
'@

$auditorReport = @'
SELECT [m].[ProcessingBranchCode], COUNT(*), COUNT(CASE
    WHEN [i].[IsSuccessful] IS NOT NULL THEN 1
END), COUNT(CASE
    WHEN [i].[IsSuccessful] = CAST(1 AS bit) THEN 1
END), COUNT(CASE
    WHEN [i].[IsSuccessful] = CAST(0 AS bit) THEN 1
END), COUNT(CASE
    WHEN [m].[Status] = 'Cancelled' THEN 1
END)
FROM [MoneyOrders] AS [m]
LEFT JOIN [IdentityVerifications] AS [i] ON [m].[MoneyOrderId] = [i].[MoneyOrderId]
GROUP BY [m].[ProcessingBranchCode]
'@

$shipmentSearch = @'
SELECT [s].[ShipmentId], [s].[ShipmentType], [s].[WeightKg]
FROM [Shipments] AS [s]
WHERE [s].[ZoneCode] = @zoneCode AND [s].[ServiceLevel] = @serviceLevel
'@

$shippingVolume = @'
SELECT [s].[ZoneCode], [s].[ServiceLevel], COUNT(*)
FROM [Shipments] AS [s]
GROUP BY [s].[ZoneCode], [s].[ServiceLevel]
'@

$dayStart = $businessDate
$dayEnd = $businessDate.AddDays(1)
$workloads = @(
	@{ Name = 'low-stock-one-branch'; Sql = 'dbo.usp_LowStockReport'; Procedure = $true
		Parameters = @(, @('@BranchCode', 'VarChar', 10, $busyBranch)) + @(, @('@Threshold', 'Int', 0, 10)) },
	@{ Name = 'low-stock-all-branches'; Sql = 'dbo.usp_LowStockReport'; Procedure = $true
		Parameters = @(, @('@BranchCode', 'VarChar', 10, $null)) + @(, @('@Threshold', 'Int', 0, 10)) },
	@{ Name = 'daily-sales-busy-branch'; Sql = $paidSaleLines; Procedure = $false
		Parameters = @(, @('@branchCode', 'VarChar', 10, $busyBranch)) + @(, @('@start', 'DateTime2', 0, $dayStart)) + @(, @('@end', 'DateTime2', 0, $dayEnd)) },
	@{ Name = 'daily-sales-small-branch'; Sql = $paidSaleLines; Procedure = $false
		Parameters = @(, @('@branchCode', 'VarChar', 10, $smallBranch)) + @(, @('@start', 'DateTime2', 0, $dayStart)) + @(, @('@end', 'DateTime2', 0, $dayEnd)) },
	@{ Name = 'daily-sales-procedure'; Sql = 'dbo.usp_DailyBranchSales'; Procedure = $true
		Parameters = @(, @('@BranchCode', 'VarChar', 10, $busyBranch)) + @(, @('@Date', 'Date', 0, $businessDate)) },
	@{ Name = 'shipment-search'; Sql = $shipmentSearch; Procedure = $false
		Parameters = @(, @('@zoneCode', 'VarChar', 50, 'PERF-ZONE-07')) + @(, @('@serviceLevel', 'VarChar', 20, 'Express')) },
	@{ Name = 'shipping-volume-report'; Sql = $shippingVolume; Procedure = $false; Parameters = @() },
	@{ Name = 'money-orders-branch-list'; Sql = $moneyOrdersList; Procedure = $false
		Parameters = @(, @('@branchCode', 'VarChar', 10, $busyBranch)) },
	@{ Name = 'money-orders-auditor-report'; Sql = $auditorReport; Procedure = $false; Parameters = @() }
)

# Three runs of the write workload, each as milliseconds,log bytes,log records.
function Invoke-WriteWorkload {
	1..3 | ForEach-Object {
		$lines = & sqlcmd -S $Server -E -C -I -b -W -h -1 -s ',' -d $database -i (Join-Path $root 'sql\performance\04-write-workload.sql') 2>&1 | ForEach-Object { "$_" }
		if ($LASTEXITCODE -ne 0) { throw "Write workload failed: $lines" }
		@($lines | Where-Object { $_ -match '^\d+,\d+,\d+$' })[-1]
	}
}

$fragmentation = @'
SET NOCOUNT ON;
SELECT i.name AS IndexName, CAST(s.avg_fragmentation_in_percent AS decimal(5, 1)) AS FragmentationPercent, s.page_count AS Pages
FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID(N'dbo.CounterTransactions'), NULL, NULL, 'LIMITED') AS s
INNER JOIN sys.indexes AS i ON i.object_id = s.object_id AND i.index_id = s.index_id
ORDER BY s.index_id;
'@

function Measure-All {
	param([string]$Phase)

	$file = Join-Path $evidence "measurements-$Phase.csv"
	$results = foreach ($workload in $workloads) {
		Write-Host "Measuring $($workload.Name) ($Phase)"
		Measure-Workload $workload $Phase
	}
	$results | Export-Csv -Path $file -NoTypeInformation -Encoding UTF8

	Invoke-WriteWorkload | Set-Content -Path (Join-Path $evidence "write-cost-$Phase.csv") -Encoding UTF8
}

function Save-IndexDefinitions {
	param([string]$FileName)

	Invoke-Text @'
SET NOCOUNT ON;
SELECT
    CONCAT(OBJECT_NAME(i.object_id), '.', i.name, ' (',
        (SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
         FROM sys.index_columns AS ic
         INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
         WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0),
        ') INCLUDE (',
        (SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY c.name)
         FROM sys.index_columns AS ic
         INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
         WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1),
        ')')
FROM sys.indexes AS i
WHERE OBJECTPROPERTY(i.object_id, 'IsUserTable') = 1 AND i.index_id > 1
ORDER BY 1;
'@ | Set-Content -Path (Join-Path $evidence $FileName) -Encoding UTF8
}

function Save-IndexSizes {
	param([string]$FileName)

	Invoke-Text @'
SET NOCOUNT ON;
SELECT
    OBJECT_NAME(i.object_id) AS TableName,
    i.name AS IndexName,
    i.type_desc AS IndexType,
    SUM(s.row_count) AS IndexRows,
    SUM(s.used_page_count) * 8 / 1024 AS UsedMb
FROM sys.indexes AS i
INNER JOIN sys.dm_db_partition_stats AS s
    ON s.object_id = i.object_id AND s.index_id = i.index_id
WHERE OBJECTPROPERTY(i.object_id, 'IsUserTable') = 1
    AND OBJECT_NAME(i.object_id) IN ('BranchStock', 'CounterTransactions', 'MoneyOrders', 'Shipments', 'TransactionLines')
GROUP BY i.object_id, i.name, i.type_desc
ORDER BY TableName, IndexName;
'@ | Set-Content -Path (Join-Path $evidence $FileName) -Encoding UTF8
}

if ($Steps -contains 'Load') {
	New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
	Invoke-Text -Target 'master' -Query @"
IF DB_ID(N'$database') IS NOT NULL
BEGIN
	ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
	DROP DATABASE [$database];
END;
CREATE DATABASE [$database]
ON PRIMARY (NAME = N'${database}_data', FILENAME = N'$DataDirectory\$database.mdf', SIZE = 8GB, FILEGROWTH = 1GB)
LOG ON (NAME = N'${database}_log', FILENAME = N'$DataDirectory\${database}_log.ldf', SIZE = 4GB, FILEGROWTH = 1GB)
COLLATE SQL_Latin1_General_CP1_CI_AS;
ALTER DATABASE [$database] SET RECOVERY SIMPLE;
"@ | Out-Null
	Invoke-File 'sql\schema\ApexPostDb-idempotent.sql' | Out-Null
	Save-IndexDefinitions 'index-definitions-migration.txt'
	Invoke-File 'sql\performance\02-baseline-indexes.sql' | Out-Null
	$started = Get-Date
	Invoke-File 'sql\performance\01-generate-data.sql' | Out-Null
	$rows = Invoke-Text "SET NOCOUNT ON; SELECT t.name AS TableName, SUM(p.rows) AS TableRows FROM sys.tables AS t INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE t.name <> '__EFMigrationsHistory' GROUP BY t.name ORDER BY t.name;"
	$rows + "Load minutes: $([math]::Round(((Get-Date) - $started).TotalMinutes, 1))" |
		Set-Content -Path (Join-Path $evidence 'dataset.txt') -Encoding UTF8
	Invoke-Text -Target 'master' -Query @"
ALTER DATABASE [$database] SET QUERY_STORE = ON;
ALTER DATABASE [$database] SET QUERY_STORE (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = ALL, INTERVAL_LENGTH_MINUTES = 1, DATA_FLUSH_INTERVAL_SECONDS = 60);
"@ | Out-Null
}

if ($Steps -contains 'Baseline') {
	(Invoke-Text $fragmentation) | Set-Content -Path (Join-Path $evidence 'fragmentation-fresh.txt') -Encoding UTF8
	Invoke-File 'sql\performance\02-baseline-indexes.sql' | Out-Null
	Save-IndexSizes 'index-sizes-before.txt'
	Measure-All 'before'
}

if ($Steps -contains 'Indexes') {
	$batches = [regex]::Split((Get-Content (Join-Path $root 'sql\performance\03-candidate-indexes.sql') -Raw), '(?m)^GO\s*$') |
		Where-Object { $_ -match 'CREATE INDEX' }
	$timings = foreach ($batch in $batches) {
		$started = Get-Date
		Invoke-Text $batch | Out-Null
		$name = [regex]::Match($batch, 'CREATE INDEX\s+(\S+)').Groups[1].Value
		"$name,$([math]::Round(((Get-Date) - $started).TotalSeconds, 1))"
	}
	$timings | Set-Content -Path (Join-Path $evidence 'index-build-seconds.csv') -Encoding UTF8
	Save-IndexSizes 'index-sizes-after.txt'
	Save-IndexDefinitions 'index-definitions-measured.txt'
}

if ($Steps -contains 'After') {
	Measure-All 'after'
}

if ($Steps -contains 'Studies') {
	$report = New-Object System.Collections.ArrayList
	function Add-Study {
		param([string]$Title, $Workload, [string]$PlanName)

		[void](Measure-Once $Workload)
		$run = Measure-Once $Workload
		$plan = Save-Plan $Workload $PlanName
		[void]$report.Add("## $Title")
		[void]$report.Add("Rows $($run.Rows); logical reads $($run.Reads); CPU $($run.Cpu) ms; elapsed $($run.Elapsed) ms")
		[void]$report.Add("Plan: $(Get-PlanShape $plan)")
		[void]$report.Add((Get-Estimates $plan))
		$warning = [regex]::Match($plan, '<PlanAffectingConvert[^>]*>').Value
		[void]$report.Add("Convert warning: $(if ($warning) { $warning } else { 'none' })")
		[void]$report.Add('')
	}

	$yearSql = "SELECT COUNT(*) FROM [CounterTransactions] AS [c] WHERE [c].[BranchCode] = @branchCode AND {0}"
	$branch = @(, @('@branchCode', 'VarChar', 10, $busyBranch))
	Add-Study 'Non-SARGable: YEAR(OccurredAtUtc) = 2026' @{ Name = 'sarg'; Procedure = $false; Parameters = $branch
		Sql = $yearSql -f 'YEAR([c].[OccurredAtUtc]) = 2026' } 'sarg-year-function.sqlplan'
	Add-Study 'SARGable: date range' @{ Name = 'sarg'; Procedure = $false; Parameters = $branch
		Sql = $yearSql -f "[c].[OccurredAtUtc] >= '20260101' AND [c].[OccurredAtUtc] < '20270101'" } 'sarg-date-range.sqlplan'

	$skuSql = 'SELECT [b].[BranchCode], [b].[Quantity] FROM [BranchStock] AS [b] WHERE [b].[Sku] = @sku'
	Add-Study 'Implicit conversion: varchar parameter, as EF Core sends' @{ Name = 'sku'; Procedure = $false; Sql = $skuSql
		Parameters = @(, @('@sku', 'VarChar', 30, 'PERF-SKU-00042')) } 'implicit-conversion-varchar.sqlplan'
	Add-Study 'Implicit conversion: nvarchar parameter' @{ Name = 'sku'; Procedure = $false; Sql = $skuSql
		Parameters = @(, @('@sku', 'NVarChar', 30, 'PERF-SKU-00042')) } 'implicit-conversion-nvarchar.sqlplan'

	# Stale statistics: new rows arrive after the histogram's last date, with
	# automatic updates off so the statistics cannot catch up.
	$newRows = @'
SET NOCOUNT ON;
INSERT INTO dbo.CounterTransactions
    (TransactionId, Currency, OccurredAtUtc, CounterId, ClerkStaffId, PaymentMethod, BranchCode, Status, CreatedBy)
SELECT TOP (200000)
    NEWID(), 'INR', DATEADD(SECOND, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 86400, '20261105'),
    'T0001-1', 'perf-clerk-0001', N'PERF Cash', 'P0001', 'Paid', 'perf'
FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b;
'@
	[void]$report.Add('## CounterTransactions fragmentation after 60,000 rolled-back random-GUID inserts')
	(Invoke-Text $fragmentation) | ForEach-Object { [void]$report.Add($_) }
	[void]$report.Add('')

	# The write cost of the one index on a column every sale changes.
	Invoke-Text 'DROP INDEX IX_BranchStock_Quantity ON dbo.BranchStock;' | Out-Null
	Invoke-WriteWorkload | Set-Content -Path (Join-Path $evidence 'write-cost-without-quantity-index.csv') -Encoding UTF8
	Invoke-Text 'CREATE INDEX IX_BranchStock_Quantity ON dbo.BranchStock (Quantity);' | Out-Null

	# The query inside usp_DailyBranchSales, with its dates in local variables.
	$localVariables = @'
DECLARE @Date date = '20260615';
DECLARE @From datetime2(3) = @Date;
DECLARE @To datetime2(3) = DATEADD(DAY, 1, @From);
SELECT t.Currency,
    SUM(CASE WHEN l.LineType = 'Goods' THEN l.Quantity * l.UnitPrice * (1 + r.Rate) ELSE 0 END),
    SUM(CASE WHEN l.LineType = 'Service' THEN l.Quantity * l.UnitPrice * (1 + r.Rate) ELSE 0 END)
FROM dbo.CounterTransactions AS t
INNER JOIN dbo.TransactionLines AS l ON l.TransactionId = t.TransactionId
LEFT JOIN dbo.TransactionTaxRates AS r ON r.TransactionId = l.TransactionId AND r.TaxCategory = l.TaxCategory
WHERE t.BranchCode = @BranchCode AND t.Status = 'Paid' AND t.OccurredAtUtc >= @From AND t.OccurredAtUtc < @To
GROUP BY t.Currency
'@
	$branchCode = @(, @('@BranchCode', 'VarChar', 10, $busyBranch))
	Add-Study 'Estimate mismatch: dates in local variables' @{ Name = 'local'; Procedure = $false; Parameters = $branchCode; Sql = $localVariables } 'estimate-local-variables.sqlplan'
	Add-Study 'Estimate mismatch: the same query with OPTION (RECOMPILE)' @{ Name = 'local'; Procedure = $false; Parameters = $branchCode; Sql = "$localVariables OPTION (RECOMPILE)" } 'estimate-local-variables-recompile.sqlplan'


	Invoke-Text -Target 'master' -Query "ALTER DATABASE [$database] SET AUTO_UPDATE_STATISTICS OFF;" | Out-Null
	Invoke-Text $newRows | Out-Null
	$estimateSql = "SELECT [c].[TransactionId], [c].[Status], [c].[ModifiedAtUtc] FROM [CounterTransactions] AS [c] WHERE [c].[BranchCode] = @branchCode AND [c].[OccurredAtUtc] >= '20261105' AND [c].[OccurredAtUtc] < '20261106' OPTION (RECOMPILE)"
	Add-Study 'Estimate mismatch: statistics stale after 200,000 new rows' @{ Name = 'stale'; Procedure = $false; Parameters = $branch; Sql = $estimateSql } 'estimate-stale-statistics.sqlplan'
	Invoke-Text 'UPDATE STATISTICS dbo.CounterTransactions WITH FULLSCAN;' | Out-Null
	Add-Study 'Estimate mismatch: after UPDATE STATISTICS' @{ Name = 'stale'; Procedure = $false; Parameters = $branch; Sql = $estimateSql } 'estimate-updated-statistics.sqlplan'
	Invoke-Text -Target 'master' -Query "ALTER DATABASE [$database] SET AUTO_UPDATE_STATISTICS ON;" | Out-Null

	[void]$report.Add('## CounterTransactions fragmentation after 200,000 random-GUID inserts')
	(Invoke-Text $fragmentation) | ForEach-Object { [void]$report.Add($_) }
	$report | Set-Content -Path (Join-Path $evidence 'studies.txt') -Encoding UTF8
}

if ($Steps -contains 'Export') {
	Invoke-Text 'EXEC sys.sp_query_store_flush_db;' | Out-Null
	Invoke-Text @'
SET NOCOUNT ON;
SELECT
    q.query_id AS QueryId,
    p.plan_id AS PlanId,
    SUM(s.count_executions) AS Executions,
    CAST(SUM(s.avg_logical_io_reads * s.count_executions) / SUM(s.count_executions) AS decimal(14, 1)) AS AvgLogicalReads,
    CAST(SUM(s.avg_cpu_time * s.count_executions) / SUM(s.count_executions) / 1000 AS decimal(14, 1)) AS AvgCpuMs,
    CAST(SUM(s.avg_duration * s.count_executions) / SUM(s.count_executions) / 1000 AS decimal(14, 1)) AS AvgDurationMs,
    LEFT(REPLACE(REPLACE(t.query_sql_text, CHAR(13), ' '), CHAR(10), ' '), 90) AS QueryText
FROM sys.query_store_query AS q
INNER JOIN sys.query_store_query_text AS t ON t.query_text_id = q.query_text_id
INNER JOIN sys.query_store_plan AS p ON p.query_id = q.query_id
INNER JOIN sys.query_store_runtime_stats AS s ON s.plan_id = p.plan_id
WHERE t.query_sql_text LIKE '%CounterTransactions%TransactionLines%'
    OR t.query_sql_text LIKE '%MoneyOrders%IdentityVerifications%'
    OR t.query_sql_text LIKE '%Shipments%'
    OR t.query_sql_text LIKE '%BranchStock%'
GROUP BY q.query_id, p.plan_id, t.query_sql_text
HAVING SUM(s.count_executions) >= 5
ORDER BY q.query_id, p.plan_id;
'@ | Set-Content -Path (Join-Path $evidence 'query-store.txt') -Encoding UTF8
}

if ($Steps -contains 'Drop') {
	Invoke-Text -Target 'master' -Query @"
IF DB_ID(N'$database') IS NOT NULL
BEGIN
	ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
	DROP DATABASE [$database];
END;
"@ | Out-Null
}

Write-Host "Evidence saved to $evidence"

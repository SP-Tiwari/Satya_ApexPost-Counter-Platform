-- Creates the empty development database. Tables come from EF Core migrations.
-- Run through scripts/setup-database.ps1, which supplies these sqlcmd variables:
--   DatabaseName    for example ApexPostDb
--   DataDirectory   folder for the data and log files
-- Files are placed explicitly because the instance default is on the system drive.
-- An existing database is left untouched.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_ID(N'$(DatabaseName)') IS NULL
BEGIN
	CREATE DATABASE [$(DatabaseName)]
	ON PRIMARY
	(
		NAME = N'$(DatabaseName)_data',
		FILENAME = N'$(DataDirectory)\$(DatabaseName).mdf',
		SIZE = 256MB,
		FILEGROWTH = 256MB
	)
	LOG ON
	(
		NAME = N'$(DatabaseName)_log',
		FILENAME = N'$(DataDirectory)\$(DatabaseName)_log.ldf',
		SIZE = 128MB,
		FILEGROWTH = 128MB
	)
	COLLATE SQL_Latin1_General_CP1_CI_AS;

	ALTER DATABASE [$(DatabaseName)] SET RECOVERY SIMPLE;

	PRINT N'Created database $(DatabaseName).';
END
ELSE
BEGIN
	PRINT N'Database $(DatabaseName) already exists; no changes made.';
END;
GO

SELECT
	d.name AS DatabaseName,
	d.collation_name AS CollationName,
	d.recovery_model_desc AS RecoveryModel,
	f.type_desc AS FileType,
	f.physical_name AS PhysicalName
FROM sys.databases AS d
INNER JOIN sys.master_files AS f ON f.database_id = d.database_id
WHERE d.name = N'$(DatabaseName)'
ORDER BY f.type;
GO

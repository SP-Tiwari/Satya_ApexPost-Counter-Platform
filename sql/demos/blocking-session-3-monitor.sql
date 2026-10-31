-- Blocking demo, session 3 of 3: run while the reader is waiting. It shows who
-- blocks whom and on which lock, then reads the row without shared locks.
SET NOCOUNT ON;

SELECT
    CAST(SERVERPROPERTY('ProductVersion') AS varchar(20)) AS ProductVersion,
    is_read_committed_snapshot_on AS ReadCommittedSnapshotOn
FROM sys.databases
WHERE database_id = DB_ID();

SELECT
    r.session_id AS BlockedSession,
    r.blocking_session_id AS BlockingSession,
    r.wait_type AS WaitType,
    r.wait_time AS WaitMilliseconds,
    r.wait_resource AS WaitResource,
    CASE s.transaction_isolation_level
        WHEN 1 THEN 'READ UNCOMMITTED'
        WHEN 2 THEN 'READ COMMITTED'
        WHEN 3 THEN 'REPEATABLE READ'
        WHEN 4 THEN 'SERIALIZABLE'
        WHEN 5 THEN 'SNAPSHOT'
    END AS IsolationLevel
FROM sys.dm_exec_requests AS r
INNER JOIN sys.dm_exec_sessions AS s
    ON s.session_id = r.session_id
WHERE r.blocking_session_id <> 0
    AND r.database_id = DB_ID();

SELECT
    l.request_session_id AS SessionId,
    l.resource_type AS ResourceType,
    l.request_mode AS LockMode,
    l.request_status AS LockStatus
FROM sys.dm_tran_locks AS l
WHERE l.resource_database_id = DB_ID()
    AND l.resource_type = 'KEY'
ORDER BY l.request_status, l.request_session_id;

-- READ UNCOMMITTED takes no shared lock, so it is not blocked, but it sees a
-- value that session 1 later rolls back. This is why reports do not use it.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT Quantity AS UncommittedQuantity
FROM dbo.BranchStock
WHERE BranchCode = 'DM01' AND Sku = 'DEMO-SKU-001';

-- The database role for the application's login. Safe to run again.
-- Add the login's user to it with ALTER ROLE apexpost_app ADD MEMBER [...].
-- Migrations run under a separate deployment identity, so this role has no
-- schema rights and nothing in the app needs DELETE.
SET NOCOUNT ON;

IF DATABASE_PRINCIPAL_ID(N'apexpost_app') IS NULL
    CREATE ROLE apexpost_app AUTHORIZATION dbo;

-- Rows the application changes.
GRANT SELECT, INSERT, UPDATE ON dbo.Branches TO apexpost_app;
GRANT SELECT, INSERT, UPDATE ON dbo.BranchStock TO apexpost_app;
GRANT SELECT, INSERT, UPDATE ON dbo.Counters TO apexpost_app;
GRANT SELECT, INSERT, UPDATE ON dbo.CounterTransactions TO apexpost_app;
GRANT SELECT, INSERT, UPDATE ON dbo.IdentityVerifications TO apexpost_app;
GRANT SELECT, INSERT, UPDATE ON dbo.MoneyOrders TO apexpost_app;
GRANT SELECT, INSERT, UPDATE ON dbo.Products TO apexpost_app;
GRANT SELECT, INSERT, UPDATE ON dbo.Shipments TO apexpost_app;
GRANT SELECT, INSERT, UPDATE ON dbo.ShippingRates TO apexpost_app;
GRANT SELECT, INSERT, UPDATE ON dbo.ShippingZones TO apexpost_app;
GRANT SELECT, INSERT, UPDATE ON dbo.Staff TO apexpost_app;
GRANT SELECT, INSERT, UPDATE ON dbo.StaffCredentials TO apexpost_app;

-- History: added to, never changed.
GRANT SELECT, INSERT ON dbo.AuditEntries TO apexpost_app;
GRANT SELECT, INSERT ON dbo.Payments TO apexpost_app;
GRANT SELECT, INSERT ON dbo.StockAdjustments TO apexpost_app;
GRANT SELECT, INSERT ON dbo.TransactionLines TO apexpost_app;
GRANT SELECT, INSERT ON dbo.TransactionTaxRates TO apexpost_app;

-- Fixed by migrations.
GRANT SELECT ON dbo.ProductCategories TO apexpost_app;
GRANT SELECT ON dbo.Roles TO apexpost_app;

GRANT EXECUTE ON dbo.usp_DailyBranchSales TO apexpost_app;
GRANT EXECUTE ON dbo.usp_LowStockReport TO apexpost_app;
GRANT EXECUTE ON dbo.usp_AdjustStock TO apexpost_app;

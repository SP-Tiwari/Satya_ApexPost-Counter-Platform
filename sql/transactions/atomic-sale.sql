-- Records one paid counter sale atomically: stock decrement, transaction, payment,
-- lines and tax rates either all commit or all roll back.
--
-- Uses the codes of the demo data (sql/demos/demo-fixture.sql or the seeder).
-- Tables are touched in the order documented in DESIGN.md section 4.7, and the
-- transaction holds no user wait, so its locks are short-lived.
SET NOCOUNT ON;
-- Any run-time error rolls the whole transaction back, including errors that
-- TRY...CATCH cannot catch in this scope.
SET XACT_ABORT ON;

DECLARE @TransactionId uniqueidentifier = NEWID();
DECLARE @BranchCode varchar(10) = 'DM01';
DECLARE @CounterId varchar(20) = 'DEMO-T01-01';
DECLARE @ClerkStaffId varchar(50) = 'demo-staff-01';
DECLARE @PaymentMethod nvarchar(50) = N'DEMO Cash';
DECLARE @Currency char(3) = 'INR';
DECLARE @OccurredAtUtc datetime2(3) = SYSUTCDATETIME();

DECLARE @Lines TABLE (Sku varchar(30) PRIMARY KEY, Quantity int NOT NULL CHECK (Quantity > 0));
INSERT INTO @Lines (Sku, Quantity) VALUES ('DEMO-SKU-001', 2), ('DEMO-SKU-002', 1);

DECLARE @TaxRates TABLE (TaxCategory varchar(20) PRIMARY KEY, Rate decimal(9, 6) NOT NULL);
INSERT INTO @TaxRates (TaxCategory, Rate)
VALUES ('Standard', 0.18), ('Reduced', 0.05), ('ZeroRated', 0);

BEGIN TRY
    BEGIN TRANSACTION;

    -- The quantity test and the decrement are one statement, so two sales
    -- cannot both take the last unit.
    UPDATE s
    SET s.Quantity = s.Quantity - l.Quantity,
        s.ModifiedAtUtc = SYSUTCDATETIME()
    FROM dbo.BranchStock AS s
    INNER JOIN @Lines AS l ON l.Sku = s.Sku
    WHERE s.BranchCode = @BranchCode
        AND s.Quantity >= l.Quantity;

    IF @@ROWCOUNT <> (SELECT COUNT(*) FROM @Lines)
    BEGIN
        ;THROW 51003, N'Insufficient stock available.', 1;
    END;

    INSERT INTO dbo.CounterTransactions
        (TransactionId, Currency, OccurredAtUtc, CounterId, ClerkStaffId,
         PaymentMethod, BranchCode, Status, CreatedBy)
    VALUES
        (@TransactionId, @Currency, @OccurredAtUtc, @CounterId, @ClerkStaffId,
         @PaymentMethod, @BranchCode, 'Paid', @ClerkStaffId);

    INSERT INTO dbo.Payments
        (PaymentId, TransactionId, EntryType, PaymentMethod, RecordedAtUtc,
         Amount, Currency, CreatedBy)
    SELECT
        NEWID(), @TransactionId, 'Payment', @PaymentMethod, @OccurredAtUtc,
        ROUND(SUM(l.Quantity * p.UnitPrice * (1 + r.Rate)), 2), @Currency, @ClerkStaffId
    FROM @Lines AS l
    INNER JOIN dbo.Products AS p ON p.Sku = l.Sku
    INNER JOIN @TaxRates AS r ON r.TaxCategory = p.TaxCategory;

    INSERT INTO dbo.TransactionLines
        (TransactionId, Description, LineType, Quantity, TaxCategory, Sku,
         UnitPrice, Currency, CreatedBy)
    SELECT
        @TransactionId, p.Name, 'Goods', l.Quantity, p.TaxCategory, p.Sku,
        p.UnitPrice, p.Currency, @ClerkStaffId
    FROM @Lines AS l
    INNER JOIN dbo.Products AS p ON p.Sku = l.Sku;

    INSERT INTO dbo.TransactionTaxRates (TransactionId, TaxCategory, Rate, CreatedBy)
    SELECT @TransactionId, TaxCategory, Rate, @ClerkStaffId
    FROM @TaxRates;

    COMMIT TRANSACTION;

    SELECT @TransactionId AS TransactionId, N'Committed' AS Outcome;
END TRY
BEGIN CATCH
    -- XACT_STATE() is -1 when the transaction can only roll back, 1 when it is
    -- still committable, and 0 when none is open. A failed sale rolls back in
    -- both of the first two cases.
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    ;THROW;
END CATCH;

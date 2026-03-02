-- NAPAS Switch - Full Database Schema
-- Combines original schema and Phase 1/Phase 2 migrations
-- Includes schema modernization improvements:
--   - Composite covering indexes for key query patterns
--   - CHECK constraints for data integrity (Direction, Amount, MessageType, ResponseCode)
--   - ErrorReason column separation from ResponseCode in UnsettledTransactions
--   - Computed TransactionDate column for future date-based partitioning
--   - UpdatedAt auto-trigger for UnsettledTransactions
--   - Settlement stored procedure (UnsettledTransactions ? TransactionLog at end-of-day)
--   - Archival stored procedures for long-term scalability
--   - Fixed AcquirerID/IssuerID column size mismatch (standardized to VARCHAR(11))
--   - TransactionType column for PURCHASE, BALANCE_INQUIRY, VOID, REVERSAL tracking
--   - SettlementDate column for settlement-day scoping
--   - OriginalTransactionId for void/reversal linking
--   - Renamed PendingTransactions ? UnsettledTransactions (settlement-day architecture)
-- Run this script in SQL Server Management Studio (SSMS)

USE [master];
GO

-- Create database if it doesn't exist
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = N'NAPASSwitch')
BEGIN
    CREATE DATABASE [NAPASSwitch];
    PRINT 'Database NAPASSwitch created.';
END
GO

USE [NAPASSwitch];
GO

PRINT '=== NAPAS Switch Database Schema Initialization ==='
PRINT ''

-- ============================================================
-- 1. Create the TransactionLog table
--    Serves as the historical audit log for all messages AND
--    the archive for settled transactions from UnsettledTransactions.
-- ============================================================
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='TransactionLog' and xtype='U')
BEGIN
    CREATE TABLE TransactionLog (
        Id BIGINT PRIMARY KEY IDENTITY(1,1),
        SessionId VARCHAR(20) NOT NULL,
        MessageType VARCHAR(4) NOT NULL
            CONSTRAINT CK_TransactionLog_MessageType CHECK (MessageType LIKE '[0-9][0-9][0-9][0-9]'),
        PAN VARCHAR(128) NULL,
        ProcessingCode VARCHAR(6) NULL,
        Amount DECIMAL(18,2) NULL
            CONSTRAINT CK_TransactionLog_Amount CHECK (Amount IS NULL OR Amount >= 0),
        STAN VARCHAR(6) NULL,
        AcquirerID VARCHAR(11) NULL,
        IssuerID VARCHAR(11) NULL,
        ResponseCode VARCHAR(3) NULL
            CONSTRAINT CK_TransactionLog_ResponseCode CHECK (ResponseCode IS NULL OR LEN(ResponseCode) BETWEEN 2 AND 3),
        TerminalID VARCHAR(16) NULL,
        MerchantID VARCHAR(15) NULL,
        TransactionTime DATETIME2 NOT NULL,
        LoggedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        ProcessingTimeMs INT NOT NULL DEFAULT 0,
        Direction VARCHAR(10) NOT NULL
            CONSTRAINT CK_TransactionLog_Direction CHECK (Direction IN ('INBOUND','OUTBOUND','COMPLETE','TIMEOUT','ERROR','ADVICE','SETTLED')),
        
        -- Transaction classification (derived from MTI + Processing Code)
        TransactionType VARCHAR(20) NULL,

        -- Settlement date (populated when settled from UnsettledTransactions, NULL for real-time audit entries)
        SettlementDate DATE NULL,
        
        -- Computed column for future date-based partitioning
        TransactionDate AS CAST(TransactionTime AS DATE) PERSISTED,
        
        -- Single-column indexes for ad-hoc queries
        INDEX IX_SessionId (SessionId),
        INDEX IX_TransactionTime (TransactionTime),
        INDEX IX_AcquirerID (AcquirerID),
        INDEX IX_IssuerID (IssuerID),
        INDEX IX_STAN (STAN)
    );

    -- Composite covering index for GetStatsAsync aggregation query
    -- Covers: WHERE TransactionTime BETWEEN @From AND @To AND Direction = 'COMPLETE'
    -- Includes all columns needed by SELECT to avoid key lookups
    CREATE NONCLUSTERED INDEX IX_TransactionLog_Stats 
        ON TransactionLog(TransactionTime, Direction)
        INCLUDE (ResponseCode, Amount, ProcessingTimeMs);

    -- Composite index for response code filtering by direction
    CREATE NONCLUSTERED INDEX IX_TransactionLog_ResponseCode_Direction 
        ON TransactionLog(ResponseCode, Direction)
        INCLUDE (Amount, TransactionTime);

    -- Index for settlement queries by date and type
    CREATE NONCLUSTERED INDEX IX_TransactionLog_Settlement
        ON TransactionLog(SettlementDate, TransactionType)
        INCLUDE (Amount, ResponseCode);

    PRINT '  TransactionLog table created successfully';
END
ELSE
BEGIN
    PRINT '- TransactionLog table already exists, checking for missing columns...'
    
    -- Sync columns for Phase 1 Migration (fixed: VARCHAR(11) to match CREATE TABLE)
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'AcquirerID')
    BEGIN
        ALTER TABLE TransactionLog ADD AcquirerID VARCHAR(11) NULL;
        PRINT '  + Added AcquirerID column';
    END
    
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'IssuerID')
    BEGIN
        ALTER TABLE TransactionLog ADD IssuerID VARCHAR(11) NULL;
        PRINT '  + Added IssuerID column';
    END

    -- Sync indexes
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_AcquirerID' AND object_id = OBJECT_ID('TransactionLog'))
    BEGIN
        CREATE INDEX IX_AcquirerID ON TransactionLog(AcquirerID);
        PRINT '  + Created index IX_AcquirerID';
    END

    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_IssuerID' AND object_id = OBJECT_ID('TransactionLog'))
    BEGIN
        CREATE INDEX IX_IssuerID ON TransactionLog(IssuerID);
        PRINT '  + Created index IX_IssuerID';
    END

    -- Sync computed column for date-based partitioning
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'TransactionDate')
    BEGIN
        ALTER TABLE TransactionLog ADD TransactionDate AS CAST(TransactionTime AS DATE) PERSISTED;
        PRINT '  + Added TransactionDate computed column';
    END

    -- Sync STAN index
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_STAN' AND object_id = OBJECT_ID('TransactionLog'))
    BEGIN
        CREATE INDEX IX_STAN ON TransactionLog(STAN);
        PRINT '  + Created index IX_STAN';
    END

    -- Widen PAN column to hold encrypted (Base64) values
    IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'PAN' AND CHARACTER_MAXIMUM_LENGTH < 128)
    BEGIN
        ALTER TABLE TransactionLog ALTER COLUMN PAN VARCHAR(128) NULL;
        PRINT '  + Widened PAN column to VARCHAR(128) for encrypted storage';
    END

    -- Sync composite covering index for stats query
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_TransactionLog_Stats' AND object_id = OBJECT_ID('TransactionLog'))
    BEGIN
        CREATE NONCLUSTERED INDEX IX_TransactionLog_Stats 
            ON TransactionLog(TransactionTime, Direction)
            INCLUDE (ResponseCode, Amount, ProcessingTimeMs);
        PRINT '  + Created composite covering index IX_TransactionLog_Stats';
    END

    -- Sync composite index for response code filtering
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_TransactionLog_ResponseCode_Direction' AND object_id = OBJECT_ID('TransactionLog'))
    BEGIN
        CREATE NONCLUSTERED INDEX IX_TransactionLog_ResponseCode_Direction 
            ON TransactionLog(ResponseCode, Direction)
            INCLUDE (Amount, TransactionTime);
        PRINT '  + Created composite index IX_TransactionLog_ResponseCode_Direction';
    END

    -- Sync CHECK constraints (only add if not already present)
    IF NOT EXISTS (SELECT * FROM sys.check_constraints WHERE name = 'CK_TransactionLog_Direction')
    BEGIN
        ALTER TABLE TransactionLog ADD CONSTRAINT CK_TransactionLog_Direction 
            CHECK (Direction IN ('INBOUND','OUTBOUND','COMPLETE','TIMEOUT','ERROR','ADVICE','SETTLED'));
        PRINT '  + Added CHECK constraint CK_TransactionLog_Direction';
    END
    ELSE
    BEGIN
        -- Update existing constraint to include ADVICE and SETTLED
        DECLARE @ConstraintDef NVARCHAR(MAX);
        SELECT @ConstraintDef = definition FROM sys.check_constraints WHERE name = 'CK_TransactionLog_Direction';
        IF @ConstraintDef NOT LIKE '%SETTLED%'
        BEGIN
            ALTER TABLE TransactionLog DROP CONSTRAINT CK_TransactionLog_Direction;
            ALTER TABLE TransactionLog ADD CONSTRAINT CK_TransactionLog_Direction 
                CHECK (Direction IN ('INBOUND','OUTBOUND','COMPLETE','TIMEOUT','ERROR','ADVICE','SETTLED'));
            PRINT '  + Updated CHECK constraint CK_TransactionLog_Direction (added SETTLED)';
        END
    END

    IF NOT EXISTS (SELECT * FROM sys.check_constraints WHERE name = 'CK_TransactionLog_Amount')
    BEGIN
        ALTER TABLE TransactionLog ADD CONSTRAINT CK_TransactionLog_Amount 
            CHECK (Amount IS NULL OR Amount >= 0);
        PRINT '  + Added CHECK constraint CK_TransactionLog_Amount';
    END

    IF NOT EXISTS (SELECT * FROM sys.check_constraints WHERE name = 'CK_TransactionLog_MessageType')
    BEGIN
        ALTER TABLE TransactionLog ADD CONSTRAINT CK_TransactionLog_MessageType 
            CHECK (MessageType LIKE '[0-9][0-9][0-9][0-9]');
        PRINT '  + Added CHECK constraint CK_TransactionLog_MessageType';
    END

    IF NOT EXISTS (SELECT * FROM sys.check_constraints WHERE name = 'CK_TransactionLog_ResponseCode')
    BEGIN
        ALTER TABLE TransactionLog ADD CONSTRAINT CK_TransactionLog_ResponseCode 
            CHECK (ResponseCode IS NULL OR LEN(ResponseCode) BETWEEN 2 AND 3);
        PRINT '  + Added CHECK constraint CK_TransactionLog_ResponseCode';
    END

    -- Phase 2: Add TransactionType column
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'TransactionType')
    BEGIN
        ALTER TABLE TransactionLog ADD TransactionType VARCHAR(20) NULL;
        PRINT '  + Added TransactionType column';
    END

    -- Phase 2: Add SettlementDate column
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'SettlementDate')
    BEGIN
        ALTER TABLE TransactionLog ADD SettlementDate DATE NULL;
        PRINT '  + Added SettlementDate column';
    END

    -- Phase 2: Settlement query index
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_TransactionLog_Settlement' AND object_id = OBJECT_ID('TransactionLog'))
    BEGIN
        CREATE NONCLUSTERED INDEX IX_TransactionLog_Settlement
            ON TransactionLog(SettlementDate, TransactionType)
            INCLUDE (Amount, ResponseCode);
        PRINT '  + Created composite index IX_TransactionLog_Settlement';
    END
END

-- ============================================================
-- 2. Create the UnsettledTransactions table
--    Holds all transactions for the current settlement day.
--    At end-of-day, settled records are moved to TransactionLog.
--    Void/reversal can only target MATCHED records from the
--    same SettlementDate.
-- ============================================================

-- Phase 2 migration: Rename PendingTransactions ? UnsettledTransactions if the old table exists
IF EXISTS (SELECT * FROM sysobjects WHERE name='PendingTransactions' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sysobjects WHERE name='UnsettledTransactions' AND xtype='U')
BEGIN
    EXEC sp_rename 'PendingTransactions', 'UnsettledTransactions';
    PRINT '  + Renamed PendingTransactions ? UnsettledTransactions';

    -- Rename constraints that reference the old table name
    IF EXISTS (SELECT * FROM sys.check_constraints WHERE name = 'CK_PendingTransactions_MessageType')
    BEGIN
        EXEC sp_rename 'CK_PendingTransactions_MessageType', 'CK_UnsettledTransactions_MessageType', 'OBJECT';
        PRINT '  + Renamed constraint CK_PendingTransactions_MessageType';
    END
    IF EXISTS (SELECT * FROM sys.check_constraints WHERE name = 'CK_PendingTransactions_Amount')
    BEGIN
        EXEC sp_rename 'CK_PendingTransactions_Amount', 'CK_UnsettledTransactions_Amount', 'OBJECT';
        PRINT '  + Renamed constraint CK_PendingTransactions_Amount';
    END
    IF EXISTS (SELECT * FROM sys.check_constraints WHERE name = 'CK_PendingTransactions_Status')
    BEGIN
        -- Drop old constraint and recreate with new values (VOIDED, REVERSED added)
        ALTER TABLE UnsettledTransactions DROP CONSTRAINT CK_PendingTransactions_Status;
        ALTER TABLE UnsettledTransactions ADD CONSTRAINT CK_UnsettledTransactions_Status 
            CHECK (Status IN ('PENDING','MATCHED','MISMATCH','EXPIRED','VOIDED','REVERSED'));
        PRINT '  + Replaced Status constraint with expanded values (added VOIDED, REVERSED)';
    END
    IF EXISTS (SELECT * FROM sys.check_constraints WHERE name = 'CK_PendingTransactions_ResponseCode')
    BEGIN
        EXEC sp_rename 'CK_PendingTransactions_ResponseCode', 'CK_UnsettledTransactions_ResponseCode', 'OBJECT';
        PRINT '  + Renamed constraint CK_PendingTransactions_ResponseCode';
    END
END

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='UnsettledTransactions' and xtype='U')
BEGIN
    CREATE TABLE UnsettledTransactions (
        Id BIGINT PRIMARY KEY IDENTITY(1,1),
        TransactionId VARCHAR(128) NOT NULL UNIQUE,
        SessionId VARCHAR(20) NOT NULL,
        MessageType VARCHAR(4) NOT NULL
            CONSTRAINT CK_UnsettledTransactions_MessageType CHECK (MessageType LIKE '[0-9][0-9][0-9][0-9]'),
        
        -- Transaction classification
        TransactionType VARCHAR(20) NOT NULL
            CONSTRAINT CK_UnsettledTransactions_TransactionType 
            CHECK (TransactionType IN ('PURCHASE','BALANCE_INQUIRY','CASH_WITHDRAWAL','CASH_DEPOSIT',
                                       'TRANSFER','REFUND','VOID','REVERSAL','FINANCIAL','NETWORK_MGMT')),
        SettlementDate DATE NOT NULL,
        
        -- Request data (encrypted PAN)
        RequestPAN VARCHAR(128) NULL,   
        RequestAmount DECIMAL(18,2) NOT NULL
            CONSTRAINT CK_UnsettledTransactions_Amount CHECK (RequestAmount >= 0),
        RequestProcessingCode VARCHAR(6) NOT NULL,
        RequestSTAN VARCHAR(6) NOT NULL,
        RequestDateTime VARCHAR(10) NOT NULL,
        RequestAcquirerID VARCHAR(11) NULL,
        RequestTerminalID VARCHAR(16) NULL,
        RequestMerchantID VARCHAR(15) NULL,
        RequestRRN VARCHAR(12) NULL,
        RequestTRN VARCHAR(50) NULL,
        
        -- Full ISO message for complete validation
        RequestMessageBytes VARBINARY(MAX) NOT NULL,
        
        -- Status tracking (VOIDED/REVERSED = void/reversal was successful on this transaction)
        Status VARCHAR(10) NOT NULL DEFAULT 'PENDING'
            CONSTRAINT CK_UnsettledTransactions_Status 
            CHECK (Status IN ('PENDING','MATCHED','MISMATCH','EXPIRED','VOIDED','REVERSED')),
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        ExpiresAt DATETIME2 NOT NULL,
        
        -- Response tracking (ResponseCode for ISO codes only, ErrorReason for mismatch details)
        ResponseReceivedAt DATETIME2 NULL,
        ResponseCode VARCHAR(3) NULL
            CONSTRAINT CK_UnsettledTransactions_ResponseCode CHECK (ResponseCode IS NULL OR LEN(ResponseCode) BETWEEN 2 AND 3),
        ErrorReason VARCHAR(255) NULL,
        
        -- Void/Reversal: link back to the original transaction
        OriginalTransactionId VARCHAR(128) NULL,
        
        INDEX IX_SessionId (SessionId),

        -- Composite index for cleanup query: WHERE Status = 'PENDING' AND ExpiresAt < @Time
        INDEX IX_UnsettledTransactions_Cleanup (Status, ExpiresAt),

        -- Index for settlement date scoping (void/reversal same-day lookup)
        INDEX IX_UnsettledTransactions_Settlement (SettlementDate, Status, TransactionType),

        -- Index for original transaction lookup (void/reversal linking)
        INDEX IX_UnsettledTransactions_OriginalTxn (OriginalTransactionId)
    );

    -- Composite index for STAN lookup: WHERE RequestSTAN = @STAN AND Status = 'PENDING'
    CREATE NONCLUSTERED INDEX IX_UnsettledTransactions_STAN_Status 
        ON UnsettledTransactions(RequestSTAN, Status)
        INCLUDE (TransactionId, SessionId, CreatedAt);

    -- Composite index for TRN lookup: WHERE RequestTRN = @TRN AND Status = 'PENDING'
    CREATE NONCLUSTERED INDEX IX_UnsettledTransactions_TRN_Status 
        ON UnsettledTransactions(RequestTRN, Status)
        INCLUDE (TransactionId, SessionId, CreatedAt);

    PRINT '  UnsettledTransactions table created successfully';
END
ELSE
BEGIN
    PRINT '- UnsettledTransactions table already exists, checking for missing columns...'

    -- Widen TransactionId column (was VARCHAR(50), needs 51+ for TXN-yyyyMMddHHmmss-GUID format)
    IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'UnsettledTransactions' AND COLUMN_NAME = 'TransactionId' AND CHARACTER_MAXIMUM_LENGTH < 128)
    BEGIN
        ALTER TABLE UnsettledTransactions ALTER COLUMN TransactionId VARCHAR(128) NOT NULL;
        PRINT '  + Widened TransactionId column to VARCHAR(128)';
    END

    -- Sync TRN column
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'UnsettledTransactions' AND COLUMN_NAME = 'RequestTRN')
    BEGIN
        ALTER TABLE UnsettledTransactions ADD RequestTRN VARCHAR(50) NULL;
        PRINT '  + Added RequestTRN column';
    END

    -- Sync ErrorReason column (separates error description from ISO ResponseCode)
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'UnsettledTransactions' AND COLUMN_NAME = 'ErrorReason')
    BEGIN
        ALTER TABLE UnsettledTransactions ADD ErrorReason VARCHAR(255) NULL;
        PRINT '  + Added ErrorReason column';
    END

    -- Phase 2: Add TransactionType column
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'UnsettledTransactions' AND COLUMN_NAME = 'TransactionType')
    BEGIN
        ALTER TABLE UnsettledTransactions ADD TransactionType VARCHAR(20) NOT NULL DEFAULT 'FINANCIAL';
        PRINT '  + Added TransactionType column';
    END

    -- Phase 2: Add SettlementDate column
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'UnsettledTransactions' AND COLUMN_NAME = 'SettlementDate')
    BEGIN
        ALTER TABLE UnsettledTransactions ADD SettlementDate DATE NOT NULL DEFAULT CAST(GETUTCDATE() AS DATE);
        PRINT '  + Added SettlementDate column';
    END

    -- Phase 2: Add OriginalTransactionId column for void/reversal linking
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'UnsettledTransactions' AND COLUMN_NAME = 'OriginalTransactionId')
    BEGIN
        ALTER TABLE UnsettledTransactions ADD OriginalTransactionId VARCHAR(128) NULL;
        PRINT '  + Added OriginalTransactionId column';
    END

    -- Sync composite TRN+Status index
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UnsettledTransactions_TRN_Status' AND object_id = OBJECT_ID('UnsettledTransactions'))
    BEGIN
        CREATE NONCLUSTERED INDEX IX_UnsettledTransactions_TRN_Status 
            ON UnsettledTransactions(RequestTRN, Status)
            INCLUDE (TransactionId, SessionId, CreatedAt);
        PRINT '  + Created composite index IX_UnsettledTransactions_TRN_Status';
    END

    -- Sync composite cleanup index (Status + ExpiresAt)
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UnsettledTransactions_Cleanup' AND object_id = OBJECT_ID('UnsettledTransactions'))
    BEGIN
        CREATE NONCLUSTERED INDEX IX_UnsettledTransactions_Cleanup 
            ON UnsettledTransactions(Status, ExpiresAt);
        PRINT '  + Created composite index IX_UnsettledTransactions_Cleanup';
    END

    -- Sync composite STAN+Status index
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UnsettledTransactions_STAN_Status' AND object_id = OBJECT_ID('UnsettledTransactions'))
    BEGIN
        CREATE NONCLUSTERED INDEX IX_UnsettledTransactions_STAN_Status 
            ON UnsettledTransactions(RequestSTAN, Status)
            INCLUDE (TransactionId, SessionId, CreatedAt);
        PRINT '  + Created composite index IX_UnsettledTransactions_STAN_Status';
    END

    -- Phase 2: Settlement date + status + type composite index
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UnsettledTransactions_Settlement' AND object_id = OBJECT_ID('UnsettledTransactions'))
    BEGIN
        CREATE NONCLUSTERED INDEX IX_UnsettledTransactions_Settlement
            ON UnsettledTransactions(SettlementDate, Status, TransactionType);
        PRINT '  + Created composite index IX_UnsettledTransactions_Settlement';
    END

    -- Phase 2: Original transaction lookup index
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_UnsettledTransactions_OriginalTxn' AND object_id = OBJECT_ID('UnsettledTransactions'))
    BEGIN
        CREATE NONCLUSTERED INDEX IX_UnsettledTransactions_OriginalTxn
            ON UnsettledTransactions(OriginalTransactionId);
        PRINT '  + Created index IX_UnsettledTransactions_OriginalTxn';
    END

    -- Sync CHECK constraints
    IF NOT EXISTS (SELECT * FROM sys.check_constraints WHERE name = 'CK_UnsettledTransactions_Amount')
    BEGIN
        ALTER TABLE UnsettledTransactions ADD CONSTRAINT CK_UnsettledTransactions_Amount 
            CHECK (RequestAmount >= 0);
        PRINT '  + Added CHECK constraint CK_UnsettledTransactions_Amount';
    END

    IF NOT EXISTS (SELECT * FROM sys.check_constraints WHERE name = 'CK_UnsettledTransactions_MessageType')
    BEGIN
        ALTER TABLE UnsettledTransactions ADD CONSTRAINT CK_UnsettledTransactions_MessageType 
            CHECK (MessageType LIKE '[0-9][0-9][0-9][0-9]');
        PRINT '  + Added CHECK constraint CK_UnsettledTransactions_MessageType';
    END

    -- Ensure Status CHECK includes VOIDED and REVERSED
    IF NOT EXISTS (SELECT * FROM sys.check_constraints WHERE name = 'CK_UnsettledTransactions_Status')
    BEGIN
        ALTER TABLE UnsettledTransactions ADD CONSTRAINT CK_UnsettledTransactions_Status 
            CHECK (Status IN ('PENDING','MATCHED','MISMATCH','EXPIRED','VOIDED','REVERSED'));
        PRINT '  + Added CHECK constraint CK_UnsettledTransactions_Status';
    END
    ELSE
    BEGIN
        DECLARE @StatusDef NVARCHAR(MAX);
        SELECT @StatusDef = definition FROM sys.check_constraints WHERE name = 'CK_UnsettledTransactions_Status';
        IF @StatusDef NOT LIKE '%VOIDED%'
        BEGIN
            ALTER TABLE UnsettledTransactions DROP CONSTRAINT CK_UnsettledTransactions_Status;
            ALTER TABLE UnsettledTransactions ADD CONSTRAINT CK_UnsettledTransactions_Status 
                CHECK (Status IN ('PENDING','MATCHED','MISMATCH','EXPIRED','VOIDED','REVERSED'));
            PRINT '  + Updated Status constraint (added VOIDED, REVERSED)';
        END
    END

    -- Widen RequestPAN column to hold encrypted (Base64) values
    IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'UnsettledTransactions' AND COLUMN_NAME = 'RequestPAN' AND CHARACTER_MAXIMUM_LENGTH < 128)
    BEGIN
        ALTER TABLE UnsettledTransactions ALTER COLUMN RequestPAN VARCHAR(128) NULL;
        PRINT '  + Widened RequestPAN column to VARCHAR(128) for encrypted storage';
    END
END

-- ============================================================
-- 3. Create UpdatedAt auto-trigger for UnsettledTransactions
--    Ensures UpdatedAt is always set correctly even for direct
--    SQL updates, not just application-level changes.
-- ============================================================

-- Drop old trigger if it references the renamed table
IF EXISTS (SELECT * FROM sys.triggers WHERE name = 'TR_PendingTransactions_UpdatedAt')
BEGIN
    DROP TRIGGER TR_PendingTransactions_UpdatedAt;
    PRINT '  + Dropped old trigger TR_PendingTransactions_UpdatedAt';
END

IF NOT EXISTS (SELECT * FROM sys.triggers WHERE name = 'TR_UnsettledTransactions_UpdatedAt')
BEGIN
    EXEC('
        CREATE TRIGGER TR_UnsettledTransactions_UpdatedAt
        ON UnsettledTransactions
        AFTER UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            -- Only fire when relevant columns change (avoids recursive trigger)
            IF UPDATE(Status) OR UPDATE(ResponseCode) OR UPDATE(ErrorReason)
            BEGIN
                UPDATE ut
                SET ut.UpdatedAt = GETUTCDATE()
                FROM UnsettledTransactions ut
                INNER JOIN inserted i ON ut.Id = i.Id;
            END
        END
    ');
    PRINT '  TR_UnsettledTransactions_UpdatedAt trigger created';
END
ELSE
    PRINT '- TR_UnsettledTransactions_UpdatedAt trigger already exists';

PRINT ''
PRINT '=== Schema Initialization Complete ==='
GO

-- ============================================================
-- 4. Settlement and archival stored procedures
--    sp_SettleTransactions: Run at end of settlement day to move
--      completed transactions from UnsettledTransactions ? TransactionLog.
--    sp_ArchiveTransactionLog: Run periodically to archive old TransactionLog rows.
--    sp_CleanupUnsettledTransactions: Clean up terminal-state rows after settlement.
-- ============================================================
PRINT ''
PRINT '--- Creating stored procedures ---'

-- ============================================================
-- 4a. Settlement procedure: UnsettledTransactions ? TransactionLog
-- ============================================================
IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_SettleTransactions')
    DROP PROCEDURE sp_SettleTransactions;
GO

CREATE PROCEDURE sp_SettleTransactions
    @SettlementDate DATE = NULL,
    @BatchSize INT = 10000
AS
BEGIN
    SET NOCOUNT ON;
    
    -- Default to yesterday's settlement date (run after midnight)
    IF @SettlementDate IS NULL
        SET @SettlementDate = DATEADD(DAY, -1, CAST(GETUTCDATE() AS DATE));
    
    DECLARE @Settled INT = 0;
    DECLARE @TotalSettled INT = 0;
    
    -- Move completed transactions (MATCHED, VOIDED, REVERSED) to TransactionLog
    WHILE 1 = 1
    BEGIN
        BEGIN TRANSACTION;
        
        INSERT INTO TransactionLog (
            SessionId, MessageType, PAN, ProcessingCode, Amount, STAN,
            AcquirerID, ResponseCode, TerminalID, MerchantID,
            TransactionTime, LoggedAt, ProcessingTimeMs, Direction,
            TransactionType, SettlementDate
        )
        SELECT TOP (@BatchSize)
            SessionId, MessageType, RequestPAN, RequestProcessingCode, RequestAmount, RequestSTAN,
            RequestAcquirerID, ResponseCode, RequestTerminalID, RequestMerchantID,
            CreatedAt, GETUTCDATE(), 0, 'SETTLED',
            TransactionType, SettlementDate
        FROM UnsettledTransactions WITH (ROWLOCK)
        WHERE SettlementDate = @SettlementDate
          AND Status IN ('MATCHED', 'VOIDED', 'REVERSED');
        
        SET @Settled = @@ROWCOUNT;
        
        DELETE TOP (@BatchSize)
        FROM UnsettledTransactions WITH (ROWLOCK)
        WHERE SettlementDate = @SettlementDate
          AND Status IN ('MATCHED', 'VOIDED', 'REVERSED');
        
        COMMIT TRANSACTION;
        
        SET @TotalSettled = @TotalSettled + @Settled;
        
        IF @Settled < @BatchSize BREAK;
        
        -- Brief pause to reduce contention
        WAITFOR DELAY '00:00:00.100';
    END
    
    PRINT 'Settled ' + CAST(@TotalSettled AS VARCHAR(20)) + ' transactions for date ' + CONVERT(VARCHAR(10), @SettlementDate, 120);
END
GO

PRINT '  sp_SettleTransactions procedure created';
GO

-- ============================================================
-- 4b. Archive old TransactionLog entries
-- ============================================================
IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_ArchiveTransactionLog')
    DROP PROCEDURE sp_ArchiveTransactionLog;
GO

CREATE PROCEDURE sp_ArchiveTransactionLog
    @RetentionDays INT = 90,
    @BatchSize INT = 10000
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @CutoffDate DATETIME2 = DATEADD(DAY, -@RetentionDays, GETUTCDATE());
    DECLARE @Deleted INT = 0;
    DECLARE @TotalDeleted INT = 0;
    
    -- Create archive table if it doesn't exist (same structure as source)
    IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='TransactionLogArchive' AND xtype='U')
    BEGIN
        SELECT TOP 0 * INTO TransactionLogArchive FROM TransactionLog;
        PRINT 'Created TransactionLogArchive table';
    END
    
    -- Archive in batches to avoid long-running transactions and lock escalation
    WHILE 1 = 1
    BEGIN
        BEGIN TRANSACTION;
        
        -- Move old rows from hot table to archive
        INSERT INTO TransactionLogArchive
        SELECT TOP (@BatchSize) *
        FROM TransactionLog WITH (ROWLOCK)
        WHERE TransactionTime < @CutoffDate;
        
        SET @Deleted = @@ROWCOUNT;
        
        DELETE TOP (@BatchSize)
        FROM TransactionLog WITH (ROWLOCK)
        WHERE TransactionTime < @CutoffDate;
        
        COMMIT TRANSACTION;
        
        SET @TotalDeleted = @TotalDeleted + @Deleted;
        
        IF @Deleted < @BatchSize BREAK;
        
        -- Brief pause to reduce contention on the hot table
        WAITFOR DELAY '00:00:00.100';
    END
    
    PRINT 'Archived ' + CAST(@TotalDeleted AS VARCHAR(20)) + ' rows from TransactionLog (older than ' + CAST(@RetentionDays AS VARCHAR(10)) + ' days)';
END
GO

PRINT '  sp_ArchiveTransactionLog procedure created';
GO

-- ============================================================
-- 4c. Cleanup terminal-state UnsettledTransactions after settlement
-- ============================================================
IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_CleanupPendingTransactions')
    DROP PROCEDURE sp_CleanupPendingTransactions;
GO

IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_CleanupUnsettledTransactions')
    DROP PROCEDURE sp_CleanupUnsettledTransactions;
GO

CREATE PROCEDURE sp_CleanupUnsettledTransactions
    @RetentionDays INT = 7,
    @BatchSize INT = 5000
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @CutoffDate DATETIME2 = DATEADD(DAY, -@RetentionDays, GETUTCDATE());
    DECLARE @Deleted INT = 0;
    DECLARE @TotalDeleted INT = 0;
    
    -- Only delete terminal-state rows (MATCHED, MISMATCH, EXPIRED, VOIDED, REVERSED) - never delete PENDING
    -- These should already have been settled; this is a safety net cleanup.
    WHILE 1 = 1
    BEGIN
        DELETE TOP (@BatchSize)
        FROM UnsettledTransactions WITH (ROWLOCK)
        WHERE Status IN ('MATCHED', 'MISMATCH', 'EXPIRED', 'VOIDED', 'REVERSED')
          AND UpdatedAt < @CutoffDate;
        
        SET @Deleted = @@ROWCOUNT;
        SET @TotalDeleted = @TotalDeleted + @Deleted;
        
        IF @Deleted < @BatchSize BREAK;
        
        WAITFOR DELAY '00:00:00.100';
    END
    
    PRINT 'Cleaned up ' + CAST(@TotalDeleted AS VARCHAR(20)) + ' completed UnsettledTransactions (older than ' + CAST(@RetentionDays AS VARCHAR(10)) + ' days)';
END
GO

PRINT '  sp_CleanupUnsettledTransactions procedure created';
GO

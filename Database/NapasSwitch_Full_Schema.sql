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

-- Safety net: drop orphaned trigger from prior partial migrations
-- (trigger can survive even after UpdatedAt column was dropped, causing runtime errors)
IF EXISTS (SELECT * FROM sys.triggers WHERE name = 'TR_UnsettledTransactions_UpdatedAt')
BEGIN
    DROP TRIGGER TR_UnsettledTransactions_UpdatedAt;
    PRINT '  + Dropped orphaned trigger TR_UnsettledTransactions_UpdatedAt';
END

-- ============================================================
-- 1. Create the MessageCycle table
--    Tracks each leg of the message cycle (FORWARDED, RECEIVED, OUTBOUND)
--    ACQ  = F32 Acquiring Institution ID
--    ISS  = F33 Forwarding/Issuing Institution ID
-- ============================================================
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='MessageCycle' and xtype='U')
BEGIN
    CREATE TABLE MessageCycle (
        Id BIGINT PRIMARY KEY IDENTITY(1,1),
        TransactionId VARCHAR(128) NOT NULL,
        SessionId VARCHAR(20) NOT NULL,
        ACQ VARCHAR(11) NULL,
        ISS VARCHAR(50) NULL,
        Direction VARCHAR(20) NOT NULL
            CONSTRAINT CK_MessageCycle_Direction CHECK (Direction IN ('INBOUND','FORWARDED','RECEIVED','OUTBOUND')),
        MessageType VARCHAR(4) NULL,
        ProcessingCode VARCHAR(6) NULL,
        Amount DECIMAL(18,2) NULL,
        STAN VARCHAR(6) NULL,
        RRN VARCHAR(12) NULL,
        ResponseCode VARCHAR(3) NULL,
        LogTime DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        RawMessage VARCHAR(MAX) NOT NULL,

        INDEX IX_MessageCycle_TransactionId (TransactionId),
        INDEX IX_MessageCycle_SessionId (SessionId)
    );
    PRINT '  MessageCycle table created successfully';
END
ELSE
BEGIN
    PRINT '- MessageCycle table already exists, checking for column renames...'

    -- Phase 3 migration: Sender -> ACQ
    IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'MessageCycle' AND COLUMN_NAME = 'Sender')
       AND NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'MessageCycle' AND COLUMN_NAME = 'ACQ')
    BEGIN
        EXEC sp_rename 'MessageCycle.Sender', 'ACQ', 'COLUMN';
        PRINT '  + Renamed column Sender -> ACQ';
    END

    -- Phase 3 migration: Name -> ISS
    IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'MessageCycle' AND COLUMN_NAME = 'Name')
       AND NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'MessageCycle' AND COLUMN_NAME = 'ISS')
    BEGIN
        EXEC sp_rename 'MessageCycle.Name', 'ISS', 'COLUMN';
        PRINT '  + Renamed column Name -> ISS';
    END

    -- Phase 3 migration: MessageLog -> RawMessage
    IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'MessageCycle' AND COLUMN_NAME = 'MessageLog')
       AND NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'MessageCycle' AND COLUMN_NAME = 'RawMessage')
    BEGIN
        EXEC sp_rename 'MessageCycle.MessageLog', 'RawMessage', 'COLUMN';
        PRINT '  + Renamed column MessageLog -> RawMessage';
    END

    -- Phase 4 migration: Add ResponseCode column
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'MessageCycle' AND COLUMN_NAME = 'ResponseCode')
    BEGIN
        ALTER TABLE MessageCycle ADD ResponseCode VARCHAR(3) NULL;
        PRINT '  + Added ResponseCode column to MessageCycle';
    END
END

-- ============================================================
-- 2. Create the TransactionLog table
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

        -- Transaction classification (derived from MTI + Processing Code)
        TransactionType VARCHAR(20) NULL,

        -- Settlement date (populated when settled from UnsettledTransactions, NULL for real-time audit entries)
        SettlementDate DATE NULL,

        -- Phase 4: Additional normalized fields for reconciliation and reporting
        RRN VARCHAR(12) NULL,
        TRN VARCHAR(50) NULL,
        AuthorizationCode VARCHAR(6) NULL,
        CurrencyCode VARCHAR(3) NULL,
        POSEntryMode VARCHAR(3) NULL,

        -- Reversal/Consolidation Fields
        OriginalTransactionId VARCHAR(128) NULL,
        ErrorReason VARCHAR(255) NULL,
        RequestMessageBytes VARBINARY(MAX) NULL,

        -- Computed column for future date-based partitioning
        TransactionDate AS CAST(TransactionTime AS DATE) PERSISTED,

        -- Consolidated Tracking Fields
        TransactionId VARCHAR(128) NOT NULL UNIQUE,
        Status VARCHAR(10) NOT NULL DEFAULT 'PENDING'
            CONSTRAINT CK_TransactionLog_Status 
            CHECK (Status IN ('PENDING','MATCHED','MISMATCH','EXPIRED','VOIDED','REVERSED','ECHO')),
        ExpiresAt DATETIME2 NULL,

        -- Single-column indexes for ad-hoc queries
        INDEX IX_SessionId (SessionId),
        INDEX IX_TransactionTime (TransactionTime),
        INDEX IX_AcquirerID (AcquirerID),
        INDEX IX_IssuerID (IssuerID),
        INDEX IX_STAN (STAN),
        INDEX IX_RRN (RRN),
        INDEX IX_TRN (TRN),
        INDEX IX_TransactionLog_Status_Expires (Status, ExpiresAt),
        INDEX IX_TransactionLog_Original (OriginalTransactionId)
    );

    -- Composite covering index for GetStatsAsync aggregation query
    CREATE NONCLUSTERED INDEX IX_TransactionLog_Stats 
        ON TransactionLog(TransactionTime)
        INCLUDE (ResponseCode, Amount);

    -- Composite index for response code filtering
    CREATE NONCLUSTERED INDEX IX_TransactionLog_ResponseCode
        ON TransactionLog(ResponseCode)
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

    -- Phase 3 Migration: Drop old indexes that depend on Direction BEFORE recreating them or dropping the column
    IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'Direction')
    BEGIN
        IF EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_TransactionLog_Stats' AND object_id = OBJECT_ID('TransactionLog'))
        BEGIN
            DROP INDEX IX_TransactionLog_Stats ON TransactionLog;
            PRINT '  + Dropped old index IX_TransactionLog_Stats';
        END
        IF EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_TransactionLog_ResponseCode_Direction' AND object_id = OBJECT_ID('TransactionLog'))
        BEGIN
            DROP INDEX IX_TransactionLog_ResponseCode_Direction ON TransactionLog;
            PRINT '  + Dropped old index IX_TransactionLog_ResponseCode_Direction';
        END
    END

    -- Sync composite covering index for stats query
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_TransactionLog_Stats' AND object_id = OBJECT_ID('TransactionLog'))
    BEGIN
        CREATE NONCLUSTERED INDEX IX_TransactionLog_Stats 
            ON TransactionLog(TransactionTime)
            INCLUDE (ResponseCode, Amount);
        PRINT '  + Created composite covering index IX_TransactionLog_Stats';
    END

    -- Sync composite index for response code filtering
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_TransactionLog_ResponseCode' AND object_id = OBJECT_ID('TransactionLog'))
    BEGIN
        CREATE NONCLUSTERED INDEX IX_TransactionLog_ResponseCode 
            ON TransactionLog(ResponseCode)
            INCLUDE (Amount, TransactionTime);
        PRINT '  + Created composite index IX_TransactionLog_ResponseCode';
    END

    -- Phase 3 Migration: Drop obsolete cycle fields (Direction, ProcessingTimeMs, LoggedAt)
    -- Must drop default constraints first since system-generated names vary per database.
    IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'Direction')
    BEGIN
        DECLARE @DirDefault NVARCHAR(256);
        SELECT @DirDefault = d.name FROM sys.default_constraints d
            JOIN sys.columns c ON d.parent_object_id = c.object_id AND d.parent_column_id = c.column_id
            WHERE d.parent_object_id = OBJECT_ID('TransactionLog') AND c.name = 'Direction';
        IF @DirDefault IS NOT NULL EXEC('ALTER TABLE TransactionLog DROP CONSTRAINT ' + @DirDefault);

        IF EXISTS (SELECT * FROM sys.check_constraints WHERE name = 'CK_TransactionLog_Direction')
            ALTER TABLE TransactionLog DROP CONSTRAINT CK_TransactionLog_Direction;
            
        ALTER TABLE TransactionLog DROP COLUMN Direction;
        PRINT '  + Removed Direction column from TransactionLog';
    END

    IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'ProcessingTimeMs')
    BEGIN
        DECLARE @PtmDefault NVARCHAR(256);
        SELECT @PtmDefault = d.name FROM sys.default_constraints d
            JOIN sys.columns c ON d.parent_object_id = c.object_id AND d.parent_column_id = c.column_id
            WHERE d.parent_object_id = OBJECT_ID('TransactionLog') AND c.name = 'ProcessingTimeMs';
        IF @PtmDefault IS NOT NULL EXEC('ALTER TABLE TransactionLog DROP CONSTRAINT ' + @PtmDefault);
        ALTER TABLE TransactionLog DROP COLUMN ProcessingTimeMs;
        PRINT '  + Removed ProcessingTimeMs column from TransactionLog';
    END

    IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'LoggedAt')
    BEGIN
        DECLARE @LaDefault NVARCHAR(256);
        SELECT @LaDefault = d.name FROM sys.default_constraints d
            JOIN sys.columns c ON d.parent_object_id = c.object_id AND d.parent_column_id = c.column_id
            WHERE d.parent_object_id = OBJECT_ID('TransactionLog') AND c.name = 'LoggedAt';
        IF @LaDefault IS NOT NULL EXEC('ALTER TABLE TransactionLog DROP CONSTRAINT ' + @LaDefault);
        ALTER TABLE TransactionLog DROP COLUMN LoggedAt;
        PRINT '  + Removed LoggedAt column from TransactionLog';
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

    -- Phase 4: Add RRN column for reconciliation
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'RRN')
    BEGIN
        ALTER TABLE TransactionLog ADD RRN VARCHAR(12) NULL;
        PRINT '  + Added RRN column';
    END

    -- Phase 4: Add TRN column for transaction reference tracking
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'TRN')
    BEGIN
        ALTER TABLE TransactionLog ADD TRN VARCHAR(50) NULL;
        PRINT '  + Added TRN column';
    END

    -- Phase 4: Add AuthorizationCode column (DE#38)
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'AuthorizationCode')
    BEGIN
        ALTER TABLE TransactionLog ADD AuthorizationCode VARCHAR(6) NULL;
        PRINT '  + Added AuthorizationCode column';
    END

    -- Phase 4: Add CurrencyCode column (DE#49)
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'CurrencyCode')
    BEGIN
        ALTER TABLE TransactionLog ADD CurrencyCode VARCHAR(3) NULL;
        PRINT '  + Added CurrencyCode column';
    END

    -- Phase 4: Add POSEntryMode column (DE#22) for chip vs mag vs contactless tracking
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'POSEntryMode')
    BEGIN
        ALTER TABLE TransactionLog ADD POSEntryMode VARCHAR(3) NULL;
        PRINT '  + Added POSEntryMode column';
    END

    -- Phase 4: RRN index for reconciliation queries
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_RRN' AND object_id = OBJECT_ID('TransactionLog'))
    BEGIN
        CREATE INDEX IX_RRN ON TransactionLog(RRN);
        PRINT '  + Created index IX_RRN';
    END

    -- Phase 4: TRN index for transaction reference lookups
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_TRN' AND object_id = OBJECT_ID('TransactionLog'))
    BEGIN
        CREATE INDEX IX_TRN ON TransactionLog(TRN);
        PRINT '  + Created index IX_TRN';
    END

    -- Consolidation: columns migrated from UnsettledTransactions into TransactionLog
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'OriginalTransactionId')
    BEGIN
        ALTER TABLE TransactionLog ADD OriginalTransactionId VARCHAR(128) NULL;
        PRINT '  + Added OriginalTransactionId column';
    END

    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'ErrorReason')
    BEGIN
        ALTER TABLE TransactionLog ADD ErrorReason VARCHAR(255) NULL;
        PRINT '  + Added ErrorReason column';
    END

    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'RequestMessageBytes')
    BEGIN
        ALTER TABLE TransactionLog ADD RequestMessageBytes VARBINARY(MAX) NULL;
        PRINT '  + Added RequestMessageBytes column';
    END

    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'TransactionId')
    BEGIN
        ALTER TABLE TransactionLog ADD TransactionId VARCHAR(128) NULL;
        PRINT '  + Added TransactionId column';
    END

    -- Deferred-compilation block: columns added above (TransactionId, Status, ExpiresAt)
    -- are not visible to the batch compiler yet. EXEC() creates a new compilation scope
    -- so the DDL referencing those columns succeeds after the ALTER TABLE has executed.
    EXEC('
        IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = ''UQ_TransactionLog_TransactionId'' AND object_id = OBJECT_ID(''TransactionLog''))
           AND NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(''TransactionLog'') AND is_unique = 1
                           AND index_id IN (SELECT ic.index_id FROM sys.index_columns ic
                                            JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                                            WHERE c.name = ''TransactionId'' AND ic.object_id = OBJECT_ID(''TransactionLog'')))
        BEGIN
            CREATE UNIQUE NONCLUSTERED INDEX UQ_TransactionLog_TransactionId
                ON TransactionLog(TransactionId)
                WHERE TransactionId IS NOT NULL;
            PRINT ''  + Created unique filtered index on TransactionId'';
        END
    ');

    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'Status')
    BEGIN
        ALTER TABLE TransactionLog ADD Status VARCHAR(10) NOT NULL
            CONSTRAINT DF_TransactionLog_Status DEFAULT 'PENDING';
        PRINT '  + Added Status column';
    END

    EXEC('
        IF NOT EXISTS (SELECT * FROM sys.check_constraints WHERE name = ''CK_TransactionLog_Status'')
        BEGIN
            ALTER TABLE TransactionLog ADD CONSTRAINT CK_TransactionLog_Status 
                CHECK (Status IN (''PENDING'',''MATCHED'',''MISMATCH'',''EXPIRED'',''VOIDED'',''REVERSED'',''ECHO''));
            PRINT ''  + Added CHECK constraint CK_TransactionLog_Status'';
        END
    ');

    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'ExpiresAt')
    BEGIN
        ALTER TABLE TransactionLog ADD ExpiresAt DATETIME2 NULL;
        PRINT '  + Added ExpiresAt column';
    END

    EXEC('
        IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = ''IX_TransactionLog_Status_Expires'' AND object_id = OBJECT_ID(''TransactionLog''))
        BEGIN
            CREATE NONCLUSTERED INDEX IX_TransactionLog_Status_Expires
                ON TransactionLog(Status, ExpiresAt);
            PRINT ''  + Created composite index IX_TransactionLog_Status_Expires'';
        END

        IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = ''IX_TransactionLog_Original'' AND object_id = OBJECT_ID(''TransactionLog''))
        BEGIN
            CREATE NONCLUSTERED INDEX IX_TransactionLog_Original
                ON TransactionLog(OriginalTransactionId);
            PRINT ''  + Created index IX_TransactionLog_Original'';
        END
    ');
END

-- ============================================================
-- 3. Drop UnsettledTransactions (Logic consolidated into TransactionLog)
-- ============================================================
IF EXISTS (SELECT * FROM sysobjects WHERE name='UnsettledTransactions' and xtype='U')
BEGIN
    DROP TABLE UnsettledTransactions;
    PRINT '  UnsettledTransactions table removed';
END

IF EXISTS (SELECT * FROM sysobjects WHERE name='PendingTransactions' and xtype='U')
BEGIN
    DROP TABLE PendingTransactions;
    PRINT '  PendingTransactions table removed';
END
GO

-- ============================================================
-- 4. Archival stored procedures
--    sp_ArchiveTransactionLog: Run periodically to archive old TransactionLog rows.
-- ============================================================
PRINT ''
PRINT '--- Creating stored procedures ---'
GO

-- ============================================================
-- 4a. Archive old TransactionLog entries
-- ============================================================

-- sp_ArchiveTransactionLog: Run periodically to archive old TransactionLog rows.
CREATE OR ALTER PROCEDURE sp_ArchiveTransactionLog
    @RetentionDays INT = 90,
    @BatchSize INT = 10000
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @CutoffDate DATETIME2 = DATEADD(DAY, -@RetentionDays, GETUTCDATE());
    DECLARE @Deleted INT = 0;
    DECLARE @TotalDeleted INT = 0;
    
    -- Create archive table if it doesn't exist
    IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='TransactionLogArchive' AND xtype='U')
    BEGIN
        SELECT TOP 0 * INTO TransactionLogArchive FROM TransactionLog;
        PRINT 'Created TransactionLogArchive table';
    END
    
    WHILE 1 = 1
    BEGIN
        BEGIN TRANSACTION;
        
        -- Move old rows from hot table to archive
        -- CRITICAL: Only archive rows that are NOT in PENDING state
        INSERT INTO TransactionLogArchive
        SELECT TOP (@BatchSize) *
        FROM TransactionLog WITH (ROWLOCK)
        WHERE TransactionTime < @CutoffDate
          AND Status != 'PENDING';
        
        SET @Deleted = @@ROWCOUNT;
        
        DELETE TOP (@BatchSize)
        FROM TransactionLog WITH (ROWLOCK)
        WHERE TransactionTime < @CutoffDate
          AND Status != 'PENDING';
        
        COMMIT TRANSACTION;
        
        SET @TotalDeleted = @TotalDeleted + @Deleted;
        
        IF @Deleted < @BatchSize BREAK;
        
        WAITFOR DELAY '00:00:00.100';
    END
    
    PRINT 'Archived ' + CAST(@TotalDeleted AS VARCHAR(20)) + ' rows from TransactionLog (older than ' + CAST(@RetentionDays AS VARCHAR(10)) + ' days)';
END
GO

-- Drop obsolete procedures
IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_CleanupUnsettledTransactions')
    DROP PROCEDURE sp_CleanupUnsettledTransactions;
GO

IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_SettleTransactions')
    DROP PROCEDURE sp_SettleTransactions;
GO

IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_CleanupPendingTransactions')
    DROP PROCEDURE sp_CleanupPendingTransactions;
GO

PRINT ''
PRINT '=== Schema consolidated and initialized ==='
GO

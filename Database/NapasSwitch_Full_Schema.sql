-- NAPAS Switch - Full Database Schema
-- Combines original schema and Phase 1 migrations
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

-- 1. Create the TransactionLog table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='TransactionLog' and xtype='U')
BEGIN
    CREATE TABLE TransactionLog (
        Id BIGINT PRIMARY KEY IDENTITY(1,1),
        SessionId VARCHAR(20) NOT NULL,
        MessageType VARCHAR(4) NOT NULL,
        PAN VARCHAR(19) NULL,
        ProcessingCode VARCHAR(6) NULL,
        Amount DECIMAL(18,2) NULL,
        STAN VARCHAR(6) NULL,
        AcquirerID VARCHAR(11) NULL,
        IssuerID VARCHAR(11) NULL,
        ResponseCode VARCHAR(3) NULL,
        TerminalID VARCHAR(16) NULL,
        MerchantID VARCHAR(15) NULL,
        TransactionTime DATETIME2 NOT NULL,
        LoggedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        ProcessingTimeMs INT NOT NULL DEFAULT 0,
        Direction VARCHAR(10) NOT NULL,
        
        -- Create indexes for common queries
        INDEX IX_SessionId ON TransactionLog(SessionId),
        INDEX IX_TransactionTime ON TransactionLog(TransactionTime),
        INDEX IX_AcquirerID ON TransactionLog(AcquirerID),
        INDEX IX_IssuerID ON TransactionLog(IssuerID)
    );
    PRINT '✓ TransactionLog table created successfully';
END
ELSE
BEGIN
    PRINT '- TransactionLog table already exists, checking for missing columns...'
    
    -- Sync columns for Phase 1 Migration
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'AcquirerID')
    BEGIN
        ALTER TABLE TransactionLog ADD AcquirerID VARCHAR(50) NULL;
        PRINT '  + Added AcquirerID column';
    END
    
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TransactionLog' AND COLUMN_NAME = 'IssuerID')
    BEGIN
        ALTER TABLE TransactionLog ADD IssuerID VARCHAR(50) NULL;
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
END

-- 2. Create the PendingTransactions table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='PendingTransactions' and xtype='U')
BEGIN
    CREATE TABLE PendingTransactions (
        Id BIGINT PRIMARY KEY IDENTITY(1,1),
        TransactionId VARCHAR(50) NOT NULL UNIQUE,
        SessionId VARCHAR(20) NOT NULL,
        MessageType VARCHAR(4) NOT NULL,
        
        -- Request data (encrypted PAN)
        RequestPAN VARCHAR(19) NULL,
        RequestAmount DECIMAL(18,2) NOT NULL,
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
        
        -- Status tracking
        Status VARCHAR(10) NOT NULL DEFAULT 'PENDING'
            CONSTRAINT CK_PendingTransactions_Status CHECK (Status IN ('PENDING','MATCHED','MISMATCH','EXPIRED')),
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        ExpiresAt DATETIME2 NOT NULL,
        
        -- Response tracking
        ResponseReceivedAt DATETIME2 NULL,
        ResponseCode VARCHAR(5) NULL,
        
        INDEX IX_TransactionId ON PendingTransactions(TransactionId),
        INDEX IX_SessionId ON PendingTransactions(SessionId),
        INDEX IX_Status ON PendingTransactions(Status),
        INDEX IX_ExpiresAt ON PendingTransactions(ExpiresAt),
        INDEX IX_STAN ON PendingTransactions(RequestSTAN),
        INDEX IX_RequestTRN ON PendingTransactions(RequestTRN)
    );
    PRINT '✓ PendingTransactions table created successfully';
END
ELSE
BEGIN
    PRINT '- PendingTransactions table already exists, checking for missing columns...'

    -- Sync TRN column
    IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'PendingTransactions' AND COLUMN_NAME = 'RequestTRN')
    BEGIN
        ALTER TABLE PendingTransactions ADD RequestTRN VARCHAR(50) NULL;
        PRINT '  + Added RequestTRN column';
    END

    -- Sync TRN index
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_RequestTRN' AND object_id = OBJECT_ID('PendingTransactions'))
    BEGIN
        CREATE INDEX IX_RequestTRN ON PendingTransactions(RequestTRN);
        PRINT '  + Created index IX_RequestTRN';
    END
END

PRINT ''
PRINT '=== Schema Initialization Complete ==='
GO

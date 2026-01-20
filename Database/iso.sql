-- NAPAS Payment Switch - Database Schema
-- Run this script in SQL Server Management Studio (SSMS)

-- Create the TransactionLog table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='TransactionLog' and xtype='U')
BEGIN
    CREATE TABLE TransactionLog (
        Id BIGINT PRIMARY KEY IDENTITY(1,1),
        SessionId VARCHAR(255) NOT NULL,
        MessageType VARCHAR(255) NOT NULL,
        PAN VARCHAR(50) NULL,
        ProcessingCode VARCHAR(50) NULL,
        Amount DECIMAL(18,2) NULL,
        STAN VARCHAR(50) NULL,
        AcquirerID VARCHAR(50) NULL,
        IssuerID VARCHAR(50) NULL,
        ResponseCode VARCHAR(50) NULL,
        TerminalID VARCHAR(50) NULL,
        MerchantID VARCHAR(50) NULL,
        TransactionTime DATETIME2 NOT NULL,
        LoggedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        ProcessingTimeMs INT NOT NULL,
        Direction VARCHAR(50) NOT NULL,
        
        -- Create indexes for common queries
        INDEX IX_SessionId ON TransactionLog(SessionId),
        INDEX IX_TransactionTime ON TransactionLog(TransactionTime),
        INDEX IX_AcquirerID ON TransactionLog(AcquirerID)
    );
    
    PRINT 'TransactionLog table created successfully';
END
ELSE
BEGIN
    PRINT 'TransactionLog table already exists';
END

-- Create the PendingTransactions table for request/response correlation
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='PendingTransactions' and xtype='U')
BEGIN
    CREATE TABLE PendingTransactions (
        Id BIGINT PRIMARY KEY IDENTITY(1,1),
        TransactionId VARCHAR(50) NOT NULL UNIQUE,
        SessionId VARCHAR(255) NOT NULL,
        MessageType VARCHAR(10) NOT NULL,
        
        -- Request data (encrypted PAN)
        RequestPAN VARCHAR(255) NULL,
        RequestAmount DECIMAL(18,2) NOT NULL,
        RequestProcessingCode VARCHAR(10) NOT NULL,
        RequestSTAN VARCHAR(10) NOT NULL,
        RequestDateTime VARCHAR(20) NOT NULL,
        RequestAcquirerID VARCHAR(20) NULL,
        RequestTerminalID VARCHAR(20) NULL,
        RequestMerchantID VARCHAR(30) NULL,
        RequestRRN VARCHAR(20) NULL,
        
        -- Full ISO message for complete validation
        RequestMessageBytes VARBINARY(MAX) NOT NULL,
        
        -- Status tracking
        Status VARCHAR(20) NOT NULL DEFAULT 'PENDING',
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
        INDEX IX_STAN ON PendingTransactions(RequestSTAN)
    );
    
    PRINT 'PendingTransactions table created successfully';
END
ELSE
BEGIN
    PRINT 'PendingTransactions table already exists';
END

-- Verify table structures
PRINT '';
PRINT '=== TransactionLog Schema ===';
SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'TransactionLog'
ORDER BY ORDINAL_POSITION;

PRINT '';
PRINT '=== PendingTransactions Schema ===';
SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'PendingTransactions'
ORDER BY ORDINAL_POSITION;
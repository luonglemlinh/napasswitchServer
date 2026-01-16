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

-- Verify table structure
SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'TransactionLog'
ORDER BY ORDINAL_POSITION;
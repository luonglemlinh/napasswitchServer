# Test Applications

This folder contains simple test applications to validate the NAPAS Switch Server implementation.

## Applications

### 1. SimpleClient
**Purpose:** Send raw ISO-8583 messages to the Switch Server  
**Port:** Connects to Switch at `127.0.0.1:8583`

**Usage:**
```bash
cd test\SimpleClient
dotnet run
```

Then paste ISO-8583 hex messages when prompted.

---

### 2. IssuerSimulator
**Purpose:** Simulate an Issuer (ISS) bank that receives messages from the Switch  
**Port:** Listens on `7000` (default)

**Usage:**
```bash
cd test\IssuerSimulator
dotnet run
```

Or with custom port:
```bash
dotnet run 7000
```

---

## Testing Workflow

### Full End-to-End Test:

1. **Start the Issuer Simulator:**
   ```bash
   cd test\IssuerSimulator
   dotnet run
   ```
   > ISS listening on port 7000...

2. **Configure Switch Server** (in BINconfig.xml):
   ```xml
   <Issuer>
     <IssuerCode>970412</IssuerCode>
     <IssuerName>VCB Bank</IssuerName>
     <BINRanges>970412</BINRanges>
     <Host>127.0.0.1</Host>
     <Port>7000</Port>
   </Issuer>
   ```

3. **Start the Switch Server:**
   ```bash
   cd NapasSwitch.Server
   dotnet run
   ```
   > Switch listening on port 8583...

4. **Send test message from Client:**
   ```bash
   cd test\SimpleClient
   dotnet run
   ```
   > Paste ISO message in HEX...

---

## Sample ISO-8583 Messages

### Valid Authorization Request (0200)

**Message:** Purchase transaction for 500.00 VND from VCB card

```
30323030F238040108A08008313937303431323334353637383930313230303030303030303035303030303031323334353637383930313233343536373839303630313233343536373839303937303431323030313233343535363738393031323334545045383132333435363736323334353637383930313233343537303400
```

**Breakdown:**
- MTI: 0200 (Authorization Request)
- DE2: 9704123456789012 (VCB Card)
- DE3: 000000 (Purchase)
- DE4: 000000050000 (500.00 VND)
- DE7: 1234567890 (Transmission date/time)
- DE11: 123456 (STAN)
- DE32: 970412 (Acquirer ID - VCB)
- DE37: 001234556789 (RRN)
- DE41: TPE81234 (Terminal ID)
- DE42: 567623456789012345 (Merchant ID)
- DE49: 704 (Currency VND)

### Expected Flow:
1. Client ? Switch: Authorization request
2. Switch: Validates message, stores in PendingTransactions
3. Switch ? ISS: Forwards to Issuer (VCB at 127.0.0.1:7000)
4. ISS ? Switch: Responds with 0210 (approval)
5. Switch: Cross-validates response matches request
6. Switch ? Client: Sends response
7. **Result:** RC=00 (Approved)

---

### Testing Correlation Validation

#### Test 1: Valid Response (Should Pass)
Send the message above. The ISS will echo back all fields correctly.
- **Expected:** RC=00, correlation validation passes

#### Test 2: Tampered Response (Should Fail)
Manually modify the ISS simulator to return different STAN or Amount:

In `IssuerSimulator\Program.cs`, modify `CreateAuthResponse`:
```csharp
// Wrong STAN - should trigger correlation error
response.SetField(11, "999999"); // Different STAN
```

- **Expected:** RC=30 (Format Error), correlation validation fails
- **Switch Log:** `[CORRELATION-ERROR] Response does not match request`
- **Database:** Transaction marked as MISMATCH in PendingTransactions

---

## Monitoring

### Switch Server Logs to Watch:
- `[PENDING-TXN]` - Request storage/retrieval
- `[CORRELATION]` - Response validation results
- `[POOL]` - Connection pool health

### Database Queries:

**Check pending transactions:**
```sql
SELECT * FROM PendingTransactions WHERE Status = 'PENDING'
```

**Check mismatched responses:**
```sql
SELECT * FROM PendingTransactions WHERE Status = 'MISMATCH'
```

**Check expired transactions:**
```sql
SELECT * FROM PendingTransactions WHERE Status = 'EXPIRED'
```

---

## Response Codes

| Code | Description |
|------|-------------|
| 00 | Approved |
| 12 | Invalid transaction |
| 14 | Invalid card |
| 15 | No such issuer |
| 30 | **Format error (Correlation failed)** |
| 51 | Insufficient funds (Amount > 10,000.00) |
| 91 | Issuer unavailable |
| 96 | System malfunction |

---

## Architecture

```
???????????????         ????????????????         ????????????????
? SimpleClient?????????>? Switch Server?????????>? IssuerSim    ?
? (POS)       ? ISO-8583?              ? ISO-8583? (ISS)        ?
? :random     ?         ? :8583        ?         ? :7000        ?
???????????????         ????????????????         ????????????????
                               ?
                               v
                        ????????????????
                        ? SQL Server   ?
                        ? (Storage)    ?
                        ????????????????
```

**Key Features:**
- ? Request/Response correlation validation
- ? PAN encryption before database storage
- ? Transaction state tracking
- ? Automatic cleanup of expired transactions
- ? Circuit breaker for database failures

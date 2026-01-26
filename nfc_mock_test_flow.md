# Quy trình Chi tiết - Giao dịch Purchase NFC Mock Test

## Điểm khởi đầu: User Tap Icon SoftPOS

**File**: `PurchaseCardActivity.java` - Dòng 135-146

```java
findViewById(R.id.ivNfcIcon).setOnClickListener(v -> {
    CardInputData mockData = new CardInputData(
        "9704189991010867647",                              // PAN
        "3101",                                             // Expiry YYMM
        "9704189991010867647=3101601000000001230",          // Track 2
        "072",                                              // POS Mode
        null, null                                          // PIN, EMV
    );
    Toast.makeText(this, "Mock NFC Triggered...", Toast.LENGTH_SHORT).show();
    processTransaction(mockData);
});
```

**Kết quả**: Gọi `processTransaction()` với Mock Data

---

## BƯỚC 1: Validation
**File**: `PurchaseCardActivity.processTransaction()` - Dòng 276-283

### Input:
- `card.getPan()` = `"9704189991010867647"`
- `card.getExpiryDate()` = `"3101"`
- `amount` = Số tiền User nhập trước đó (ví dụ: `"10000"`)

### Xử lý:
```java
TransactionValidator.ValidationResult v = TransactionValidator.validate(card, amount, isPurchase);
if (v != TransactionValidator.ValidationResult.VALID) {
    // Hiển thị lỗi
    return;
}
```

### Check:
1. ✅ Luhn Check số thẻ `9704189991010867647`
2. ✅ Expiry Check `3101` (tháng 01, năm 2031)
3. ✅ Amount > 0

**Kết quả**: PASS → Tiếp tục

---

## BƯỚC 2: Tạo Transaction Context
**File**: `PurchaseCardActivity.processTransaction()` - Dòng 285-303

### 2.1: Khởi tạo
```java
TransactionContext ctx = new TransactionContext();
ctx.txnType = TxnType.PURCHASE;
```

### 2.2: Format Amount
**Code**: Dòng 274
```java
String amtF4 = TransactionContext.formatAmount12(amount);
```

**Logic**:
```
Input: "10000"
→ Clean: "10000"
→ × 100: 1000000
→ Pad 12: "000001000000"
```

**Lưu**: `ctx.amount4 = "000001000000"`

### 2.3: Generate STAN
**Code**: Dòng 288
```java
ctx.stan11 = configManager.getAndIncrementTrace();
```

**Logic**: Lấy counter từ SharedPreferences, tăng 1, format 6 số
```
Counter hiện tại: 122
→ Tăng lên: 123
→ Format: "000123"
```

**Lưu**: `ctx.stan11 = "000123"`

### 2.4: Generate Date/Time
**Code**: Dòng 289
```java
ctx.generateDateTime();
```

**File**: `TransactionContext.java` - Dòng 55-68

**Logic**:
```java
Date now = new Date();  // 2026-01-26 10:09:07 (GMT+7)

SimpleDateFormat f7 = new SimpleDateFormat("MMddHHmmss", Locale.US);
// Không set TimeZone → Dùng Local Time (GMT+7)

ctx.transmissionDt7 = f7.format(now);   // "0126100907"
ctx.localTime12 = "100907";             // HHmmss
ctx.localDate13 = "0126";               // MMdd
ctx.settlementDate15 = "0126";          // MMdd (same as DE 13)
```

**Lưu**:
- `ctx.transmissionDt7 = "0126100907"`
- `ctx.localTime12 = "100907"`
- `ctx.localDate13 = "0126"`
- `ctx.settlementDate15 = "0126"`

### 2.5: Calculate RRN
**Code**: Dòng 290
```java
ctx.rrn37 = TransactionContext.calculateRrn(configManager.getServerId(), ctx.stan11);
```

**File**: `TransactionContext.java` - Dòng 71-89

**Logic**:
```
Năm hiện tại: 2026 → Lấy số cuối: "6"
Ngày Julian: Ngày thứ 26 của năm → "026"
Server ID: "00" (từ Config)
STAN: "000123"

RRN = "6" + "026" + "00" + "000123" = "602600000123"
```

**Lưu**: `ctx.rrn37 = "602600000123"`

### 2.6: Load Config
**Code**: Dòng 293-300
```java
ctx.mcc18 = configManager.getMcc18();                           // "5411"
ctx.acquirerId32 = configManager.getAcquirerId32();             // "970400"
ctx.currency49 = configManager.getCurrencyCode49();             // "704"
ctx.terminalId41 = configManager.getTerminalId();               // "AUTO0001"
ctx.merchantId42 = configManager.getMerchantId();               // "MYSOFTPOSSHOP01"
ctx.merchantNameLocation43 = configManager.getMerchantName();   // "MYSOFTPOS BANK       HA NOI        VNM"
ctx.ip = configManager.getServerIp();                           // "192.168.1.100"
ctx.port = configManager.getServerPort();                       // 8080
```

---

## BƯỚC 3: Build ISO Message
**File**: `PurchaseCardActivity.processTransaction()` - Dòng 306-308

```java
IsoMessage req = Iso8583Builder.buildPurchaseMsg(ctx, card);
```

**File**: `Iso8583Builder.java` - Dòng 17-76

### 3.1: Set Mandatory Fields
```java
IsoMessage m = new IsoMessage("0200");  // MTI

m.setField(2, "9704189991010867647");        // PAN
m.setField(3, "000000");                     // Processing Code
m.setField(4, "000001000000");               // Amount
m.setField(7, "0126100907");                 // Trans DateTime
m.setField(11, "000123");                    // STAN
m.setField(12, "100907");                    // Local Time
m.setField(13, "0126");                      // Local Date
m.setField(14, "3101");                      // Expiry (tạm set)
m.setField(15, "0126");                      // Settlement Date
m.setField(18, "5411");                      // MCC
m.setField(22, "072");                       // POS Entry Mode (NFC)
m.setField(25, "00");                        // POS Condition
m.setField(32, "970400");                    // Acquirer ID
m.setField(37, "602600000123");              // RRN
m.setField(41, "AUTO0001");                  // Terminal ID
m.setField(42, "MYSOFTPOSSHOP01");           // Merchant ID
m.setField(43, "MYSOFTPOS BANK       HA NOI        VNM");  // Name/Loc
m.setField(49, "704");                       // Currency
```

### 3.2: NFC Specific Logic (Dòng 48-66)
```java
if (card.isContactless()) {  // TRUE vì Track 2 != null
    // DE 35: Track 2
    String track2 = card.getTrack2().replace('=', 'D');
    // "9704189991010867647=3101601000000001230"
    // → "9704189991010867647D3101601000000001230"
    m.setField(35, track2);
    
    // XÓA DE 14
    m.setField(14, null);
    
    // DE 52: PIN Block (Optional - không có trong Mock)
    // ctx.pinBlock52 = null → Không gửi
    
    // DE 63: Reserved (Optional - không có trong Mock)
    // ctx.field60 = null → Không gửi
}
```

### 3.3: Kết quả Message
**Fields cuối cùng**:
```
MTI: 0200
DE 2: 9704189991010867647
DE 3: 000000
DE 4: 000001000000
DE 7: 0126100907
DE 11: 000123
DE 12: 100907
DE 13: 0126
DE 15: 0126
DE 18: 5411
DE 22: 072
DE 25: 00
DE 32: 970400
DE 35: 9704189991010867647D3101601000000001230
DE 37: 602600000123
DE 41: AUTO0001
DE 42: MYSOFTPOSSHOP01
DE 43: MYSOFTPOS BANK       HA NOI        VNM
DE 49: 704
```

**Tổng**: 19 trường

---

## BƯỚC 4: Pack Message
**File**: `PurchaseCardActivity.processTransaction()` - Dòng 311

```java
byte[] packed = StandardIsoPacker.pack(req);
```

**File**: `StandardIsoPacker.java` - Dòng 74-175

### 4.1: Write MTI
```
"0200" → Bytes: 30 32 30 30
```

### 4.2: Calculate Bitmap
**Primary Bitmap** (Fields 2-64):
```
Present: 2, 3, 4, 7, 11, 12, 13, 15, 18, 22, 25, 32, 35, 37, 41, 42, 43, 49
→ Bitmap: F2 3A 44 81 28 E0 80 00
```

**Secondary Bitmap**: Không cần (không có field > 64)

### 4.3: Encode Fields
**DE 2 (LLVAR)**:
```
Length: 19
Packed: "19" + "9704189991010867647"
Hex: 31 39 39 37 30 34 31 38 39 39 39 31 30 31 30 38 36 37 36 34 37
```

**DE 3 (NUMERIC 6)**:
```
Value: "000000"
Hex: 30 30 30 30 30 30
```

**DE 4 (NUMERIC 12)**:
```
Value: "000001000000"
Hex: 30 30 30 30 30 31 30 30 30 30 30 30
```

**DE 35 (LLVAR)**:
```
Value: "9704189991010867647D3101601000000001230"
Length: 39
Packed: "39" + "9704189991010867647D3101601000000001230"
Hex: 33 39 39 37 30 34... (41 bytes total)
```

### 4.4: Final Packet
```
Tổng độ dài: ~200+ bytes
Format: MTI (4) + Bitmap (8) + Fields (190+)
```

---

## BƯỚC 5: Log Request
**File**: `PurchaseCardActivity.processTransaction()` - Dòng 313-314

```java
FileLogger.logPacket(this, "SEND 0200", packed);
```

**File**: `FileLogger.java` - Dòng 22-57

### Logic:
1. Chuyển `packed` thành Hex String
2. Tạo timestamp: `"10:09:07.123"`
3. Tạo log entry: `"[10:09:07.123] [SEND 0200] 30323030F23A..."`
4. Ghi vào file: `/Android/data/com.example.mysoftpos/files/iso_logs/iso_log_20260126.txt`
5. **ĐỒNG BỘ** (Synchronous) - Chờ ghi xong mới return

---

## BƯỚC 6: Lưu DB
**File**: `PurchaseCardActivity.processTransaction()` - Dòng 316-323

```java
entity.traceNumber = "000123";
entity.amount = "10000";
entity.pan = "9704189991010867647";
entity.status = "PENDING";
entity.requestHex = "30323030F23A...";  // Hex String
entity.timestamp = System.currentTimeMillis();
appDatabase.transactionDao().insert(entity);
```

---

## BƯỚC 7: Gửi Network
**File**: `PurchaseCardActivity.processTransaction()` - Dòng 326-340

```java
IsoNetworkClient client = new IsoNetworkClient("192.168.1.100", 8080);
byte[] resp;
try {
    resp = client.sendAndReceive(packed);  // Blocking call, timeout 30s
    
    // Log Response ngay lập tức
    FileLogger.logPacket(this, "RECV 0210", resp);
    
} catch (SocketTimeoutException e) {
    // Timeout → Auto-Reversal
    FileLogger.logString(this, "ERROR", "Timeout waiting for response");
    handleAutoReversal(ctx, card, entity);
    return;
}
```

**Giả sử nhận được Response**:
```
Hex: 30323130... (MTI 0210)
```

---

## BƯỚC 8: Unpack Response
**File**: `PurchaseCardActivity.processTransaction()` - Dòng 343-347

```java
IsoMessage respMsg = new StandardIsoPacker().unpack(resp);
```

### Parse:
1. MTI: `"0210"`
2. Bitmap: Parse 8 bytes primary
3. Fields: Parse theo SCHEMA
4. Kết quả: Lấy DE 39 (Response Code)

```java
String rc = respMsg.getField(39);  // Ví dụ: "00" (APPROVED)
```

---

## BƯỚC 9: Update DB
**File**: `PurchaseCardActivity.processTransaction()` - Dòng 345-347

```java
entity.responseHex = "30323130...";  // Response Hex
entity.status = "00".equals(rc) ? "APPROVED" : "DECLINED " + rc;
appDatabase.transactionDao().update(entity);
```

---

## BƯỚC 10: Hiển thị Kết quả
**File**: `PurchaseCardActivity.processTransaction()` - Dòng 349-354

```java
runOnUiThread(() -> {
    showLoading(false);
    String msg = ResponseCodeHelper.getMessage(rc);  // "Giao dịch thành công"
    showResult("00".equals(rc), msg, 
               StandardIsoPacker.bytesToHex(resp), 
               StandardIsoPacker.bytesToHex(packed));
});
```

### Chuyển màn hình:
```java
Intent i = new Intent(this, TransactionResultActivity.class);
i.putExtra("RESULT_TYPE", ResultType.SUCCESS);
i.putExtra("MESSAGE", "Giao dịch thành công");
i.putExtra("TXN_TYPE", TxnType.PURCHASE);
i.putExtra("ISO_RESPONSE", "30323130...");
i.putExtra("ISO_REQUEST", "30323030...");
startActivity(i);
finish();
```

---

## Tổng kết Timeline

```
0ms    : User Tap Icon
1ms    : Tạo Mock CardInputData
2ms    : Gọi processTransaction()
3ms    : Validation (Luhn, Expiry, Amount) ✅
5ms    : Tạo Context (STAN, DateTime, RRN, Config)
10ms   : Build ISO Message (19 fields)
15ms   : Pack → byte[] (~200 bytes)
20ms   : Log Request (File I/O) ✅
30ms   : DB Insert (entity) ✅
50ms   : Network Send → Server
???ms  : Chờ Server xử lý...
1500ms : Network Receive ← Server Response
1505ms : Log Response (File I/O) ✅
1510ms : Unpack Response → Parse DE 39
1515ms : DB Update (status) ✅
1520ms : Show Result Screen
```

**Thời gian trung bình**: ~1.5-2 giây (tùy network)

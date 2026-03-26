$hex = "30323030723C648108E0900031393937303431363636303632323632313939323330303030303030303030303036323632303030333236313431333037313131333736313431333037303332363331303135343131373034303131303030363937303438383630383530313131313337364155544F303030314D30303030303030303038383134374D59534F4654504F532042414E4B2020202020202020204841204E4F49202020202020202037303437303430363132353235304444394445363644"

$bytes = [byte[]]::new($hex.Length / 2)
for ($i = 0; $i -lt $hex.Length; $i += 2) {
    $bytes[$i / 2] = [Convert]::ToByte($hex.Substring($i, 2), 16)
}

$mti = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 4)
Write-Host "MTI: $mti"

$bitmapBytes = $bytes[4..11]
$fields = @()
for ($byteIdx = 0; $byteIdx -lt 8; $byteIdx++) {
    for ($bitIdx = 7; $bitIdx -ge 0; $bitIdx--) {
        $fieldNum = ($byteIdx * 8) + (8 - $bitIdx)
        if (($bitmapBytes[$byteIdx] -band (1 -shl $bitIdx)) -ne 0) {
            $fields += $fieldNum
        }
    }
}
Write-Host "Bitmap: $([BitConverter]::ToString($bitmapBytes).Replace('-',''))"
Write-Host "Fields present: $($fields -join ', ')"
Write-Host ""

# Using NAPAS schema field definitions
# Fixed fields: field -> length
$fixedFields = @{3=6; 4=12; 7=10; 11=6; 12=6; 13=4; 14=4; 15=4; 18=4; 19=3; 22=3; 23=3; 25=2; 37=12; 38=6; 39=2; 41=8; 42=15; 43=40; 49=3; 50=3; 51=3; 70=3; 90=42; 95=42}
# Binary fixed: field -> byte length (read as binary, display as hex)
$binaryFixed = @{52=8; 64=8; 128=8}
# LLVAR fields
$llvarFields = @(2, 32, 33, 35, 62, 100, 102, 103)
# LLLVAR fields
$lllvarFields = @(36, 43, 48, 54, 55, 60, 63, 104, 105, 120)
# Fixed 15
$fixedFields[123] = 15

$fieldNames = @{
    2="PAN"; 3="Processing Code"; 4="Amount"; 7="DateTime"; 
    11="STAN"; 12="Local Time"; 13="Local Date"; 14="Expiry"; 15="Settlement";
    18="MCC"; 19="Acq Country"; 22="POS Entry"; 23="Card Seq";
    25="POS Condition"; 32="Acquirer ID"; 33="Forwarding/Issuer ID"; 
    35="Track 2"; 37="RRN"; 38="Auth Code"; 39="Response Code";
    41="Terminal ID"; 42="Merchant ID"; 43="Merchant Name/Location"; 49="Currency";
    52="PIN Block"; 55="EMV/ICC Data"; 63="TRN (Private)"; 100="Receiving ID"
}

$pos = 12  # After MTI (4) + Bitmap (8)
foreach ($f in ($fields | Sort-Object)) {
    if ($f -eq 1) { continue }
    $name = if ($fieldNames.ContainsKey($f)) { $fieldNames[$f] } else { "Field $f" }
    
    try {
        if ($binaryFixed.ContainsKey($f)) {
            $len = $binaryFixed[$f]
            
            # [NAPAS-FIX] Handle 16-char ASCII Hex shift for DE#52
            if ($f -eq 52 -and $len -eq 8) {
                $isHex = $true
                for ($i=0; $i -lt 16; $i++) {
                    $b = $bytes[$pos + $i]
                    if (-not (($b -ge 48 -and $b -le 57) -or ($b -ge 65 -and $b -le 70) -or ($b -ge 97 -and $b -le 102))) {
                        $isHex = $false; break
                    }
                }
                if ($isHex) {
                    Write-Host "WARNING: DE#52 detected as 16-char ASCII Hex. Adjusting length to 16 to prevent parsing shift." -ForegroundColor Yellow
                    $len = 16
                }
            }

            if ($len -eq 16 -and $f -eq 52) {
                $val = [System.Text.Encoding]::ASCII.GetString($bytes, $pos, 16)
            } else {
                $val = [BitConverter]::ToString($bytes, $pos, $len).Replace("-","")
            }
            
            Write-Host ("DE#{0,-3} ({1,-25}): {2}  (length {3})" -f $f, $name, $val, $len)
            $pos += $len
        }
        elseif ($fixedFields.ContainsKey($f)) {
            $len = $fixedFields[$f]
            $val = [System.Text.Encoding]::ASCII.GetString($bytes, $pos, $len)
            Write-Host ("DE#{0,-3} ({1,-25}): {2}" -f $f, $name, $val)
            $pos += $len
        }
        elseif ($f -in $llvarFields) {
            $lenStr = [System.Text.Encoding]::ASCII.GetString($bytes, $pos, 2)
            $len = [int]$lenStr
            $pos += 2
            $val = [System.Text.Encoding]::ASCII.GetString($bytes, $pos, $len)
            Write-Host ("DE#{0,-3} ({1,-25}): [{2}] {3}" -f $f, $name, $len, $val)
            $pos += $len
        }
        elseif ($f -in $lllvarFields) {
            $lenStr = [System.Text.Encoding]::ASCII.GetString($bytes, $pos, 3)
            $len = [int]$lenStr
            $pos += 3
            # Check if binary
            if ($f -eq 55) {
                $val = [BitConverter]::ToString($bytes, $pos, $len).Replace("-","")
            } else {
                $val = [System.Text.Encoding]::ASCII.GetString($bytes, $pos, $len)
            }
            Write-Host ("DE#{0,-3} ({1,-25}): [{2}] {3}" -f $f, $name, $len, $val)
            $pos += $len
        }
        else {
            Write-Host ("DE#{0,-3} ({1,-25}): ??? UNKNOWN FORMAT - stopping" -f $f, $name)
            break
        }
    } catch {
        Write-Host ("DE#{0,-3} ({1,-25}): ERROR at pos $pos - $_" -f $f, $name)
        break
    }
}

Write-Host ""
Write-Host "=== KEY FINDINGS ==="
Write-Host "DE#52 (PIN Block) present: $($fields -contains 52)"
Write-Host "DE#33 (Issuer ID) present: $($fields -contains 33)"
Write-Host "DE#55 (EMV Data)  present: $($fields -contains 55)"
Write-Host "Total message bytes: $($bytes.Length), parsed up to pos: $pos"

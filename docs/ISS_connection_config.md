# ISS Connection Configuration Guide

This guide explains how to add, remove, or modify Issuer (ISS) connections in the NAPAS Switch.

## Configuration File
All issuer connections are managed in:
`[SolutionRoot]/Config/BINconfig.xml`

## Adding a New Issuer
To add a new issuer, add a `<Bank>` entry within the `<Banks>` tag.

### XML Structure
```xml
<Bank>
  <BankCode>MYBANK</BankCode>
  <BankName>My New Bank</BankName>
  <IssuerCode>9704XX</IssuerCode>
  <IssuerName>My Bank Name</IssuerName>
  <Host>127.0.0.1</Host>
  <Port>9000</Port>
  <Timeout>30000</Timeout>
  <IsDefault>false</IsDefault>
  <Bins>
    <Bin>123456</Bin>
  </Bins>
</Bank>
```

### Field Descriptions
| Field | Description |
|-------|-------------|
| `BankCode` | Short alphanumeric code for the bank. |
| `BankName` | Descriptive name of the bank. |
| `IssuerCode` | unique ID for routing (often 6 digits). |
| `Host` | IP address of the Issuer's host. |
| `Port` | Port for the TCP connection. |
| `Timeout` | Connection/read timeout in milliseconds. |
| `IsDefault` | Set to `true` for the primary NAPAS TS. Only one default is supported. |
| `Bins` | List of BINs (DE#2 prefixes) that route to this bank. |

## Single vs Multiple Connections
- **Persistent Connections:** By default, the switch maintains **1 persistent connection** per issuer.
- **Sign-on (0800):** The switch sends a single Sign-on (0800) message automatically upon establishing the connection.
- **Auto-Reconnect:** If the connection drops, the switch will attempt to reconnect and re-sign on automatically.

## Applying Changes
1. Modify `BINconfig.xml`.
2. Restart the `NapasSwitch.Server` application.
3. Observe the console logs to verify the new connection and sign-on:
   `[TS-CONN] Connecting to My Bank Name at 127.0.0.1:9000...`
   `[TS-CONN] Successfully connected and signed on to My Bank Name`

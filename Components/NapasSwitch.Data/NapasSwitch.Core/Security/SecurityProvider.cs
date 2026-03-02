using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using core.Helpers;

namespace core.Security
{
    
    /// Interface for Hardware Security Module operations
    /// In production, this would connect to a real HSM (Thales, Futurex, etc.)
    
    public interface IHsmProvider
    {
        
        /// Encrypt PIN block using zone master key
        
        byte[] EncryptPinBlock(byte[] clearPinBlock, string keyId);

        
        /// Decrypt PIN block using zone master key
        
        byte[] DecryptPinBlock(byte[] encryptedPinBlock, string keyId);

        
        /// Translate PIN block from one key to another
        /// Used when forwarding from ACQ to ISS with different keys
        
        byte[] TranslatePinBlock(byte[] pinBlock, string sourceKeyId, string destKeyId);

        
        /// Generate MAC for message integrity
        
        byte[] GenerateMAC(byte[] data, string keyId);

        
        /// Verify MAC for message integrity
        
        bool VerifyMAC(byte[] data, byte[] mac, string keyId);

        
        /// Encrypt sensitive data (PAN, CVV, etc.)
        
        byte[] EncryptData(byte[] clearData, string keyId);

        
        /// Decrypt sensitive data
        
        byte[] DecryptData(byte[] encryptedData, string keyId);

        
        /// Generate a new working key
        
        byte[] GenerateWorkingKey(string masterKeyId);
    }


    /// Software-based HSM stub for development/testing.
    /// Keys are persisted to a local file so encrypted data survives restarts.
    /// WARNING: DO NOT USE IN PRODUCTION — Use a real HSM (Thales payShield, Futurex, AWS CloudHSM)
    /// with persisted, versioned keys managed via a proper KMS.

    public class SoftwareHsmStub : IHsmProvider
    {
        private readonly Dictionary<string, byte[]> _keys;
        private static readonly string KeyFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Config", "hsm_stub_keys.json");

        public SoftwareHsmStub()
        {
            string? allowStub = Environment.GetEnvironmentVariable("ALLOW_HSM_STUB");
            if (!string.Equals(allowStub, "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    "SoftwareHsmStub is disabled. Set environment variable ALLOW_HSM_STUB=true to enable it. " +
                    "DO NOT use this in production — use a real HSM (Thales payShield, Futurex, AWS CloudHSM).");
            }

            _keys = LoadOrCreateKeys();
            SwitchLogger.ForContext("HSM").Warn("Using software HSM stub — NOT FOR PRODUCTION");
        }

        private static Dictionary<string, byte[]> LoadOrCreateKeys()
        {
            try
            {
                if (File.Exists(KeyFilePath))
                {
                    var json = File.ReadAllText(KeyFilePath);
                    var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (stored != null && stored.Count > 0)
                    {
                        var keys = new Dictionary<string, byte[]>();
                        foreach (var kvp in stored)
                            keys[kvp.Key] = Convert.FromBase64String(kvp.Value);
                        SwitchLogger.ForContext("HSM").Info("Loaded persisted stub keys from {Path}", Path.GetFileName(KeyFilePath));
                        return keys;
                    }
                }
            }
            catch (Exception ex)
            {
                SwitchLogger.ForContext("HSM").Warn("Failed to load persisted keys: {Error}. Generating new keys.", ex.Message);
            }

            // Generate fresh keys and persist them
            var newKeys = new Dictionary<string, byte[]>
            {
                ["ZMK_ACQ"] = GenerateRandomKey(32),
                ["ZMK_ISS"] = GenerateRandomKey(32),
                ["MAC_KEY"] = GenerateRandomKey(32),
                ["DATA_KEY"] = GenerateRandomKey(32)
            };

            try
            {
                var dir = Path.GetDirectoryName(KeyFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var serializable = new Dictionary<string, string>();
                foreach (var kvp in newKeys)
                    serializable[kvp.Key] = Convert.ToBase64String(kvp.Value);

                File.WriteAllText(KeyFilePath, JsonSerializer.Serialize(serializable, new JsonSerializerOptions { WriteIndented = true }));
                SwitchLogger.ForContext("HSM").Info("Generated and persisted new stub keys to {Path}", Path.GetFileName(KeyFilePath));
            }
            catch (Exception ex)
            {
                SwitchLogger.ForContext("HSM").Warn("Failed to persist keys: {Error}. Keys will be lost on restart!", ex.Message);
            }

            return newKeys;
        }

        public byte[] EncryptPinBlock(byte[] clearPinBlock, string keyId)
        {
            if (!_keys.TryGetValue(keyId, out var key))
                throw new InvalidOperationException($"Key not found: {keyId}");

            return EncryptAES(clearPinBlock, key);
        }

        public byte[] DecryptPinBlock(byte[] encryptedPinBlock, string keyId)
        {
            if (!_keys.TryGetValue(keyId, out var key))
                throw new InvalidOperationException($"Key not found: {keyId}");

            return DecryptAES(encryptedPinBlock, key);
        }

        public byte[] TranslatePinBlock(byte[] pinBlock, string sourceKeyId, string destKeyId)
        {
            // Decrypt with source key
            var clearPinBlock = DecryptPinBlock(pinBlock, sourceKeyId);
            
            // Re-encrypt with destination key
            return EncryptPinBlock(clearPinBlock, destKeyId);
        }

        public byte[] GenerateMAC(byte[] data, string keyId)
        {
            if (!_keys.TryGetValue(keyId, out var key))
                throw new InvalidOperationException($"Key not found: {keyId}");

            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(data);
        }

        public bool VerifyMAC(byte[] data, byte[] mac, string keyId)
        {
            var computedMac = GenerateMAC(data, keyId);
            return CryptographicEquals(mac, computedMac);
        }

        public byte[] EncryptData(byte[] clearData, string keyId)
        {
            if (!_keys.TryGetValue(keyId, out var key))
                throw new InvalidOperationException($"Key not found: {keyId}");

            return EncryptAES(clearData, key);
        }

        public byte[] DecryptData(byte[] encryptedData, string keyId)
        {
            if (!_keys.TryGetValue(keyId, out var key))
                throw new InvalidOperationException($"Key not found: {keyId}");

            return DecryptAES(encryptedData, key);
        }

        public byte[] GenerateWorkingKey(string masterKeyId)
        {
            return GenerateRandomKey(32);
        }

        private static byte[] EncryptAES(byte[] data, byte[] key)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();
            
            using var encryptor = aes.CreateEncryptor();
            var encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);
            
            // Prepend IV to encrypted data
            var result = new byte[aes.IV.Length + encrypted.Length];
            Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
            Buffer.BlockCopy(encrypted, 0, result, aes.IV.Length, encrypted.Length);
            
            return result;
        }

        private static byte[] DecryptAES(byte[] encryptedData, byte[] key)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            
            // Extract IV from beginning of data
            var iv = new byte[16];
            Buffer.BlockCopy(encryptedData, 0, iv, 0, 16);
            aes.IV = iv;
            
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(encryptedData, 16, encryptedData.Length - 16);
        }

        private static byte[] GenerateRandomKey(int length)
        {
            var key = new byte[length];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(key);
            return key;
        }

        private static bool CryptographicEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }
    }

    
    /// Secure data handler for sensitive payment card data
    
    public class SecureDataHandler
    {
        private readonly IHsmProvider _hsm;

        public SecureDataHandler(IHsmProvider hsm)
        {
            _hsm = hsm;
        }

        
        /// Encrypt PAN for storage (PCI-DSS compliant)
        
        public string EncryptPAN(string pan)
        {
            if (string.IsNullOrEmpty(pan)) return pan;
            
            var clearBytes = Encoding.UTF8.GetBytes(pan);
            var encrypted = _hsm.EncryptData(clearBytes, "DATA_KEY");
            return Convert.ToBase64String(encrypted);
        }

        
        /// Decrypt PAN
        
        public string DecryptPAN(string encryptedPan)
        {
            if (string.IsNullOrEmpty(encryptedPan)) return encryptedPan;
            
            var encrypted = Convert.FromBase64String(encryptedPan);
            var decrypted = _hsm.DecryptData(encrypted, "DATA_KEY");
            return Encoding.UTF8.GetString(decrypted);
        }

        
        /// Mask PAN for display/logging (show first 6, last 4)
        
        public static string MaskPAN(string? pan)
        {
            if (string.IsNullOrEmpty(pan) || pan.Length < 13)
                return "****";

            return $"{pan[..6]}{"".PadLeft(pan.Length - 10, '*')}{pan[^4..]}";
        }

        
        /// Tokenize PAN (replace with non-sensitive token)
        
        public string TokenizePAN(string pan)
        {
            // In production, this would use a token vault
            // For now, create a hash-based token
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(pan + "salt"));
            return $"TKN{Convert.ToHexString(hash)[..16]}";
        }

        
        /// Translate PIN block when routing from ACQ to ISS
        
        public byte[] TranslatePinBlock(byte[] pinBlock, string acqCode, string issCode)
        {
            string sourceKey = $"ZMK_ACQ"; // In production: per-acquirer keys
            string destKey = $"ZMK_ISS";   // In production: per-issuer keys
            
            return _hsm.TranslatePinBlock(pinBlock, sourceKey, destKey);
        }
    }
}

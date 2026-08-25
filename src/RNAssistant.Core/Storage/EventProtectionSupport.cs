using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    internal static class EventProtectionSupport
    {
        public static bool IsSupportedHashAlgorithm(string value)
        {
            return string.Equals(value, HistoryIntegrityModes.Sha256, StringComparison.Ordinal) ||
                string.Equals(value, HistoryIntegrityModes.HmacSha256, StringComparison.Ordinal);
        }

        public static bool Matches(
            StorageProtector protector,
            string hashAlgorithm,
            string protectionKeyId,
            string encryptedData)
        {
            protector = protector ?? StorageProtector.None;
            if (!string.Equals(hashAlgorithm, protector.CurrentHashAlgorithm, StringComparison.Ordinal)) return false;
            if (protector.Encrypts != !string.IsNullOrWhiteSpace(encryptedData)) return false;
            if (protector.UsesHmac || protector.Encrypts)
            {
                return !string.IsNullOrWhiteSpace(protectionKeyId) &&
                    string.Equals(protectionKeyId, protector.KeyId, StringComparison.OrdinalIgnoreCase);
            }
            return string.IsNullOrWhiteSpace(protectionKeyId);
        }

        public static string ProtectPayload(
            JToken data,
            StorageProtector protector,
            string purpose,
            Encoding encoding)
        {
            protector = protector ?? StorageProtector.None;
            if (!protector.Encrypts) throw new InvalidOperationException("Event payload protection is not enabled.");
            encoding = encoding ?? throw new ArgumentNullException("encoding");
            var plaintext = encoding.GetBytes(data == null ? "null" : data.ToString(Formatting.None));
            return Convert.ToBase64String(protector.Protect(plaintext, purpose));
        }

        public static JToken UnprotectPayload(
            string encryptedData,
            StorageProtector protector,
            string purpose,
            Encoding encoding)
        {
            if (string.IsNullOrWhiteSpace(encryptedData)) return null;
            protector = protector ?? StorageProtector.None;
            encoding = encoding ?? throw new ArgumentNullException("encoding");
            var stored = Convert.FromBase64String(encryptedData);
            var plaintext = protector.Unprotect(stored, purpose);
            var parsed = JToken.Parse(encoding.GetString(plaintext));
            return parsed.Type == JTokenType.Null ? null : parsed;
        }
    }
}

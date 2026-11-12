using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    // Certificate-free RSA signatures and password-protected issuer data; no CAS/history keys.
    public static class DeploymentConfigurationCrypto
    {
        public const int PasswordIterations = 600000;
        public const int MaximumBytes = 131072;
        private static readonly byte[] KeyHeader = Encoding.ASCII.GetBytes("RNAKEY01");
        private static readonly byte[] SignatureContext = Encoding.ASCII.GetBytes("RNAssistant.DeploymentConfig.v1\n");
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 16 };
        private sealed class SignedConfiguration
        {
            public int FormatVersion { get; set; }
            public string Payload { get; set; }
            public string Signature { get; set; }
        }
        public static DeploymentTrust CreateIssuer(string password, out byte[] encryptedKey)
        {
            CheckPassword(password);
            using (var rsa = CreateRsa(3072))
            {
                var publicKey = rsa.ExportParameters(false); var salt = RandomBytes(32);
                if (publicKey.Modulus?.Length != 384) throw new CryptographicException("RSA issuer must use a 3072-bit key.");
                byte[] verifier;
                using (var derive = new Rfc2898DeriveBytes(password, salt, PasswordIterations, HashAlgorithmName.SHA256)) verifier = derive.GetBytes(32);
                var privateParameters = rsa.ExportParameters(true);
                var secret = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(privateParameters));
                try { encryptedKey = ProtectKey(secret, password); }
                finally { Array.Clear(secret, 0, secret.Length); ClearPrivate(privateParameters); }
                return new DeploymentTrust { PublicKey = new DeploymentPublicKey { Modulus = publicKey.Modulus, Exponent = publicKey.Exponent },
                    PasswordSalt = Convert.ToBase64String(salt), PasswordHash = Convert.ToBase64String(verifier), PasswordIterations = PasswordIterations };
            }
        }
        public static void ValidateTrust(DeploymentTrust trust)
        {
            if (trust?.PublicKey?.Modulus == null || trust.PublicKey.Exponent == null ||
                trust.PublicKey.Modulus.Length < 256 || trust.PublicKey.Modulus.Length > 512 || trust.PublicKey.Exponent.Length > 8 ||
                trust.PasswordIterations < PasswordIterations || trust.PasswordIterations > 2000000 ||
                Convert.FromBase64String(trust.PasswordSalt).Length != 32 || Convert.FromBase64String(trust.PasswordHash).Length != 32) throw Invalid();
            using (var rsa = PublicRsa(trust)) { }
        }
        public static bool VerifyPassword(DeploymentTrust trust, string password)
        {
            CheckPassword(password);
            using (var derive = new Rfc2898DeriveBytes(password, Convert.FromBase64String(trust.PasswordSalt), trust.PasswordIterations, HashAlgorithmName.SHA256))
                return Equal(derive.GetBytes(32), Convert.FromBase64String(trust.PasswordHash));
        }
        public static byte[] Sign(DeploymentConfiguration configuration, byte[] encryptedKey, string password, DeploymentTrust trust)
        {
            var secret = UnprotectKey(encryptedKey, password);
            RSAParameters parameters = default(RSAParameters);
            try
            {
                parameters = JsonConvert.DeserializeObject<RSAParameters>(new UTF8Encoding(false, true).GetString(secret), Json);
                if (parameters.D == null || !Equal(parameters.Modulus, trust.PublicKey.Modulus) || !Equal(parameters.Exponent, trust.PublicKey.Exponent)) throw Invalid();
                using (var rsa = CreateRsa())
                {
                    rsa.ImportParameters(parameters);
                    var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(configuration));
                    var envelope = new SignedConfiguration { FormatVersion = 1, Payload = Convert.ToBase64String(payload),
                        Signature = Convert.ToBase64String(rsa.SignData(SignatureContext.Concat(payload).ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
                    var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(envelope))));
                    if (encoded.Length > MaximumBytes) throw Invalid();
                    return encoded;
                }
            }
            finally { Array.Clear(secret, 0, secret.Length); ClearPrivate(parameters); }
        }
        public static DeploymentConfiguration Verify(byte[] encoded, DeploymentTrust trust)
        {
            if (encoded == null || encoded.Length < 1 || encoded.Length > MaximumBytes) throw Invalid();
            var envelope = JsonConvert.DeserializeObject<SignedConfiguration>(new UTF8Encoding(false, true).GetString(
                Convert.FromBase64String(Encoding.ASCII.GetString(encoded))), Json);
            if (envelope?.FormatVersion != 1) throw Invalid();
            var payload = Convert.FromBase64String(envelope.Payload);
            using (var rsa = PublicRsa(trust))
                if (!rsa.VerifyData(SignatureContext.Concat(payload).ToArray(), Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw Invalid();
            return JsonConvert.DeserializeObject<DeploymentConfiguration>(new UTF8Encoding(false, true).GetString(payload), Json);
        }
        private static RSA PublicRsa(DeploymentTrust trust)
        {
            var rsa = CreateRsa();
            try { rsa.ImportParameters(new RSAParameters { Modulus = trust.PublicKey.Modulus, Exponent = trust.PublicKey.Exponent }); return rsa; }
            catch { rsa.Dispose(); throw; }
        }
        private static RSA CreateRsa(int keySize = 0)
        {
#if NET8_0_OR_GREATER
            var rsa = RSA.Create();
            if (keySize > 0) rsa.KeySize = keySize;
            return rsa;
#else
            // .NET Framework's default RSA provider may ignore a later KeySize assignment.
            // CNG keeps generation, import, signing and verification on one provider.
            return keySize > 0 ? (RSA)new RSACng(keySize) : new RSACng();
#endif
        }
        // v1: header + random salt (32) + IV (16) + AES-256-CBC ciphertext + HMAC-SHA256 (32).
        // Authenticate before decryption; independent derived encryption/MAC keys.
        private static byte[] ProtectKey(byte[] secret, string password)
        {
            var salt = RandomBytes(32); var iv = RandomBytes(16); var keys = Derive(password, salt);
            try
            {
                using (var aes = Aes.Create())
                {
                    aes.Key = keys.Take(32).ToArray(); aes.IV = iv;
                    using (var encrypt = aes.CreateEncryptor())
                    using (var mac = new HMACSHA256(keys.Skip(32).ToArray()))
                    {
                        var bytes = KeyHeader.Concat(salt).Concat(iv).Concat(encrypt.TransformFinalBlock(secret, 0, secret.Length)).ToArray();
                        return bytes.Concat(mac.ComputeHash(bytes)).ToArray();
                    }
                }
            }
            finally { Array.Clear(keys, 0, keys.Length); }
        }
        private static byte[] UnprotectKey(byte[] bytes, string password)
        {
            CheckPassword(password);
            if (bytes == null || bytes.Length < 104 || bytes.Length > MaximumBytes || !bytes.Take(8).SequenceEqual(KeyHeader)) throw Invalid();
            var keys = Derive(password, bytes.Skip(8).Take(32).ToArray());
            try
            {
                var bodyLength = bytes.Length - 32;
                using (var mac = new HMACSHA256(keys.Skip(32).ToArray()))
                    if (!Equal(mac.ComputeHash(bytes, 0, bodyLength), bytes.Skip(bodyLength).ToArray())) throw Invalid();
                using (var aes = Aes.Create())
                {
                    aes.Key = keys.Take(32).ToArray(); aes.IV = bytes.Skip(40).Take(16).ToArray();
                    using (var decrypt = aes.CreateDecryptor()) return decrypt.TransformFinalBlock(bytes, 56, bodyLength - 56);
                }
            }
            finally { Array.Clear(keys, 0, keys.Length); }
        }
        private static byte[] Derive(string password, byte[] salt)
        { using (var derive = new Rfc2898DeriveBytes(password, salt, PasswordIterations, HashAlgorithmName.SHA256)) return derive.GetBytes(64); }
        private static byte[] RandomBytes(int count)
        { var bytes = new byte[count]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); return bytes; }
        private static bool Equal(byte[] a, byte[] b)
        { if (a == null || b == null) return false; var difference = a.Length ^ b.Length; for (var i = 0; i < Math.Min(a.Length, b.Length); i++) difference |= a[i] ^ b[i]; return difference == 0; }
        private static void ClearPrivate(RSAParameters p)
        { foreach (var bytes in new[] { p.D, p.P, p.Q, p.DP, p.DQ, p.InverseQ }) if (bytes != null) Array.Clear(bytes, 0, bytes.Length); }
        private static void CheckPassword(string password)
        { if (string.IsNullOrWhiteSpace(password) || password.Length > 1024) throw Invalid(); }
        private static CryptographicException Invalid() { return new CryptographicException("Неверные данные ключа, пароль или подпись конфигурации."); }
        public static byte[] ReadBounded(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (file.Length < 1 || file.Length > MaximumBytes) throw Invalid();
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[4096]; int read;
                    while ((read = file.Read(buffer, 0, buffer.Length)) != 0)
                    { if (output.Length + read > MaximumBytes) throw Invalid(); output.Write(buffer, 0, read); }
                    return output.ToArray();
                }
            }
        }
    }
}

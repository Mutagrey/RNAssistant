using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed class DeploymentConfigurationStore : IDisposable
    {
        // Raise deliberately to invalidate every older signed configuration in the new build.
        // This is independent of the product and file-format versions.
        public const int RequiredPolicyVersion = 1;
        private readonly object _sync = new object();
        private readonly AppDataPaths _paths;
        private readonly DeploymentTrust _trust;
        private DeploymentBrowserSettings _browser = new DeploymentBrowserSettings();
        private DeploymentConfigState _state;
        private string _contentHash, _session;
        private SecureString _password;
        private DateTime _expiresUtc, _retryAfterUtc;
        private bool _disposed;

        public DeploymentConfigurationStore(AppDataPaths paths, DeploymentTrust trust = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            try
            {
                var candidate = trust ?? EmbeddedTrust();
                DeploymentConfigurationCrypto.ValidateTrust(candidate);
                _trust = candidate;
            }
            catch (Exception error) when (PolicyFailure(error)) { _trust = null; }
            Refresh();
        }
        private static DeploymentTrust EmbeddedTrust()
        {
            using (var stream = typeof(DeploymentConfigurationStore).Assembly.GetManifestResourceStream("RNAssistant.DeploymentTrust"))
            {
                if (stream == null) return null;
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return JsonConvert.DeserializeObject<DeploymentTrust>(reader.ReadToEnd());
            }
        }
        public bool Allows(Uri address, BrowserAccessKind kind)
        {
            lock (_sync)
            {
                ThrowIfDisposed(); Refresh();
                if (address == null || !address.IsAbsoluteUri || !string.IsNullOrEmpty(address.UserInfo) ||
                    (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)) return false;
                return _state == DeploymentConfigState.Applied &&
                    (kind == BrowserAccessKind.Manual && _browser.AllowAnyManualSite || _browser.AllowedSites.Any(pattern => HttpOriginPattern.Matches(pattern, address)));
            }
        }
        public DeploymentConfigStatus Status()
        { lock (_sync) { ThrowIfDisposed(); Refresh(); return StatusCore(); } }
        private DeploymentConfigStatus StatusCore() { return new DeploymentConfigStatus { State = _state }; }
        private void Refresh()
        {
            if (!File.Exists(_paths.DeploymentConfigFile)) { Reset(DeploymentConfigState.Absent); return; }
            if (_trust == null) { Reset(DeploymentConfigState.Rejected); return; }
            try
            {
                var bytes = DeploymentConfigurationCrypto.ReadBounded(_paths.DeploymentConfigFile);
                string hash;
                using (var sha = SHA256.Create()) hash = Convert.ToBase64String(sha.ComputeHash(bytes));
                if (hash == _contentHash) return;
                Reset(DeploymentConfigState.Rejected);
                var policy = DeploymentConfigurationCrypto.Verify(bytes, _trust);
                if (policy?.FormatVersion != 1 || policy.PolicyVersion != RequiredPolicyVersion ||
                    policy.IssuedUtc.Kind != DateTimeKind.Utc || policy.Browser?.AllowedSites == null) return;
                _browser = new DeploymentBrowserSettings { AllowedSites = HttpOriginPattern.NormalizeList(policy.Browser.AllowedSites),
                    AllowAnyManualSite = policy.Browser.AllowAnyManualSite };
                _state = DeploymentConfigState.Applied; _contentHash = hash;
            }
            catch (Exception error) when (PolicyFailure(error)) { Reset(DeploymentConfigState.Rejected); }
        }
        public DeploymentConfigEditor Unlock(string password)
        {
            lock (_sync)
            {
                ThrowIfDisposed(); LockCore();
                if (DateTime.UtcNow < _retryAfterUtc) throw new InvalidOperationException("Повторите ввод пароля через несколько секунд.");
                if (_trust == null) throw new InvalidOperationException("В этой сборке не настроен режим разработчика.");
                try
                {
                    if (!DeploymentConfigurationCrypto.VerifyPassword(_trust, password)) throw new CryptographicException();
                    _password = new SecureString();
                    foreach (var character in password) _password.AppendChar(character);
                    _password.MakeReadOnly();
                    _session = Guid.NewGuid().ToString("N"); _expiresUtc = DateTime.UtcNow.AddMinutes(10);
                    Refresh(); return Editor();
                }
                catch (Exception error) when (PolicyFailure(error))
                {
                    LockCore(); _retryAfterUtc = DateTime.UtcNow.AddSeconds(2);
                    throw new InvalidOperationException("Неверный пароль разработчика.");
                }
            }
        }
        public DeploymentConfigEditor Save(string sessionToken, IEnumerable<string> sites, bool allowAnyManualSite, string privateKeyPath)
        {
            lock (_sync)
            {
                RequireSession(sessionToken);
                var normalized = HttpOriginPattern.NormalizeList(sites);
                if (string.IsNullOrWhiteSpace(privateKeyPath)) throw new InvalidOperationException("Укажите путь к закрытому ключу .bin.");
                var policy = new DeploymentConfiguration { FormatVersion = 1, PolicyVersion = RequiredPolicyVersion, IssuedUtc = DateTime.UtcNow,
                    Browser = new DeploymentBrowserSettings { AllowedSites = normalized, AllowAnyManualSite = allowAnyManualSite } };
                byte[] encoded;
                var pointer = Marshal.SecureStringToBSTR(_password);
                try { encoded = DeploymentConfigurationCrypto.Sign(policy, DeploymentConfigurationCrypto.ReadBounded(privateKeyPath), Marshal.PtrToStringBSTR(pointer), _trust); }
                finally { Marshal.ZeroFreeBSTR(pointer); }
                StorageFileSystem.WriteAtomic(_paths.DeploymentConfigFile, path => File.WriteAllBytes(path, encoded));
                Refresh();
                if (_state != DeploymentConfigState.Applied || !_browser.AllowedSites.SequenceEqual(normalized) || _browser.AllowAnyManualSite != allowAnyManualSite)
                    throw new IOException("Не удалось подтвердить сохранённую конфигурацию.");
                return Editor();
            }
        }
        public string ConfigurationDirectory(string sessionToken)
        { lock (_sync) { RequireSession(sessionToken); return _paths.Root; } }
        private void RequireSession(string token)
        {
            ThrowIfDisposed();
            if (_password == null || _session != token || DateTime.UtcNow >= _expiresUtc)
            { LockCore(); throw new InvalidOperationException("Режим разработчика закрыт. Введите пароль повторно."); }
        }
        private DeploymentConfigEditor Editor()
        { return new DeploymentConfigEditor { SessionToken = _session, ExpiresUtc = _expiresUtc, Sites = new List<string>(_browser.AllowedSites),
            AllowAnyManualSite = _browser.AllowAnyManualSite, PolicyVersion = RequiredPolicyVersion, FilePath = _paths.DeploymentConfigFile, Status = StatusCore() }; }
        public void Lock() { lock (_sync) LockCore(); }
        private void LockCore() { _password?.Dispose(); _password = null; _session = null; _expiresUtc = default(DateTime); }
        private void Reset(DeploymentConfigState state) { _browser = new DeploymentBrowserSettings(); _contentHash = null; _state = state; }
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(DeploymentConfigurationStore)); }
        public void Dispose() { lock (_sync) { _disposed = true; LockCore(); } }
        private static bool PolicyFailure(Exception error)
        { return error is IOException || error is UnauthorizedAccessException || error is CryptographicException ||
            error is FormatException || error is JsonException || error is InvalidOperationException || error is ArgumentException; }
    }
}

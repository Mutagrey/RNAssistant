using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.DeploymentKeys
{
    internal sealed class IssuerFilesResult
    {
        internal string PublicFile;
        internal string PrivateFile;
        internal bool Replaced;
    }
    internal static class IssuerFiles
    {
        internal static void ValidateDestination(string repository, bool replace)
        {
            var root = Path.GetFullPath(repository);
            if (!File.Exists(Path.Combine(root, "src", "RNAssistant.Core", "RNAssistant.Core.csproj"))) throw new InvalidOperationException("Выберите корень проекта RNAssistant.");
            if (!replace && (File.Exists(PublicPath(root)) || File.Exists(PrivatePath(root))))
                throw new InvalidOperationException("Ключи уже существуют. Их замена требует явного выбора, новой сборки приложения и новых конфигов.");
        }
        internal static IssuerFilesResult Generate(string repository, string password, bool replace)
        {
            ValidateDestination(repository, replace);
            if (string.IsNullOrWhiteSpace(password) || password.Length < 12 || password.Length > 1024)
                throw new InvalidOperationException("Используйте парольную фразу от 12 до 1024 символов.");
            var root = Path.GetFullPath(repository); var trustPath = PublicPath(root); var keyPath = PrivatePath(root);
            var replacing = File.Exists(trustPath) || File.Exists(keyPath);
            byte[] key;
            var trust = DeploymentConfigurationCrypto.CreateIssuer(password, out key);
            var probe = new DeploymentConfiguration { FormatVersion = 1, PolicyVersion = 1, IssuedUtc = DateTime.UtcNow, Browser = new DeploymentBrowserSettings() };
            DeploymentConfigurationCrypto.Verify(DeploymentConfigurationCrypto.Sign(probe, key, password, trust), trust);
            Directory.CreateDirectory(Path.GetDirectoryName(trustPath));
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath));
            if (replacing)
            {
                var backup = Path.Combine(Path.GetDirectoryName(keyPath), "backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backup);
                if (File.Exists(keyPath)) File.Copy(keyPath, Path.Combine(backup, "signing-key.bin"));
                if (File.Exists(trustPath)) File.Copy(trustPath, Path.Combine(backup, "deployment-trust.json"));
            }
            Write(keyPath, key);
            Write(trustPath, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(trust, Formatting.Indented)));
            return new IssuerFilesResult { PublicFile = trustPath, PrivateFile = keyPath, Replaced = replacing };
        }
        private static string PublicPath(string root) { return Path.Combine(root, "config", "deployment-trust.json"); }
        private static string PrivatePath(string root) { return Path.Combine(root, ".local", "deployment", "signing-key.bin"); }
        private static void Write(string path, byte[] bytes)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
#if NET8_0_OR_GREATER
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#endif
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

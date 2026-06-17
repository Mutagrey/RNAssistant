using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RNAssistant.Core.Storage
{
    public sealed class AppDataPaths
    {
        public string Root { get; private set; }
        public string SettingsFile { get; private set; }
        public string SecretFile { get; private set; }
        public string SkillsFile { get; private set; }
        public string ChatDirectory { get; private set; }
        public string ContextDirectory { get; private set; }
        public string WebViewUserDataDirectory { get; private set; }

        public static AppDataPaths CreateDefault()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RNAssistant");
            var paths = new AppDataPaths
            {
                Root = root,
                SettingsFile = Path.Combine(root, "settings.json"),
                SecretFile = Path.Combine(root, "secret.bin"),
                SkillsFile = Path.Combine(root, "skills.json"),
                ChatDirectory = Path.Combine(root, "chats"),
                ContextDirectory = Path.Combine(root, "contexts"),
                WebViewUserDataDirectory = Path.Combine(root, "webview")
            };
            paths.Ensure();
            return paths;
        }

        public void Ensure()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(ChatDirectory);
            Directory.CreateDirectory(ContextDirectory);
            Directory.CreateDirectory(WebViewUserDataDirectory);
        }

        public static string SafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                value = "empty";
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}


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
        public string ToolsDirectory { get; private set; }
        public string SkillsDirectory { get; private set; }
        public string VbaBackupDirectory { get; private set; }
        public string ChatDirectory { get; private set; }
        public string HtmlArtifactBodyDirectory { get; private set; }
        public string WebViewUserDataDirectory { get; private set; }
        public string AttachmentDirectory { get; private set; }

        public static AppDataPaths CreateDefault()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RNAssistant");
            return CreateForRoot(root);
        }

        public static AppDataPaths CreateForRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException("Root path is required.", "root");
            }

            var paths = new AppDataPaths
            {
                Root = root,
                SettingsFile = Path.Combine(root, "settings.json"),
                SecretFile = Path.Combine(root, "secret.bin"),
                ToolsDirectory = Path.Combine(root, "tools"),
                SkillsDirectory = Path.Combine(root, "skills"),
                VbaBackupDirectory = Path.Combine(root, "vba-backups"),
                ChatDirectory = Path.Combine(root, "chats"),
                HtmlArtifactBodyDirectory = Path.Combine(root, "html-artifact-bodies"),
                AttachmentDirectory = Path.Combine(root, "attachments"),
                WebViewUserDataDirectory = Path.Combine(root, "webview")
            };
            paths.Ensure();
            return paths;
        }

        public void Ensure()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(ToolsDirectory);
            Directory.CreateDirectory(SkillsDirectory);
            Directory.CreateDirectory(VbaBackupDirectory);
            Directory.CreateDirectory(ChatDirectory);
            Directory.CreateDirectory(HtmlArtifactBodyDirectory);
            Directory.CreateDirectory(AttachmentDirectory);
            Directory.CreateDirectory(WebViewUserDataDirectory);
        }

        public void ClearRuntimeData()
        {
            ClearDirectory(ChatDirectory);
            ClearDirectory(HtmlArtifactBodyDirectory);
            ClearDirectory(VbaBackupDirectory);
            ClearDirectory(AttachmentDirectory);
            ClearDirectory(WebViewUserDataDirectory);
            Ensure();
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

        private static void ClearDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(directory))
            {
                TryDeleteFile(file);
            }

            foreach (var child in Directory.GetDirectories(directory))
            {
                TryDeleteDirectory(child);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

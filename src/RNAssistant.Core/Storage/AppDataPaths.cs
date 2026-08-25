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
        public string HistorySecretFile { get; private set; }
        public string HistoryProtectionSaltFile { get; private set; }
        public string ToolsDirectory { get; private set; }
        public string SkillsDirectory { get; private set; }
        public string VbaJournalDirectory { get; private set; }
        public string ChatDirectory { get; private set; }
        public string ChatBlobDirectory { get; private set; }
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
                HistorySecretFile = Path.Combine(root, "history-secret.bin"),
                HistoryProtectionSaltFile = Path.Combine(root, "history-protection.salt"),
                ToolsDirectory = Path.Combine(root, "tools"),
                SkillsDirectory = Path.Combine(root, "skills"),
                VbaJournalDirectory = Path.Combine(root, "vba-journals"),
                ChatDirectory = Path.Combine(root, "chats"),
                ChatBlobDirectory = Path.Combine(root, "chat-blobs"),
                AttachmentDirectory = Path.Combine(root, "attachments"),
                WebViewUserDataDirectory = Path.Combine(root, "webview")
            };
            paths.Ensure();
            return paths;
        }

        public void Ensure()
        {
            EnsureManagedDirectory(Root);
            EnsureManagedDirectory(ToolsDirectory);
            EnsureManagedDirectory(SkillsDirectory);
            EnsureManagedDirectory(VbaJournalDirectory);
            EnsureManagedDirectory(ChatDirectory);
            EnsureManagedDirectory(ChatBlobDirectory);
            EnsureManagedDirectory(AttachmentDirectory);
            EnsureManagedDirectory(WebViewUserDataDirectory);
        }

        public void ClearRuntimeData()
        {
            ClearDirectory(ChatDirectory);
            ClearDirectory(ChatBlobDirectory);
            ClearDirectory(VbaJournalDirectory);
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
            if (StorageFileSystem.IsReparsePoint(directory))
            {
                throw new IOException("Managed storage directory cannot be a reparse point: " + directory);
            }

            foreach (var file in Directory.GetFiles(directory))
            {
                TryDeleteFile(file);
            }

            foreach (var child in Directory.GetDirectories(directory))
            {
                StorageFileSystem.TryDeleteDirectory(child);
            }
        }

        private static void EnsureManagedDirectory(string directory)
        {
            StorageFileSystem.EnsureRegularDirectory(directory);
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

    }
}

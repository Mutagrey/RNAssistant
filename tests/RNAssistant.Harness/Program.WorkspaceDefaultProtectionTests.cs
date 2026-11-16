using System;
using System.IO;
using RNAssistant.Application;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceDefaultStorageRequiresConfiguredProtection()
        {
            foreach (var encrypted in new[] { false, true })
            WithTempPaths(paths => {
                new JsonFileStore().Save(paths.SettingsFile, new AppSettings {
                    HistoryIntegrityMode = HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionMode = encrypted ? HistoryEncryptionModes.Aes256CbcHmacSha256 : HistoryEncryptionModes.None,
                    HistoryKeySource = HistoryKeySources.CustomSecret
                });
                // The harness deliberately has no DPAPI fixture key. Default
                // application composition must refuse the protected root before
                // installing an unprotected resource owner or accepting a write.
                RuntimeThrows<InvalidOperationException>(() => {
                    using (var application = new WorkspaceApplicationService(paths)) { }
                });
                RuntimeThrows<InvalidOperationException>(() => new PluginStore(paths));
                AssertTrue(!Directory.Exists(paths.JournalResourceDirectory) ||
                    Directory.GetFiles(paths.JournalResourceDirectory, "*.jsonl", SearchOption.AllDirectories).Length == 0,
                    "missing key cannot create an unprotected resource stream");
                AssertEqual(0, Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length,
                    "missing key cannot publish plaintext bodies");
            });
        }
    }
}

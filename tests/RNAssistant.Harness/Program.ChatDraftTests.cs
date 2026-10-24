using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ProtectedChatDraftsRoundTrip()
        {
            WithTempPaths(paths =>
            {
                Func<byte[], byte[]> codec = bytes => bytes.Reverse().Select(value => (byte)(value ^ 0x5a)).ToArray();
                var store = new ProtectedChatDraftStore(paths, codec, codec);
                var office = new ChatInputDraft { Owner = "office", ChatId = "office-a", Text = "unsent secret",
                    ResourceDraftIds = new List<string> { "staged-a" }, NextAnnotationNumber = 3,
                    PendingOfficeInput = new PendingOfficeChatInput {
                        OperationId = "operation-a", Text = "unsent secret", Delivery = "Steer",
                        ResourceDraftIds = new List<string> { "staged-a" } } };
                var workspace = new ChatInputDraft { Owner = "workspace", WorkspaceId = "project-a", SessionId = "session-a",
                    Text = "later answer", Mode = "plan", Reasoning = true, PendingInput = new PendingChatInput {
                        RequestId = "request-a", Message = "later answer", Mode = "plan", Reasoning = true } };
                store.Save(office);
                store.Save(workspace);
                store.Save(new ChatInputDraft { Owner = "workspace", WorkspaceId = "project-b", SessionId = "session-a",
                    Text = "other project", Mode = "agent" });
                AssertTrue(!Encoding.UTF8.GetString(File.ReadAllBytes(paths.ChatDraftFile)).Contains("unsent secret"),
                    "draft file must not contain plaintext");
                var reopened = new ProtectedChatDraftStore(paths, codec, codec).Load();
                AssertTrue(reopened.Count == 3 && reopened.Single(item => item.Owner == "office").ResourceDraftIds.Single() == "staged-a",
                    "Office text and staged resource identity survive reopen");
                AssertTrue(reopened.Single(item => item.Owner == "office").PendingOfficeInput.OperationId == "operation-a",
                    "Office uncertain-send identity survives reopen");
                AssertEqual(3, reopened.Single(item => item.Owner == "office").NextAnnotationNumber,
                    "unsent annotation numbers do not get reused after reopening a draft");
                AssertTrue(reopened.Single(item => item.WorkspaceId == "project-a").PendingInput.RequestId == "request-a",
                    "workspace retry identity survives reopen");
                store.DeleteWorkspace("project-a", "session-a");
                AssertTrue(store.Load().Count == 2 && store.Load().Any(item => item.WorkspaceId == "project-b"),
                    "workspace deletion is exact");
                store.DeleteOffice("office-a");
                AssertTrue(store.Load().Count == 1, "Office deletion is exact");
                var encrypted = File.ReadAllBytes(paths.ChatDraftFile);
                encrypted[0] ^= 0x20;
                File.WriteAllBytes(paths.ChatDraftFile, encrypted);
                RuntimeThrows<Exception>(() => store.Load());
                paths.ClearRuntimeData();
                AssertTrue(!File.Exists(paths.ChatDraftFile), "clear runtime data removes unsent drafts");
            });
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RNAssistant.Core.Models;
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
                var selection = new HtmlVisualSelection { Tag = "h1", StableUiId = "", Selector = "h1:nth-of-type(1)",
                    Attributes = new Dictionary<string, string>(), Ancestry = new List<HtmlVisualAncestor>(),
                    TextExcerpt = "Title", Rect = new HtmlVisualRect { X = 10, Y = 20, Width = 100, Height = 30 },
                    Viewport = new HtmlVisualViewport { Width = 800, Height = 600, Dpr = 1 } };
                var office = new ChatInputDraft { Owner = "office", ChatId = "office-a", Text = "unsent secret",
                    ResourceDraftIds = new List<string> { "staged-a" }, NextAnnotationNumber = 3,
                    PendingVisualAnnotation = new PendingVisualAnnotation { ArtifactId = "html-r1",
                        ArtifactRevision = 1, Number = 2, Comment = "Make it green", Selection = selection,
                        ReplacingDraftId = "staged-a" },
                    PendingOfficeInput = new PendingOfficeChatInput {
                        OperationId = "operation-a", Text = "unsent secret", Delivery = "Steer",
                        ResourceDraftIds = new List<string> { "staged-a" } } };
                var workspace = new ChatInputDraft { Owner = "workspace", WorkspaceId = "project-a", SessionId = "session-a",
                    Text = "later answer", Mode = "plan", Reasoning = true,
                    ResourceDraftIds = new List<string> { "plugin-selection-a" }, NextAnnotationNumber = 2,
                    PendingVisualAnnotation = new PendingVisualAnnotation { ArtifactId = "rel_saved",
                        ArtifactRevision = 1, Number = 1, Comment = "Adjust heading", Selection = selection,
                        ReplacingDraftId = "plugin-selection-a" },
                    PendingInput = new PendingChatInput {
                        RequestId = "request-a", Message = "later answer", Mode = "plan", Reasoning = true,
                        MatchesDraftVersion = true,
                        ResourceDraftIds = new List<string> { "plugin-selection-a" } } };
                store.Save(office);
                store.Save(workspace);
                store.Save(new ChatInputDraft { Owner = "workspace", WorkspaceId = "project-b", SessionId = "session-a",
                    Text = "other project", Mode = "agent" });
                AssertTrue(!Encoding.UTF8.GetString(File.ReadAllBytes(paths.ChatDraftFile)).Contains("unsent secret"),
                    "draft file must not contain plaintext");
                AssertTrue(!Encoding.UTF8.GetString(File.ReadAllBytes(paths.ChatDraftFile)).Contains("Make it green"),
                    "unfinished annotation text must use the protected draft store");
                var reopened = new ProtectedChatDraftStore(paths, codec, codec).Load();
                AssertTrue(reopened.Count == 3 && reopened.Single(item => item.Owner == "office").ResourceDraftIds.Single() == "staged-a",
                    "Office text and staged resource identity survive reopen");
                AssertTrue(reopened.Single(item => item.Owner == "office").PendingOfficeInput.OperationId == "operation-a",
                    "Office uncertain-send identity survives reopen");
                AssertEqual(3, reopened.Single(item => item.Owner == "office").NextAnnotationNumber,
                    "unsent annotation numbers do not get reused after reopening a draft");
                AssertEqual("Make it green", reopened.Single(item => item.Owner == "office").PendingVisualAnnotation.Comment,
                    "unfinished visual annotation survives reopen with its original comment");
                AssertEqual("staged-a", reopened.Single(item => item.Owner == "office").PendingVisualAnnotation.ReplacingDraftId,
                    "pending edit remains bound to its staged attachment");
                office.PendingVisualAnnotation.Number = office.NextAnnotationNumber;
                RuntimeThrows<InvalidDataException>(() => store.Save(office));
                office.PendingVisualAnnotation.Number = 2;
                AssertTrue(reopened.Single(item => item.WorkspaceId == "project-a").PendingInput.RequestId == "request-a",
                    "workspace retry identity survives reopen");
                AssertTrue(reopened.Single(item => item.WorkspaceId == "project-a").PendingInput.MatchesDraftVersion,
                    "the exact submitted draft relation survives protected storage");
                workspace.PendingInput.MatchesDraftVersion = false;
                store.Save(workspace);
                AssertTrue(!store.Load().Single(item => item.WorkspaceId == "project-a").PendingInput.MatchesDraftVersion,
                    "a newer identical draft is distinct from the submitted version");
                AssertEqual("plugin-selection-a", reopened.Single(item => item.WorkspaceId == "project-a").ResourceDraftIds.Single(),
                    "workspace visual attachment survives protected draft reopen");
                AssertEqual("Adjust heading", reopened.Single(item => item.WorkspaceId == "project-a").PendingVisualAnnotation.Comment,
                    "unfinished plugin annotation survives protected draft reopen");
                AssertEqual("plugin-selection-a", reopened.Single(item => item.WorkspaceId == "project-a").PendingInput.ResourceDraftIds.Single(),
                    "uncertain workspace input keeps the exact visual draft identity");
                workspace.PendingInput = new PendingChatInput { RequestId = "steer-request", Message = "clarification",
                    SteerRunId = "exact-run", Mode = "agent" };
                store.Save(workspace);
                AssertEqual("exact-run", store.Load().Single(item => item.WorkspaceId == "project-a").PendingInput.SteerRunId,
                    "uncertain steer retains its exact run after reopen");
                workspace.PendingInput.SteerRunId = " ";
                RuntimeThrows<InvalidDataException>(() => store.Save(workspace));
                workspace.PendingInput = new PendingChatInput { RequestId = "edit-request", Message = "revised",
                    EditMessageId = "user-message", ExpectedRevision = 7, Mode = "plan" };
                store.Save(workspace);
                var editPending = new ProtectedChatDraftStore(paths, codec, codec).Load()
                    .Single(item => item.WorkspaceId == "project-a").PendingInput;
                AssertTrue(editPending.RequestId == "edit-request" && editPending.EditMessageId == "user-message" &&
                    editPending.ExpectedRevision == 7 && editPending.ResourceDraftIds.Count == 0,
                    "uncertain workspace edit keeps exact message and revision without new resources");
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

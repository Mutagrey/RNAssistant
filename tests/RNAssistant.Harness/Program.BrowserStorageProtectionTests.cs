using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void BrowserUsesSharedProtectedResources()
        {
            foreach (var encrypted in new[] { false, true }) WithTempPaths(paths => {
                var protector = new StorageProtector(HistoryIntegrityModes.HmacSha256,
                    encrypted ? HistoryEncryptionModes.Aes256CbcHmacSha256 : HistoryEncryptionModes.None,
                    "synthetic-browser-protection", Enumerable.Repeat((byte)19, 16).ToArray());
                var resources = new ResourceJournal(paths, protector);
                var payloads = new ChatBlobStore(paths, () => protector);
                var session = new ChatSession { Host = "Workspace", WorkspaceId = "protected-browser-workspace" };
                var owner = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace,
                    ScopeId = session.WorkspaceId, ChatId = session.Id };
                var source = new HarnessBrowserReadSource { Target = new BrowserPageHandle(session.Id,
                    "protected-page", 1, browserTabId: "protected-tab", ownerScope: owner) };
                var scope = new ResourceAuthorityScopeId("conversation", session.Id);
                resources.Capture(scope); // A protected journal already exists before Browser is bound.
                var contribution = new BrowserCapabilityService(() => source).ForOwner(owner, session,
                    new AppSettings(), "protected-run", null, resources, resources, resources, payloads);
                Func<IReadOnlyToolHandler, string, ToolPolicy, Dictionary<string, object>, ToolHandlerResult> execute =
                    (handler, name, policy, arguments) => {
                        var call = new ToolCall(Guid.NewGuid().ToString("N"), name, "{}");
                        var context = new ToolExecutionContext(call, new ToolPolicySnapshot(name, "test", policy),
                            "protected-run", "turn", "step", DateTime.UtcNow, false, 1);
                        return handler.ExecuteAsync(new ToolHandlerContext(context, arguments), CancellationToken.None)
                            .GetAwaiter().GetResult();
                    };
                var found = execute(contribution.ResourceFind, "core.find", ResourceFindToolHandler.Policy,
                    new Dictionary<string, object>());
                AssertEqual(ToolResultStatus.Ok, found.Result.Status, "Browser discovery reads the shared protected journal");
                var read = execute(contribution.ResourceRead, "core.read", ResourceReadToolHandler.Policy,
                    new Dictionary<string, object> { ["target"] = source.Target.Target, ["representation"] = "text" });
                AssertEqual(ToolResultStatus.Ok, read.Result.Status, "Browser read publishes through the supplied authority");
                AssertEqual(1, source.Captures, "one live Browser capture");
                var identity = new ResourceIdentity(ResourceUri.Create("browser", session.Id, source.Target.SessionId));
                var head = resources.GetHead(scope, identity);
                AssertTrue(head?.Revision != null, "runtime owner sees the Browser publication immediately");
                var view = resources.GetView(scope, head.Revision, "text");
                AssertEqual(protector.EncryptionMode, view.Payload.Encryption, "Browser CAS uses the runtime encryption mode");
                AssertEqual(encrypted ? protector.KeyId : null, view.Payload.ProtectionKeyId,
                    "encrypted Browser CAS uses the runtime protection key");
                var reopened = new ResourceJournal(paths, protector);
                var retained = reopened.GetView(scope, head.Revision, "text");
                var reopenedPayloads = new ChatBlobStore(paths, () => protector);
                AssertTrue(reopenedPayloads.ReadText(retained.Payload.ToBlobReference()).Contains("button: Save [disabled]"),
                    "retained Browser bytes remain readable after reopening the protected owner");
                RuntimeThrows<System.IO.InvalidDataException>(() => new ResourceJournal(paths).Capture(scope));
            });
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void BrowserRuntimeResetRetainsHtmlNotifications()
        {
            WithPluginPaths(paths => {
                var workspaces = new WorkspaceStore(paths);
                var workspace = workspaces.ConnectProject(Path.Combine(paths.Root, "reset-html-source"), true);
                File.WriteAllText(Path.Combine(workspace.RootPath, "index.html"), "<!doctype html><h1>Source survives reset</h1>");
                workspaces.SetHtmlProjectDefinition(workspace, null, new HtmlProjectDefinition {
                    EntryPath = "index.html", Members = new[] { "index.html" } });
                using (var service = new PluginApplicationService(paths))
                {
                    var changes = new List<BrowserVersionStateChanged>();
                    service.VersionChanged += changes.Add;
                    var owner = new BrowserOwnerScope { Kind = BrowserOwnerKind.User, ScopeId = "local" };
                    var locator = BrowserLocalLocator.ForProject(workspace.WorkspaceId, "index.html");
                    Func<BrowserVersionResult> save = () => {
                        var target = service.ResolveBrowserLocator(new BrowserResolveRequest { OwnerScope = owner, Locator = locator }).Target;
                        var state = service.BrowserVersionState(new BrowserVersionRequest { Target = target });
                        return service.BrowserVersionCommand(new BrowserVersionCommand { Target = target,
                            Action = BrowserVersionAction.Save, ExpectedStateRevision = state.StateRevision,
                            ExpectedContentRevision = state.ContentRevision, ExpectedDefinitionRevision = state.DefinitionRevision,
                            OperationId = Guid.NewGuid().ToString("N") }, CancellationToken.None);
                    };
                    var before = save();
                    AssertEqual(BrowserVersionOutcome.Saved, before.Outcome, before.Message);
                    AssertEqual(1, changes.Count, "initial HTML owner forwards one saved-version notification");
                    var previousChat = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = workspace.WorkspaceId }).SessionId;
                    for (var reset = 0; reset < 2; reset++)
                    {
                        var previousHtml = service.HtmlProjects;
                        paths.ClearRuntimeData();
                        service.ResetAfterRuntimeDataClear();
                        changes.Clear();
                        var staleNotification = (Action<BrowserVersionStateChanged>)typeof(WorkspaceHtmlVersionService)
                            .GetField("VersionChanged", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(previousHtml);
                        staleNotification?.Invoke(new BrowserVersionStateChanged { Reason = BrowserVersionAction.Save });
                        AssertEqual(0, changes.Count, "replaced HTML owner cannot forward late notifications into the current service");
                        AssertTrue(service.Conversations.ConversationStore.ReadHeader(previousChat) == null,
                            "reset consumers cannot reuse a cleared conversation header");
                        var chat = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = workspace.WorkspaceId });
                        AssertTrue(chat.SessionId != previousChat, "the replacement composition creates a new chat in the preserved workspace");
                        previousChat = chat.SessionId;
                        var saved = save();
                        AssertEqual(BrowserVersionOutcome.Saved, saved.Outcome, saved.Message);
                        AssertEqual(1L, saved.Version.Ordinal, "reset starts a new version journal over the preserved source files");
                        AssertEqual(1, changes.Count, "replacement HTML owner forwards one save notification");
                        AssertEqual(saved.Version.VersionId, changes[0].VersionId, "save notification identifies the exact new version");
                        var savedLocator = BrowserLocalLocator.ForProject(workspace.WorkspaceId, "index.html", saved.Version.VersionId);
                        var savedTarget = service.ResolveBrowserLocator(new BrowserResolveRequest { OwnerScope = owner, Locator = savedLocator }).Target;
                        var savedSource = service.ReadBrowserSource(new BrowserSourceRequest { OwnerScope = owner, Locator = savedLocator }, savedTarget);
                        AssertEqual(service.Conversations.WorkspaceFiles.ReadText(workspace, "index.html").Text, savedSource.Text,
                            "saved Browser source and the new conversation file port share the replacement resource composition");
                        var removed = service.BrowserVersionCommand(new BrowserVersionCommand { Target = saved.State.Target,
                            Action = BrowserVersionAction.Delete, VersionId = saved.Version.VersionId,
                            ExpectedStateRevision = saved.State.StateRevision, Confirmed = true,
                            OperationId = Guid.NewGuid().ToString("N") }, CancellationToken.None);
                        AssertEqual(BrowserVersionOutcome.Removed, removed.Outcome, removed.Message);
                        AssertEqual(2, changes.Count, "replacement HTML owner also forwards the delete notification exactly once");
                        AssertEqual(BrowserVersionAction.Delete, changes[1].Reason, "other Browser tabs can invalidate the deleted saved target");
                        AssertEqual(workspace.WorkspaceId, changes[1].Target.WorkspaceId, "notification preserves the exact workspace scope");
                    }
                }
            });
        }
    }
}

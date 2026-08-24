using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void HtmlWorkspaceCheckpointsStayInternalUntilMutation()
        {
            var empty = new ChatSession();
            var emptyId = HtmlWorkspaceArtifactService.CaptureCurrent(empty, "Before chat turn");
            AssertTrue(string.IsNullOrWhiteSpace(emptyId), "empty pre-turn workspace needs no artifact");
            AssertEqual(0, empty.Artifacts.Count, "empty pre-turn workspace creates no artifact");

            var session = new ChatSession();
            var executor = new HtmlArtifactToolExecutor();
            var write = new ToolCommand { ToolId = HtmlArtifactToolExecutor.UpsertToolId };
            write.Arguments["resourceType"] = "file";
            write.Arguments["name"] = "index.html";
            write.Arguments["content"] = "<h1>Report</h1>";
            write.Arguments["setActive"] = true;
            var writeResult = executor.ExecuteControllerTool(write, session, false);
            AssertTrue(writeResult.Success, "html mutation succeeds");
            var revisionId = session.ActiveHtmlArtifactId;

            var writeMessage = new ChatMessage
            {
                Role = "assistant",
                HtmlWorkspaceCheckpointId = revisionId,
                Activity = new ChatActivity
                {
                    ToolId = write.ToolId,
                    DataJson = writeResult.DataJson
                }
            };
            var read = new ToolCommand { ToolId = HtmlArtifactToolExecutor.ReadWorkspaceToolId };
            var readResult = executor.ExecuteControllerTool(read, session, false);
            var readMessage = new ChatMessage
            {
                Role = "assistant",
                HtmlWorkspaceCheckpointId = revisionId,
                ArtifactIds = new List<string> { revisionId },
                Activity = new ChatActivity
                {
                    ToolId = read.ToolId,
                    DataJson = readResult.DataJson
                }
            };
            session.Messages.Add(writeMessage);
            session.Messages.Add(readMessage);

            ChatArtifactService.LinkMessageArtifacts(session, 0);

            AssertTrue(writeMessage.ArtifactIds.Contains(revisionId), "html mutation links its revision artifact");
            AssertTrue(!readMessage.ArtifactIds.Contains(revisionId), "html read does not expose the checkpoint artifact");
        }

        private static void HtmlArtifactBodiesAreExternalizedAndHydrated()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "book", "Book.xlsx", "HTML revisions");
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "<h1>First revision</h1>", true);
                var firstId = HtmlWorkspaceArtifactService.CaptureCurrent(session, "First");
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "<h1>Second revision</h1>", true);
                var secondId = HtmlWorkspaceArtifactService.CaptureCurrent(session, "Second");
                session.Artifacts.Add(new ChatArtifact
                {
                    Kind = ChatArtifactKinds.Markdown,
                    Title = "Small note",
                    InlineText = "keep inline"
                });
                store.Save(session);

                var persisted = JObject.Parse(File.ReadAllText(SessionFile(paths, session)));
                var persistedArtifacts = (JArray)persisted["Artifacts"];
                AssertEqual(ChatSession.CurrentFormatVersion, (int)persisted["FormatVersion"], "externalized session format");
                AssertTrue(persistedArtifacts
                    .Where(item => string.Equals((string)item["Kind"], ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
                    .All(item => item["InlineText"] == null), "html bodies omitted from chat json");
                AssertEqual("keep inline", (string)persistedArtifacts.Single(item =>
                    string.Equals((string)item["Kind"], ChatArtifactKinds.Markdown, StringComparison.OrdinalIgnoreCase))["InlineText"],
                    "small artifact body stays inline");
                AssertEqual(2, Directory.GetFiles(BodyDirectory(paths, session.Id), "*.json").Length, "revision body files");

                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertTrue(string.IsNullOrWhiteSpace(loaded.Artifacts.Single(artifact => artifact.Id == firstId).InlineText),
                    "inactive revision remains lazy");
                AssertTrue(!string.IsNullOrWhiteSpace(loaded.Artifacts.Single(artifact => artifact.Id == secondId).InlineText),
                    "active revision is hydrated");
                AssertTrue(store.LoadHtmlArtifactBody(loaded, firstId), "inactive revision loaded on demand");
                AssertTrue(HtmlWorkspaceArtifactService.Restore(loaded, firstId), "first external revision restored");
                AssertEqual("<h1>First revision</h1>", loaded.HtmlWorkspace.Files.Single().Content, "first external revision content");
                AssertTrue(HtmlWorkspaceArtifactService.Restore(loaded, secondId), "second external revision restored");
                paths.ClearRuntimeData();
                AssertEqual(0, Directory.GetDirectories(paths.HtmlArtifactBodyDirectory).Length, "runtime reset clears revision bodies");
            });
        }

        private static void InlineHtmlArtifactBodiesMigrateOnSave()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "legacy-doc", "Legacy.docx", "Legacy HTML");
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "<main>Legacy inline body</main>", true);
                var artifactId = HtmlWorkspaceArtifactService.CaptureCurrent(session, "Legacy");
                session.FormatVersion = 2;
                File.WriteAllText(SessionFile(paths, session), JsonConvert.SerializeObject(session, Formatting.Indented));

                var legacy = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertTrue(!string.IsNullOrWhiteSpace(legacy.Artifacts.Single().InlineText), "legacy inline body loaded");
                store.Save(legacy);

                var migrated = JObject.Parse(File.ReadAllText(SessionFile(paths, session)));
                AssertEqual(ChatSession.CurrentFormatVersion, (int)migrated["FormatVersion"], "legacy session migrated to current format");
                AssertTrue(((JArray)migrated["Artifacts"]).Single()["InlineText"] == null, "migrated inline body omitted");
                AssertEqual(1, Directory.GetFiles(BodyDirectory(paths, session.Id), "*.json").Length, "migrated body file created");
                AssertTrue(HtmlWorkspaceArtifactService.Restore(store.Load(session.Id), artifactId), "migrated revision restores");
            });
        }

        private static void HtmlArtifactBodiesFollowForkPruneAndDelete()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var source = store.Create("Excel", "fork-book", "Fork.xlsx", "Source");
                HtmlArtifactToolExecutor.UpsertFile(source, "index.html", "html", "one", true);
                var firstId = HtmlWorkspaceArtifactService.CaptureCurrent(source, "One");
                HtmlArtifactToolExecutor.UpsertFile(source, "index.html", "html", "two", true);
                var secondId = HtmlWorkspaceArtifactService.CaptureCurrent(source, "Two");
                source.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "checkpoint",
                    HtmlWorkspaceCheckpointId = secondId,
                    ArtifactIds = new List<string> { secondId }
                });
                store.Save(source);
                source = store.Load(source.Host, source.DocumentKey, source.Id);
                AssertTrue(string.IsNullOrWhiteSpace(source.Artifacts.Single(artifact => artifact.Id == firstId).InlineText),
                    "source parent revision remains lazy before fork");

                var fork = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Fork");
                fork.Messages = ChatCloneService.CloneMessages(source.Messages);
                store.LoadHtmlArtifactBodies(
                    source,
                    ChatArtifactService.ReachableForMessages(source.Artifacts, fork.Messages)
                        .Where(artifact => string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
                        .Select(artifact => artifact.Id));
                fork.Artifacts = ChatCloneService.CloneArtifactsForMessages(source.Artifacts, fork.Messages);
                fork.ActiveHtmlArtifactId = secondId;
                AssertTrue(HtmlWorkspaceArtifactService.Restore(fork, secondId), "fork workspace restored before save");
                store.Save(fork);
                AssertEqual(2, Directory.GetDirectories(paths.HtmlArtifactBodyDirectory).Length, "fork has independent body directory");

                source.Artifacts = source.Artifacts.Where(artifact => artifact.Id != firstId).ToList();
                source.Artifacts.Single().ParentArtifactId = null;
                store.Save(source);
                AssertEqual(1, Directory.GetFiles(BodyDirectory(paths, source.Id), "*.json").Length, "pruned revision body removed");
                AssertEqual(2, Directory.GetFiles(BodyDirectory(paths, fork.Id), "*.json").Length, "fork revision bodies preserved");

                AssertTrue(store.Delete(source.Host, source.DocumentKey, source.Id), "source chat deleted");
                AssertTrue(!Directory.Exists(BodyDirectory(paths, source.Id)), "source body directory deleted");
                var loadedFork = store.Load(fork.Host, fork.DocumentKey, fork.Id);
                AssertTrue(store.LoadHtmlArtifactBody(loadedFork, firstId), "fork parent revision loaded on demand");
                AssertTrue(HtmlWorkspaceArtifactService.Restore(loadedFork, firstId), "fork remains independent after source delete");
                AssertTrue(store.Delete(fork.Host, fork.DocumentKey, fork.Id), "fork chat deleted");
                AssertEqual(0, Directory.GetDirectories(paths.HtmlArtifactBodyDirectory).Length, "all body directories deleted");
            });
        }

        private static void BrokenHtmlArtifactBodyDoesNotReplaceWorkspace()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var session = store.Create("PowerPoint", "deck", "Deck.pptx", "Broken body");
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "persisted", true);
                var artifactId = HtmlWorkspaceArtifactService.CaptureCurrent(session, "Persisted");
                store.Save(session);
                File.WriteAllText(Directory.GetFiles(BodyDirectory(paths, session.Id), "*.json").Single(), "{ broken");

                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                loaded.HtmlWorkspace.Files.Single().Content = "keep current workspace";
                AssertTrue(!HtmlWorkspaceArtifactService.Restore(loaded, artifactId), "broken revision rejected");
                AssertEqual("keep current workspace", loaded.HtmlWorkspace.Files.Single().Content, "failed restore leaves workspace unchanged");
            });
        }

        private static void LazyHtmlArtifactBodySupportsEditRewind()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "edit-doc", "Edit.docx", "Lazy edit");
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "before edit", true);
                var beforeId = HtmlWorkspaceArtifactService.CaptureCurrent(session, "Before edit");
                var target = new ChatMessage
                {
                    Role = "user",
                    Content = "Original request",
                    HtmlWorkspaceCheckpointId = beforeId
                };
                session.Messages.Add(target);
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Old response" });
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "after edit", true);
                var afterId = HtmlWorkspaceArtifactService.CaptureCurrent(session, "After edit");
                store.Save(session);

                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertTrue(string.IsNullOrWhiteSpace(loaded.Artifacts.Single(artifact => artifact.Id == beforeId).InlineText),
                    "edit checkpoint body starts lazy");
                AssertTrue(!string.IsNullOrWhiteSpace(loaded.Artifacts.Single(artifact => artifact.Id == afterId).InlineText),
                    "active edit body starts hydrated");
                var service = new ChatHistoryEditService(
                    delegate { },
                    delegate { },
                    store.LoadHtmlArtifactBody);

                service.RewriteUserMessage(loaded, loaded.Id, target.Id, -1, "Updated request");

                AssertEqual("before edit", loaded.HtmlWorkspace.Files.Single().Content, "lazy edit restores checkpoint content");
                AssertEqual(beforeId, loaded.ActiveHtmlArtifactId, "lazy edit restores checkpoint id");
                AssertTrue(!loaded.Artifacts.Any(artifact => artifact.Id == afterId), "lazy edit prunes future revision");
            });
        }

        private static string SessionFile(AppDataPaths paths, ChatSession session)
        {
            return Path.Combine(
                paths.ChatDirectory,
                AppDataPaths.SafeFileName((session.Host ?? string.Empty) + "|" + (session.DocumentKey ?? string.Empty)),
                AppDataPaths.SafeFileName(session.Id) + ".json");
        }

        private static string BodyDirectory(AppDataPaths paths, string sessionId)
        {
            return Path.Combine(paths.HtmlArtifactBodyDirectory, AppDataPaths.SafeFileName(sessionId));
        }
    }
}

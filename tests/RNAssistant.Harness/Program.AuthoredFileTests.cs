using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void AuthoredHtmlViewerReadsRetainedRevisions()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                session.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                var store = new ChatStore(FixturePaths.Value);
                var tools = executor.GetControllerTools().ToList();
                var source = "<!doctype html><main>Пример🙂 before</main><script>window.run()</script>";
                var created = executor.ExecuteManual(Command(AuthoredFileToolCatalog.CreateToolId,
                    "name", "index.html", "content", source), tools, new AppSettings(), false, false, session);
                AssertTrue(created.Success, "HTML creation succeeds: " + created.Message);
                var original = session.Artifacts.Single(item => item.Title == "index.html");
                AssertTrue(ChatArtifactResourceProvider.FindExactAttachment(session, original) == null,
                    "created HTML has retained file bytes, not an uploaded attachment");
                var viewer = new ArtifactViewerService(executor.ResourceGateway);
                Action<ChatSession, ChatArtifact, string> assertSource = (owner, artifact, expected) =>
                {
                    using (var plane = new ResourceDataPlaneService(executor.ResourceGateway))
                    {
                        var text = new StringBuilder();
                        var uri = ChatResourceUri.CreateArtifactRevisionUri(owner, artifact);
                        string cursor = null;
                        do
                        {
                            var page = viewer.ReadPage(owner, uri, cursor, plane);
                            AssertEqual(ArtifactViewerKinds.Text, page.ViewerKind, "HTML is always inert source");
                            AssertEqual(uri, page.ResourceUri, "viewer preserves the selected revision");
                            AssertEqual(TextPatternEngine.Sha256(expected), page.ContentSha256, "viewer pins decoded text");
                            AssertEqual(text.Length, page.Offset, "HTML pages are contiguous");
                            AssertEqual(expected.Length, page.TotalCharacters, "HTML total is exact");
                            text.Append(ReadViewerBatch(plane, page));
                            plane.Close(owner.Id, "viewer", page.Data.LeaseId);
                            cursor = page.NextCursor;
                            if (cursor == null) AssertTrue(page.Complete && page.SourceComplete, "HTML source is complete");
                            else AssertTrue(text.Length < expected.Length, "HTML continuation makes bounded progress");
                        } while (cursor != null);
                        AssertEqual(expected, text.ToString(), "viewer returns exact saved HTML");
                    }
                };
                assertSource(session, original, source);
                var patched = executor.ExecuteManual(Command(AuthoredFileToolCatalog.PatchToolId,
                    "target", (string)JObject.Parse(created.DataJson)["target"],
                    "patches", new[] { new { find = "before", replace = "after" } }),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(patched.Success, "HTML patch succeeds: " + patched.Message);
                var latest = session.Artifacts.Single(item => item.Title == "index.html" && item.Revision == 2);
                var changed = source.Replace("before", "after");
                assertSource(session, latest, changed);
                assertSource(session, original, source);

                var projection = ArtifactLibraryProjectionService.Project(session);
                var head = projection.Heads.Single(item => item.Title == "index.html");
                AssertEqual(latest.Id, head.ArtifactId, "one authored-file head selects the latest version");
                AssertEqual(ArtifactLibraryResourceClasses.VersionedDocument, head.ResourceClass, "authored HTML is not an upload");
                AssertEqual(ArtifactLibraryGroups.AuthoredDocuments, head.Group, "authored HTML belongs with documents");
                AssertEqual("v2", head.VersionLabel, "authored file has a version label");
                var history = ArtifactLibraryProjectionService.History(session, new ArtifactLibraryHistoryRequest
                { ChatId = session.Id, ExpectedSessionRevision = session.Revision, HeadArtifactId = head.ArtifactId });
                AssertEqual(2, history.Items.Count, "both exact HTML revisions remain accessible");
                store.Save(session);
                var restarted = store.Load(session.Id);
                assertSource(restarted, restarted.Artifacts.Single(item => item.Id == original.Id), source);
                assertSource(restarted, restarted.Artifacts.Single(item => item.Id == latest.Id), changed);

                var uploadedText = source + new string('x', 33000);
                var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(uploadedText)).ToArray();
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(FixturePaths.Value), store.DocumentArtifacts);
                var draft = ingestion.Stage(session, "copy.htm", "text/html", bytes);
                var message = new ChatMessage { Role = "user", Attachments = ingestion.LoadDrafts(session, new[] { draft.Id }).ToList() };
                session.Messages.Add(message);
                ingestion.CommitAndLink(session, message, session.Messages.Count - 1);
                var upload = session.Artifacts.Single(item => item.Title == "copy.htm");
                var target = executor.ResourceGateway.Find(session, "copy.htm", "document").Items.Single().Target;
                var copied = executor.ExecuteManual(Command(AuthoredFileToolCatalog.CopyToolId,
                    "source", target), tools, new AppSettings(), false, false, session);
                AssertTrue(copied.Success, "HTML upload copies as an authored file: " + copied.Message);
                var copy = session.Artifacts.Single(item => item.Title == "copy.htm" && item.Kind == ChatArtifactKinds.File);
                AssertEqual("text/html", copy.MimeType, "copied HTML retains its MIME type");
                assertSource(session, copy, uploadedText);
                AssertTrue(store.DocumentArtifacts.ExportBytes(session, ChatResourceUri.CreateArtifactRevision(session, copy))
                    .SequenceEqual(bytes), "source viewing preserves UTF-16 file bytes");
                AssertEqual(ArtifactLibraryResourceClasses.ImmutableOriginal,
                    ArtifactLibraryProjectionService.Project(session).Heads.Single(item => item.ArtifactId == upload.Id).ResourceClass,
                    "uploaded originals retain their separate viewer and import policy");
                var rawHash = copy.ContentSha256;
                store.Save(session);
                restarted = new ChatStore(FixturePaths.Value).Load(session.Id);
                var retained = restarted.Artifacts.Single(item => item.Id == copy.Id);
                AssertTrue(retained.InlineText == null, "opening a chat leaves authored file bodies lazy");
                AssertTrue(store.LoadArtifactBody(restarted, copy.Id), "explicit authored-file read uses its owner");
                AssertEqual(uploadedText, retained.InlineText, "UTF-16 text survives restart");
                store.Save(restarted);
                restarted = new ChatStore(FixturePaths.Value).Load(session.Id);
                retained = restarted.Artifacts.Single(item => item.Id == copy.Id);
                AssertEqual(rawHash, retained.ContentSha256, "saving decoded text cannot replace original bytes");
                AssertTrue(store.DocumentArtifacts.ExportBytes(restarted, ChatResourceUri.CreateArtifactRevision(restarted, retained))
                    .SequenceEqual(bytes), "UTF-16 BOM and bytes survive hydration and another save");
                assertSource(restarted, retained, uploadedText);
                var rawPath = System.IO.Path.Combine(FixturePaths.Value.ChatBlobDirectory,
                    rawHash.Substring(0, 2), rawHash + ".blob");
                System.IO.File.Move(rawPath, rawPath + ".held");
                try
                {
                    restarted = new ChatStore(FixturePaths.Value).Load(session.Id);
                    AssertTrue(restarted != null, "a missing authored body does not hide the conversation");
                    AssertTrue(!store.LoadArtifactBody(restarted, copy.Id), "missing exact body cannot silently fall back");
                }
                finally { System.IO.File.Move(rawPath + ".held", rawPath); }
            });
        }

        private static void AuthoredFileLargeCopyPatch()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                session.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                var store = new ChatStore(FixturePaths.Value);
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(FixturePaths.Value), store.DocumentArtifacts);
                var source = "const a = 1;\n" + new string('x', 10 * 1024 * 1024) + "\nconst unique = slow(slow(value));\n";
                var originalBytes = Encoding.UTF8.GetBytes(source);
                var draft = ingestion.Stage(session, "large.js", "text/plain", originalBytes);
                var message = new ChatMessage { Role = "user", Attachments = ingestion.LoadDrafts(session, new[] { draft.Id }).ToList() };
                session.Messages.Add(message);
                ingestion.CommitAndLink(session, message, 0);
                var original = session.Artifacts.Single(item => item.Title == "large.js");
                var originalRef = ChatResourceUri.CreateArtifactRevision(session, original);
                var sourceTarget = executor.ResourceGateway.Find(session, "large.js", "document").Items.Single().Target;
                var tools = executor.GetControllerTools().ToList();
                var copied = executor.ExecuteManual(Command(AuthoredFileToolCatalog.CopyToolId, "source", sourceTarget),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(copied.Success, "large file copy: " + copied.Message);
                var copyTarget = (string)JObject.Parse(copied.DataJson)["target"];
                var copy = session.Artifacts.Single(item => item.Kind == ChatArtifactKinds.File && item.Title == "large.js");
                var copiedRef = ChatResourceUri.CreateArtifactRevision(session, copy);
                AssertEqual(originalBytes.Length, (int)copy.ContentByteLength.Value, "copy retains exact byte length");
                AssertTrue(store.DocumentArtifacts.ExportBytes(session, copiedRef).SequenceEqual(originalBytes), "copy preserves exact original bytes");
                var patches = new[] { new { find = "slow(slow(value))", replace = "fast(value)" } };
                var patchCommand = Command(AuthoredFileToolCatalog.PatchToolId, "target", copyTarget, "patches", patches);
                var patched = executor.ExecuteManual(patchCommand, tools, new AppSettings(), false, false, session);
                AssertTrue(patched.Success, "large file patch: " + patched.Message);
                AssertTrue(!patched.DataJson.Contains("rna://") && !patched.DataJson.Contains("\"offset\"") &&
                    !patched.DataJson.Contains("\"version\""),
                    "model result keeps exact references and coordinates private");
                var latest = session.Artifacts.Where(item => item.Kind == ChatArtifactKinds.File && item.Title == "large.js")
                    .OrderByDescending(item => item.Revision).First();
                var latestRef = ChatResourceUri.CreateArtifactRevision(session, latest);
                var expected = source.Replace("slow(slow(value))", "fast(value)");
                AssertTrue(store.DocumentArtifacts.ExportBytes(session, latestRef).SequenceEqual(Encoding.UTF8.GetBytes(expected)),
                    "read-back contains only the intended edit");
                var catalog = new ArtifactCatalogService(new DocumentAuthorityRegistry(FixturePaths.Value),
                    store.DocumentArtifacts, new ArtifactWorkingSetService(store.DocumentArtifacts,
                        new ResourceMutationJournal(FixturePaths.Value)), new ResourceAuthorityStore(FixturePaths.Value),
                    executor.Payloads);
                var downloaded = catalog.Export(session, new ArtifactTransferRequest { ChatId = session.Id,
                    DocumentId = session.DocumentAuthorityId, ResourceUri = latestRef.Uri });
                AssertEqual("large.js", downloaded.FileName, "Library export preserves the requested code extension");
                AssertTrue(downloaded.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(expected)),
                    "Library export returns the exact verified edited bytes");
                AssertTrue(store.DocumentArtifacts.ExportBytes(session, originalRef).SequenceEqual(originalBytes),
                    "uploaded original remains byte-for-byte unchanged");
                var renamed = executor.ExecuteManual(Command(AuthoredFileToolCatalog.CopyToolId,
                    "source", sourceTarget, "name", "code-export.md"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(renamed.Success, "10 MiB code original can be saved under a requested .md extension");
                var renamedArtifact = session.Artifacts.Single(item => item.Kind == ChatArtifactKinds.File && item.Title == "code-export.md");
                var renamedDownload = catalog.Export(session, new ArtifactTransferRequest { ChatId = session.Id,
                    DocumentId = session.DocumentAuthorityId,
                    ResourceUri = ChatResourceUri.CreateArtifactRevisionUri(session, renamedArtifact) });
                AssertEqual("code-export.md", renamedDownload.FileName, "renamed large file downloads as Markdown");
                AssertTrue(renamedDownload.Bytes.SequenceEqual(originalBytes),
                    "changing the output extension does not alter 10 MiB of source code");
                var diff = new RunChangesService(a => a.InlineText, null, null).Read(session, latest.RunId);
                AssertTrue(diff.Items.Any(item => item.Availability == "hunks" &&
                    item.Hunks.Any(h => h.Before.Contains("slow(slow(value))") && h.After.Contains("fast(value)"))),
                    "run changes presents verified bounded file hunks");
                AssertTrue(!executor.ExecuteManual(patchCommand, tools, new AppSettings(), false, false, session).Success,
                    "stale file target cannot overwrite a newer version");
                var part = store.DocumentArtifacts.ReadIndexedTextPart(session, latestRef, 1);
                AssertTrue(part.PartCount > 300 && part.Text.Length <= 32000, "large authored file is navigable by bounded parts");
                var viewer = new ArtifactViewerService(executor.ResourceGateway);
                var dataPlane = new ResourceDataPlaneService(executor.ResourceGateway);
                var viewerPage = viewer.ReadPage(session, latestRef.Uri, null, dataPlane);
                for (var pageNumber = 0; pageNumber < 17; pageNumber++)
                    viewerPage = viewer.ReadPage(session, latestRef.Uri, viewerPage.NextCursor, dataPlane);
                AssertTrue(viewerPage.Offset > ArtifactViewerService.MaximumDocumentCharacters &&
                    viewerPage.NextCursor != null && !viewerPage.FullReadAllowed,
                    "large file viewer pages past the small-document ceiling without enabling full copy");
                var found = store.DocumentArtifacts.SearchTextOccurrences(session, latestRef, "fast(value)", 10, false);
                AssertTrue(!found.ScanTruncated && found.Matches.Count == 1, "exact large-file search reaches the changed tail");
                var nextTarget = (string)JObject.Parse(patched.DataJson)["target"];
                var modelSearch = executor.ResourceGateway.SearchWithin(session, nextTarget, "fast(value)", 10,
                    false, System.Threading.CancellationToken.None);
                AssertTrue(modelSearch.ScanComplete && modelSearch.Matches.Single().PartNumber > 300,
                    "semantic search locates the late changed part");
                AssertTrue(executor.ResourceGateway.Outline(session, nextTarget, 1, 10).PartCount > 300,
                    "semantic outline navigates the complete authored file");
                var ambiguous = executor.ExecuteManual(Command(AuthoredFileToolCatalog.PatchToolId,
                    "target", nextTarget,
                    "patches", new[] { new { find = "xx", replace = "x" } }),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(!ambiguous.Success, "ambiguous patch is refused without another revision");
                store.Save(session);
                var restarted = store.Load(session.Id);
                AssertTrue(store.DocumentArtifacts.ExportBytes(restarted, latestRef).SequenceEqual(Encoding.UTF8.GetBytes(expected)),
                    "verified large-file version survives chat restart");

                var markdown = "# Large\n" + new string('m', 300000);
                var mdDraft = ingestion.Stage(session, "large.md", "text/markdown", Encoding.UTF8.GetBytes(markdown));
                var mdMessage = new ChatMessage { Role = "user", Attachments = ingestion.LoadDrafts(session, new[] { mdDraft.Id }).ToList() };
                session.Messages.Add(mdMessage); ingestion.CommitAndLink(session, mdMessage, session.Messages.Count - 1);
                var mdTarget = executor.ResourceGateway.Find(session, "large.md", "document").Items.Single().Target;
                var mdCopy = executor.ExecuteManual(Command(AuthoredFileToolCatalog.CopyToolId, "source", mdTarget),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(mdCopy.Success, "Markdown beyond the inline save limit copies as a real .md file");
                var mdArtifact = session.Artifacts.Single(item => item.Kind == ChatArtifactKinds.File && item.Title == "large.md");
                AssertTrue(store.DocumentArtifacts.ExportBytes(session, ChatResourceUri.CreateArtifactRevision(session, mdArtifact))
                    .SequenceEqual(Encoding.UTF8.GetBytes(markdown)), "large .md export preserves all bytes");
                var mdDownload = catalog.Export(session, new ArtifactTransferRequest { ChatId = session.Id,
                    DocumentId = session.DocumentAuthorityId,
                    ResourceUri = ChatResourceUri.CreateArtifactRevisionUri(session, mdArtifact) });
                AssertEqual("large.md", mdDownload.FileName, "Library downloads large Markdown with its .md extension");
                AssertTrue(mdDownload.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(markdown)),
                    "Library downloads complete Markdown beyond the inline save limit");

                var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("first\r\nslow(value)\r\n")).ToArray();
                var codeDraft = ingestion.Stage(session, "unicode.cs", "text/plain", utf16);
                var codeMessage = new ChatMessage { Role = "user", Attachments = ingestion.LoadDrafts(session, new[] { codeDraft.Id }).ToList() };
                session.Messages.Add(codeMessage); ingestion.CommitAndLink(session, codeMessage, session.Messages.Count - 1);
                var codeTarget = executor.ResourceGateway.Find(session, "unicode.cs", "document").Items.Single().Target;
                var codeCopy = executor.ExecuteManual(Command(AuthoredFileToolCatalog.CopyToolId, "source", codeTarget),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(codeCopy.Success, "UTF-16 source copies without conversion");
                var codePatch = executor.ExecuteManual(Command(AuthoredFileToolCatalog.PatchToolId,
                    "target", (string)JObject.Parse(codeCopy.DataJson)["target"],
                    "patches", new[] { new { find = "slow(value)", replace = "fast(value)" } }),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(codePatch.Success, "UTF-16 source patches losslessly");
                var codeLatest = session.Artifacts.Where(item => item.Kind == ChatArtifactKinds.File && item.Title == "unicode.cs")
                    .OrderByDescending(item => item.Revision).First();
                var expectedUtf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("first\r\nfast(value)\r\n")).ToArray();
                AssertTrue(store.DocumentArtifacts.ExportBytes(session, ChatResourceUri.CreateArtifactRevision(session, codeLatest))
                    .SequenceEqual(expectedUtf16), "UTF-16 BOM and CRLF survive exact patch and export");
                var codeRef = ChatResourceUri.CreateArtifactRevision(session, codeLatest);
                var priorText = executor.ResourceAuthority.Revisions.GetView(executor.ResourceAuthority.Scope(session, true), codeRef, "text");
                AssertTrue(priorText == null, "new file revision has no preexisting text read view");
                AssertEqual("first\r\nfast(value)\r\n", executor.ResourceGateway.Read(session,
                    new ResourceReadRequest { Reference = codeRef, Representation = "text", MaxChars = 32000 }).Result.Text,
                    "UTF-16 authored file is readable as text through the gateway");
                AssertEqual("first\r\nfast(value)\r\n", store.DocumentArtifacts.ReadIndexedTextPart(session, codeRef, 1).Text,
                    "UTF-16 authored file is indexed without losing source bytes");
                var created = executor.ExecuteManual(Command(AuthoredFileToolCatalog.CreateToolId,
                    "name", "new.py", "content", "print('ready')\n"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(created.Success, "small code file can be authored in its requested format");
                var newFile = session.Artifacts.Single(item => item.Kind == ChatArtifactKinds.File && item.Title == "new.py");
                AssertEqual("print('ready')\n", Encoding.UTF8.GetString(store.DocumentArtifacts.ExportBytes(session,
                    ChatResourceUri.CreateArtifactRevision(session, newFile))), "created source is exactly exportable");
                var unicodeCreated = executor.ExecuteManual(Command(AuthoredFileToolCatalog.CreateToolId,
                    "name", "emoji.txt", "content", "A😀B"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(unicodeCreated.Success, "Unicode text file creation succeeds");
                var emojiFile = session.Artifacts.Single(item => item.Kind == ChatArtifactKinds.File && item.Title == "emoji.txt");
                var emojiRef = ChatResourceUri.CreateArtifactRevision(session, emojiFile);
                var emojiFirst = executor.ResourceGateway.Read(session,
                    new ResourceReadRequest { Reference = emojiRef, Representation = "text", MaxChars = 2 });
                AssertEqual("A", emojiFirst.Result.Text, "bounded page does not split a surrogate pair");
                var emojiSecond = executor.ResourceGateway.Read(session,
                    new ResourceReadRequest { Reference = emojiRef, Representation = "text", MaxChars = 2,
                        Cursor = emojiFirst.Result.NextCursor });
                AssertEqual("😀", emojiSecond.Result.Text, "continuation resumes at the full character");
            });
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void HtmlProjectDefinitionAndCaptureSet()
        {
            WithPluginPaths(paths => {
                var store = new WorkspaceStore(paths); var files = new WorkspaceFileService(paths);
                var workspace = store.CreateHiddenHtmlProject(Path.Combine(paths.Root, "html-members"), "Members", "index.html");
                files.CreateText(workspace, "index.html", "<h1>First</h1>");
                files.CreateText(workspace, "style.css", "h1 { color: green; }");
                File.WriteAllBytes(Path.Combine(workspace.RootPath, "asset.bin"), new byte[] { 0, 255, 1 });
                AssertTrue(store.GetHtmlProjectDefinition(workspace).Definition == null, "legacy entry does not silently discover a member-set");
                var requested = new HtmlProjectDefinition { EntryPath = "index.html", Members = new[] { "style.css", "index.html", "asset.bin" } };
                var defined = store.SetHtmlProjectDefinition(workspace, null, requested);
                AssertTrue(defined.Definition.Members.SequenceEqual(new[] { "asset.bin", "index.html", "style.css" }), "canonical member order");
                var reopened = store.Open(workspace.RootPath);
                AssertEqual(defined.Fingerprint, store.GetHtmlProjectDefinition(reopened).Fingerprint, "definition survives owner recreation");
                AssertEqual(defined.Fingerprint, store.SetHtmlProjectDefinition(workspace, defined.Fingerprint, requested).Fingerprint,
                    "equivalent definition is a no-op");
                var stale = false;
                try { store.SetHtmlProjectDefinition(workspace, null, requested); }
                catch (WorkspaceFileException error) { stale = error.Code == "project_definition_changed"; }
                AssertTrue(stale, "stale config write refuses before publication");
                var invalid = false;
                try { store.SetHtmlProjectDefinition(workspace, defined.Fingerprint, new HtmlProjectDefinition {
                    EntryPath = "index.html", Members = new[] { "index.html", ".rnassistant/workspace.json" } }); }
                catch (WorkspaceFileException) { invalid = true; }
                AssertTrue(invalid, "control metadata cannot become content");
                using (var pin = store.PinHtmlProjectDefinition(workspace))
                using (var capture = files.CaptureSet(workspace, pin.State.Definition.Members))
                {
                    AssertEqual(3, capture.Files.Count, "capture retains only the declared members including binary");
                    AssertTrue(new ChatBlobStore(paths).ReadBytes(capture.Files[0].Evidence.Payload.ToBlobReference())
                        .SequenceEqual(new byte[] { 0, 255, 1 }), "binary bytes retained through existing CAS");
                    using (var cancelled = new CancellationTokenSource(100))
                    {
                        var refused = Task.Run(() => { try { store.SetHtmlProjectDefinition(workspace, defined.Fingerprint,
                            requested, cancelled.Token); return false; } catch (OperationCanceledException) { return true; } });
                        AssertTrue(refused.GetAwaiter().GetResult(), "definition lease spans content capture and prevents config mutation");
                    }
                    pin.Validate(); capture.Validate();
                }
                using (var capture = files.CaptureSet(workspace, defined.Definition.Members))
                {
                    var blocked = false;
                    try { File.WriteAllText(Path.Combine(workspace.RootPath, "index.html"), "<h1>Other</h1>"); }
                    catch (IOException) { blocked = true; }
                    if (blocked) capture.Validate();
                    else {
                        var drift = false;
                        try { capture.Validate(); } catch (WorkspaceFileException error) { drift = error.Code == "project_source_changed"; }
                        AssertTrue(drift, "platforms without write-sharing denial still reject detected in-place drift");
                    }
                }
                files.FaultPoint = stage => { if (stage == "after-capture-set-member") throw new IOException("capture interrupted"); };
                try { using (files.CaptureSet(workspace, defined.Definition.Members)) { } throw new Exception("fault not reached"); }
                catch (IOException) { }
                files.FaultPoint = null;
                using (var capture = files.CaptureSet(workspace, defined.Definition.Members)) capture.Validate();
                var budget = false;
                try { using (files.CaptureSet(workspace, defined.Definition.Members, 1)) { } }
                catch (WorkspaceFileException error) { budget = error.Code == "project_too_large"; }
                AssertTrue(budget, "aggregate byte budget refuses before file snapshot publication");
            });
        }
    }
}

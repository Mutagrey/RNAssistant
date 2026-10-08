using System;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceBinaryViewsStayExact()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                var provider = new WorkspaceFileResourceProvider(files, workspace);
                Action<string, Action> rejected = (code, action) => {
                    var denied = false;
                    try { action(); } catch (WorkspaceFileException ex) { denied = ex.Code == code; }
                    AssertTrue(denied, "expected fail-closed " + code);
                };
                var text = files.CreateText(workspace, "index.html", "\uFEFF<img src='picture.png'>\r\n");
                var textBytes = provider.ReadBytes("index.html");
                textBytes.RequireCompleteExactBytes();
                AssertEqual(text.Reference.Revision, textBytes.Result.Resource.Reference.Revision,
                    "materializing raw view cannot advance the text head");
                AssertEqual(text.Text, provider.ReadExact("index.html", text.Reference).Result.Text, "BOM/EOL preserved");
                File.WriteAllBytes(Path.Combine(workspace.RootPath, "raw.bin"), new byte[] { 0xff, 0, 1 });
                var raw = provider.ReadBytes("raw.bin");
                raw.RequireCompleteExactBytes();
                rejected("encoding_ambiguous", () => provider.Read("raw.bin"));
                provider.ReadBytes("raw.bin"); // Refresh raw authority after the rejected text representation.
                var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6p9sAAAAASUVORK5CYII=");
                var path = Path.Combine(workspace.RootPath, "picture.png");
                File.WriteAllBytes(path, png);
                var image = provider.ReadBytes("picture.png");
                image.RequireCompleteExactBytes();
                AssertEqual("image/png", image.Result.Resource.MimeType, "MIME comes from exact image header");
                AssertEqual(1, image.Result.Binary.Width, "bounded image width");
                AssertTrue(image.Result.Resource.Representations.Contains(ResourceRepresentations.Image), "image capability after capture");
                var store = new WorkspaceWebSnapshotStore(paths, files);
                var snapshot = store.PublishSnapshot(workspace, "index.html", new[] { textBytes, image }.Select(read =>
                    new WebSnapshotFile(read.Result.Resource.Metadata["relativePath"], read.Result.Resource.Reference,
                        read.Result.CompleteViewPayload, read.Evidence.Single().View)));
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                provider.ReadBytes("picture.png");
                var reducer = new EvidenceStateReducer();
                AssertEqual(EvidenceState.Superseded, reducer.Reduce(image.Evidence.Single(), provider.CaptureAuthority(image.Evidence)).State,
                    "binary drift invalidates browser source evidence");
                File.Delete(path);
                provider = new WorkspaceFileResourceProvider(new WorkspaceFileService(paths), workspace);
                var retained = provider.ReadSnapshotBytes(store.ReadSnapshot(workspace, snapshot.Id), "picture.png");
                retained.RequireCompleteExactBytes();
                AssertTrue(png.SequenceEqual(retained.Result.Bytes), "restart and deletion retain exact image bytes");
                AssertEqual(text.Text, provider.ReadSnapshot(snapshot, "index.html").Result.Text,
                    "raw snapshot retains exact text view");
                var foreign = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "foreign"));
                rejected("invalid_resource_ref", () => new WorkspaceFileResourceProvider(files, foreign).ReadSnapshotBytes(snapshot, "picture.png"));
                rejected("invalid_resource_ref", () => provider.ReadExactBytes("raw.bin", image.Result.Resource.Reference));
                var authority = new ResourceAuthorityStore(paths);
                var unpublished = new ResourceRef(image.Result.Resource.Reference.Uri, "r_unpublished");
                var evidence = image.Evidence.Single();
                authority.RegisterRevision(evidence.ScopeId, new ResourceRevisionMetadata(unpublished, evidence.ContentSha256, evidence.Payload));
                authority.RegisterView(evidence.ScopeId, new ResourceRevisionView(unpublished, "raw", evidence.ContentSha256,
                    evidence.Payload, ResourceCoverage.Whole()));
                rejected("snapshot_unavailable", () => provider.ReadExactBytes("picture.png", unpublished));
                var collected = CasService(paths, new ChatStore(paths), new VbaJournalStore(paths), () => StorageProtector.None).Collect();
                AssertTrue(collected.Completed && png.SequenceEqual(provider.ReadSnapshotBytes(snapshot, "picture.png").Result.Bytes),
                    "retained binary manifest roots survive GC");
                var blob = new ChatBlobStore(paths).PathFor(evidence.Payload.Sha256);
                File.WriteAllBytes(blob, new byte[] { 0 });
                rejected("snapshot_unavailable", () => provider.ReadSnapshotBytes(snapshot, "picture.png"));
                File.Delete(blob);
                File.WriteAllBytes(path, png);
                rejected("snapshot_unavailable", () => provider.ReadExactBytes("picture.png", image.Result.Resource.Reference));
                File.WriteAllBytes(Path.Combine(workspace.RootPath, "large.bin"), new byte[1024 * 1024 + 1]);
                rejected("file_too_large", () => provider.ReadBytes("large.bin"));
                File.WriteAllBytes(Path.Combine(workspace.RootPath, "empty.bin"), new byte[0]);
                provider.ReadBytes("empty.bin").RequireCompleteExactBytes();
                files.FaultPoint = stage => { if (stage == "after-dispatch") throw new IOException("interrupted"); };
                rejected("effect_unknown", () => files.CreateText(workspace, "uncertain.txt", "dispatched"));
                rejected("unresolved_previous_effect", () => provider.ReadBytes("uncertain.txt"));
            });
        }
    }
}

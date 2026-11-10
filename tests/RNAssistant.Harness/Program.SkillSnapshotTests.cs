using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void SkillSnapshotCopiesPreserveFrozenBindings()
        {
            var source = new SkillDefinition { Id = "snapshot-skill", Host = "Common", Name = "Snapshot", Description = "Frozen",
                Version = "2.3.4", BodyMarkdown = new string('x', 12000) + " Ω😀", Enabled = true, BuiltIn = true,
                StoragePath = "skills/source", Publication = new ResourceRef("rna://skills/snapshot", "r1"),
                Package = new SkillPackageManifest { FormatVersion = 1, PackageId = "snapshot-package" } };
            source.Package.RequiredTools.Add(new SkillToolDependency { Id = "core.read", ContractVersion = "2", Revision = new string('a', 64) });
            source.Package.OptionalTools.Add(new SkillToolDependency { Id = "core.search", ContractVersion = "3", Fallback = "Use exact reads." });
            source.References.Add(new SkillReferenceMetadata { Path = "guide.md", ByteLength = 12,
                Revision = "r2", Payload = new PayloadRef(new string('b', 64), 12, "text/markdown", "aes", "key") });
            var expected = JsonConvert.SerializeObject(source);
            var expectedRevision = SkillRevision.Compute(source);
            var snapshot = new SkillCatalogSnapshot(new[] { source }, "publication-7");
            var generation = snapshot.Generation;
            var expectedMetadata = JsonConvert.SerializeObject(snapshot.Metadata);
            source.BodyMarkdown = "Changed after publication";
            source.Package.RequiredTools[0].Id = "wrong-tool";
            source.Package.OptionalTools.Clear();
            source.References[0].Path = "wrong.md";
            var first = snapshot.Skills.Single();
            AssertEqual(expected, JsonConvert.SerializeObject(first), "all definition fields freeze at construction");
            first.BodyMarkdown = "Changed request";
            first.Package.RequiredTools[0].Revision = "wrong-revision";
            first.Package.OptionalTools[0].Fallback = "wrong-fallback";
            first.References[0].Payload = new PayloadRef(new string('c', 64), 0);
            first.References.Add(new SkillReferenceMetadata { Path = "new.md" });
            var metadata = snapshot.Metadata.Single();
            metadata.Name = "Changed presentation"; metadata.Tools.Clear(); metadata.References.Add("leak.md");
            AssertEqual(expected, JsonConvert.SerializeObject(snapshot.Skills.Single()), "request mutation cannot change later exact definitions");
            AssertEqual(expectedMetadata, JsonConvert.SerializeObject(snapshot.Metadata), "metadata lists and records remain detached");
            AssertEqual(expectedRevision, snapshot.Revision("snapshot-skill"), "revision stays bound to frozen definition");
            AssertEqual(generation, snapshot.Generation, "publication generation is stable");
            AssertEqual(null, snapshot.Revision("missing"), "unknown skill has no authority");
            var disabled = new SkillDefinition { Id = "disabled", Enabled = false, References = null,
                Package = new SkillPackageManifest { RequiredTools = null, OptionalTools = null } };
            var disabledSnapshot = new SkillCatalogSnapshot(new[] { disabled });
            AssertEqual(JsonConvert.SerializeObject(disabled), JsonConvert.SerializeObject(disabledSnapshot.Skills.Single()), "null nested lists retain their values");
            AssertEqual(0, disabledSnapshot.Metadata.Count, "disabled skills stay out of available metadata");
            AssertEqual(0, new SkillCatalogSnapshot(null).Skills.Count, "empty catalog stays empty");

            var catalog = Enumerable.Range(0, 16).Select(index => new SkillDefinition {
                Id = "perf-" + index.ToString("D2", CultureInfo.InvariantCulture), BodyMarkdown = new string('q', 12000) }).ToArray();
            var measured = new SkillCatalogSnapshot(catalog);
            var warm = measured.Skills.Count + measured.Metadata.Count;
            var before = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            var characters = 0;
            for (var iteration = 0; iteration < 24; iteration++)
            {
                characters += measured.Skills.Sum(item => item.BodyMarkdown.Length);
                AssertEqual(16, measured.Metadata.Count, "metadata cardinality stays fixed");
            }
            clock.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            AssertEqual(4608000, characters, "same immutable instruction content is read on every access");
            Console.WriteLine("skill-snapshot skills=16 reads=24 characters=" + characters + " allocated=" + allocated +
                " wallMs=" + clock.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture));
        }
    }
}

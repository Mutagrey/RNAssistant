using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    // Immutable current-package snapshot used at authoring/runtime boundaries.
    // Immutable history remains a separate Skill Library contour.
    public sealed class SkillPackageSource
    {
        private readonly SkillPackageReferenceSource[] _references;

        public int ContractVersion
        {
            get { return SkillRevision.CurrentContractVersion; }
        }
        public string Revision { get; private set; }
        public string Id { get; private set; }
        public string Host { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public string Version { get; private set; }
        public string BodyMarkdown { get; private set; }
        public bool Enabled { get; private set; }
        public IReadOnlyList<SkillPackageReferenceSource> References
        {
            get { return Array.AsReadOnly(_references); }
        }

        public SkillPackageSource(string id, string host, string name,
            string description, string version, string bodyMarkdown,
            bool enabled,
            IEnumerable<SkillPackageReferenceSource> references)
        {
            Id = id ?? string.Empty;
            Host = host ?? string.Empty;
            Name = name ?? string.Empty;
            Description = description ?? string.Empty;
            Version = version ?? string.Empty;
            BodyMarkdown = bodyMarkdown ?? string.Empty;
            Enabled = enabled;
            _references = (references ??
                    new SkillPackageReferenceSource[0])
                .Where(item => item != null)
                .Select(item => new SkillPackageReferenceSource(
                    item.Path, item.ByteLength, item.Revision))
                .ToArray();
            Revision = SkillRevision.Compute(ToDefinition());
        }

        public static SkillPackageSource Capture(SkillDefinition skill)
        {
            if (skill == null) return null;
            return new SkillPackageSource(
                skill.Id, skill.Host, skill.Name, skill.Description,
                skill.Version, skill.BodyMarkdown, skill.Enabled,
                (skill.References ?? new List<SkillReferenceMetadata>())
                    .Where(item => item != null)
                    .Select(item => new SkillPackageReferenceSource(
                        item.Path, item.ByteLength, item.Revision)));
        }

        public SkillDefinition ToDefinition()
        {
            return new SkillDefinition
            {
                Id = Id,
                Host = Host,
                Name = Name,
                Description = Description,
                Version = Version,
                BodyMarkdown = BodyMarkdown,
                Enabled = Enabled,
                BuiltIn = false,
                References = _references.Select(item =>
                    new SkillReferenceMetadata
                    {
                        Path = item.Path,
                        ByteLength = item.ByteLength,
                        Revision = item.Revision
                    }).ToList()
            };
        }
    }

    public sealed class SkillPackageReferenceSource
    {
        public string Path { get; private set; }
        public long ByteLength { get; private set; }
        public string Revision { get; private set; }

        public SkillPackageReferenceSource(
            string path, long byteLength, string revision)
        {
            Path = path ?? string.Empty;
            ByteLength = byteLength;
            Revision = revision ?? string.Empty;
        }
    }
}

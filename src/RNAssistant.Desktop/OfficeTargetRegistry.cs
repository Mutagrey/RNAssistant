using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Desktop
{
    internal enum TargetSelectionMode
    {
        Manual = 0,
        AutoFollow = 1
    }

    internal sealed class OfficeTargetRegistry
    {
        private readonly List<OfficeTargetEntry> _targets;

        public OfficeTargetRegistry()
        {
            _targets = new List<OfficeTargetEntry>();
            Mode = TargetSelectionMode.AutoFollow;
        }

        public TargetSelectionMode Mode { get; set; }

        public string SelectedTargetId { get; private set; }

        public IReadOnlyList<OfficeTargetEntry> Targets
        {
            get { return _targets.ToArray(); }
        }

        public OfficeTargetEntry SelectedTarget
        {
            get { return FindById(SelectedTargetId); }
        }

        public OfficeTargetEntry Upsert(OfficeTargetDescriptor target)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.Host))
            {
                return null;
            }

            var normalized = Clone(target);
            var id = BuildId(normalized);
            var existing = FindById(id);
            if (existing == null)
            {
                existing = new OfficeTargetEntry(id, normalized);
                _targets.Add(existing);
            }
            else
            {
                existing.Target = normalized;
                existing.LastSeenUtc = DateTime.UtcNow;
            }

            return existing;
        }

        public void UpsertMany(IEnumerable<OfficeTargetDescriptor> targets)
        {
            foreach (var target in targets ?? new OfficeTargetDescriptor[0])
            {
                Upsert(target);
            }
        }

        public OfficeTargetEntry Select(string id)
        {
            var entry = FindById(id);
            if (entry != null)
            {
                SelectedTargetId = entry.Id;
            }
            return entry;
        }

        public OfficeTargetEntry Select(OfficeTargetDescriptor target)
        {
            var entry = Upsert(target);
            if (entry != null)
            {
                SelectedTargetId = entry.Id;
            }
            return entry;
        }

        public OfficeTargetEntry FindById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return _targets.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyList<OfficeTargetEntry> ForHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host) || string.Equals(host, "All", StringComparison.OrdinalIgnoreCase))
            {
                return Targets;
            }

            return _targets
                .Where(s => s.Target != null && string.Equals(s.Target.Host, host, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        private static string BuildId(OfficeTargetDescriptor target)
        {
            return Normalize(target.Host) + "|" +
                target.Hwnd + "|" +
                target.ProcessId + "|" +
                Normalize(Identity(target));
        }

        private static string Identity(OfficeTargetDescriptor target)
        {
            if (!string.IsNullOrWhiteSpace(target.FullName)) return target.FullName;
            if (!string.IsNullOrWhiteSpace(target.Path)) return target.Path;
            if (!string.IsNullOrWhiteSpace(target.DocumentKey)) return target.DocumentKey;
            if (!string.IsNullOrWhiteSpace(target.EntryId)) return target.EntryId;
            if (!string.IsNullOrWhiteSpace(target.FolderPath)) return target.FolderPath;
            if (!string.IsNullOrWhiteSpace(target.Name)) return target.Name;
            return "window";
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static OfficeTargetDescriptor Clone(OfficeTargetDescriptor source)
        {
            return new OfficeTargetDescriptor
            {
                Host = source.Host,
                FullName = source.FullName,
                Path = source.Path,
                Name = source.Name,
                DocumentKey = source.DocumentKey,
                EntryId = source.EntryId,
                FolderPath = source.FolderPath,
                Selection = source.Selection,
                Action = source.Action,
                Hwnd = source.Hwnd,
                ProcessId = source.ProcessId
            };
        }
    }

    internal sealed class OfficeTargetEntry
    {
        public OfficeTargetEntry(string id, OfficeTargetDescriptor target)
        {
            Id = id;
            Target = target;
            LastSeenUtc = DateTime.UtcNow;
        }

        public string Id { get; private set; }
        public OfficeTargetDescriptor Target { get; set; }
        public DateTime LastSeenUtc { get; set; }

        public string DisplayName
        {
            get
            {
                if (Target == null)
                {
                    return "Unknown target";
                }

                var title = Target.Name;
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = Target.FullName;
                }
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = Target.FolderPath;
                }
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = Target.EntryId;
                }
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = "window " + Target.Hwnd;
                }

                return Target.Host + " - " + title + SelectionSuffix(Target.Selection);
            }
        }

        private static string SelectionSuffix(string selection)
        {
            return string.IsNullOrWhiteSpace(selection) ? string.Empty : " - " + selection;
        }
    }
}

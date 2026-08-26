using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    public sealed partial class VbaJournalStore
    {
        public VbaMutationQueryPage QueryMutations(string host, string documentKey, VbaMutationQueryRequest request)
        {
            request = request ?? new VbaMutationQueryRequest();
            NormalizeMutationQuery(request);
            var source = ReadEvents(host, documentKey).Where(item => item != null).OrderBy(item => item.Sequence).ToList();
            var cursor = ParseMutationCursor(request.Cursor);
            var snapshotSequence = cursor == null
                ? (source.Count == 0 ? 0 : source[source.Count - 1].Sequence)
                : cursor.SnapshotSequence;
            var snapshot = source.Where(item => item.Sequence <= snapshotSequence).ToList();
            var rows = BuildMutationQueryRows(snapshot);
            var filtered = rows.Where(item => MatchesMutationQuery(item, request))
                .OrderByDescending(item => item.LastSequence)
                .ThenBy(item => item.MutationId, StringComparer.Ordinal)
                .ToList();
            var offset = cursor == null ? 0 : Math.Min(cursor.Offset, filtered.Count);
            var pageSize = request.PageSize <= 0 ? 100 : Math.Min(MaxMutationPageSize, request.PageSize);
            var page = filtered.Skip(offset).Take(pageSize).ToList();
            var nextOffset = offset + page.Count;
            var hasMore = nextOffset < filtered.Count;
            return new VbaMutationQueryPage
            {
                TotalEvents = snapshot.Count,
                TotalRows = rows.Count,
                TotalMatches = filtered.Count,
                Cursor = request.Cursor,
                NextCursor = hasMore
                    ? MutationCursorPrefix + snapshotSequence.ToString(CultureInfo.InvariantCulture) + ":" + nextOffset.ToString(CultureInfo.InvariantCulture)
                    : null,
                HasMore = hasMore,
                Rows = page
            };
        }

        public VbaMutationDetail GetMutationDetail(string host, string documentKey, string mutationId)
        {
            mutationId = (mutationId ?? string.Empty).Trim();
            if (mutationId.Length == 0) throw new ArgumentException("mutationId is required.", "mutationId");
            var events = ReadEvents(host, documentKey).Where(item => item != null).OrderBy(item => item.Sequence).ToList();
            var related = events.Where(item => string.Equals(item.MutationId, mutationId, StringComparison.OrdinalIgnoreCase)).ToList();
            var moduleRecords = ProjectMutations(events);
            var packageRecords = ProjectPackageMutations(events);
            var module = moduleRecords.FirstOrDefault(item => item.Prepared != null &&
                string.Equals(item.Prepared.MutationId, mutationId, StringComparison.OrdinalIgnoreCase));
            if (module != null) return BuildMutationDetail(module, related);
            var package = packageRecords.FirstOrDefault(item => item.Prepared != null &&
                string.Equals(item.Prepared.MutationId, mutationId, StringComparison.OrdinalIgnoreCase));
            if (package != null) return BuildPackageMutationDetail(package, related);
            throw new VbaJournalException("VBA mutation was not found: " + mutationId + ".");
        }

        private static void NormalizeMutationQuery(VbaMutationQueryRequest request)
        {
            request.Search = (request.Search ?? string.Empty).Trim();
            request.Kind = TrimOrNull(request.Kind);
            request.Status = TrimOrNull(request.Status);
            request.RunId = TrimOrNull(request.RunId);
            request.TurnId = TrimOrNull(request.TurnId);
            request.StepId = TrimOrNull(request.StepId);
            request.ToolCallId = TrimOrNull(request.ToolCallId);
            if (request.Search.Length > MaxMutationSearchChars)
            {
                throw new ArgumentException("VBA mutation search is limited to " + MaxMutationSearchChars + " characters.", "request");
            }
            if (!VbaMutationKinds.IsValid(request.Kind))
            {
                throw new ArgumentException("Unsupported VBA mutation kind: " + request.Kind + ".", "request");
            }
            ParseMutationCursor(request.Cursor);
        }

        private static MutationCursor ParseMutationCursor(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var parts = value.Split(':');
            long snapshot;
            int offset;
            if (parts.Length != 3 || !string.Equals(parts[0] + ":", MutationCursorPrefix, StringComparison.Ordinal) ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out snapshot) || snapshot < 0 ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)
            {
                throw new ArgumentException("Invalid VBA mutation cursor.", "value");
            }
            return new MutationCursor { SnapshotSequence = snapshot, Offset = offset };
        }

        private static List<VbaMutationQueryRow> BuildMutationQueryRows(IReadOnlyList<VbaJournalEvent> events)
        {
            var rows = new List<VbaMutationQueryRow>();
            var related = events.Where(item => !string.IsNullOrWhiteSpace(item.MutationId))
                .GroupBy(item => item.MutationId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Sequence).ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var record in ProjectMutations(events))
            {
                List<VbaJournalEvent> source;
                if (record.Prepared == null || !related.TryGetValue(record.Prepared.MutationId, out source)) continue;
                var preparedEvent = source.First(item => string.Equals(item.Type, VbaJournalEventTypes.MutationPrepared, StringComparison.Ordinal));
                var terminalEvent = source.FirstOrDefault(item => string.Equals(item.Type, VbaJournalEventTypes.MutationTerminal, StringComparison.Ordinal));
                rows.Add(new VbaMutationQueryRow
                {
                    MutationId = record.Prepared.MutationId,
                    Kind = VbaMutationKinds.Module,
                    Operation = record.Prepared.Operation,
                    Status = record.Terminal == null ? VbaMutationStatuses.Open : record.Terminal.Status,
                    CreatedUtc = preparedEvent.CreatedUtc,
                    CompletedUtc = terminalEvent == null ? (DateTime?)null : terminalEvent.CreatedUtc,
                    FirstSequence = preparedEvent.Sequence,
                    LastSequence = terminalEvent == null ? preparedEvent.Sequence : terminalEvent.Sequence,
                    SessionId = record.Prepared.SessionId,
                    RunId = record.Prepared.RunId,
                    TurnId = record.Prepared.TurnId,
                    StepId = record.Prepared.StepId,
                    ToolCallId = record.Prepared.ToolCallId,
                    ModuleName = record.Prepared.ModuleName,
                    ComponentType = record.Prepared.ComponentType,
                    BackupId = record.Prepared.BackupId,
                    ComponentCount = 1,
                    ComponentNames = new List<string> { record.Prepared.ModuleName },
                    ErrorCode = record.Terminal == null ? null : record.Terminal.ErrorCode,
                    Message = record.Terminal == null ? null : record.Terminal.Message,
                    SourceEventSeqs = SourceSequences(preparedEvent, terminalEvent),
                    SourceEventIds = SourceEventIds(preparedEvent, terminalEvent)
                });
            }
            foreach (var record in ProjectPackageMutations(events))
            {
                List<VbaJournalEvent> source;
                if (record.Prepared == null || !related.TryGetValue(record.Prepared.MutationId, out source)) continue;
                var preparedEvent = source.First(item => string.Equals(item.Type, VbaJournalEventTypes.PackageMutationPrepared, StringComparison.Ordinal));
                var terminalEvent = source.FirstOrDefault(item => string.Equals(item.Type, VbaJournalEventTypes.PackageMutationTerminal, StringComparison.Ordinal));
                var components = record.Prepared.Components ?? new List<VbaPackageMutationComponent>();
                var rename = string.Equals(record.Prepared.Operation, "rename", StringComparison.OrdinalIgnoreCase);
                var sourceComponent = rename
                    ? components.FirstOrDefault(item => item.BeforeExists && !item.IntendedAfterExists)
                    : null;
                rows.Add(new VbaMutationQueryRow
                {
                    MutationId = record.Prepared.MutationId,
                    Kind = rename ? VbaMutationKinds.Module : VbaMutationKinds.Package,
                    Operation = record.Prepared.Operation,
                    Status = record.Terminal == null ? VbaMutationStatuses.Open : record.Terminal.Status,
                    CreatedUtc = preparedEvent.CreatedUtc,
                    CompletedUtc = terminalEvent == null ? (DateTime?)null : terminalEvent.CreatedUtc,
                    FirstSequence = preparedEvent.Sequence,
                    LastSequence = terminalEvent == null ? preparedEvent.Sequence : terminalEvent.Sequence,
                    SessionId = record.Prepared.SessionId,
                    RunId = record.Prepared.RunId,
                    TurnId = record.Prepared.TurnId,
                    StepId = record.Prepared.StepId,
                    ToolCallId = record.Prepared.ToolCallId,
                    ModuleName = sourceComponent == null ? null : sourceComponent.ModuleName,
                    ComponentType = sourceComponent == null ? null : sourceComponent.BeforeComponentType,
                    PackageId = rename ? null : record.Prepared.PackageId,
                    PackageVersion = rename ? null : record.Prepared.PackageVersion,
                    ComponentCount = rename ? 1 : components.Count,
                    ComponentNames = components.Select(item => item.ModuleName).Where(item => !string.IsNullOrWhiteSpace(item)).ToList(),
                    ErrorCode = record.Terminal == null ? null : record.Terminal.ErrorCode,
                    Message = record.Terminal == null ? null : record.Terminal.Message,
                    SourceEventSeqs = SourceSequences(preparedEvent, terminalEvent),
                    SourceEventIds = SourceEventIds(preparedEvent, terminalEvent)
                });
            }
            return rows;
        }

        private static bool MatchesMutationQuery(VbaMutationQueryRow row, VbaMutationQueryRequest request)
        {
            if (!MatchesValue(request.Kind, row.Kind) || !MatchesValue(request.Status, row.Status) ||
                !MatchesValue(request.RunId, row.RunId) || !MatchesValue(request.TurnId, row.TurnId) ||
                !MatchesValue(request.StepId, row.StepId) || !MatchesValue(request.ToolCallId, row.ToolCallId))
            {
                return false;
            }
            var terms = (request.Search ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0) return true;
            var text = string.Join("\n", new[]
            {
                row.MutationId, row.Kind, row.Operation, row.Status, row.SessionId, row.RunId, row.TurnId,
                row.StepId, row.ToolCallId, row.ModuleName, row.ComponentType, row.BackupId,
                row.PackageId, row.PackageVersion, row.ErrorCode, row.Message,
                string.Join(" ", row.ComponentNames ?? new List<string>())
            }.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray());
            return terms.All(term => text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private VbaMutationDetail BuildMutationDetail(VbaMutationRecord record, IReadOnlyList<VbaJournalEvent> events)
        {
            var prepared = record.Prepared;
            var terminal = record.Terminal;
            var detail = MutationDetailBase(
                prepared.MutationId,
                VbaMutationKinds.Module,
                prepared.Operation,
                terminal == null ? VbaMutationStatuses.Open : terminal.Status,
                prepared.CreatedUtc,
                terminal == null ? (DateTime?)null : terminal.CreatedUtc,
                prepared.SessionId,
                prepared.RunId,
                prepared.TurnId,
                prepared.StepId,
                prepared.ToolCallId,
                terminal == null ? null : terminal.ErrorCode,
                terminal == null ? null : terminal.Message,
                events);
            detail.Components.Add(new VbaMutationComponentDetail
            {
                ModuleName = prepared.ModuleName,
                BeforeExists = prepared.BeforeExists,
                BeforeComponentType = prepared.BeforeExists ? prepared.ComponentType : null,
                BeforeCodeSha256 = prepared.BeforeCodeSha256,
                BeforeCode = ReadMutationCode(prepared.BeforeExists, prepared.BeforeCodeReference, prepared.MutationId, prepared.ModuleName, "before"),
                IntendedAfterExists = prepared.IntendedAfterExists,
                IntendedAfterComponentType = prepared.IntendedAfterExists ? prepared.ComponentType : null,
                IntendedAfterCodeSha256 = prepared.IntendedAfterCodeSha256,
                IntendedAfterCode = ReadMutationCode(prepared.IntendedAfterExists, prepared.IntendedAfterCodeReference, prepared.MutationId, prepared.ModuleName, "intended-after"),
                BackupId = prepared.BackupId,
                CanRestore = prepared.BeforeExists && !string.IsNullOrWhiteSpace(prepared.BackupId),
                ActualExists = terminal == null ? (bool?)null : terminal.ActualExists,
                ActualComponentType = terminal != null && terminal.ActualExists == true ? prepared.ComponentType : null,
                ActualCodeSha256 = terminal == null ? null : terminal.ActualCodeSha256,
                MatchesBefore = terminal == null ? (bool?)null : MatchesModuleState(
                    terminal.ActualExists, terminal.ActualCodeSha256, terminal.ActualComparableCodeSha256,
                    prepared.BeforeExists, prepared.BeforeCodeSha256, prepared.BeforeComparableCodeSha256),
                MatchesIntendedAfter = terminal == null ? (bool?)null : MatchesModuleState(
                    terminal.ActualExists, terminal.ActualCodeSha256, terminal.ActualComparableCodeSha256,
                    prepared.IntendedAfterExists, prepared.IntendedAfterCodeSha256, prepared.IntendedAfterComparableCodeSha256),
                ErrorCode = terminal == null ? null : terminal.ErrorCode,
                Message = terminal == null ? null : terminal.Message
            });
            return detail;
        }

        private VbaMutationDetail BuildPackageMutationDetail(VbaPackageMutationRecord record, IReadOnlyList<VbaJournalEvent> events)
        {
            var prepared = record.Prepared;
            var terminal = record.Terminal;
            var rename = string.Equals(prepared.Operation, "rename", StringComparison.OrdinalIgnoreCase);
            var detail = MutationDetailBase(
                prepared.MutationId,
                rename ? VbaMutationKinds.Module : VbaMutationKinds.Package,
                prepared.Operation,
                terminal == null ? VbaMutationStatuses.Open : terminal.Status,
                prepared.CreatedUtc,
                terminal == null ? (DateTime?)null : terminal.CreatedUtc,
                prepared.SessionId,
                prepared.RunId,
                prepared.TurnId,
                prepared.StepId,
                prepared.ToolCallId,
                terminal == null ? null : terminal.ErrorCode,
                terminal == null ? null : terminal.Message,
                events);
            if (!rename)
            {
                detail.PackageId = prepared.PackageId;
                detail.PackageVersion = prepared.PackageVersion;
            }
            foreach (var component in prepared.Components ?? new List<VbaPackageMutationComponent>())
            {
                var assessment = terminal == null || terminal.Components == null
                    ? null
                    : terminal.Components.FirstOrDefault(item => string.Equals(item.ModuleName, component.ModuleName, StringComparison.OrdinalIgnoreCase));
                detail.Components.Add(new VbaMutationComponentDetail
                {
                    ModuleName = component.ModuleName,
                    BeforeExists = component.BeforeExists,
                    BeforeComponentType = component.BeforeComponentType,
                    BeforeCodeSha256 = component.BeforeCodeSha256,
                    BeforeCode = ReadMutationCode(component.BeforeExists, component.BeforeCodeReference, prepared.MutationId, component.ModuleName, "before"),
                    IntendedAfterExists = component.IntendedAfterExists,
                    IntendedAfterComponentType = component.IntendedAfterComponentType,
                    IntendedAfterCodeSha256 = component.IntendedAfterCodeSha256,
                    IntendedAfterCode = ReadMutationCode(component.IntendedAfterExists, component.IntendedAfterCodeReference, prepared.MutationId, component.ModuleName, "intended-after"),
                    BackupId = component.BackupId,
                    CanRestore = component.BeforeExists && !string.IsNullOrWhiteSpace(component.BackupId),
                    ActualExists = assessment == null ? (bool?)null : assessment.ActualExists,
                    ActualComponentType = assessment == null ? null : assessment.ActualComponentType,
                    ActualCodeSha256 = assessment == null ? null : assessment.ActualCodeSha256,
                    MatchesBefore = assessment == null ? (bool?)null : assessment.MatchesBefore,
                    MatchesIntendedAfter = assessment == null ? (bool?)null : assessment.MatchesIntendedAfter,
                    ErrorCode = assessment == null ? null : assessment.ErrorCode,
                    Message = assessment == null ? null : assessment.Message
                });
            }
            return detail;
        }

        private static VbaMutationDetail MutationDetailBase(
            string mutationId,
            string kind,
            string operation,
            string status,
            DateTime createdUtc,
            DateTime? completedUtc,
            string sessionId,
            string runId,
            string turnId,
            string stepId,
            string toolCallId,
            string errorCode,
            string message,
            IEnumerable<VbaJournalEvent> events)
        {
            var source = (events ?? new List<VbaJournalEvent>()).OrderBy(item => item.Sequence).ToList();
            return new VbaMutationDetail
            {
                MutationId = mutationId,
                Kind = kind,
                Operation = operation,
                Status = status,
                CreatedUtc = createdUtc,
                CompletedUtc = completedUtc,
                SessionId = sessionId,
                RunId = runId,
                TurnId = turnId,
                StepId = stepId,
                ToolCallId = toolCallId,
                ErrorCode = errorCode,
                Message = message,
                SourceEventSeqs = source.Select(item => item.Sequence).ToList(),
                SourceEventIds = source.Select(item => item.EventId).Where(item => !string.IsNullOrWhiteSpace(item)).ToList()
            };
        }

        private string ReadMutationCode(bool exists, ChatBlobReference reference, string mutationId, string moduleName, string side)
        {
            if (!exists) return null;
            var code = _blobs.ReadText(reference);
            if (code == null)
            {
                throw new VbaJournalException("VBA " + side + " source is missing, corrupt, or protected with another key for mutation " +
                    mutationId + ", module " + moduleName + ".");
            }
            return code;
        }

        private static bool MatchesModuleState(
            bool? actualExists,
            string actualSha256,
            string actualComparableSha256,
            bool expectedExists,
            string expectedSha256,
            string expectedComparableSha256)
        {
            if (!actualExists.HasValue || actualExists.Value != expectedExists) return false;
            if (!expectedExists) return true;
            return !string.IsNullOrWhiteSpace(actualSha256) &&
                    string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(actualComparableSha256) &&
                    string.Equals(actualComparableSha256, expectedComparableSha256, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesValue(string expected, string actual)
        {
            return string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimOrNull(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length == 0 ? null : value;
        }

        private static List<long> SourceSequences(params VbaJournalEvent[] events)
        {
            return events.Where(item => item != null).Select(item => item.Sequence).ToList();
        }

        private static List<string> SourceEventIds(params VbaJournalEvent[] events)
        {
            return events.Where(item => item != null && !string.IsNullOrWhiteSpace(item.EventId)).Select(item => item.EventId).ToList();
        }

        private static IReadOnlyList<VbaMutationRecord> ProjectMutations(IEnumerable<VbaJournalEvent> events)
        {
            var records = new List<VbaMutationRecord>();
            var byId = new Dictionary<string, VbaMutationRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var journalEvent in events ?? new List<VbaJournalEvent>())
            {
                if (journalEvent.Data == null) continue;
                if (string.Equals(journalEvent.Type, VbaJournalEventTypes.MutationPrepared, StringComparison.Ordinal))
                {
                    var prepared = journalEvent.Data.ToObject<VbaMutationPreparation>();
                    if (!ValidPreparation(journalEvent, prepared) || byId.ContainsKey(prepared.MutationId))
                    {
                        throw new VbaJournalException("The VBA mutation journal contains an invalid preparation.");
                    }
                    var record = new VbaMutationRecord { Prepared = prepared };
                    byId.Add(prepared.MutationId, record);
                    records.Add(record);
                }
                else if (string.Equals(journalEvent.Type, VbaJournalEventTypes.MutationTerminal, StringComparison.Ordinal))
                {
                    var terminal = journalEvent.Data.ToObject<VbaMutationTerminal>();
                    VbaMutationRecord record;
                    if (terminal == null || string.IsNullOrWhiteSpace(terminal.MutationId) ||
                        !string.Equals(journalEvent.MutationId, terminal.MutationId, StringComparison.OrdinalIgnoreCase) ||
                        !VbaMutationStatuses.IsTerminal(terminal.Status) ||
                        !byId.TryGetValue(terminal.MutationId, out record) || record.Terminal != null)
                    {
                        throw new VbaJournalException("The VBA mutation journal contains an invalid terminal record.");
                    }
                    if (!SameCorrelation(journalEvent, record.Prepared))
                    {
                        throw new VbaJournalException("The VBA mutation journal terminal correlation is invalid.");
                    }
                    record.Terminal = terminal;
                }
            }
            return records;
        }

        private static IReadOnlyList<VbaPackageMutationRecord> ProjectPackageMutations(IEnumerable<VbaJournalEvent> events)
        {
            var records = new List<VbaPackageMutationRecord>();
            var byId = new Dictionary<string, VbaPackageMutationRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var journalEvent in events ?? new List<VbaJournalEvent>())
            {
                if (journalEvent.Data == null) continue;
                if (string.Equals(journalEvent.Type, VbaJournalEventTypes.PackageMutationPrepared, StringComparison.Ordinal))
                {
                    var prepared = journalEvent.Data.ToObject<VbaPackageMutationPreparation>();
                    if (!ValidPackagePreparation(journalEvent, prepared) || byId.ContainsKey(prepared.MutationId))
                    {
                        throw new VbaJournalException("The VBA mutation journal contains an invalid package preparation.");
                    }
                    var record = new VbaPackageMutationRecord { Prepared = prepared };
                    byId.Add(prepared.MutationId, record);
                    records.Add(record);
                }
                else if (string.Equals(journalEvent.Type, VbaJournalEventTypes.PackageMutationTerminal, StringComparison.Ordinal))
                {
                    var terminal = journalEvent.Data.ToObject<VbaPackageMutationTerminal>();
                    VbaPackageMutationRecord record;
                    if (terminal == null || string.IsNullOrWhiteSpace(terminal.MutationId) ||
                        !string.Equals(journalEvent.MutationId, terminal.MutationId, StringComparison.OrdinalIgnoreCase) ||
                        !VbaMutationStatuses.IsTerminal(terminal.Status) || terminal.Components == null ||
                        !byId.TryGetValue(terminal.MutationId, out record) || record.Terminal != null)
                    {
                        throw new VbaJournalException("The VBA mutation journal contains an invalid package terminal record.");
                    }
                    if (!SameCorrelation(journalEvent, record.Prepared) ||
                        terminal.Components.Count != record.Prepared.Components.Count ||
                        terminal.Components.Any(item => item == null || string.IsNullOrWhiteSpace(item.ModuleName)) ||
                        terminal.Components.GroupBy(item => item.ModuleName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) ||
                        terminal.Components.Any(item => !record.Prepared.Components.Any(component =>
                            string.Equals(component.ModuleName, item.ModuleName, StringComparison.OrdinalIgnoreCase))))
                    {
                        throw new VbaJournalException("The VBA mutation journal package terminal correlation is invalid.");
                    }
                    record.Terminal = terminal;
                }
            }
            return records;
        }

        private static void AddBackup(IDictionary<string, VbaModuleBackup> backups, VbaModuleBackup backup)
        {
            if (backup == null || string.IsNullOrWhiteSpace(backup.BackupId) || backup.CodeReference == null) return;
            if (!backups.ContainsKey(backup.BackupId)) backups.Add(backup.BackupId, backup);
        }

        private static bool ValidBackup(VbaJournalEvent journalEvent, VbaModuleBackup backup)
        {
            return journalEvent != null && backup != null &&
                string.IsNullOrWhiteSpace(journalEvent.MutationId) &&
                !string.IsNullOrWhiteSpace(backup.BackupId) &&
                !string.IsNullOrWhiteSpace(backup.ModuleName) &&
                string.Equals(journalEvent.Host, backup.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.DocumentKey, backup.DocumentKey, StringComparison.OrdinalIgnoreCase) &&
                ValidReference(backup.CodeReference) &&
                string.Equals(backup.CodeSha256, backup.CodeReference.Sha256, StringComparison.OrdinalIgnoreCase) &&
                backup.CodeByteLength == backup.CodeReference.ByteLength;
        }

        private static bool ValidPreparation(VbaJournalEvent journalEvent, VbaMutationPreparation prepared)
        {
            if (journalEvent == null || prepared == null || string.IsNullOrWhiteSpace(prepared.MutationId) ||
                string.IsNullOrWhiteSpace(prepared.Operation) || string.IsNullOrWhiteSpace(prepared.ModuleName) ||
                !string.Equals(journalEvent.MutationId, prepared.MutationId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(journalEvent.Host, prepared.Host, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(journalEvent.DocumentKey, prepared.DocumentKey, StringComparison.OrdinalIgnoreCase) ||
                !SameCorrelation(journalEvent, prepared)) return false;
            if (prepared.BeforeExists != (prepared.BeforeCodeReference != null) ||
                prepared.BeforeExists != !string.IsNullOrWhiteSpace(prepared.BackupId) ||
                prepared.IntendedAfterExists != (prepared.IntendedAfterCodeReference != null)) return false;
            if (prepared.BeforeExists && (!ValidReference(prepared.BeforeCodeReference) ||
                !string.Equals(prepared.BeforeCodeSha256, prepared.BeforeCodeReference.Sha256, StringComparison.OrdinalIgnoreCase))) return false;
            return !prepared.IntendedAfterExists || ValidReference(prepared.IntendedAfterCodeReference) &&
                string.Equals(prepared.IntendedAfterCodeSha256, prepared.IntendedAfterCodeReference.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ValidPackagePreparation(VbaJournalEvent journalEvent, VbaPackageMutationPreparation prepared)
        {
            if (journalEvent == null || prepared == null || string.IsNullOrWhiteSpace(prepared.MutationId) ||
                string.IsNullOrWhiteSpace(prepared.Operation) || string.IsNullOrWhiteSpace(prepared.PackageId) ||
                prepared.Components == null || prepared.Components.Count == 0 ||
                !string.Equals(journalEvent.MutationId, prepared.MutationId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(journalEvent.Host, prepared.Host, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(journalEvent.DocumentKey, prepared.DocumentKey, StringComparison.OrdinalIgnoreCase) ||
                !SameCorrelation(journalEvent, prepared)) return false;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var component in prepared.Components)
            {
                if (component == null || string.IsNullOrWhiteSpace(component.ModuleName) ||
                    component.BeforeExists && string.IsNullOrWhiteSpace(component.BeforeComponentType) ||
                    !component.BeforeExists && !string.IsNullOrWhiteSpace(component.BeforeComponentType) ||
                    component.IntendedAfterExists && string.IsNullOrWhiteSpace(component.IntendedAfterComponentType) ||
                    !component.IntendedAfterExists && !string.IsNullOrWhiteSpace(component.IntendedAfterComponentType) ||
                    !names.Add(component.ModuleName) ||
                    component.BeforeExists != (component.BeforeCodeReference != null) ||
                    component.IntendedAfterExists != (component.IntendedAfterCodeReference != null) ||
                    prepared.RetainBackups && component.BeforeExists != !string.IsNullOrWhiteSpace(component.BackupId) ||
                    !prepared.RetainBackups && !string.IsNullOrWhiteSpace(component.BackupId) ||
                    component.BeforeExists && (!ValidReference(component.BeforeCodeReference) || !ValidSha256(component.BeforeCodeSha256)) ||
                    component.IntendedAfterExists && (!ValidReference(component.IntendedAfterCodeReference) || !ValidSha256(component.IntendedAfterCodeSha256)) ||
                    !string.IsNullOrWhiteSpace(component.BeforeComparableCodeSha256) && !ValidSha256(component.BeforeComparableCodeSha256) ||
                    !string.IsNullOrWhiteSpace(component.IntendedAfterComparableCodeSha256) && !ValidSha256(component.IntendedAfterComparableCodeSha256))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SameCorrelation(VbaJournalEvent journalEvent, VbaMutationPreparation prepared)
        {
            return journalEvent != null && prepared != null &&
                string.Equals(journalEvent.RunId ?? string.Empty, prepared.RunId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.TurnId ?? string.Empty, prepared.TurnId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.StepId ?? string.Empty, prepared.StepId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.ToolCallId ?? string.Empty, prepared.ToolCallId ?? string.Empty, StringComparison.Ordinal);
        }

        private static bool SameCorrelation(VbaJournalEvent journalEvent, VbaPackageMutationPreparation prepared)
        {
            return journalEvent != null && prepared != null &&
                string.Equals(journalEvent.RunId ?? string.Empty, prepared.RunId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.TurnId ?? string.Empty, prepared.TurnId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.StepId ?? string.Empty, prepared.StepId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.ToolCallId ?? string.Empty, prepared.ToolCallId ?? string.Empty, StringComparison.Ordinal);
        }

        private static bool ValidSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F'))) return false;
            }
            return true;
        }

        private static bool ValidReference(ChatBlobReference reference)
        {
            if (reference == null || reference.ByteLength < 0 || string.IsNullOrWhiteSpace(reference.Sha256) || reference.Sha256.Length != 64)
            {
                return false;
            }
            for (var index = 0; index < reference.Sha256.Length; index++)
            {
                var character = reference.Sha256[index];
                if (!((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F'))) return false;
            }
            return true;
        }

        private sealed class MutationCursor
        {
            public long SnapshotSequence { get; set; }
            public int Offset { get; set; }
        }

    }
}

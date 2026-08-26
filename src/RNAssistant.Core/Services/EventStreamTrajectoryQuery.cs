using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public interface ITrajectoryQuery
    {
        TrajectoryQueryPage Query(IReadOnlyList<SessionEvent> events, TrajectoryQueryRequest request);
        TrajectoryViewPage QueryView(IReadOnlyList<SessionEvent> events, TrajectoryViewQueryRequest request);
    }

    /// <summary>
    /// Disposable in-memory projection rebuilt from the validated canonical event stream for every query.
    /// </summary>
    public sealed class EventStreamTrajectoryQuery : ITrajectoryQuery
    {
        private const int MaxPageSize = 200;
        private const int MaxSearchChars = 512;
        private const string CursorPrefix = "seq:";
        private const string ViewCursorPrefix = "view:";

        public TrajectoryQueryPage Query(IReadOnlyList<SessionEvent> events, TrajectoryQueryRequest request)
        {
            request = request ?? new TrajectoryQueryRequest();
            Validate(request);
            var source = (events ?? new List<SessionEvent>()).Where(item => item != null).OrderBy(item => item.Sequence).ToList();
            var currentTargets = BuildCurrentTargets(source);
            var records = source.Select(item => Project(item, currentTargets)).ToList();
            var filtered = records.Where(item => Matches(item, request)).OrderByDescending(item => item.Event.Sequence).ToList();
            var beforeSequence = ParseCursor(request.Cursor);
            var available = beforeSequence.HasValue
                ? filtered.Where(item => item.Event.Sequence < beforeSequence.Value).ToList()
                : filtered;
            var pageSize = request.PageSize <= 0 ? 100 : Math.Min(MaxPageSize, request.PageSize);
            var page = available.Take(pageSize).ToList();
            var hasMore = available.Count > page.Count;
            return new TrajectoryQueryPage
            {
                TotalEvents = source.Count,
                TotalMatches = filtered.Count,
                Cursor = request.Cursor,
                NextCursor = hasMore && page.Count > 0 ? CursorPrefix + page[page.Count - 1].Event.Sequence.ToString(CultureInfo.InvariantCulture) : null,
                HasMore = hasMore,
                Records = page
            };
        }

        public TrajectoryViewPage QueryView(IReadOnlyList<SessionEvent> events, TrajectoryViewQueryRequest request)
        {
            request = request ?? new TrajectoryViewQueryRequest();
            Validate(request);
            var source = (events ?? new List<SessionEvent>()).Where(item => item != null).OrderBy(item => item.Sequence).ToList();
            var cursor = ParseViewCursor(request.Cursor, request.View);
            var snapshotSequence = cursor == null
                ? (source.Count == 0 ? 0 : source[source.Count - 1].Sequence)
                : cursor.SnapshotSequence;
            var snapshot = source.Where(item => item.Sequence <= snapshotSequence).ToList();
            var rows = TrajectoryDerivedProjection.Build(snapshot, request.View);
            var filtered = rows.Where(item => Matches(item, request))
                .OrderByDescending(item => item.LastSequence)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList();
            var offset = cursor == null ? 0 : cursor.Offset;
            if (offset > filtered.Count) offset = filtered.Count;
            var pageSize = request.PageSize <= 0 ? 100 : Math.Min(MaxPageSize, request.PageSize);
            var page = filtered.Skip(offset).Take(pageSize).ToList();
            var nextOffset = offset + page.Count;
            var hasMore = nextOffset < filtered.Count;
            return new TrajectoryViewPage
            {
                View = request.View,
                TotalEvents = snapshot.Count,
                TotalRows = rows.Count,
                TotalMatches = filtered.Count,
                Cursor = request.Cursor,
                NextCursor = hasMore
                    ? ViewCursorPrefix + request.View + ":" + snapshotSequence.ToString(CultureInfo.InvariantCulture) + ":" + nextOffset.ToString(CultureInfo.InvariantCulture)
                    : null,
                HasMore = hasMore,
                Rows = page
            };
        }

        private static void Validate(TrajectoryQueryRequest request)
        {
            request.Search = (request.Search ?? string.Empty).Trim();
            request.Visibility = TrimOrNull(request.Visibility);
            request.RunId = TrimOrNull(request.RunId);
            request.TurnId = TrimOrNull(request.TurnId);
            request.StepId = TrimOrNull(request.StepId);
            request.ToolCallId = TrimOrNull(request.ToolCallId);
            request.ArtifactId = TrimOrNull(request.ArtifactId);
            request.ResourceUri = TrimOrNull(request.ResourceUri);
            request.Status = TrimOrNull(request.Status);
            request.EventTypes = (request.EventTypes ?? new List<string>())
                .Select(TrimOrNull)
                .Where(value => value != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!TrajectoryVisibility.IsValid(request.Visibility))
            {
                throw new ArgumentException("Unsupported trajectory visibility: " + request.Visibility + ".", "request");
            }
            if (request.MinSequence.HasValue && request.MaxSequence.HasValue && request.MinSequence.Value > request.MaxSequence.Value)
            {
                throw new ArgumentException("Trajectory minSequence cannot exceed maxSequence.", "request");
            }
            if ((request.Search ?? string.Empty).Length > MaxSearchChars)
            {
                throw new ArgumentException("Trajectory search is limited to " + MaxSearchChars + " characters.", "request");
            }
            ResourceAddress ignoredResource;
            if (request.ResourceUri != null && !ResourceUri.TryParse(request.ResourceUri, out ignoredResource))
            {
                throw new ArgumentException("Trajectory resourceUri must be canonical.", "request");
            }
            if (request.EventTypes.Count > 64 || request.EventTypes.Any(value => value.Length > 128))
            {
                throw new ArgumentException("Trajectory eventTypes accepts up to 64 names of 128 characters.", "request");
            }
            ParseCursor(request.Cursor);
        }

        private static void Validate(TrajectoryViewQueryRequest request)
        {
            request.View = (request.View ?? string.Empty).Trim().ToLowerInvariant();
            request.Search = (request.Search ?? string.Empty).Trim();
            request.RunId = TrimOrNull(request.RunId);
            request.TurnId = TrimOrNull(request.TurnId);
            request.StepId = TrimOrNull(request.StepId);
            request.ToolCallId = TrimOrNull(request.ToolCallId);
            request.ArtifactId = TrimOrNull(request.ArtifactId);
            request.Status = TrimOrNull(request.Status);
            if (!TrajectoryViews.IsSupported(request.View) || string.Equals(request.View, TrajectoryViews.Raw, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Unsupported derived trajectory view: " + request.View + ".", "request");
            }
            if (request.MinSequence.HasValue && request.MaxSequence.HasValue && request.MinSequence.Value > request.MaxSequence.Value)
            {
                throw new ArgumentException("Trajectory minSequence cannot exceed maxSequence.", "request");
            }
            if (request.Search.Length > MaxSearchChars)
            {
                throw new ArgumentException("Trajectory search is limited to " + MaxSearchChars + " characters.", "request");
            }
            ParseViewCursor(request.Cursor, request.View);
        }

        private static string TrimOrNull(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length == 0 ? null : value;
        }

        private static long? ParseCursor(string cursor)
        {
            if (string.IsNullOrWhiteSpace(cursor)) return null;
            long sequence;
            if (!cursor.StartsWith(CursorPrefix, StringComparison.Ordinal) ||
                !long.TryParse(cursor.Substring(CursorPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence <= 0)
            {
                throw new ArgumentException("Invalid trajectory cursor.", "cursor");
            }
            return sequence;
        }

        private static ViewCursor ParseViewCursor(string cursor, string view)
        {
            if (string.IsNullOrWhiteSpace(cursor)) return null;
            var parts = cursor.Split(':');
            long snapshot;
            int offset;
            if (parts.Length != 4 || !string.Equals(parts[0] + ":", ViewCursorPrefix, StringComparison.Ordinal) ||
                !string.Equals(parts[1], view, StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out snapshot) || snapshot < 0 ||
                !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)
            {
                throw new ArgumentException("Invalid trajectory view cursor.", "cursor");
            }
            return new ViewCursor { SnapshotSequence = snapshot, Offset = offset };
        }

        private static bool Matches(TrajectoryViewRow row, TrajectoryViewQueryRequest request)
        {
            if (request.MinSequence.HasValue && row.LastSequence < request.MinSequence.Value ||
                request.MaxSequence.HasValue && row.FirstSequence > request.MaxSequence.Value ||
                !MatchesValue(request.RunId, row.RunId, new string[0]) ||
                !MatchesValue(request.TurnId, row.TurnId, new string[0]) ||
                !MatchesValue(request.StepId, row.StepId, new string[0]) ||
                !MatchesValue(request.ToolCallId, row.ToolCallId, new string[0]) ||
                !MatchesValue(request.ArtifactId, row.ArtifactId, new[] { row.ParentArtifactId }) ||
                !MatchesValue(request.Status, row.Status, new string[0])) return false;
            var terms = (request.Search ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0) return true;
            var text = string.Join("\n", new[]
            {
                row.Id, row.View, row.Kind, row.Title, row.Status, row.RunId, row.TurnId, row.StepId,
                row.ToolCallId, row.ToolId, row.ArtifactId, row.ParentArtifactId,
                row.Data == null ? string.Empty : row.Data.ToString(Formatting.None)
            }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
            return terms.All(term => text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static Dictionary<string, long> BuildCurrentTargets(IEnumerable<SessionEvent> events)
        {
            var targets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var sessionEvent in events ?? new List<SessionEvent>())
            {
                if (!string.Equals(sessionEvent.Type, SessionEventTypes.SessionCommit, StringComparison.Ordinal) || sessionEvent.Data == null) continue;
                foreach (var operation in Operations(sessionEvent))
                {
                    foreach (var target in OperationTargets(operation)) targets[target] = sessionEvent.Sequence;
                }
            }
            return targets;
        }

        private static TrajectoryEventRecord Project(SessionEvent sessionEvent, IDictionary<string, long> currentTargets)
        {
            var visibility = TrajectoryVisibility.LogOnly;
            if (string.Equals(sessionEvent.Type, SessionEventTypes.SessionCreated, StringComparison.Ordinal) ||
                string.Equals(sessionEvent.Type, SessionEventTypes.SessionForked, StringComparison.Ordinal))
            {
                visibility = TrajectoryVisibility.Current;
            }
            else if (string.Equals(sessionEvent.Type, SessionEventTypes.SessionCommit, StringComparison.Ordinal))
            {
                var targets = Operations(sessionEvent).SelectMany(OperationTargets).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                visibility = targets.Any(target => currentTargets.ContainsKey(target) && currentTargets[target] == sessionEvent.Sequence)
                    ? TrajectoryVisibility.Current
                    : TrajectoryVisibility.Shadowed;
            }

            return new TrajectoryEventRecord
            {
                Event = sessionEvent,
                Visibility = visibility,
                SourceEventSeqs = new List<long> { sessionEvent.Sequence },
                SourceEventIds = string.IsNullOrWhiteSpace(sessionEvent.EventId)
                    ? new List<string>()
                    : new List<string> { sessionEvent.EventId },
                ToolCallIds = ExtractValues(sessionEvent.Data, "ToolCallId", "tool_call_id"),
                ArtifactIds = ExtractArtifactIds(sessionEvent),
                ResourceRefs = ExtractResourceRefs(sessionEvent == null ? null : sessionEvent.Data),
                Statuses = ExtractValues(sessionEvent.Data, "Status", "ExecutionStatus", "ResponseStatus")
            };
        }

        private static bool Matches(TrajectoryEventRecord record, TrajectoryQueryRequest request)
        {
            var sessionEvent = record.Event;
            if (request.MinSequence.HasValue && sessionEvent.Sequence < request.MinSequence.Value ||
                request.MaxSequence.HasValue && sessionEvent.Sequence > request.MaxSequence.Value) return false;
            if ((request.EventTypes ?? new List<string>()).Any() && !(request.EventTypes ?? new List<string>()).Any(value =>
                string.Equals(value, sessionEvent.Type, StringComparison.OrdinalIgnoreCase))) return false;
            if (!MatchesValue(request.RunId, sessionEvent.RunId, ExtractValues(sessionEvent.Data, "RunId")) ||
                !MatchesValue(request.TurnId, sessionEvent.TurnId, ExtractValues(sessionEvent.Data, "TurnId")) ||
                !MatchesValue(request.StepId, sessionEvent.StepId, ExtractValues(sessionEvent.Data, "StepId")) ||
                !MatchesList(request.ToolCallId, record.ToolCallIds) ||
                !MatchesList(request.ArtifactId, record.ArtifactIds) ||
                !MatchesResourceUri(request.ResourceUri, record.ResourceRefs) ||
                !MatchesList(request.Status, record.Statuses) ||
                !string.IsNullOrWhiteSpace(request.Visibility) && !string.Equals(request.Visibility, record.Visibility, StringComparison.OrdinalIgnoreCase)) return false;
            return MatchesSearch(record, request.Search);
        }

        private static bool MatchesValue(string expected, string direct, IEnumerable<string> nested)
        {
            return string.IsNullOrWhiteSpace(expected) ||
                string.Equals(expected, direct, StringComparison.OrdinalIgnoreCase) ||
                (nested ?? new string[0]).Any(value => string.Equals(expected, value, StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesList(string expected, IEnumerable<string> values)
        {
            return string.IsNullOrWhiteSpace(expected) ||
                (values ?? new string[0]).Any(value => string.Equals(expected, value, StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesResourceUri(string expected, IEnumerable<ResourceRef> references)
        {
            return string.IsNullOrWhiteSpace(expected) ||
                (references ?? new ResourceRef[0]).Any(reference => reference != null &&
                    string.Equals(expected, reference.Uri, StringComparison.Ordinal));
        }

        private static bool MatchesSearch(TrajectoryEventRecord record, string search)
        {
            var terms = (search ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0) return true;
            var sessionEvent = record.Event;
            var text = string.Join("\n", new[]
            {
                sessionEvent.Type,
                sessionEvent.EventId,
                sessionEvent.RunId,
                sessionEvent.TurnId,
                sessionEvent.StepId,
                record.Visibility,
                string.Join(" ", record.ToolCallIds),
                string.Join(" ", record.ArtifactIds),
                string.Join(" ", (record.ResourceRefs ?? new List<ResourceRef>()).Select(reference => reference.Uri)),
                string.Join(" ", record.Statuses),
                sessionEvent.Data == null ? string.Empty : sessionEvent.Data.ToString(Formatting.None),
                sessionEvent.Payload == null ? string.Empty : sessionEvent.Payload.Sha256 + " " + sessionEvent.Payload.ContentType
            }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
            return terms.All(term => text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static IEnumerable<JObject> Operations(SessionEvent sessionEvent)
        {
            var operations = sessionEvent == null || sessionEvent.Data == null
                ? null
                : Property(sessionEvent.Data, "Operations") as JArray;
            return operations == null ? new List<JObject>() : operations.OfType<JObject>();
        }

        private static IEnumerable<string> OperationTargets(JObject operation)
        {
            var type = StringProperty(operation, "Type");
            var data = Property(operation, "Data") as JObject ?? new JObject();
            if (string.Equals(type, SessionOperationTypes.SessionMetadataSet, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.ActiveReferencesSet, StringComparison.OrdinalIgnoreCase))
            {
                return data.Properties().Select(property => type + ":" + property.Name).ToList();
            }
            if (string.Equals(type, SessionOperationTypes.ContextSet, StringComparison.OrdinalIgnoreCase)) return new[] { "context" };
            if (string.Equals(type, SessionOperationTypes.RunStarted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.RunUpdated, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.RunEnded, StringComparison.OrdinalIgnoreCase)) return new[] { "run" };
            if (string.Equals(type, SessionOperationTypes.MessagesReorder, StringComparison.OrdinalIgnoreCase)) return new[] { "messages:order" };
            if (string.Equals(type, SessionOperationTypes.ArtifactsReorder, StringComparison.OrdinalIgnoreCase)) return new[] { "artifacts:order" };
            if (IsMessageOperation(type)) return TargetWithId("message", data);
            if (IsArtifactOperation(type)) return TargetWithId("artifact", data);
            return new string[0];
        }

        private static bool IsMessageOperation(string type)
        {
            return string.Equals(type, SessionOperationTypes.MessageUpdated, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.UserMessageAppended, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.AssistantMessageAppended, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.ToolCallRecorded, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.ToolResultRecorded, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.ToolExecutionStarted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.ToolExecutionFinished, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.MessageRemove, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsArtifactOperation(string type)
        {
            return string.Equals(type, SessionOperationTypes.ArtifactRevisionCreated, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.ArtifactRemove, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> TargetWithId(string prefix, JObject data)
        {
            var value = Property(data, "Value") as JObject;
            var id = StringProperty(value, "Id") ?? StringProperty(data, "Id");
            return string.IsNullOrWhiteSpace(id) ? new string[0] : new[] { prefix + ":" + id };
        }

        private static List<string> ExtractArtifactIds(SessionEvent sessionEvent)
        {
            var values = ExtractValues(sessionEvent == null ? null : sessionEvent.Data,
                "ArtifactId", "ParentArtifactId", "ActiveHtmlArtifactId", "ActiveTaskListArtifactId", "ActivePlanDocumentArtifactId", "ActiveContextCheckpointId");
            foreach (var operation in Operations(sessionEvent).Where(operation => IsArtifactOperation(StringProperty(operation, "Type"))))
            {
                var data = Property(operation, "Data") as JObject;
                var value = data == null ? null : Property(data, "Value") as JObject;
                AddDistinct(values, StringProperty(value, "Id") ?? StringProperty(data, "Id"));
            }
            return values;
        }

        private static List<ResourceRef> ExtractResourceRefs(JToken token)
        {
            var result = new List<ResourceRef>();
            ExtractResourceRefs(token, result);
            return result
                .GroupBy(reference => reference.Uri + "\n" + (reference.Revision ?? string.Empty), StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }

        private static void ExtractResourceRefs(JToken token, ICollection<ResourceRef> result)
        {
            if (token == null || result == null) return;
            var value = token as JValue;
            if (value != null && value.Type == JTokenType.String)
            {
                ResourceAddress ignored;
                var uri = (string)value;
                if (ResourceUri.TryParse(uri, out ignored)) result.Add(new ResourceRef(uri));
                return;
            }
            var obj = token as JObject;
            if (obj != null)
            {
                var uriToken = Property(obj, "uri");
                ResourceAddress ignored;
                var uri = uriToken == null ? null : (string)uriToken;
                if (ResourceUri.TryParse(uri, out ignored))
                {
                    result.Add(new ResourceRef(uri, (string)Property(obj, "revision")));
                    return;
                }
            }
            foreach (var child in token.Children()) ExtractResourceRefs(child, result);
        }

        private static List<string> ExtractValues(JToken token, params string[] names)
        {
            var result = new List<string>();
            ExtractValues(token, new HashSet<string>(names ?? new string[0], StringComparer.OrdinalIgnoreCase), result);
            return result;
        }

        private static void ExtractValues(JToken token, ISet<string> names, IList<string> result)
        {
            var value = token as JValue;
            if (token == null || value != null) return;
            var obj = token as JObject;
            if (obj != null)
            {
                foreach (var property in obj.Properties())
                {
                    if (names.Contains(property.Name)) AddTokenValues(result, property.Value);
                    ExtractValues(property.Value, names, result);
                }
                return;
            }
            foreach (var child in token.Children()) ExtractValues(child, names, result);
        }

        private static void AddTokenValues(IList<string> result, JToken token)
        {
            var array = token as JArray;
            if (array != null)
            {
                foreach (var item in array) AddTokenValues(result, item);
                return;
            }
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Object) return;
            AddDistinct(result, Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture));
        }

        private static void AddDistinct(IList<string> values, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !values.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase))) values.Add(value);
        }

        private static JToken Property(JToken token, string name)
        {
            var obj = token as JObject;
            return obj == null ? null : obj.Properties().FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
        }

        private static string StringProperty(JToken token, string name)
        {
            var value = Property(token, name);
            return value == null || value.Type == JTokenType.Null ? null : (string)value;
        }

        private sealed class ViewCursor
        {
            public long SnapshotSequence { get; set; }
            public int Offset { get; set; }
        }
    }
}

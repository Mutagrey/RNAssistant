using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Qualification
{
    public sealed class QualificationManifestException : FormatException
    {
        public QualificationManifestException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public QualificationManifestException(string code, string message, Exception innerException)
            : base(message, innerException)
        {
            Code = code;
        }

        public string Code { get; private set; }
    }

    internal static class QualificationJson
    {
        private const int MaximumJsonChars = 262144;
        private static readonly Regex JsonLiteral = new Regex(
            @"\A(?:true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)\z",
            RegexOptions.CultureInvariant);

        internal static JObject ReadObject(string json, string subject, int maximumChars = MaximumJsonChars)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new QualificationManifestException("json_required", subject + " must be a JSON object.");
            if (json.Length > maximumChars)
                throw new QualificationManifestException("json_too_large", subject + " exceeds its bounded size limit.");
            try
            {
                RejectJsonExtensions(json);
                using (var reader = new JsonTextReader(new StringReader(json))
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal,
                    MaxDepth = 32
                })
                {
                    var root = JObject.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                    if (reader.Read()) throw new JsonReaderException("More than one JSON value.");
                    return root;
                }
            }
            catch (JsonException ex)
            {
                throw new QualificationManifestException("invalid_json", subject + " is invalid JSON: " + ex.Message, ex);
            }
        }

        internal static void EnsureJsonValue(string json, string subject)
        {
            if (json == null) return;
            if (json.Length > MaximumJsonChars)
                throw new QualificationManifestException("evidence_too_large", subject + " exceeds the 262144 character limit.");
            try
            {
                RejectJsonExtensions(json);
                using (var reader = new JsonTextReader(new StringReader(json))
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal,
                    MaxDepth = 32
                })
                {
                    JToken.ReadFrom(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                    if (reader.Read()) throw new JsonReaderException("More than one JSON value.");
                }
            }
            catch (JsonException ex)
            {
                throw new QualificationManifestException("invalid_evidence_json", subject + " is invalid JSON: " + ex.Message, ex);
            }
        }

        internal static void EnsureOnly(JObject value, IEnumerable<string> fields, string subject)
        {
            var allowed = new HashSet<string>(fields, StringComparer.Ordinal);
            var unexpected = value.Properties().FirstOrDefault(property => !allowed.Contains(property.Name));
            if (unexpected != null)
                throw new QualificationManifestException("unknown_field", subject + " contains unsupported field: " + unexpected.Name + ".");
        }

        internal static string RequiredString(JObject value, string field, int maximum, string subject)
        {
            var token = value[field];
            if (token == null || token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token))
                throw new QualificationManifestException("required_field", subject + "." + field + " must be a non-empty string.");
            var result = ((string)token).Trim();
            if (result.Length > maximum)
                throw new QualificationManifestException("field_too_long", subject + "." + field + " exceeds " + maximum + " characters.");
            return result;
        }

        internal static string OptionalString(JObject value, string field, int maximum, string subject)
        {
            var token = value[field];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String)
                throw new QualificationManifestException("field_type", subject + "." + field + " must be a string.");
            var result = ((string)token).Trim();
            if (result.Length > maximum)
                throw new QualificationManifestException("field_too_long", subject + "." + field + " exceeds " + maximum + " characters.");
            return result.Length == 0 ? null : result;
        }

        internal static IReadOnlyList<string> StringArray(JObject value, string field, int maximumItems,
            int maximumItemLength, bool required, string subject)
        {
            var array = value[field] as JArray;
            if (array == null)
            {
                if (!required && value[field] == null) return new string[0];
                throw new QualificationManifestException("field_type", subject + "." + field + " must be an array.");
            }
            if ((required && array.Count == 0) || array.Count > maximumItems)
                throw new QualificationManifestException("array_bounds", subject + "." + field + " has an invalid item count.");
            var result = new List<string>(array.Count);
            foreach (var item in array)
            {
                if (item.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)item))
                    throw new QualificationManifestException("array_item", subject + "." + field + " contains an invalid string.");
                var text = ((string)item).Trim();
                if (text.Length > maximumItemLength)
                    throw new QualificationManifestException("array_item", subject + "." + field + " contains an overlong string.");
                result.Add(text);
            }
            if (result.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
                throw new QualificationManifestException("duplicate_item", subject + "." + field + " contains duplicates.");
            return Array.AsReadOnly(result.ToArray());
        }

        internal static string Sha256(string value)
        {
            using (var algorithm = SHA256.Create())
            {
                var bytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value));
                var result = new StringBuilder(bytes.Length * 2);
                foreach (var item in bytes) result.Append(item.ToString("x2"));
                return result.ToString();
            }
        }

        private static void RejectJsonExtensions(string raw)
        {
            for (var i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                if (IsWhitespace(c) || "{}[]:".IndexOf(c) >= 0) continue;
                if (c == ',')
                {
                    var next = i + 1;
                    while (next < raw.Length && IsWhitespace(raw[next])) next++;
                    if (next < raw.Length && (raw[next] == '}' || raw[next] == ']'))
                        throw new JsonReaderException("Trailing commas are not JSON.");
                    continue;
                }
                if (c == '"')
                {
                    var closed = false;
                    while (++i < raw.Length)
                    {
                        c = raw[i];
                        if (c == '"') { closed = true; break; }
                        if (c < 0x20) throw new JsonReaderException("Unescaped control character in string.");
                        if (c != '\\') continue;
                        if (++i >= raw.Length) break;
                        c = raw[i];
                        if ("\"\\/bfnrt".IndexOf(c) >= 0) continue;
                        if (c != 'u' || i + 4 >= raw.Length)
                            throw new JsonReaderException("Invalid JSON string escape.");
                        for (var digit = 0; digit < 4; digit++)
                            if (!Uri.IsHexDigit(raw[++i])) throw new JsonReaderException("Invalid Unicode escape.");
                    }
                    if (!closed) throw new JsonReaderException("Unterminated JSON string.");
                    continue;
                }
                var start = i;
                while (i < raw.Length && !IsWhitespace(raw[i]) && "{}[],:".IndexOf(raw[i]) < 0) i++;
                if (!JsonLiteral.IsMatch(raw.Substring(start, i - start)))
                    throw new JsonReaderException("Invalid JSON literal or unquoted property.");
                var after = i;
                while (after < raw.Length && IsWhitespace(raw[after])) after++;
                if (after < raw.Length && raw[after] == ':')
                    throw new JsonReaderException("JSON property names require double quotes.");
                i--;
            }
        }

        private static bool IsWhitespace(char value)
        {
            return value == ' ' || value == '\t' || value == '\r' || value == '\n';
        }
    }

    public sealed class QualificationManifestParser
    {
        private static readonly Regex IdPattern = new Regex(
            "^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
        private static readonly Regex RevisionPattern = new Regex(
            "^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Hosts = new HashSet<string>(
            new[] { "*", "Excel", "Word", "PowerPoint", "Outlook" }, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Suites = new HashSet<string>(
            new[] { "quick", "full", "release" }, StringComparer.Ordinal);
        private static readonly HashSet<string> WorkspacePolicies = new HashSet<string>(
            new[] { "read-only", "runner-owned", "explicit-disposable-copy" }, StringComparer.Ordinal);

        public QualificationPack Parse(string json)
        {
            var root = QualificationJson.ReadObject(json, "Qualification manifest");
            QualificationJson.EnsureOnly(root, new[]
            {
                "schemaVersion", "id", "revision", "title", "description", "hosts", "suite",
                "workspacePolicy", "requirements", "coverage", "steps"
            }, "Qualification manifest");
            if (root["schemaVersion"] == null || root["schemaVersion"].Type != JTokenType.Integer ||
                (int)root["schemaVersion"] != 1)
                throw new QualificationManifestException("schema_version", "schemaVersion must be 1.");

            var id = QualificationJson.RequiredString(root, "id", 96, "Qualification manifest");
            var revision = QualificationJson.RequiredString(root, "revision", 32, "Qualification manifest");
            if (!IdPattern.IsMatch(id))
                throw new QualificationManifestException("pack_id", "Pack id must be a lowercase dotted identifier.");
            if (!RevisionPattern.IsMatch(revision))
                throw new QualificationManifestException("pack_revision", "Pack revision contains unsupported characters.");
            var title = QualificationJson.RequiredString(root, "title", 160, "Qualification manifest");
            var description = QualificationJson.OptionalString(root, "description", 2000, "Qualification manifest");
            var hosts = QualificationJson.StringArray(root, "hosts", 8, 32, true, "Qualification manifest");
            if (hosts.Any(host => !Hosts.Contains(host)) || (hosts.Count > 1 && hosts.Contains("*")))
                throw new QualificationManifestException("pack_hosts", "hosts contains an unsupported or ambiguous host.");
            var suite = QualificationJson.RequiredString(root, "suite", 16, "Qualification manifest").ToLowerInvariant();
            if (!Suites.Contains(suite))
                throw new QualificationManifestException("pack_suite", "suite must be quick, full, or release.");
            var workspace = QualificationJson.RequiredString(root, "workspacePolicy", 40, "Qualification manifest");
            if (!WorkspacePolicies.Contains(workspace))
                throw new QualificationManifestException("workspace_policy", "workspacePolicy is not safe or supported.");
            var requirements = QualificationJson.StringArray(root, "requirements", 64, 96, false, "Qualification manifest");
            var coverage = QualificationJson.StringArray(root, "coverage", 128, 96, true, "Qualification manifest");
            ValidateIdentifiers(requirements, "requirement");
            ValidateIdentifiers(coverage, "coverage");

            var stepTokens = root["steps"] as JArray;
            if (stepTokens == null || stepTokens.Count == 0 || stepTokens.Count > 100)
                throw new QualificationManifestException("step_count", "steps must contain between 1 and 100 entries.");
            var steps = new List<QualificationStep>(stepTokens.Count);
            var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cleanupStarted = false;
            for (var index = 0; index < stepTokens.Count; index++)
            {
                var stepObject = stepTokens[index] as JObject;
                if (stepObject == null)
                    throw new QualificationManifestException("step_type", "Each step must be an object.");
                var step = ParseStep(stepObject, index, knownIds);
                if (!knownIds.Add(step.Id))
                    throw new QualificationManifestException("duplicate_step", "Duplicate step id: " + step.Id + ".");
                if (cleanupStarted && step.Kind != QualificationStepKind.Cleanup)
                    throw new QualificationManifestException("cleanup_order", "cleanup steps must form the final step group.");
                cleanupStarted = cleanupStarted || step.Kind == QualificationStepKind.Cleanup;
                steps.Add(step);
            }
            if (!steps.Any(step => step.Kind == QualificationStepKind.Assertion && step.Required))
                throw new QualificationManifestException("assertion_required", "A pack requires at least one required typed assertion.");
            return new QualificationPack(id, revision, QualificationJson.Sha256(json), title, description,
                Array.AsReadOnly(hosts.ToArray()), suite, workspace,
                Array.AsReadOnly(requirements.ToArray()), Array.AsReadOnly(coverage.ToArray()),
                Array.AsReadOnly(steps.ToArray()));
        }

        private static QualificationStep ParseStep(JObject value, int index, HashSet<string> knownIds)
        {
            var subject = "steps[" + index + "]";
            QualificationJson.EnsureOnly(value, new[]
            {
                "id", "kind", "title", "dependsOn", "instructionKey", "action", "assertion",
                "prompt", "timeoutSeconds", "required"
            }, subject);
            var id = QualificationJson.RequiredString(value, "id", 64, subject);
            if (!IdPattern.IsMatch(id))
                throw new QualificationManifestException("step_id", subject + ".id must be a lowercase identifier.");
            QualificationStepKind kind;
            var kindName = QualificationJson.RequiredString(value, "kind", 32, subject);
            if (!TryStepKind(kindName, out kind))
                throw new QualificationManifestException("step_kind", "Unsupported qualification step kind: " + kindName + ".");
            var title = QualificationJson.OptionalString(value, "title", 160, subject) ?? id;
            var dependsOn = QualificationJson.StringArray(value, "dependsOn", 16, 64, false, subject);
            foreach (var dependency in dependsOn)
            {
                if (!knownIds.Contains(dependency))
                    throw new QualificationManifestException("step_dependency",
                        subject + " dependency must reference an earlier step: " + dependency + ".");
            }
            var instruction = QualificationJson.OptionalString(value, "instructionKey", 128, subject);
            var action = QualificationJson.OptionalString(value, "action", 128, subject);
            var assertion = QualificationJson.OptionalString(value, "assertion", 128, subject);
            var prompt = QualificationJson.OptionalString(value, "prompt", 8000, subject);
            ValidateConditionalFields(subject, kind, instruction, action, assertion, prompt);
            var timeout = value["timeoutSeconds"] == null ? 120 : value["timeoutSeconds"].Type == JTokenType.Integer
                ? (int)value["timeoutSeconds"] : -1;
            if (timeout < 1 || timeout > 1800)
                throw new QualificationManifestException("step_timeout", subject + ".timeoutSeconds must be between 1 and 1800.");
            var required = value["required"] == null ? true : value["required"].Type == JTokenType.Boolean
                ? (bool)value["required"] : throw new QualificationManifestException("step_required", subject + ".required must be boolean.");
            return new QualificationStep(id, kind, title, Array.AsReadOnly(dependsOn.ToArray()), instruction,
                action, assertion, prompt, timeout, required);
        }

        private static void ValidateConditionalFields(string subject, QualificationStepKind kind,
            string instruction, string action, string assertion, string prompt)
        {
            if (kind == QualificationStepKind.UserAction)
            {
                if (instruction == null || action != null || assertion != null || prompt != null)
                    throw new QualificationManifestException("step_shape", subject + " userAction requires only instructionKey.");
                return;
            }
            if (kind == QualificationStepKind.Assertion)
            {
                if (assertion == null || action != null || instruction != null || prompt != null)
                    throw new QualificationManifestException("step_shape", subject + " assertion requires only assertion.");
                return;
            }
            if (kind == QualificationStepKind.AgentTask)
            {
                if (prompt == null || action != null || assertion != null || instruction != null)
                    throw new QualificationManifestException("step_shape", subject + " agentTask requires only prompt.");
                return;
            }
            if (action == null || assertion != null || instruction != null || prompt != null)
                throw new QualificationManifestException("step_shape", subject + " requires only an allowlisted action.");
        }

        private static bool TryStepKind(string value, out QualificationStepKind kind)
        {
            switch (value)
            {
                case "precondition": kind = QualificationStepKind.Precondition; return true;
                case "fixture": kind = QualificationStepKind.Fixture; return true;
                case "agentTask": kind = QualificationStepKind.AgentTask; return true;
                case "hostProbe": kind = QualificationStepKind.HostProbe; return true;
                case "userAction": kind = QualificationStepKind.UserAction; return true;
                case "confirmation": kind = QualificationStepKind.Confirmation; return true;
                case "restart": kind = QualificationStepKind.Restart; return true;
                case "fault": kind = QualificationStepKind.Fault; return true;
                case "assertion": kind = QualificationStepKind.Assertion; return true;
                case "cleanup": kind = QualificationStepKind.Cleanup; return true;
                default: kind = default(QualificationStepKind); return false;
            }
        }

        internal static string StepKindName(QualificationStepKind kind)
        {
            switch (kind)
            {
                case QualificationStepKind.Precondition: return "precondition";
                case QualificationStepKind.Fixture: return "fixture";
                case QualificationStepKind.AgentTask: return "agentTask";
                case QualificationStepKind.HostProbe: return "hostProbe";
                case QualificationStepKind.UserAction: return "userAction";
                case QualificationStepKind.Confirmation: return "confirmation";
                case QualificationStepKind.Restart: return "restart";
                case QualificationStepKind.Fault: return "fault";
                case QualificationStepKind.Assertion: return "assertion";
                case QualificationStepKind.Cleanup: return "cleanup";
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        internal static string OutcomeName(QualificationStepOutcome outcome)
        {
            switch (outcome)
            {
                case QualificationStepOutcome.NotRun: return "not_run";
                case QualificationStepOutcome.Running: return "running";
                case QualificationStepOutcome.AwaitingUser: return "awaiting_user";
                case QualificationStepOutcome.Passed: return "passed";
                case QualificationStepOutcome.Failed: return "failed";
                case QualificationStepOutcome.Blocked: return "blocked";
                case QualificationStepOutcome.Cancelled: return "cancelled";
                case QualificationStepOutcome.Unknown: return "unknown";
                default: throw new ArgumentOutOfRangeException(nameof(outcome));
            }
        }

        internal static QualificationStepOutcome ParseOutcome(string value)
        {
            foreach (QualificationStepOutcome outcome in Enum.GetValues(typeof(QualificationStepOutcome)))
                if (string.Equals(OutcomeName(outcome), value, StringComparison.Ordinal)) return outcome;
            throw new QualificationManifestException("event_outcome", "Unsupported qualification outcome: " + value + ".");
        }

        internal static string RunStatusName(QualificationRunStatus status)
        {
            switch (status)
            {
                case QualificationRunStatus.Ready: return "ready";
                case QualificationRunStatus.Running: return "running";
                case QualificationRunStatus.AwaitingUser: return "awaiting_user";
                case QualificationRunStatus.Verifying: return "verifying";
                case QualificationRunStatus.Passed: return "passed";
                case QualificationRunStatus.Failed: return "failed";
                case QualificationRunStatus.Blocked: return "blocked";
                case QualificationRunStatus.Cancelled: return "cancelled";
                default: throw new ArgumentOutOfRangeException(nameof(status));
            }
        }

        internal static QualificationRunStatus ParseRunStatus(string value)
        {
            foreach (QualificationRunStatus status in Enum.GetValues(typeof(QualificationRunStatus)))
                if (string.Equals(RunStatusName(status), value, StringComparison.Ordinal)) return status;
            throw new QualificationManifestException("event_status", "Unsupported qualification run status: " + value + ".");
        }

        private static void ValidateIdentifiers(IEnumerable<string> values, string subject)
        {
            foreach (var value in values)
                if (!Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9]*(?:[._-][A-Za-z0-9]+)*$", RegexOptions.CultureInvariant))
                    throw new QualificationManifestException(subject + "_id", "Invalid " + subject + " id: " + value + ".");
        }
    }
}

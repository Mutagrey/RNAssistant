using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal sealed class PromptSettingsService
    {
        internal const int MaximumPromptCharacters = 100000;
        private const int PreparedContractVersion = 1;

        private static readonly string[] EditableFields =
        {
            "systemPrompt",
            "agentToolsPrompt",
            "agentSkillsPrompt",
            "chatSystemPrompt",
            "planSystemPrompt",
            "systemPromptRole",
            "contextCompactionPrompt",
            "chatTitlePrompt",
            "attachmentAnalysisPrompt"
        };

        private readonly Func<AppSettings> _loadSettings;
        private readonly Action<AppSettings> _saveSettings;

        internal PromptSettingsService(
            Func<AppSettings> loadSettings,
            Action<AppSettings> saveSettings)
        {
            _loadSettings = loadSettings;
            _saveSettings = saveSettings;
        }

        internal bool CanRead { get { return _loadSettings != null; } }

        internal PromptToolOutcome Read(
            IDictionary<string, object> arguments)
        {
            if (_loadSettings == null)
            {
                return PromptToolOutcome.Error(
                    "Prompt settings store is not available.", null,
                    "prompt_settings_unavailable", false);
            }
            var current = _loadSettings();
            if (ToolArgumentReader.Boolean(
                arguments, "includeDefaults", false))
            {
                return PromptToolOutcome.Ok(
                    "RNAssistant prompt templates and defaults read.",
                    JsonConvert.SerializeObject(new
                    {
                        current = ToPayload(current),
                        defaults = ToPayload(new AppSettings())
                    }), PromptToolEffect.None);
            }
            return PromptToolOutcome.Ok(
                "RNAssistant prompt templates read.",
                JsonConvert.SerializeObject(ToPayload(current)),
                PromptToolEffect.None);
        }

        internal PromptSavePreparation PrepareSave(
            IDictionary<string, object> arguments)
        {
            var validation = ValidateSave(arguments);
            if (validation != null)
                return new PromptSavePreparation(validation);
            var source = _loadSettings() ?? new AppSettings();
            var fields = SuppliedFields(arguments);
            var intended = Apply(source, arguments);
            var beforeHash = Hash(TargetPayload(source, fields));
            var intendedHash = Hash(TargetPayload(intended, fields));
            var prepared = new JObject
            {
                ["version"] = PreparedContractVersion,
                ["fields"] = new JArray(fields),
                ["argumentsSha256"] = Hash(ArgumentPayload(arguments)),
                ["beforeSha256"] = beforeHash
            }.ToString(Formatting.None);
            var preview = new JObject
            {
                ["type"] = "rnassistant.promptSavePreview",
                ["version"] = 1,
                ["fields"] = new JArray(fields),
                ["changed"] = !string.Equals(
                    beforeHash, intendedHash, StringComparison.Ordinal)
            }.ToString(Formatting.None);
            return new PromptSavePreparation(
                PromptToolOutcome.Ok(
                    "Confirmation required to save RNAssistant prompt templates.",
                    preview, PromptToolEffect.None),
                prepared);
        }

        internal PromptToolOutcome Save(
            IDictionary<string, object> arguments,
            string preparedStateJson,
            Action markDispatchPossible)
        {
            var validation = ValidateSave(arguments);
            if (validation != null) return validation;
            JObject prepared;
            try
            {
                prepared = JObject.Parse(preparedStateJson ?? string.Empty);
            }
            catch (JsonException)
            {
                return PromptToolOutcome.Error(
                    "Prompt save preparation is invalid.", null,
                    "prompt_preparation_invalid", false);
            }
            var fields = SuppliedFields(arguments);
            if (prepared.Value<int?>("version") != PreparedContractVersion ||
                !JToken.DeepEquals(prepared["fields"], new JArray(fields)) ||
                !string.Equals((string)prepared["argumentsSha256"],
                    Hash(ArgumentPayload(arguments)), StringComparison.Ordinal))
            {
                return PromptToolOutcome.Error(
                    "Prompt save preparation does not match the accepted call.",
                    null, "prompt_preparation_mismatch", false);
            }

            var source = _loadSettings() ?? new AppSettings();
            if (!string.Equals((string)prepared["beforeSha256"],
                Hash(TargetPayload(source, fields)), StringComparison.Ordinal))
            {
                return PromptToolOutcome.Error(
                    "Prompt settings changed after confirmation was requested. Read them again before retrying.",
                    null, "prompt_settings_changed", true);
            }
            var intended = Apply(source, arguments);
            var intendedHash = Hash(TargetPayload(intended, fields));
            if (string.Equals((string)prepared["beforeSha256"],
                intendedHash, StringComparison.Ordinal))
            {
                return PromptToolOutcome.Ok(
                    "RNAssistant prompt templates are already up to date.",
                    JsonConvert.SerializeObject(ToPayload(source)),
                    PromptToolEffect.VerifiedNoChange);
            }

            if (markDispatchPossible != null) markDispatchPossible();
            _saveSettings(intended);
            var saved = _loadSettings() ?? new AppSettings();
            if (!string.Equals(intendedHash,
                Hash(TargetPayload(saved, fields)), StringComparison.Ordinal))
            {
                return PromptToolOutcome.Unknown(
                    "Prompt settings did not verify after save. Inspect current settings before retrying.",
                    JsonConvert.SerializeObject(new
                    {
                        fields = fields,
                        expectedSha256 = intendedHash,
                        actualSha256 = Hash(TargetPayload(saved, fields))
                    }),
                    "prompt_settings_verification_failed");
            }
            return PromptToolOutcome.Ok(
                "RNAssistant prompt templates saved.",
                JsonConvert.SerializeObject(ToPayload(saved)),
                PromptToolEffect.VerifiedChange);
        }

        private PromptToolOutcome ValidateSave(
            IDictionary<string, object> arguments)
        {
            var fields = SuppliedFields(arguments);
            if (fields.Length == 0)
            {
                return PromptToolOutcome.Error(
                    "Prompt save requires at least one supplied prompt field.",
                    null, "prompt_update_empty", true);
            }
            if (_loadSettings == null)
            {
                return PromptToolOutcome.Error(
                    "Prompt settings store is not available.", null,
                    "prompt_settings_unavailable", false);
            }
            if (_saveSettings == null)
            {
                return PromptToolOutcome.Error(
                    "Prompt settings store is read-only.", null,
                    "prompt_settings_read_only", false);
            }
            if (fields.Where(field => !string.Equals(
                    field, "systemPromptRole", StringComparison.Ordinal))
                .Select(field => ToolArgumentReader.String(
                    arguments, field, string.Empty))
                .Any(value => (value ?? string.Empty).Length >
                    MaximumPromptCharacters))
            {
                return PromptToolOutcome.Error(
                    "Prompt template exceeds the 100000 character limit.",
                    null, "prompt_too_large", false);
            }
            return null;
        }

        private static AppSettings Apply(
            AppSettings source,
            IDictionary<string, object> arguments)
        {
            var settings = (source ?? new AppSettings()).Clone();
            foreach (var field in SuppliedFields(arguments))
            {
                SetValue(settings, field, ToolArgumentReader.String(
                    arguments, field, string.Empty));
            }
            return settings;
        }

        private static string[] SuppliedFields(
            IDictionary<string, object> arguments)
        {
            return EditableFields.Where(field => arguments != null &&
                    arguments.ContainsKey(field))
                .ToArray();
        }

        private static JObject ArgumentPayload(
            IDictionary<string, object> arguments)
        {
            var result = new JObject();
            foreach (var field in SuppliedFields(arguments))
                result[field] = ToolArgumentReader.String(
                    arguments, field, string.Empty);
            return result;
        }

        private static JObject TargetPayload(
            AppSettings settings,
            IEnumerable<string> fields)
        {
            var result = new JObject();
            foreach (var field in fields ?? new string[0])
                result[field] = Value(settings ?? new AppSettings(), field);
            return result;
        }

        private static void SetValue(
            AppSettings settings, string field, string value)
        {
            switch (field)
            {
                case "systemPrompt": settings.SystemPrompt = value; break;
                case "agentToolsPrompt": settings.AgentToolsPrompt = value; break;
                case "agentSkillsPrompt": settings.AgentSkillsPrompt = value; break;
                case "chatSystemPrompt": settings.ChatSystemPrompt = value; break;
                case "planSystemPrompt": settings.PlanSystemPrompt = value; break;
                case "systemPromptRole": settings.SystemPromptRole =
                    NormalizePromptRole(value); break;
                case "contextCompactionPrompt":
                    settings.ContextCompactionPrompt = value; break;
                case "chatTitlePrompt": settings.ChatTitlePrompt = value; break;
                case "attachmentAnalysisPrompt":
                    settings.AttachmentAnalysisPrompt = value; break;
            }
        }

        private static string Value(AppSettings settings, string field)
        {
            switch (field)
            {
                case "systemPrompt": return settings.SystemPrompt;
                case "agentToolsPrompt": return settings.AgentToolsPrompt;
                case "agentSkillsPrompt": return settings.AgentSkillsPrompt;
                case "chatSystemPrompt": return settings.ChatSystemPrompt;
                case "planSystemPrompt": return settings.PlanSystemPrompt;
                case "systemPromptRole": return settings.SystemPromptRole;
                case "contextCompactionPrompt":
                    return settings.ContextCompactionPrompt;
                case "chatTitlePrompt": return settings.ChatTitlePrompt;
                case "attachmentAnalysisPrompt":
                    return settings.AttachmentAnalysisPrompt;
                default: return string.Empty;
            }
        }

        private static object ToPayload(AppSettings settings)
        {
            settings = settings ?? new AppSettings();
            return new
            {
                format = "markdown",
                systemPrompt = settings.SystemPrompt,
                agentToolsPrompt = settings.AgentToolsPrompt,
                agentSkillsPrompt = settings.AgentSkillsPrompt,
                chatSystemPrompt = settings.ChatSystemPrompt,
                planSystemPrompt = settings.PlanSystemPrompt,
                systemPromptRole = settings.SystemPromptRole,
                contextCompactionPrompt = settings.ContextCompactionPrompt,
                chatTitlePrompt = settings.ChatTitlePrompt,
                attachmentAnalysisPrompt = settings.AttachmentAnalysisPrompt
            };
        }

        private static string NormalizePromptRole(string value)
        {
            if (string.Equals(value, "system",
                StringComparison.OrdinalIgnoreCase)) return "system";
            if (string.Equals(value, "user",
                StringComparison.OrdinalIgnoreCase)) return "user";
            return "developer";
        }

        private static string Hash(JToken value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(
                        Encoding.UTF8.GetBytes((value ?? JValue.CreateNull())
                            .ToString(Formatting.None))))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }
    }
}

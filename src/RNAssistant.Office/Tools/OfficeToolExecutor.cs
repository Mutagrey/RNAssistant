using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Tools
{
    public sealed class OfficeToolExecutor
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly PipelineToolExecutor _pipelineExecutor;
        private readonly VbaToolExecutor _vbaExecutor;
        private readonly SkillToolExecutor _skillExecutor;
        private readonly ToolAuthoringExecutor _toolAuthoringExecutor;
        private readonly PromptToolExecutor _promptToolExecutor;
        private readonly HtmlArtifactToolExecutor _htmlArtifactExecutor;

        public OfficeToolExecutor(
            IOfficeApplicationAdapter adapter,
            VbaBackupStore vbaBackupStore,
            SkillStore skillStore,
            ToolStore toolStore = null,
            Func<AppSettings> loadSettings = null,
            Action<AppSettings> saveSettings = null)
        {
            _adapter = adapter;
            _pipelineExecutor = new PipelineToolExecutor();
            _vbaExecutor = new VbaToolExecutor(adapter, vbaBackupStore);
            _skillExecutor = new SkillToolExecutor(adapter, skillStore);
            _toolAuthoringExecutor = new ToolAuthoringExecutor(adapter, toolStore);
            _promptToolExecutor = new PromptToolExecutor(loadSettings, saveSettings);
            _htmlArtifactExecutor = new HtmlArtifactToolExecutor();
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            return _vbaExecutor.GetControllerTools()
                .Concat(_skillExecutor.GetControllerTools())
                .Concat(_toolAuthoringExecutor.GetControllerTools())
                .Concat(_promptToolExecutor.GetControllerTools())
                .Concat(_htmlArtifactExecutor.GetControllerTools());
        }

        public ToolResult Execute(ToolCommand command, IReadOnlyList<ToolDefinition> skills, AppSettings settings, bool dryRun, bool manualRun, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Execute(command, skills, settings, dryRun, manualRun, null, cancellationToken);
        }

        public ToolResult Execute(ToolCommand command, IReadOnlyList<ToolDefinition> skills, AppSettings settings, bool dryRun, bool manualRun, ChatSession session, CancellationToken cancellationToken = default(CancellationToken))
        {
            return ExecuteCommand(command, skills, settings, 0, dryRun, manualRun, session, cancellationToken);
        }

        public string VbaToolId(string suffix)
        {
            return _vbaExecutor.ToolId(suffix);
        }

        public ToolResult ValidateToolDefinition(ToolDefinition tool)
        {
            return ToolAuthoringExecutor.ValidateToolDefinition(tool);
        }

        private ToolResult ExecuteCommand(ToolCommand command, IReadOnlyList<ToolDefinition> skills, AppSettings settings, int depth, bool dryRun, bool manualRun, ChatSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            settings = settings ?? new AppSettings();
            if (command == null || string.IsNullOrWhiteSpace(command.ToolId))
            {
                return ToolResult.Fail("Tool command is empty.");
            }

            if (depth > 8)
            {
                return ToolResult.Fail("Pipeline nesting limit exceeded.");
            }

            var knownTools = KnownTools(skills).ToList();
            var tool = FindTool(knownTools, command.ToolId);
            if (tool == null)
            {
                return UnknownTool(command.ToolId, knownTools);
            }
            if (!tool.Enabled)
            {
                return DisabledTool(command.ToolId, knownTools);
            }

            var customTool = tool != null && !tool.BuiltIn ? tool : null;
            var effectiveTool = EffectiveTool(tool, knownTools);

            if (ToolSafetyPolicy.RequiresConfirmation(effectiveTool, settings, dryRun, manualRun))
            {
                return ToolResult.WaitingConfirmation("Tool requires confirmation before execution: " + command.ToolId);
            }

            if (customTool != null && string.Equals(customTool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase))
            {
                return _pipelineExecutor.Execute(
                    customTool,
                    command,
                    skills,
                    settings,
                    depth + 1,
                    dryRun,
                    manualRun,
                    (nested, nestedSkills, nestedSettings, nestedDepth, nestedDryRun, nestedManualRun, nestedCancellationToken) =>
                        ExecuteCommand(nested, nestedSkills, nestedSettings, nestedDepth, nestedDryRun, nestedManualRun, session, nestedCancellationToken),
                    cancellationToken);
            }

            if (customTool != null && string.Equals(customTool.Executor, "vba", StringComparison.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _vbaExecutor.ExecuteCustomTool(customTool, command, settings, dryRun, manualRun);
            }

            if (customTool != null)
            {
                return ToolResult.Fail("Tool executor is not runnable yet: " + customTool.Executor);
            }

            if (_vbaExecutor.IsControllerTool(command.ToolId))
            {
                return _vbaExecutor.ExecuteControllerTool(
                    command,
                    skills,
                    settings,
                    dryRun,
                    manualRun,
                    (nested, nestedSkills, nestedSettings, nestedDepth, nestedDryRun, nestedManualRun, nestedCancellationToken) =>
                        ExecuteCommand(nested, nestedSkills, nestedSettings, nestedDepth, nestedDryRun, nestedManualRun, session, nestedCancellationToken),
                    cancellationToken);
            }

            if (_skillExecutor.IsControllerTool(command.ToolId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _skillExecutor.ExecuteControllerTool(command, settings, dryRun, manualRun);
            }

            if (_toolAuthoringExecutor.IsControllerTool(command.ToolId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _toolAuthoringExecutor.ExecuteControllerTool(command, settings, dryRun, manualRun);
            }

            if (_promptToolExecutor.IsControllerTool(command.ToolId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _promptToolExecutor.ExecuteControllerTool(command, dryRun);
            }

            if (_htmlArtifactExecutor.IsControllerTool(command.ToolId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _htmlArtifactExecutor.ExecuteControllerTool(command, settings, session, dryRun);
            }

            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would execute " + command.ToolId, JsonConvert.SerializeObject(command.Arguments));
            }

            if (string.Equals(command.ToolId, VbaToolId("vba_replace_module"), StringComparison.OrdinalIgnoreCase))
            {
                _vbaExecutor.BackupModuleBeforeReplace(command, settings);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return _adapter.ExecuteTool(command);
        }

        private static ToolDefinition EffectiveTool(ToolDefinition tool, IReadOnlyList<ToolDefinition> knownTools)
        {
            if (tool == null || tool.MutatesDocument || !ToolSafetyPolicy.EffectiveMutatesDocument(tool, knownTools))
            {
                return tool;
            }

            return new ToolDefinition
            {
                Id = tool.Id,
                Host = tool.Host,
                Name = tool.Name,
                Description = tool.Description,
                ArgumentSchemaJson = tool.ArgumentSchemaJson,
                Executor = tool.Executor,
                RequiresConfirmation = tool.RequiresConfirmation,
                MutatesDocument = true,
                AgentCanRun = tool.AgentCanRun,
                PipelineJson = tool.PipelineJson,
                Code = tool.Code,
                Readme = tool.Readme,
                StoragePath = tool.StoragePath,
                Enabled = tool.Enabled,
                BuiltIn = tool.BuiltIn,
                RiskLevel = tool.RiskLevel,
                UseWhen = tool.UseWhen,
                DoNotUseWhen = tool.DoNotUseWhen,
                ExamplesJson = tool.ExamplesJson,
                PreconditionsJson = tool.PreconditionsJson,
                VerifyJson = tool.VerifyJson,
                CapabilityStatus = tool.CapabilityStatus,
                Limitations = tool.Limitations,
                ReplacementToolId = tool.ReplacementToolId
            };
        }

        private IEnumerable<ToolDefinition> KnownTools(IEnumerable<ToolDefinition> providedTools)
        {
            var result = new List<ToolDefinition>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddTools(result, seen, providedTools);
            AddTools(result, seen, _adapter.GetBuiltInTools());
            AddTools(result, seen, _vbaExecutor.GetControllerTools());
            AddTools(result, seen, _skillExecutor.GetControllerTools());
            AddTools(result, seen, _toolAuthoringExecutor.GetControllerTools());
            AddTools(result, seen, _promptToolExecutor.GetControllerTools());
            AddTools(result, seen, _htmlArtifactExecutor.GetControllerTools());
            return result;
        }

        private static void AddTools(ICollection<ToolDefinition> result, ISet<string> seen, IEnumerable<ToolDefinition> tools)
        {
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                if (tool == null || string.IsNullOrWhiteSpace(tool.Id) || seen.Contains(tool.Id))
                {
                    continue;
                }

                seen.Add(tool.Id);
                result.Add(tool);
            }
        }

        private static ToolDefinition FindTool(IEnumerable<ToolDefinition> tools, string toolId)
        {
            return (tools ?? new ToolDefinition[0]).FirstOrDefault(s =>
                string.Equals(s.Id, toolId, StringComparison.OrdinalIgnoreCase));
        }

        private static ToolResult UnknownTool(string requestedToolId, IReadOnlyList<ToolDefinition> knownTools)
        {
            var suggestions = SuggestToolIds(requestedToolId, knownTools);
            var message = "Unknown tool id: " + requestedToolId + ". Use only available tool ids.";
            if (suggestions.Count > 0)
            {
                message += " Did you mean: " + string.Join(", ", suggestions.ToArray()) + "?";
            }

            return ToolResult.Fail(message, ToolDiagnosticJson(requestedToolId, knownTools, suggestions, false));
        }

        private static ToolResult DisabledTool(string requestedToolId, IReadOnlyList<ToolDefinition> knownTools)
        {
            return ToolResult.Fail(
                "Tool is disabled: " + requestedToolId + ". Enable it or use another available tool id.",
                ToolDiagnosticJson(requestedToolId, knownTools, new List<string>(), true));
        }

        private static string ToolDiagnosticJson(string requestedToolId, IReadOnlyList<ToolDefinition> knownTools, IReadOnlyList<string> suggestions, bool disabled)
        {
            return JsonConvert.SerializeObject(new
            {
                requestedToolId = requestedToolId,
                disabled = disabled,
                suggestions = suggestions ?? new string[0],
                availableToolIds = (knownTools ?? new ToolDefinition[0])
                    .Where(tool => tool != null && tool.Enabled && !string.IsNullOrWhiteSpace(tool.Id))
                    .Select(tool => tool.Id)
                    .ToArray()
            });
        }

        private static List<string> SuggestToolIds(string requestedToolId, IReadOnlyList<ToolDefinition> knownTools)
        {
            var requestedTokens = ExpandedTokens(Tokenize(requestedToolId));
            if (requestedTokens.Count == 0)
            {
                return new List<string>();
            }

            return (knownTools ?? new ToolDefinition[0])
                .Where(tool => tool != null && tool.Enabled && !string.IsNullOrWhiteSpace(tool.Id))
                .Select(tool => new { Tool = tool, Score = SuggestionScore(requestedTokens, tool) })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Tool.Id.Length)
                .Take(5)
                .Select(item => item.Tool.Id)
                .ToList();
        }

        private static int SuggestionScore(ISet<string> requestedTokens, ToolDefinition tool)
        {
            var candidateTokens = ExpandedTokens(Tokenize(
                (tool.Id ?? string.Empty) + " " +
                (tool.Name ?? string.Empty) + " " +
                (tool.Description ?? string.Empty)));
            var score = 0;
            foreach (var token in requestedTokens)
            {
                if (candidateTokens.Contains(token))
                {
                    score += token.Length <= 2 ? 1 : 3;
                }
            }

            return score;
        }

        private static ISet<string> ExpandedTokens(IEnumerable<string> tokens)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens ?? new string[0])
            {
                AddExpandedToken(result, token);
            }

            return result;
        }

        private static void AddExpandedToken(ISet<string> result, string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var value = token.Trim().ToLowerInvariant();
            result.Add(value);
            if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase) && value.Length > 3)
            {
                result.Add(value.Substring(0, value.Length - 1));
            }

            if (string.Equals(value, "create", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "make", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "new", StringComparison.OrdinalIgnoreCase))
            {
                result.Add("add");
            }
            if (string.Equals(value, "worksheet", StringComparison.OrdinalIgnoreCase))
            {
                result.Add("sheet");
            }
            if (string.Equals(value, "diagram", StringComparison.OrdinalIgnoreCase))
            {
                result.Add("chart");
            }
            if (string.Equals(value, "delete", StringComparison.OrdinalIgnoreCase))
            {
                result.Add("remove");
            }
        }

        private static IEnumerable<string> Tokenize(string value)
        {
            var token = string.Empty;
            var previousWasLower = false;
            foreach (var ch in value ?? string.Empty)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    if (char.IsUpper(ch) && previousWasLower && token.Length > 0)
                    {
                        yield return token;
                        token = string.Empty;
                    }

                    token += char.ToLowerInvariant(ch);
                    previousWasLower = char.IsLower(ch);
                    continue;
                }

                if (token.Length > 0)
                {
                    yield return token;
                    token = string.Empty;
                }
                previousWasLower = false;
            }

            if (token.Length > 0)
            {
                yield return token;
            }
        }
    }
}

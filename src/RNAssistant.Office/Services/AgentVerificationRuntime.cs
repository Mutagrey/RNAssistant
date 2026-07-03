using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class VerificationRunner
    {
        public IEnumerable<ToolCommand> BuildVerificationCommands(ToolCommand command, ToolDefinition tool, IReadOnlyList<ToolDefinition> allTools)
        {
            if (command == null || tool == null || !tool.MutatesDocument)
            {
                yield break;
            }

            ToolCommand explicitCommand;
            if (TryBuildExplicitVerification(command, tool, out explicitCommand) &&
                HasReadOnlyTool(allTools, explicitCommand.ToolId))
            {
                yield return explicitCommand;
                yield break;
            }

            var host = tool.Host ?? string.Empty;
            if (string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                if (HasReadOnlyTool(allTools, "excel.list_sheets") &&
                    (string.Equals(command.ToolId, "excel.add_sheet", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(command.ToolId, "excel.rename_sheet", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return new ToolCommand { ToolId = "excel.list_sheets", Description = "Deterministic verification" };
                    yield break;
                }
                if (HasReadOnlyTool(allTools, "excel.list_charts") && Contains(command.ToolId, "chart"))
                {
                    yield return CopyArgs(new ToolCommand { ToolId = "excel.list_charts", Description = "Deterministic verification" }, command, "sheet");
                    yield break;
                }
                if (HasReadOnlyTool(allTools, "excel.read_range") &&
                    (command.Arguments.ContainsKey("address") ||
                     command.Arguments.ContainsKey("range") ||
                     command.Arguments.ContainsKey("sourceRange") ||
                     command.Arguments.ContainsKey("startAddress")))
                {
                    var verify = new ToolCommand { ToolId = "excel.read_range", Description = "Deterministic verification" };
                    CopyArg(command, verify, "sheet");
                    var address = FirstArg(command, "address", "range", "sourceRange", "startAddress");
                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        verify.Arguments["address"] = address;
                    }
                    yield return verify;
                    yield break;
                }
                if (HasReadOnlyTool(allTools, "excel.workbook_summary"))
                {
                    yield return new ToolCommand { ToolId = "excel.workbook_summary", Description = "Deterministic verification" };
                    yield break;
                }
            }

            if (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase) && HasReadOnlyTool(allTools, "word.read_document"))
            {
                var verify = new ToolCommand { ToolId = "word.read_document", Description = "Deterministic verification" };
                verify.Arguments["maxChars"] = 12000;
                yield return verify;
                yield break;
            }

            if (string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase) && HasReadOnlyTool(allTools, "powerpoint.read_slides"))
            {
                var verify = new ToolCommand { ToolId = "powerpoint.read_slides", Description = "Deterministic verification" };
                verify.Arguments["maxSlides"] = 20;
                yield return verify;
                yield break;
            }

            if (string.Equals(host, "Outlook", StringComparison.OrdinalIgnoreCase) && HasReadOnlyTool(allTools, "outlook.get_context"))
            {
                yield return new ToolCommand { ToolId = "outlook.get_context", Description = "Deterministic verification" };
            }
        }

        private static bool TryBuildExplicitVerification(ToolCommand command, ToolDefinition tool, out ToolCommand verify)
        {
            verify = null;
            if (string.IsNullOrWhiteSpace(tool.VerifyJson))
            {
                return false;
            }
            try
            {
                var root = JObject.Parse(tool.VerifyJson);
                var toolId = (string)root["toolId"];
                if (string.IsNullOrWhiteSpace(toolId))
                {
                    return false;
                }
                verify = new ToolCommand { ToolId = toolId, Description = "Deterministic verification" };
                var args = root["argumentsFrom"] as JObject;
                if (args != null)
                {
                    foreach (var property in args.Properties())
                    {
                        var source = (property.Value.Value<string>() ?? string.Empty).Replace("previous.arguments.", string.Empty);
                        if (command.Arguments.ContainsKey(source))
                        {
                            verify.Arguments[property.Name] = command.Arguments[source];
                        }
                    }
                }
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool HasReadOnlyTool(IEnumerable<ToolDefinition> tools, string id)
        {
            return (tools ?? new ToolDefinition[0]).Any(t =>
                t != null &&
                t.Enabled &&
                !t.MutatesDocument &&
                !t.MutatesLocalState &&
                !t.RequiresConfirmation &&
                t.RiskLevel == 0 &&
                t.AgentCanRun &&
                string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static bool Contains(string value, string term)
        {
            return (value ?? string.Empty).IndexOf(term ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ToolCommand CopyArgs(ToolCommand target, ToolCommand source, params string[] names)
        {
            foreach (var name in names ?? new string[0])
            {
                CopyArg(source, target, name);
            }
            return target;
        }

        private static void CopyArg(ToolCommand source, ToolCommand target, string name)
        {
            if (source != null && target != null && source.Arguments.ContainsKey(name))
            {
                target.Arguments[name] = source.Arguments[name];
            }
        }

        private static string FirstArg(ToolCommand command, params string[] names)
        {
            foreach (var name in names ?? new string[0])
            {
                if (command != null && command.Arguments.ContainsKey(name) && command.Arguments[name] != null)
                {
                    return Convert.ToString(command.Arguments[name]);
                }
            }
            return null;
        }
    }

    internal sealed class VerificationExecutionResult
    {
        public ToolResult Result { get; set; }
        public bool TimedOut { get; set; }
    }

    internal sealed class VerificationExecutor
    {
        private readonly TimeSpan _timeout;

        public VerificationExecutor(TimeSpan timeout)
        {
            _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : timeout;
        }

        public async Task<VerificationExecutionResult> ExecuteAsync(
            string toolId,
            Func<ToolResult> execute,
            CancellationToken cancellationToken)
        {
            if (execute == null)
            {
                throw new ArgumentNullException("execute");
            }

            var execution = Task.Run(execute);
            var timeout = Task.Delay(_timeout, cancellationToken);
            var completed = await Task.WhenAny(execution, timeout).ConfigureAwait(false);
            if (completed == execution)
            {
                return new VerificationExecutionResult
                {
                    Result = await execution.ConfigureAwait(false)
                };
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = execution.ContinueWith(
                task =>
                {
                    var ignored = task.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return new VerificationExecutionResult
            {
                TimedOut = true,
                Result = ToolResult.Fail(
                    "Deterministic verification timed out after " +
                    Math.Max(1, Convert.ToInt32(Math.Ceiling(_timeout.TotalSeconds))) +
                    " seconds while running " + (toolId ?? string.Empty) + ". The mutation completed, but verification did not.")
            };
        }
    }

    internal sealed class RecipeExpander
    {
        public IEnumerable<ToolCommand> Expand(ToolCommand recipe, IReadOnlyList<AgentObservation> observations)
        {
            if (recipe == null || !string.Equals(recipe.ToolId, "recipe.excel.make_table_pretty", StringComparison.OrdinalIgnoreCase))
            {
                yield return recipe;
                yield break;
            }

            var range = FindRange(observations);
            var format = new ToolCommand { ToolId = "excel.format_range", Description = "Apply clean table formatting" };
            format.Arguments["sheet"] = "active";
            format.Arguments["address"] = string.IsNullOrWhiteSpace(range) ? "used_range" : range;
            format.Arguments["bold"] = true;
            format.Arguments["horizontalAlignment"] = "center";
            yield return format;

            var autofit = new ToolCommand { ToolId = "excel.autofit", Description = "Autofit formatted table" };
            autofit.Arguments["sheet"] = "active";
            autofit.Arguments["address"] = string.IsNullOrWhiteSpace(range) ? string.Empty : range;
            yield return autofit;
        }

        private static string FindRange(IEnumerable<AgentObservation> observations)
        {
            foreach (var observation in observations ?? new AgentObservation[0])
            {
                var text = (observation == null ? string.Empty : observation.Summary + " " + observation.FactsJson) ?? string.Empty;
                var match = System.Text.RegularExpressions.Regex.Match(text, "[A-Z]{1,3}[0-9]+:[A-Z]{1,3}[0-9]+");
                if (match.Success)
                {
                    return match.Value;
                }
            }
            return null;
        }
    }
}

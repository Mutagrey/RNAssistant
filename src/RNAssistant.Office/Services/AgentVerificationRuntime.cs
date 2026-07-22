using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class VerificationRunner
    {
        public IEnumerable<ToolCommand> BuildVerificationCommands(ToolCommand command, ToolDefinition tool, IReadOnlyList<ToolDefinition> allTools, ToolResult mutationResult = null)
        {
            if (command == null || tool == null || !tool.MutatesDocument)
            {
                yield break;
            }

            var resultVerification = BuildResultVerification(mutationResult);
            if (resultVerification != null)
            {
                if (HasReadOnlyTool(allTools, resultVerification.ToolId))
                {
                    yield return resultVerification;
                }
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
            var vbaReadToolId = VbaReadToolId(host);
            if (Contains(command.ToolId, "vba_") &&
                !Contains(command.ToolId, "run_macro") &&
                command.Arguments.ContainsKey("moduleName") &&
                HasReadOnlyTool(allTools, vbaReadToolId))
            {
                var verify = CopyArgs(new ToolCommand { ToolId = vbaReadToolId, Description = "Deterministic VBA verification" }, command, "moduleName");
                if (command.Arguments.ContainsKey("code"))
                {
                    verify.Arguments["__expectedCodeSha256"] = VbaToolExecutor.CodeSha256(Convert.ToString(command.Arguments["code"]));
                }
                yield return verify;
                yield break;
            }

            if (string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                if (HasReadOnlyTool(allTools, "excel.list_sheets") &&
                    (string.Equals(command.ToolId, "excel.add_sheet", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(command.ToolId, "excel.rename_sheet", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return new ToolCommand { ToolId = "excel.list_sheets", Description = "Deterministic verification" };
                    yield break;
                }
                if (HasReadOnlyTool(allTools, "excel.get_chart") &&
                    (string.Equals(command.ToolId, "excel.update_chart", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(command.ToolId, "excel.add_chart", StringComparison.OrdinalIgnoreCase)) &&
                    command.Arguments.ContainsKey("chartName"))
                {
                    yield return CopyArgs(new ToolCommand { ToolId = "excel.get_chart", Description = "Deterministic chart verification" }, command, "sheet", "chartName");
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

        private static ToolCommand BuildResultVerification(ToolResult result)
        {
            var spec = result == null ? null : result.Verification;
            if (spec == null || string.IsNullOrWhiteSpace(spec.ToolId))
            {
                return null;
            }

            var command = new ToolCommand { ToolId = spec.ToolId, Description = "Deterministic result verification" };
            foreach (var pair in spec.Arguments ?? new Dictionary<string, object>())
            {
                command.Arguments[pair.Key] = pair.Value;
            }
            if (!string.IsNullOrWhiteSpace(spec.ExpectedCodeSha256))
            {
                command.Arguments["__expectedCodeSha256"] = spec.ExpectedCodeSha256;
            }
            return command;
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

        private static string VbaReadToolId(string host)
        {
            if (string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                return "excel.vba_read_module";
            }
            if (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase))
            {
                return "word.vba_read_module";
            }
            if (string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                return "powerpoint.vba_read_module";
            }
            return string.Empty;
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

    internal static class VerificationResultValidator
    {
        public static ToolResult Validate(ToolCommand mutation, ToolCommand verification, ToolResult result)
        {
            if (mutation == null || verification == null || result == null || !result.Success)
            {
                return result;
            }

            try
            {
                if (string.Equals(verification.ToolId, "excel.get_chart", StringComparison.OrdinalIgnoreCase))
                {
                    var actual = JObject.Parse(result.DataJson ?? "{}");
                    var mismatch = FirstChartMismatch(mutation, actual);
                    return string.IsNullOrWhiteSpace(mismatch)
                        ? result
                        : ToolResult.Fail("Chart verification failed: " + mismatch, result.DataJson);
                }

                if (string.Equals(mutation.ToolId, "excel.delete_chart", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(verification.ToolId, "excel.list_charts", StringComparison.OrdinalIgnoreCase))
                {
                    var chartName = Argument(mutation, "chartName");
                    var charts = JArray.Parse(result.DataJson ?? "[]");
                    if (charts.OfType<JObject>().Any(chart => string.Equals((string)chart["name"], chartName, StringComparison.OrdinalIgnoreCase)))
                    {
                        return ToolResult.Fail("Chart verification failed: chart still exists: " + chartName, result.DataJson);
                    }
                }

                if (Contains(verification.ToolId, "vba_read_module"))
                {
                    var actual = JObject.Parse(result.DataJson ?? "{}");
                    var expectedHash = Argument(verification, "__expectedCodeSha256");
                    if (string.IsNullOrWhiteSpace(expectedHash) && mutation.Arguments.ContainsKey("code"))
                    {
                        expectedHash = VbaToolExecutor.CodeSha256(Argument(mutation, "code"));
                    }
                    if (string.IsNullOrWhiteSpace(expectedHash))
                    {
                        return ToolResult.Fail("VBA verification failed: expected module code is unavailable.", result.DataJson, "vba_verification_missing_expected", false);
                    }

                    var actualHash = VbaToolExecutor.CodeSha256((string)actual["code"]);
                    if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return ToolResult.Fail("VBA verification failed: module code does not match the requested code.", result.DataJson, "vba_verification_mismatch", true);
                    }
                }
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail("Verification returned invalid JSON: " + ex.Message, result.DataJson);
            }

            return result;
        }

        private static string FirstChartMismatch(ToolCommand mutation, JObject actual)
        {
            var stringFields = new[]
            {
                new[] { "chartName", "name" },
                new[] { "title", "title" },
                new[] { "xAxisTitle", "xAxisTitle" },
                new[] { "yAxisTitle", "yAxisTitle" },
                new[] { "sourceRange", "sourceRange" }
            };
            foreach (var pair in stringFields)
            {
                if (!mutation.Arguments.ContainsKey(pair[0]) || actual[pair[1]] == null)
                {
                    continue;
                }
                var expected = Argument(mutation, pair[0]);
                var value = Convert.ToString(actual[pair[1]]);
                if (!string.Equals(expected, value, StringComparison.OrdinalIgnoreCase))
                {
                    return pair[0] + " expected '" + expected + "' but was '" + value + "'.";
                }
            }

            if (mutation.Arguments.ContainsKey("chartType") && actual["chartType"] != null)
            {
                var expectedType = Argument(mutation, "chartType");
                var actualType = Convert.ToString(actual["chartType"]);
                if (actualType.IndexOf(expectedType, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return "chartType expected '" + expectedType + "' but was '" + actualType + "'.";
                }
            }

            foreach (var field in new[] { "left", "top", "width", "height" })
            {
                if (!mutation.Arguments.ContainsKey(field) || actual[field] == null)
                {
                    continue;
                }
                double expected;
                double value;
                if (double.TryParse(Argument(mutation, field), out expected) &&
                    double.TryParse(Convert.ToString(actual[field]), out value) &&
                    Math.Abs(expected - value) > 1.0)
                {
                    return field + " expected " + expected + " but was " + value + ".";
                }
            }
            return null;
        }

        private static string Argument(ToolCommand command, string name)
        {
            object value;
            return command != null && command.Arguments.TryGetValue(name, out value) && value != null
                ? Convert.ToString(value)
                : string.Empty;
        }

        private static bool Contains(string value, string term)
        {
            return (value ?? string.Empty).IndexOf(term ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
        }
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
                    " seconds while running " + (toolId ?? string.Empty) + ". The mutation completed, but verification did not.",
                    null,
                    "verification_timeout",
                    false)
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

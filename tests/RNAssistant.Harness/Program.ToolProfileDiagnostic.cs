using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static string DiagnosticFileHash(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        // Isolated live diagnostic, not a production profile/default or a unit test.
        private static int WorkspaceToolProfileDiagnostic(string directory, string profileName, string scenario)
        {
            if (profileName != "broad" && profileName != "compact") throw new ArgumentException("Use broad or compact.");
            if (scenario != "edit" && scenario != "copy") throw new ArgumentException("Use edit or copy.");
            var endpoint = Environment.GetEnvironmentVariable("RNA_BASE_URL");
            var model = Environment.GetEnvironmentVariable("RNA_MODEL");
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("RNA_BASE_URL and RNA_MODEL are required.");
            var evidence = Path.GetFullPath(directory);
            if (Directory.Exists(evidence)) throw new ArgumentException("Use a fresh evidence directory.");
            Directory.CreateDirectory(evidence);
            var root = Path.Combine(evidence, "project");
            var paths = AppDataPaths.CreateForRoot(Path.Combine(evidence, "store"));
            var utf8 = new UTF8Encoding(false, true);
            Action<string, string> write = (path, text) => {
                var target = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(target, text, utf8);
            };
            var source = "// 月次レポート\r\nexport function buildInvoiceTitle() {\r\n  return \"Old invoice\";\r\n}\r\n";
            write("src/reports/invoice.ts", source);
            write("assets/templates/письмо.txt", "Здравствуйте!\r\nIhre Rechnung ist bereit.\r\n請求書をご確認ください。\r\n");
            var extensions = new[] { "ts", "py", "cs", "bas" };
            for (var i = 0; i < 128; i++)
                write("src/components/group-" + (i / 16) + "/sample-" + i + "." + extensions[i % 4],
                    string.Join("\n", Enumerable.Range(0, 32).Select(line => "// Пример / Beispiel / 例 " + i + ":" + line + " unrelatedComponent")) + "\n");
            Directory.CreateDirectory(Path.Combine(root, "exports"));
            Func<Dictionary<string, string>> capture = () => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => new { Path = path.Substring(root.Length + 1).Replace('\\', '/'), File = path })
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ToDictionary(item => item.Path, item => DiagnosticFileHash(item.File), StringComparer.Ordinal);
            var corpus = capture();
            var workspace = new WorkspaceStore(paths).Open(root);
            var before = capture();
            var task = scenario == "edit"
                ? "Найди реализацию buildInvoiceTitle в этом проекте и замени возвращаемую строку Old invoice на Monthly invoice. Другие файлы и остальной код не меняй. Проверь результат."
                : "Создай точную копию assets/templates/письмо.txt в exports/письмо-копия.txt штатной файловой операцией копирования, без ручного переписывания содержимого. Исходный файл и остальные файлы не меняй. Проверь результат.";
            var settings = new AppSettings {
                BaseUrl = endpoint, Model = model, ContextWindowOverrideTokens = 32768,
                AgentResponseMode = Environment.GetEnvironmentVariable("RNA_RESPONSE_MODE") ?? AgentResponseModes.JsonSchema,
                SystemPromptRole = "developer", ToolResultRole = ToolResultRoles.User,
                ReasoningRequestMode = ReasoningRequestModes.ReasoningEffort,
                MaxTokens = 4096, Temperature = 0, SmartChatTitles = false, StreamResponses = false,
                MaxAgentIterations = 12, MaxAgentToolSteps = 20, MaxAgentFormatRetries = 2,
                RequestTimeoutSeconds = 90, AutoCalibrateTokenEstimate = false,
                AutoCompressContext = false, AutoConfirmToolActions = true
            };
            var ids = new[] { "core.skills.load", "core.find", "core.read", "core.search",
                "core.tools.search", "core.tools.read", "core.ask", "core.write", "core.patch", "core.replace" };
            var profile = profileName == "broad" ? CallableToolProfile.AllDirect("workspace-direct")
                : CallableToolProfile.Explicit("workspace-compact-pilot", ids);
            Action<string, object> save = (name, value) => File.WriteAllText(Path.Combine(evidence, name),
                JsonConvert.SerializeObject(value, Formatting.Indented), utf8);
            save("profile.json", new { profileName, scenario, profile.Id, compactIds = ids, settings,
                modelDigest = Environment.GetEnvironmentVariable("RNA_MODEL_DIGEST") });
            save("corpus.json", corpus); save("before.json", before);
            File.WriteAllText(Path.Combine(evidence, "task.txt"), task, utf8);
            var timer = Stopwatch.StartNew();
            WorkspaceRunResult result = null; Exception failure = null; ChatSession session = null;
            using (var service = new WorkspaceConversationService(paths, toolProfile: profile))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4)))
            {
                session = service.CreateSession(workspace, "Tool profile pilot");
                try
                {
                    result = service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                        SessionId = session.Id, Message = task, Mode = ChatModes.Agent, AutoConfirmToolActions = true,
                        ReasoningEnabled = false, Acceptance = new WorkspaceRunAcceptance { MinimumVerifiedWrites = 1 }
                    }, settings, () => null, progress: update => Console.WriteLine("PROGRESS " + update.Kind),
                        cancellationToken: deadline.Token, modelDigest: Environment.GetEnvironmentVariable("RNA_MODEL_DIGEST")).GetAwaiter().GetResult();
                }
                catch (Exception ex) { failure = ex; }
                session = service.GetSession(workspace, session.Id);
            }
            timer.Stop();
            var after = capture(); save("after.json", after); save("session.json", session); save("result.json", result);
            var expected = new Dictionary<string, string>(before, StringComparer.Ordinal);
            if (scenario == "edit") expected["src/reports/invoice.ts"] = TextPatternEngine.Sha256(source.Replace("Old invoice", "Monthly invoice"));
            else expected["exports/письмо-копия.txt"] = before["assets/templates/письмо.txt"];
            var exact = expected.Count == after.Count && expected.All(pair => after.ContainsKey(pair.Key) && after[pair.Key] == pair.Value);
            // Also assert actual byte identity (including CRLF), not only normalized text.
            var exactBytes = scenario == "edit"
                ? File.ReadAllBytes(Path.Combine(root, "src/reports/invoice.ts")).SequenceEqual(utf8.GetBytes(source.Replace("Old invoice", "Monthly invoice")))
                : File.Exists(Path.Combine(root, "exports/письмо-копия.txt")) &&
                    File.ReadAllBytes(Path.Combine(root, "assets/templates/письмо.txt")).SequenceEqual(File.ReadAllBytes(Path.Combine(root, "exports/письмо-копия.txt")));
            var calls = session.Messages.Where(message => message.AcceptedCallOrigin != null).ToArray();
            var completed = session.Messages.Where(message => message.ExecutionProgress != null && message.ProtocolMessage).ToArray();
            var copyUsed = completed.Any(message => message.ToolName == "resources.copy" && message.ExecutionProgress.Outcome == ToolExecutionOutcome.Ok);
            var taskPassed = exact && exactBytes && (scenario != "copy" || copyUsed);
            var passed = taskPassed && failure == null && result?.Summary.Lifecycle == RunLifecycle.Completed &&
                result.Summary.ToolCounts.WriteUnknown == 0 && result.Acceptance.State == WorkspaceAcceptanceState.Passed;
            save("acceptance.json", new { passed, taskPassed, exact, exactBytes, copyUsed,
                seconds = timer.Elapsed.TotalSeconds, failure = failure?.ToString(), summary = result?.Summary,
                callSequence = calls.Select(message => message.ToolName), calls = calls.Length,
                selectedSkills = session.LastRun?.SelectedSkills.Select(skill => skill.Id),
                toolPackEvents = new ChatStore(paths).ReadEvents(session.Host, session.DocumentKey, session.Id)
                    .Where(item => item.Type == SessionEventTypes.ToolPackExtensionAccepted || item.Type == SessionEventTypes.ToolPackExtensionRejected)
                    .Select(item => new { item.Type, item.Data }) });
            Console.WriteLine("ACCEPTANCE " + passed + " task=" + taskPassed + " calls=" + calls.Length);
            return passed ? 0 : 1;
        }
    }
}

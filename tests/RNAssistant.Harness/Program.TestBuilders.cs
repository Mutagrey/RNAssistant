using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ToolDefinition CustomTool(string host, string id)
        {
            return new ToolDefinition
            {
                Id = id,
                Host = host,
                Name = id,
                Executor = "pipeline",
                Enabled = true,
                BuiltIn = false,
                PipelineJson = "{\"steps\":[]}"
            };
        }

        private static bool HasTool(IEnumerable<ToolDefinition> tools, string id)
        {
            foreach (var tool in tools)
            {
                if (tool != null && string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasSkill(IEnumerable<SkillDefinition> skills, string id)
        {
            foreach (var skill in skills)
            {
                if (skill != null && string.Equals(skill.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static ToolDefinition FindTool(IEnumerable<ToolDefinition> tools, string id)
        {
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                if (tool != null && string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return tool;
                }
            }

            return null;
        }

        private static bool ContainsMessage(IEnumerable<ChatMessage> messages, string text)
        {
            foreach (var message in messages ?? new ChatMessage[0])
            {
                if (message != null && (message.Content ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static List<ToolDefinition> BuildPipelineTools(bool requiresConfirmation)
        {
            return new List<ToolDefinition>
            {
                new ToolDefinition
                {
                    Id = "excel.make_report",
                    Host = "Excel",
                    Name = "Make report",
                    Executor = "pipeline",
                    Enabled = true,
                    RequiresConfirmation = requiresConfirmation,
                    PipelineJson = "{" +
                        "\"steps\":[" +
                        "{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"{{args.sheet}}\"}}," +
                        "{\"id\":\"table\",\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"{{args.sheet}}\",\"startAddress\":\"A1\",\"values\":\"[[\\\"Month\\\",\\\"Sales\\\"]]\"}}" +
                        "]}"
                }
            };
        }

        private static List<ToolDefinition> BuildStepPlaceholderPipelineTools()
        {
            return new List<ToolDefinition>
            {
                new ToolDefinition
                {
                    Id = "excel.chain_report",
                    Host = "Excel",
                    Name = "Chain report",
                    Executor = "pipeline",
                    Enabled = true,
                    PipelineJson = "{" +
                        "\"steps\":[" +
                        "{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}," +
                        "{\"id\":\"table\",\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"Report\",\"sourceMessage\":\"{{steps.sheet.message}}\",\"sourceSuccess\":\"{{steps.sheet.success}}\"}}" +
                        "]}"
                }
            };
        }

        private static List<ToolDefinition> BuildThreeStepPipelineTools()
        {
            return new List<ToolDefinition>
            {
                new ToolDefinition
                {
                    Id = "excel.full_report",
                    Host = "Excel",
                    Name = "Full report",
                    Executor = "pipeline",
                    Enabled = true,
                    PipelineJson = "{" +
                        "\"steps\":[" +
                        "{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"{{args.sheet}}\"}}," +
                        "{\"id\":\"table\",\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"{{args.sheet}}\",\"startAddress\":\"A1\"}}," +
                        "{\"id\":\"chart\",\"toolId\":\"excel.add_chart\",\"arguments\":{\"sheet\":\"{{args.sheet}}\",\"sourceRange\":\"A1:B2\",\"chartType\":\"column\",\"title\":\"Report\"}}" +
                        "]}"
                }
            };
        }

        private static ToolCommand Command(string id, params object[] keyValues)
        {
            var command = new ToolCommand { ToolId = id };
            for (var i = 0; i + 1 < (keyValues == null ? 0 : keyValues.Length); i += 2)
            {
                command.Arguments[Convert.ToString(keyValues[i])] = keyValues[i + 1];
            }

            return command;
        }

        private static string AgentBlock(params ToolCommand[] commands)
        {
            return "```rnassistant-agent\n" +
                JsonConvert.SerializeObject(new
                {
                    steps = (commands ?? new ToolCommand[0]).Select(command => new
                    {
                        toolId = command.ToolId,
                        arguments = command.Arguments
                    }).ToArray()
                }) +
                "\n```";
        }

        private static ChatCompletionService ChatServiceWithResponses(
            FakeOfficeAdapter adapter,
            OfficeToolExecutor executor,
            ICollection<IReadOnlyList<ChatMessage>> calls,
            params string[] responses)
        {
            var index = 0;
            return new ChatCompletionService(
                adapter,
                executor,
                delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (calls != null)
                    {
                        calls.Add(new List<ChatMessage>(messages ?? new ChatMessage[0]));
                    }

                    var content = index < (responses == null ? 0 : responses.Length)
                        ? responses[index]
                        : "Done.";
                    index += 1;
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = content,
                        PromptTokens = 10,
                        CompletionTokens = 2,
                        TotalTokens = 12
                    });
                });
        }

        private sealed class ScenarioLlm
        {
            private readonly List<ScenarioTurn> _turns = new List<ScenarioTurn>();
            private int _index;

            public readonly List<IReadOnlyList<ChatMessage>> Calls = new List<IReadOnlyList<ChatMessage>>();

            public ScenarioLlm Add(string response, string[] mustContain, string[] mustNotContain)
            {
                _turns.Add(new ScenarioTurn
                {
                    Response = response ?? string.Empty,
                    MustContain = mustContain ?? new string[0],
                    MustNotContain = mustNotContain ?? new string[0]
                });
                return this;
            }

            public ScenarioLlm Add(string response, params string[] mustContain)
            {
                return Add(response, mustContain, null);
            }

            public Task<LlmCompletionResult> CompleteAsync(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var captured = new List<ChatMessage>(messages ?? new ChatMessage[0]);
                Calls.Add(captured);
                if (_index >= _turns.Count)
                {
                    throw new InvalidOperationException("Scenario LLM has no response for turn " + (_index + 1) + ".");
                }

                var turn = _turns[_index];
                var prompt = FlattenMessages(captured);
                for (var i = 0; i < turn.MustContain.Length; i++)
                {
                    AssertContains(prompt, turn.MustContain[i], "scenario turn " + (_index + 1) + " prompt");
                }

                for (var i = 0; i < turn.MustNotContain.Length; i++)
                {
                    if (prompt.IndexOf(turn.MustNotContain[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        throw new InvalidOperationException("scenario turn " + (_index + 1) + " prompt unexpectedly contained '" + turn.MustNotContain[i] + "'");
                    }
                }

                _index += 1;
                return Task.FromResult(new LlmCompletionResult
                {
                    Content = turn.Response,
                    PromptTokens = 10,
                    CompletionTokens = 2,
                    TotalTokens = 12
                });
            }

            public ChatCompletionService CreateService(FakeOfficeAdapter adapter, OfficeToolExecutor executor)
            {
                return new ChatCompletionService(adapter, executor, CompleteAsync);
            }

            private sealed class ScenarioTurn
            {
                public string Response { get; set; }
                public string[] MustContain { get; set; }
                public string[] MustNotContain { get; set; }
            }
        }

        private static ChatSession NewSession(FakeOfficeAdapter adapter)
        {
            return new ChatSession
            {
                Host = adapter.HostName,
                DocumentKey = adapter.DocumentKey,
                DocumentTitle = adapter.DocumentTitle,
                Title = "New chat"
            };
        }

        private static DocumentContext NewContext(FakeOfficeAdapter adapter)
        {
            return new DocumentContext
            {
                Host = adapter.HostName,
                DocumentKey = adapter.DocumentKey,
                Title = adapter.DocumentTitle
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Skills;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office
{
    public sealed class AssistantController
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly AppDataPaths _paths;
        private readonly SettingsService _settingsService;
        private readonly ChatStore _chatStore;
        private readonly ContextStore _contextStore;
        private readonly SkillStore _skillStore;
        private readonly LlmClient _llmClient;
        private readonly PromptComposer _promptComposer;
        private readonly SkillCommandParser _commandParser;
        private readonly object _syncRoot;
        private string _queuedQuickAction;

        public AssistantController(IOfficeApplicationAdapter adapter)
        {
            _adapter = adapter;
            _paths = AppDataPaths.CreateDefault();
            _settingsService = new SettingsService(_paths);
            _chatStore = new ChatStore(_paths);
            _contextStore = new ContextStore(_paths);
            _skillStore = new SkillStore(_paths);
            _llmClient = new LlmClient(() => _settingsService.LoadApiKey());
            _promptComposer = new PromptComposer();
            _commandParser = new SkillCommandParser();
            _syncRoot = new object();
        }

        public string HostName { get { return _adapter.HostName; } }

        public string InitializeJson()
        {
            var session = LoadSession();
            var state = new
            {
                host = _adapter.HostName,
                documentKey = _adapter.DocumentKey,
                title = _adapter.DocumentTitle,
                settings = _settingsService.Load(),
                hasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey()),
                skills = GetVisibleSkills(),
                context = LoadContext(),
                messages = session.Messages,
                quickAction = DequeueQuickAction()
            };
            return JsonConvert.SerializeObject(state);
        }

        public async Task<string> SendChatAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return JsonConvert.SerializeObject(new { message = string.Empty, skillResults = new SkillResult[0], messages = LoadSession().Messages });
            }

            var settings = _settingsService.Load();
            var session = LoadSession();
            session.Messages.Add(new ChatMessage { Role = "user", Content = text });

            var skills = GetVisibleSkills().Where(s => s.Enabled).ToList();
            var systemPrompt = _promptComposer.ComposeSystemPrompt(
                settings,
                _adapter.HostName,
                _adapter.GetDocumentSnapshot(settings.ContextCharLimit),
                skills);

            var messages = BuildPromptMessages(systemPrompt, session.Messages, settings.ContextCharLimit);
            var assistantText = await _llmClient.CompleteAsync(settings, messages).ConfigureAwait(false);
            var assistantMessage = new ChatMessage { Role = "assistant", Content = assistantText };
            session.Messages.Add(assistantMessage);

            var results = new List<SkillResult>();
            foreach (var command in _commandParser.Parse(assistantText))
            {
                var result = _adapter.ExecuteSkill(command);
                results.Add(result);
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "Local skill result for `" + command.SkillId + "`: " + result.Message + (string.IsNullOrWhiteSpace(result.DataJson) ? string.Empty : "\n```json\n" + result.DataJson + "\n```")
                });
            }

            _chatStore.Save(session);
            return JsonConvert.SerializeObject(new { message = assistantText, skillResults = results, messages = session.Messages });
        }

        public string GetSettingsJson()
        {
            return JsonConvert.SerializeObject(new
            {
                settings = _settingsService.Load(),
                hasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey())
            });
        }

        public string SaveSettingsJson(string settingsJson, string apiKey)
        {
            var settings = JsonConvert.DeserializeObject<AppSettings>(settingsJson) ?? new AppSettings();
            _settingsService.Save(settings);
            if (apiKey != null)
            {
                _settingsService.SaveApiKey(apiKey);
            }

            return GetSettingsJson();
        }

        public string GetSkillsJson()
        {
            return JsonConvert.SerializeObject(GetVisibleSkills());
        }

        public string SaveSkillsJson(string skillsJson)
        {
            var skills = JsonConvert.DeserializeObject<List<SkillDefinition>>(skillsJson) ?? new List<SkillDefinition>();
            _skillStore.Save(skills.Where(s => !s.BuiltIn));
            return GetSkillsJson();
        }

        public string GetContextJson()
        {
            return JsonConvert.SerializeObject(LoadContext());
        }

        public string ClearContextJson()
        {
            _contextStore.Clear(_adapter.HostName, _adapter.DocumentKey);
            return GetContextJson();
        }

        public void QueueQuickAction(string action)
        {
            lock (_syncRoot)
            {
                _queuedQuickAction = action;
            }
        }

        public Task<string> RunQuickActionAsync(string action)
        {
            string prompt;
            switch ((action ?? string.Empty).ToLowerInvariant())
            {
                case "summarize":
                    prompt = "Сделай краткое summary текущего документа. Если нужны данные документа, используй доступные skills.";
                    break;
                case "explain-selection":
                    prompt = "Объясни выделенный фрагмент. Если надо, прочитай выделение через skill.";
                    break;
                case "draft-rewrite":
                    prompt = "Помоги написать или улучшить текст для текущего документа/письма. Сначала уточни цель, если данных недостаточно.";
                    break;
                case "run-skill":
                    prompt = "Покажи доступные skills для этого Office-приложения и предложи, что можно выполнить.";
                    break;
                case "settings":
                    prompt = "/open-settings";
                    break;
                case "context":
                    prompt = "/open-context";
                    break;
                default:
                    prompt = action ?? string.Empty;
                    break;
            }

            return Task.FromResult(JsonConvert.SerializeObject(new { prompt = prompt }));
        }

        private string DequeueQuickAction()
        {
            lock (_syncRoot)
            {
                var action = _queuedQuickAction;
                _queuedQuickAction = null;
                return action;
            }
        }

        private ChatSession LoadSession()
        {
            return _chatStore.LoadOrCreate(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle);
        }

        private DocumentContext LoadContext()
        {
            return _contextStore.LoadOrCreate(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle);
        }

        private List<SkillDefinition> GetVisibleSkills()
        {
            var result = new List<SkillDefinition>();
            result.AddRange(_adapter.GetBuiltInSkills() ?? new SkillDefinition[0]);
            result.AddRange(_skillStore.Load().Where(s =>
                string.Equals(s.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Host, "Common", StringComparison.OrdinalIgnoreCase)));
            return result.OrderBy(s => s.Host).ThenBy(s => s.Id).ToList();
        }

        private static List<ChatMessage> BuildPromptMessages(string systemPrompt, IEnumerable<ChatMessage> sessionMessages, int charLimit)
        {
            var result = new List<ChatMessage> { new ChatMessage { Role = "system", Content = systemPrompt } };
            var remaining = Math.Max(4000, charLimit);
            foreach (var message in sessionMessages.Reverse())
            {
                if (string.IsNullOrEmpty(message.Content))
                {
                    continue;
                }

                remaining -= message.Content.Length;
                if (remaining < 0)
                {
                    break;
                }

                result.Insert(1, message);
            }

            return result;
        }
    }
}


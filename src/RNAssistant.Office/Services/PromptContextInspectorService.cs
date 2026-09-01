using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class PromptContextInspectorService
    {
        private const int RelaxedHistoryBudgetTokens = 500000000;
        private const int MaxSectionItems = 40;
        private const int MaxPreviewChars = 900;
        private const int MaxRawChars = 512000;

        private readonly IOfficeApplicationAdapter _adapter;
        private readonly AppDataPaths _paths;
        private readonly AppSettings _estimationSettings;

        public PromptContextInspectorService(IOfficeApplicationAdapter adapter, AppDataPaths paths)
            : this(adapter, paths, null)
        {
        }

        private PromptContextInspectorService(
            IOfficeApplicationAdapter adapter,
            AppDataPaths paths,
            AppSettings estimationSettings)
        {
            _adapter = adapter;
            _paths = paths;
            _estimationSettings = estimationSettings;
        }

        public PromptContextInspectorResponse Inspect(
            ChatSession session,
            DocumentContext context,
            AppSettings settings,
            IReadOnlyList<ToolCatalogEntry> tools,
            IReadOnlyList<SkillDefinition> skills,
            IReadOnlyList<ChatAttachment> attachments,
            string draftText,
            bool includeRaw)
        {
            settings = settings ?? new AppSettings();
            var inspection = new PromptContextInspectorService(_adapter, _paths, settings);
            return inspection.InspectCore(
                session,
                context,
                settings,
                tools,
                skills,
                attachments,
                draftText,
                includeRaw);
        }

        private PromptContextInspectorResponse InspectCore(
            ChatSession session,
            DocumentContext context,
            AppSettings settings,
            IReadOnlyList<ToolCatalogEntry> tools,
            IReadOnlyList<SkillDefinition> skills,
            IReadOnlyList<ChatAttachment> attachments,
            string draftText,
            bool includeRaw)
        {
            settings = settings ?? new AppSettings();
            session = session ?? new ChatSession();
            attachments = attachments ?? new ChatAttachment[0];
            draftText = draftText ?? string.Empty;

            var previewSession = ChatCloneService.CloneSessionSnapshot(session) ?? new ChatSession();
            previewSession.Messages = previewSession.Messages ?? new List<ChatMessage>();
            previewSession.Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = draftText,
                HtmlWorkspaceCheckpoint = ChatResourceUri.ResolveArtifactRevision(previewSession, previewSession.ActiveHtmlArtifactId),
                Attachments = new List<ChatAttachment>(attachments)
            });

            var mode = ChatModes.Normalize(previewSession.Mode);
            var policy = ConversationRunPolicy.For(mode);
            var runnableCatalog = ConversationRunService.PrepareToolsForMode(mode, tools);
            var enabledSkills = policy.SelectSkills(skills);
            CapabilityCatalogService.ThrowOnCollision(runnableCatalog, enabledSkills);
            CapabilityCatalogService.BindReadSchema(runnableCatalog, enabledSkills);
            var toolPack = CallableToolPack.Create(
                mode,
                _adapter == null ? string.Empty : _adapter.HostName,
                null,
                runnableCatalog);
            var runnableTools = toolPack.Tools;
            var capabilityCatalog = toolPack.CapabilityContext(enabledSkills);
            var options = ConversationModelSession.BuildRequestOptions(
                mode,
                AgentResponseModes.Normalize(settings.AgentResponseMode),
                runnableTools,
                previewSession,
                null);
            var repairReserveTokens = ModelProtocolClient.EstimateFormatRepairOverheadTokens(settings);
            var continuationReserveTokens = ModelContextBudget.ContinuationReserveTokens(settings);
            var inputLimit = ModelContextBudget.InputBudgetTokens(settings);
            var messageBudget = Math.Max(1, inputLimit -
                EstimateRequestOptionsTokens(options) -
                repairReserveTokens -
                continuationReserveTokens);

            var relaxed = false;
            List<ChatMessage> messages;
            try
            {
                messages = BuildMessages(mode, draftText, previewSession, context, settings,
                    runnableTools, enabledSkills, attachments, messageBudget, capabilityCatalog);
            }
            catch (PromptBudgetExceededException)
            {
                relaxed = true;
                messages = BuildMessages(mode, draftText, previewSession, context, settings,
                    runnableTools, enabledSkills, attachments, RelaxedHistoryBudgetTokens, capabilityCatalog);
            }

            var usedTokens = ModelContextBudget.EstimateAdmittedRequestTokens(
                messages,
                options,
                settings,
                repairReserveTokens,
                continuationReserveTokens);
            var contextWindow = Math.Max(4096, ModelContextBudget.ContextWindowTokens(settings));
            var safety = ModelContextBudget.SafetyReserveTokens(contextWindow);
            var reservedOutput = Math.Max(1, contextWindow - safety - inputLimit);

            var sections = BuildSections(
                mode,
                messages,
                options,
                session,
                previewSession,
                context,
                settings,
                runnableTools,
                enabledSkills,
                capabilityCatalog,
                attachments,
                draftText,
                usedTokens,
                repairReserveTokens,
                continuationReserveTokens);
            var lastUsage = (session.Messages ?? new List<ChatMessage>())
                .Where(item => item != null && item.PromptTokens.HasValue)
                .OrderByDescending(item => item.CreatedUtc)
                .FirstOrDefault();
            var estimateMultiplier = TokenEstimateCalibration.EffectiveMultiplier(settings);
            var estimateIntercept = TokenEstimateCalibration.EffectiveInterceptTokens(settings);
            var calibrationSamples = settings.AutoCalibrateTokenEstimate
                ? TokenEstimateCalibration.SampleCount(settings)
                : 0;
            var estimateNotice = calibrationSamples > 0
                ? "≈ уточнено по " + calibrationSamples + " API usage для этой модели."
                : "≈ рассчитано по UTF-8 объёму.";
            estimateNotice += mode != ChatModes.Chat
                ? " Даже пустой " + (mode == ChatModes.Plan ? "Plan" : "Agent") + " включает system prompt, bootstrap-схемы tools и компактный capability-каталог."
                : " Даже пустой Chat включает system prompt и схемы read-only resource tools.";

            var response = new PromptContextInspectorResponse
            {
                ChatId = session.Id,
                SessionRevision = session.Revision,
                Mode = mode,
                Model = settings.Model ?? session.Model ?? string.Empty,
                UsedTokens = usedTokens,
                InputLimitTokens = inputLimit,
                ContextWindowTokens = contextWindow,
                ReservedOutputTokens = reservedOutput,
                SafetyTokens = safety,
                RemainingInputTokens = Math.Max(0, inputLimit - usedTokens),
                Percent = inputLimit <= 0 ? 0 : Math.Min(100, (int)Math.Round(usedTokens * 100.0 / inputLimit)),
                MessageCount = messages.Count,
                OverBudget = relaxed || usedTokens > inputLimit,
                Estimated = true,
                EstimateMultiplier = estimateMultiplier,
                EstimateInterceptTokens = estimateIntercept,
                ManualEstimateMultiplier = settings.TokenEstimateMultiplier <= 0
                    ? AppSettings.DefaultTokenEstimateMultiplier
                    : settings.TokenEstimateMultiplier,
                AutoCalibrateEstimate = settings.AutoCalibrateTokenEstimate,
                CalibrationSamples = calibrationSamples,
                EstimateMethod = "utf8_bytes_div_4_linear_calibrated",
                LastPromptTokens = lastUsage == null ? null : lastUsage.PromptTokens,
                LastPromptUtc = lastUsage == null ? null : (DateTime?)lastUsage.CreatedUtc,
                LastRunId = lastUsage == null ? string.Empty : lastUsage.RunId ?? string.Empty,
                Notice = relaxed || usedTokens > inputLimit
                    ? "Оценочный состав с обязательными schemas/reserves превышает лимит. Runtime попробует допустимое сжатие истории, а если этого недостаточно — остановит основной запрос; сократите историю или выберите модель с большим контекстом. " + estimateNotice
                    : estimateNotice + " Снимок обновляется только вручную.",
                Sections = sections,
                GeneratedUtc = DateTime.UtcNow
            };

            if (includeRaw)
            {
                var raw = BuildRawRequest(mode, settings.Model, messages, options);
                response.RawTruncated = raw.Length > MaxRawChars;
                response.RawRequestJson = response.RawTruncated
                    ? raw.Substring(0, MaxRawChars) + "\n\n[structure truncated]"
                    : raw;
            }

            return response;
        }

        private List<ChatMessage> BuildMessages(
            string mode,
            string draftText,
            ChatSession session,
            DocumentContext context,
            AppSettings settings,
            IReadOnlyList<ToolCatalogEntry> tools,
            IReadOnlyList<SkillDefinition> skills,
            IReadOnlyList<ChatAttachment> attachments,
            int historyBudgetTokens,
            JObject capabilityCatalog)
        {
            return new ConversationPromptComposer().BuildMessages(
                mode,
                draftText,
                _adapter,
                tools,
                skills,
                context,
                settings,
                session,
                attachments,
                false,
                historyBudgetTokens,
                capabilityCatalog);
        }

        private List<PromptContextSectionDto> BuildSections(
            string mode,
            IReadOnlyList<ChatMessage> messages,
            LlmRequestOptions options,
            ChatSession sourceSession,
            ChatSession previewSession,
            DocumentContext context,
            AppSettings settings,
            IReadOnlyList<ToolCatalogEntry> tools,
            IReadOnlyList<SkillDefinition> skills,
            JObject capabilityCatalog,
            IReadOnlyList<ChatAttachment> attachments,
            string draftText,
            int usedTokens,
            int repairReserveTokens,
            int continuationReserveTokens)
        {
            var sections = new List<PromptContextSectionDto>();
            var current = messages == null || messages.Count == 0 ? null : messages[messages.Count - 1];
            var currentTokens = EstimateMessageTokens(current);
            var hasStandaloneInstruction = messages != null && messages.Count > 1 && IsInstructionRole(messages[0].Role);
            var instructionMessageTokens = hasStandaloneInstruction
                ? EstimateMessageTokens(messages[0])
                : 0;

            string instruction;
            string agentGeneralInstruction = string.Empty;
            string agentToolInstruction = string.Empty;
            string agentSkillInstruction = string.Empty;
            string instructionEnvelope;
            string runtimeJson = string.Empty;
            if (mode != ChatModes.Chat)
            {
                agentGeneralInstruction = mode == ChatModes.Plan
                    ? ConversationPromptComposer.ResolvePlanPrompt(settings)
                    : ConversationPromptComposer.ResolveGeneralPrompt(settings);
                agentToolInstruction = ConversationPromptComposer.ResolveToolPrompt(settings);
                agentSkillInstruction = ConversationPromptComposer.ResolveSkillPrompt(settings);
            }
            else
            {
                agentGeneralInstruction = ConversationPromptComposer.ResolveChatPrompt(settings);
            }
            instruction = ConversationPromptComposer.BuildInstruction(mode, settings);
            runtimeJson = ConversationPromptComposer.BuildRuntimeContext(
                mode,
                _adapter,
                tools,
                skills,
                context,
                previewSession,
                settings,
                capabilityCatalog);
            instructionEnvelope = instruction + "\n\nRUNTIME_CONTEXT:\n" + runtimeJson;

            var embeddedInstructionTokens = hasStandaloneInstruction || current == null
                ? 0
                : Math.Min(currentTokens, EstimateTextTokens(instructionEnvelope));
            var runtimeBudget = hasStandaloneInstruction ? instructionMessageTokens : embeddedInstructionTokens;
            AddAllocatedSections(sections, BuildRuntimeSeeds(
                mode,
                agentGeneralInstruction,
                agentToolInstruction,
                agentSkillInstruction,
                runtimeJson,
                sourceSession), runtimeBudget);

            var historyStart = hasStandaloneInstruction ? 1 : 0;
            var history = new List<ChatMessage>();
            for (var index = historyStart; messages != null && index < messages.Count - 1; index++)
            {
                if (messages[index] != null) history.Add(messages[index]);
            }
            var protocolHistory = history.Where(IsProtocolMessage).ToList();
            var regularHistory = history.Where(item => !IsProtocolMessage(item)).ToList();
            AddMessageSection(sections, "history", "История чата", "Активное окно и checkpoint", regularHistory);
            AddMessageSection(sections, "tool_history", "Tool calls и результаты", "Только записи, повторно отправляемые модели", protocolHistory);

            var currentBudget = Math.Max(0, currentTokens - embeddedInstructionTokens);
            AddAllocatedSections(sections, BuildCurrentSeeds(
                current,
                instructionEnvelope,
                embeddedInstructionTokens > 0,
                attachments,
                draftText), currentBudget);

            var optionsTokens = EstimateRequestOptionsTokens(options);
            if (optionsTokens > 0)
            {
                var schema = options == null ? string.Empty : options.ResponseSchemaJson ?? string.Empty;
                sections.Add(new PromptContextSectionDto
                {
                    Id = "response_format",
                    Title = "Формат ответа",
                    Tokens = optionsTokens,
                    Count = string.IsNullOrWhiteSpace(schema) ? 1 : 2,
                    Detail = options == null ? string.Empty : options.ResponseFormat,
                    Included = true,
                    Items = BoundItems(new List<PromptContextItemDto>
                    {
                        Item("response-format", "format", options == null ? "response format" : options.ResponseFormat,
                            string.Empty, EstimateTextTokens(options == null ? string.Empty : options.ResponseFormat),
                            options == null ? string.Empty : options.ResponseFormat),
                        string.IsNullOrWhiteSpace(schema) ? null : Item(
                            "response-schema", "schema", options.ResponseSchemaName ?? "response schema", string.Empty,
                            EstimateTextTokens(schema), schema)
                    }.Where(item => item != null).ToList(), optionsTokens)
                });
            }

            if (repairReserveTokens > 0)
            {
                sections.Add(new PromptContextSectionDto
                {
                    Id = "format_repair_reserve",
                    Title = "Резерв FORMAT_REPAIR",
                    Tokens = repairReserveTokens,
                    Count = 1,
                    Detail = "Худший ограниченный parser error для следующей разрешённой попытки",
                    Included = true,
                    Items = new List<PromptContextItemDto>
                    {
                        Item("format-repair-reserve", "reserve", "FORMAT_REPAIR reserve", string.Empty,
                            repairReserveTokens,
                            "Не отправляется в первом запросе; место сохраняется для одной bounded repair-инструкции.")
                    }
                });
            }

            if (continuationReserveTokens > 0)
            {
                sections.Add(new PromptContextSectionDto
                {
                    Id = "continuation_reserve",
                    Title = "Резерв следующего шага",
                    Tokens = continuationReserveTokens,
                    Count = 1,
                    Detail = "Ответ модели, минимальный result envelope и пропорциональный запас",
                    Included = true,
                    Items = new List<PromptContextItemDto>
                    {
                        Item("continuation-reserve", "reserve", "Continuation reserve", string.Empty,
                            continuationReserveTokens,
                            "Не отправляется как текст; admission сохраняет место как минимум для разрешённого ответа модели и result envelope.")
                    }
                });
            }

            var excluded = BuildExcludedSection(sourceSession, previewSession, true);
            if (excluded != null) sections.Add(excluded);

            var estimateIntercept = TokenEstimateCalibration.EffectiveInterceptTokens(settings);
            if (estimateIntercept > 0)
            {
                sections.Add(new PromptContextSectionDto
                {
                    Id = "estimate_overhead",
                    Title = "Overhead модели",
                    Tokens = estimateIntercept,
                    Count = 1,
                    Detail = "Линейная калибровка по API usage",
                    Included = true,
                    Items = new List<PromptContextItemDto>
                    {
                        Item("estimate-intercept", "estimate", "Постоянная поправка", string.Empty,
                            estimateIntercept, "Не отдельный текст запроса; поправка модели по прошлым usage.")
                    }
                });
            }

            var included = sections.Where(section => section.Included).OrderByDescending(section => section.Tokens).ToList();
            var difference = usedTokens - included.Sum(section => section.Tokens);
            if (difference != 0 && included.Count > 0)
            {
                included[0].Tokens = Math.Max(0, included[0].Tokens + difference);
            }
            included.AddRange(sections.Where(section => !section.Included));
            return included;
        }

        private List<SectionSeed> BuildRuntimeSeeds(
            string mode,
            string generalInstruction,
            string toolInstruction,
            string skillInstruction,
            string runtimeJson,
            ChatSession session)
        {
            var root = string.IsNullOrWhiteSpace(runtimeJson) ? new JObject() : JObject.Parse(runtimeJson);
            var tools = root["tools"] as JArray ?? new JArray();
            var capabilities = root["capabilities"] as JObject ?? new JObject();
            var capabilityItems = capabilities["items"] as JArray ?? new JArray();
            var userContext = root["user_context"] as JArray ?? new JArray();
            var artifactIndex = (string)root["artifacts"] ?? string.Empty;
            var baseJson = new JObject
            {
                ["mode"] = root["mode"] == null ? mode : root["mode"].DeepClone(),
                ["host"] = root["host"] == null ? string.Empty : root["host"].DeepClone(),
                ["document"] = root["document"] == null ? new JObject() : root["document"].DeepClone()
            }.ToString(Formatting.None);
            var baseItems = new List<PromptContextItemDto>
            {
                Item(mode + "-system-prompt", "instruction", mode == ChatModes.Plan ? "Plan system prompt" : mode == ChatModes.Agent ? "Agent system prompt" : "Chat system prompt", string.Empty,
                    EstimateTextTokens(generalInstruction), generalInstruction),
                Item("runtime-document", "runtime", "Документ и host", string.Empty,
                    EstimateTextTokens(baseJson), baseJson)
            };
            var seeds = new List<SectionSeed>
            {
                new SectionSeed
                {
                    Id = "instructions",
                    Title = "Общие инструкции и runtime",
                    Detail = (mode == ChatModes.Plan ? "Plan" : mode == ChatModes.Agent ? "Agent" : "Chat") + " prompt, document identity и JSON-обвязка",
                    RawTokens = Math.Max(1, baseItems.Sum(item => item.Tokens) + 8),
                    Count = baseItems.Count,
                    Items = baseItems
                }
            };

            if (!string.IsNullOrWhiteSpace(toolInstruction))
            {
                seeds.Add(new SectionSeed
                {
                    Id = "tool_instructions",
                    Title = "Правила tools",
                    Detail = "Отдельный AgentToolsPrompt",
                    RawTokens = Math.Max(1, EstimateTextTokens(toolInstruction)),
                    Count = 1,
                    Items = new List<PromptContextItemDto>
                    {
                        Item("agent-tools-prompt", "instruction", "Agent tools prompt", string.Empty,
                            EstimateTextTokens(toolInstruction), toolInstruction)
                    }
                });
            }
            if (!string.IsNullOrWhiteSpace(skillInstruction))
            {
                seeds.Add(new SectionSeed
                {
                    Id = "skill_instructions",
                    Title = "Правила skills",
                    Detail = "Отдельный AgentSkillsPrompt",
                    RawTokens = Math.Max(1, EstimateTextTokens(skillInstruction)),
                    Count = 1,
                    Items = new List<PromptContextItemDto>
                    {
                        Item("agent-skills-prompt", "instruction", "Agent skills prompt", string.Empty,
                            EstimateTextTokens(skillInstruction), skillInstruction)
                    }
                });
            }

            if (tools.Count > 0)
            {
                var items = tools.OfType<JObject>().Select(BuildToolItem).ToList();
                seeds.Add(new SectionSeed
                {
                    Id = "tools",
                    Title = "Tools и схемы",
                    Detail = tools.Count + " runnable tools",
                    RawTokens = Math.Max(1, EstimateTextTokens(tools.ToString(Formatting.None))),
                    Count = tools.Count,
                    Items = items
                });
            }
            else
            {
                seeds[0].RawTokens += EstimateTextTokens("\"tools\":[]");
            }

            if (capabilities.HasValues)
            {
                seeds.Add(new SectionSeed
                {
                    Id = "capabilities",
                    Title = "Capability catalog",
                    Detail = capabilityItems.Count + "/" + ((int?)capabilities["total"] ?? capabilityItems.Count) +
                        " compact exact-id tools and skills",
                    RawTokens = Math.Max(1, EstimateTextTokens(capabilities.ToString(Formatting.None))),
                    Count = capabilityItems.Count,
                    Items = capabilityItems.OfType<JObject>().Select(item => Item(
                        (string)item["id"],
                        (string)item["kind"] ?? "capability",
                        (string)item["name"] ?? (string)item["id"] ?? "Capability",
                        ((string)item["kind"] ?? "capability") + ": " + ((string)item["id"] ?? string.Empty),
                        EstimateTextTokens(item.ToString(Formatting.None)),
                        (string)item["summary"] ?? string.Empty)).ToList()
                });
            }
            else
            {
                seeds[0].RawTokens += EstimateTextTokens("\"capabilities\":{\"items\":[]}");
            }

            if (userContext.Count > 0)
            {
                var items = userContext.OfType<JObject>().Select((item, index) => Item(
                    "context-" + index,
                    "context",
                    (string)item["title"] ?? "Контекст",
                    string.Join(" · ", new[] { (string)item["kind"], (string)item["reference"] }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()),
                    EstimateTextTokens(item.ToString(Formatting.None)),
                    (string)item["content"] ?? string.Empty)).ToList();
                seeds.Add(new SectionSeed
                {
                    Id = "document_context",
                    Title = "Контекст документа",
                    Detail = userContext.Count + " элементов",
                    RawTokens = Math.Max(1, EstimateTextTokens(userContext.ToString(Formatting.None))),
                    Count = userContext.Count,
                    Items = items
                });
            }
            else
            {
                seeds[0].RawTokens += EstimateTextTokens("\"user_context\":[]");
            }

            if (!string.IsNullOrWhiteSpace(artifactIndex))
            {
                var artifactItems = BuildArtifactItems(session, artifactIndex);
                seeds.Add(new SectionSeed
                {
                    Id = "artifacts",
                    Title = "Индекс артефактов",
                    Detail = artifactItems.Count + " ссылок; содержимое не вставляется целиком",
                    RawTokens = Math.Max(1, EstimateTextTokens(artifactIndex)),
                    Count = artifactItems.Count,
                    Items = artifactItems
                });
            }
            return seeds;
        }

        private PromptContextItemDto BuildToolItem(JObject item)
        {
            var function = item["function"] as JObject ?? new JObject();
            var safety = item["safety"] as JObject;
            var safetyText = safety == null
                ? string.Empty
                : string.Join(" · ", new[]
                {
                    (bool?)safety["mutates_document"] == true ? "изменяет документ" : null,
                    (bool?)safety["mutates_local_state"] == true ? "изменяет локальные данные" : null,
                    (bool?)safety["requires_confirmation"] == true ? "подтверждение" : null
                }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
            return Item(
                (string)function["name"],
                "tool",
                (string)function["name"] ?? "Tool",
                safetyText,
                EstimateTextTokens(item.ToString(Formatting.None)),
                item.ToString(Formatting.Indented));
        }

        private List<SectionSeed> BuildCurrentSeeds(
            ChatMessage current,
            string instructionEnvelope,
            bool instructionEmbedded,
            IReadOnlyList<ChatAttachment> attachments,
            string draftText)
        {
            var content = current == null ? string.Empty : current.Content ?? string.Empty;
            var prefix = instructionEnvelope + "\n\nUSER_REQUEST:\n";
            if (instructionEmbedded && content.StartsWith(prefix, StringComparison.Ordinal))
            {
                content = content.Substring(prefix.Length);
            }
            var requestText = string.IsNullOrWhiteSpace(content) ? draftText ?? string.Empty : content;
            var requestItems = new List<PromptContextItemDto>
            {
                Item("current-user", "message", "Текущий запрос", "user",
                    EstimateTextTokens(requestText), requestText)
            };
            var seeds = new List<SectionSeed>
            {
                new SectionSeed
                {
                    Id = "current_request",
                    Title = "Текущий запрос",
                    Detail = "Текст в поле и transport overhead",
                    RawTokens = Math.Max(1, EstimateTextTokens(requestText) + 5),
                    Count = 1,
                    Items = requestItems
                }
            };
            AddAttachmentSeed(seeds, attachments);
            return seeds;
        }

        private void AddAttachmentSeed(List<SectionSeed> seeds, IReadOnlyList<ChatAttachment> attachments)
        {
            var list = (attachments ?? new ChatAttachment[0]).Where(item => item != null).ToList();
            if (list.Count == 0) return;
            var items = list.Select(item =>
            {
                var tokens = AttachmentTokens(item);
                return new PromptContextItemDto
                {
                    Id = item.Id,
                    Kind = "attachment",
                    Title = item.FileName ?? "Вложение",
                    Subtitle = string.Join(" · ", new[]
                    {
                        item.Kind,
                        item.TextTruncated ? "текст обрезан" : null,
                        item.ExtractionWarning
                    }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()),
                    Tokens = tokens,
                    Characters = Math.Max(item.ExtractedCharCount, (item.ExtractedText ?? string.Empty).Length),
                    SizeBytes = item.Size,
                    Preview = BoundText(item.ExtractedText, MaxPreviewChars),
                    Reference = item.Id
                };
            }).ToList();
            seeds.Add(new SectionSeed
            {
                Id = "attachments",
                Title = "Вложения",
                Detail = list.Count + " файлов",
                RawTokens = Math.Max(1, items.Sum(item => item.Tokens)),
                Count = list.Count,
                Items = items
            });
        }

        private int AttachmentTokens(ChatAttachment attachment)
        {
            if (attachment == null) return 0;
            var tokens = ModelContextBudget.EstimateCharacterCountTokens(
                Math.Max(attachment.ExtractedCharCount, (attachment.ExtractedText ?? string.Empty).Length),
                _estimationSettings);
            if (string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase))
            {
                tokens += ModelContextBudget.EstimatedImageTokens;
            }
            if (string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase))
            {
                tokens += ModelContextBudget.EstimateAudioTokens(attachment.Size);
            }
            return tokens;
        }

        private void AddMessageSection(
            ICollection<PromptContextSectionDto> sections,
            string id,
            string title,
            string detail,
            IReadOnlyList<ChatMessage> messages)
        {
            if (messages == null || messages.Count == 0) return;
            var items = messages.Select(BuildMessageItem).Reverse().ToList();
            sections.Add(new PromptContextSectionDto
            {
                Id = id,
                Title = title,
                Tokens = messages.Sum(message => EstimateMessageTokens(message)),
                Count = messages.Count,
                Detail = detail,
                Included = true,
                Items = BoundItems(items)
            });
        }

        private PromptContextItemDto BuildMessageItem(ChatMessage message)
        {
            var role = message == null ? string.Empty : message.Role ?? string.Empty;
            var toolName = message == null ? string.Empty : message.ToolName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(toolName) && message != null && message.ToolCalls != null)
            {
                toolName = message.ToolCalls.Where(call => call != null).Select(call => call.Name).FirstOrDefault() ?? string.Empty;
            }
            var title = string.IsNullOrWhiteSpace(toolName)
                ? RoleTitle(role)
                : toolName;
            var preview = message == null ? string.Empty : message.Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(preview) && message != null && message.ToolCalls != null && message.ToolCalls.Count > 0)
            {
                preview = JsonConvert.SerializeObject(message.ToolCalls, Formatting.Indented);
            }
            var attachmentNames = message == null || message.Attachments == null
                ? string.Empty
                : string.Join(", ", message.Attachments.Where(item => item != null).Select(item => item.FileName).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
            return new PromptContextItemDto
            {
                Id = message == null ? string.Empty : message.Id,
                Kind = IsProtocolMessage(message) ? "protocol" : "message",
                Title = title,
                Subtitle = string.Join(" · ", new[] { role, attachmentNames }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()),
                Tokens = EstimateMessageTokens(message),
                Characters = preview.Length,
                Preview = BoundText(preview, MaxPreviewChars),
                Reference = message == null ? string.Empty : message.RunId ?? string.Empty
            };
        }

        private PromptContextSectionDto BuildExcludedSection(ChatSession sourceSession, ChatSession previewSession, bool includeProtocol)
        {
            var active = PromptBudgetComposer.ConversationHistory(previewSession, includeProtocol, true);
            var activeIds = new HashSet<string>(
                active.Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id)).Select(item => item.Id),
                StringComparer.OrdinalIgnoreCase);
            var excluded = (sourceSession.Messages ?? new List<ChatMessage>())
                .Where(item => item != null && !activeIds.Contains(item.Id))
                .ToList();
            if (excluded.Count == 0) return null;
            var items = excluded.AsEnumerable().Reverse().Select(item => new PromptContextItemDto
            {
                Id = item.Id,
                Kind = "excluded",
                Title = item.Activity != null ? item.Activity.Title ?? "UI activity" : RoleTitle(item.Role),
                Subtitle = item.Role ?? string.Empty,
                Tokens = 0,
                Characters = (item.Content ?? string.Empty).Length,
                Preview = BoundText(item.Content, MaxPreviewChars),
                Reference = item.RunId ?? string.Empty,
                Reason = ExcludedReason(item, includeProtocol)
            }).ToList();
            return new PromptContextSectionDto
            {
                Id = "excluded",
                Title = "Не отправляется сейчас",
                Tokens = 0,
                Count = excluded.Count,
                Detail = "История сохранена локально, но не входит в активный prompt",
                Included = false,
                Items = BoundItems(items)
            };
        }

        private static string ExcludedReason(ChatMessage message, bool includeProtocol)
        {
            if (message == null) return "не входит в активное окно";
            if (message.ExcludeFromModelContext) return "исключено из model context";
            if (message.Activity != null) return "UI activity без model message";
            if (message.ProtocolMessage && !includeProtocol) return "tool protocol не отправляется в Chat mode";
            return "заменено checkpoint или не входит в активное окно";
        }

        private List<PromptContextItemDto> BuildArtifactItems(ChatSession session, string artifactIndex)
        {
            var items = new List<PromptContextItemDto>();
            foreach (var artifact in (session == null ? null : session.Artifacts) ?? new List<ChatArtifact>())
            {
                if (artifact == null || string.IsNullOrWhiteSpace(artifact.Id) ||
                    artifactIndex.IndexOf(artifact.Id, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                var line = artifactIndex.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(value => value.IndexOf(artifact.Id, StringComparison.OrdinalIgnoreCase) >= 0) ?? artifact.Id;
                items.Add(new PromptContextItemDto
                {
                    Id = artifact.Id,
                    Kind = "artifact",
                    Title = artifact.Title ?? artifact.Id,
                    Subtitle = string.Join(" · ", new[]
                    {
                        artifact.Kind,
                        "rev. " + Math.Max(1, artifact.Revision)
                    }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()),
                    Tokens = EstimateTextTokens(line),
                    Characters = (artifact.InlineText ?? string.Empty).Length,
                    SizeBytes = ArtifactStoredBytes(session, artifact),
                    Preview = BoundText(line, MaxPreviewChars),
                    Reference = artifact.Id
                });
            }
            if (items.Count == 0 && !string.IsNullOrWhiteSpace(artifactIndex))
            {
                items.Add(Item("artifact-index", "artifact", "Индекс артефактов", "reference only",
                    EstimateTextTokens(artifactIndex), artifactIndex));
            }
            return items;
        }

        private long ArtifactStoredBytes(ChatSession session, ChatArtifact artifact)
        {
            if (artifact == null) return 0;
            var metadataSize = MetadataSize(artifact.MetadataJson);
            if (metadataSize > 0) return metadataSize;
            if (artifact.ContentByteLength.HasValue) return Math.Max(0, artifact.ContentByteLength.Value);
            if (!string.IsNullOrEmpty(artifact.InlineText)) return Encoding.UTF8.GetByteCount(artifact.InlineText);
            return _paths == null
                ? 0
                : SafeRelativeFileLength(_paths.AttachmentDirectory, artifact.RelativePath);
        }

        private static long MetadataSize(string metadataJson)
        {
            if (string.IsNullOrWhiteSpace(metadataJson)) return 0;
            try
            {
                var root = JObject.Parse(metadataJson);
                return (long?)root["Size"] ?? (long?)root["size"] ?? 0;
            }
            catch (JsonException)
            {
                return 0;
            }
        }

        private static long SafeRelativeFileLength(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath)) return 0;
            try
            {
                var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var candidate = Path.GetFullPath(Path.Combine(rootPath, relativePath));
                if (!candidate.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) return 0;
                return FileLength(candidate);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return 0;
            }
        }

        private static long FileLength(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return 0;
            }
        }

        private static void AddAllocatedSections(
            ICollection<PromptContextSectionDto> sections,
            IReadOnlyList<SectionSeed> seeds,
            int tokenBudget)
        {
            if (tokenBudget <= 0 || seeds == null || seeds.Count == 0) return;
            var included = seeds.Where(seed => seed != null).ToList();
            if (included.Count == 0) return;
            var rawTotal = included.Sum(seed => Math.Max(1, seed.RawTokens));
            var remaining = tokenBudget;
            for (var index = 0; index < included.Count; index++)
            {
                var seed = included[index];
                var allocated = index == included.Count - 1
                    ? remaining
                    : Math.Min(remaining, (int)Math.Floor(tokenBudget * Math.Max(1, seed.RawTokens) / (double)rawTotal));
                remaining -= allocated;
                sections.Add(new PromptContextSectionDto
                {
                    Id = seed.Id,
                    Title = seed.Title,
                    Tokens = allocated,
                    Count = seed.Count,
                    Detail = seed.Detail,
                    Included = true,
                    Items = BoundItems(seed.Items, allocated)
                });
            }
        }

        private static IReadOnlyList<PromptContextItemDto> BoundItems(
            IReadOnlyList<PromptContextItemDto> source,
            int sectionTokens = -1)
        {
            var items = (source ?? new PromptContextItemDto[0])
                .Where(item => item != null)
                .Select(CloneItem)
                .ToList();
            if (sectionTokens >= 0 && items.Count > 0)
            {
                var rawTotal = items.Sum(item => Math.Max(1, item.Tokens));
                var remaining = sectionTokens;
                for (var index = 0; index < items.Count; index++)
                {
                    var allocated = index == items.Count - 1
                        ? remaining
                        : Math.Min(remaining, (int)Math.Floor(sectionTokens * Math.Max(1, items[index].Tokens) / (double)rawTotal));
                    items[index].Tokens = allocated;
                    remaining -= allocated;
                }
            }
            items = items.OrderByDescending(item => item.Tokens).ToList();
            if (items.Count <= MaxSectionItems) return items;
            var visible = items.Take(MaxSectionItems - 1).ToList();
            var omitted = items.Skip(MaxSectionItems - 1).ToList();
            visible.Add(new PromptContextItemDto
            {
                Id = "omitted",
                Kind = "summary",
                Title = "Ещё " + omitted.Count + " элементов",
                Tokens = omitted.Sum(item => item.Tokens),
                Characters = omitted.Sum(item => item.Characters),
                SizeBytes = omitted.Sum(item => item.SizeBytes),
                Reason = "Список сокращён только в интерфейсе"
            });
            return visible;
        }

        private static PromptContextItemDto CloneItem(PromptContextItemDto item)
        {
            return new PromptContextItemDto
            {
                Id = item.Id,
                Kind = item.Kind,
                Title = item.Title,
                Subtitle = item.Subtitle,
                Tokens = item.Tokens,
                Characters = item.Characters,
                SizeBytes = item.SizeBytes,
                Preview = item.Preview,
                Reference = item.Reference,
                Reason = item.Reason
            };
        }

        private static PromptContextItemDto Item(
            string id,
            string kind,
            string title,
            string subtitle,
            int tokens,
            string preview)
        {
            return new PromptContextItemDto
            {
                Id = id ?? string.Empty,
                Kind = kind ?? string.Empty,
                Title = title ?? string.Empty,
                Subtitle = subtitle ?? string.Empty,
                Tokens = Math.Max(0, tokens),
                Characters = (preview ?? string.Empty).Length,
                Preview = BoundText(preview, MaxPreviewChars)
            };
        }

        private static string BuildRawRequest(
            string mode,
            string model,
            IEnumerable<ChatMessage> messages,
            LlmRequestOptions options)
        {
            return JsonConvert.SerializeObject(new
            {
                mode = mode,
                model = model ?? string.Empty,
                estimated = true,
                messages = (messages ?? new ChatMessage[0]).Where(message => message != null).Select(message => new
                {
                    role = message.Role,
                    content = message.Content,
                    protocolMessage = message.ProtocolMessage,
                    toolCallId = message.ToolCallId,
                    toolName = message.ToolName,
                    toolCalls = message.ToolCalls,
                    attachments = (message.Attachments ?? new List<ChatAttachment>()).Where(item => item != null).Select(item => new
                    {
                        id = item.Id,
                        fileName = item.FileName,
                        kind = item.Kind,
                        size = item.Size,
                        extractedCharCount = item.ExtractedCharCount,
                        textTruncated = item.TextTruncated
                    })
                }),
                requestOptions = options == null ? null : new
                {
                    responseFormat = options.ResponseFormat,
                    responseSchemaName = options.ResponseSchemaName,
                    responseSchemaJson = options.ResponseSchemaJson,
                    reasoningEnabled = options.ReasoningEnabled
                }
            }, Formatting.Indented, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
        }

        private static bool IsInstructionRole(string role)
        {
            return string.Equals(role, "system", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsProtocolMessage(ChatMessage message)
        {
            return message != null && (message.ProtocolMessage ||
                (message.ToolCalls != null && message.ToolCalls.Count > 0) ||
                !string.IsNullOrWhiteSpace(message.ToolCallId));
        }

        private static string RoleTitle(string role)
        {
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)) return "Сообщение пользователя";
            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)) return "Ответ ассистента";
            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase)) return "Результат tool";
            if (string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase)) return "Developer message";
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)) return "System message";
            return string.IsNullOrWhiteSpace(role) ? "Сообщение" : role;
        }

        private static string BoundText(string value, int maxChars)
        {
            value = value ?? string.Empty;
            if (value.Length <= maxChars) return value;
            return value.Substring(0, maxChars).TrimEnd() + "…";
        }

        private int EstimateTextTokens(string text)
        {
            return ModelContextBudget.EstimateTextTokens(text, _estimationSettings);
        }

        private int EstimateMessageTokens(ChatMessage message)
        {
            return ModelContextBudget.EstimateMessageTokens(message, _estimationSettings);
        }

        private int EstimateMessagesTokens(IEnumerable<ChatMessage> messages)
        {
            return ModelContextBudget.EstimateMessagesTokens(messages, _estimationSettings);
        }

        private int EstimateRequestOptionsTokens(LlmRequestOptions options)
        {
            return ModelContextBudget.EstimateRequestOptionsTokens(options, _estimationSettings);
        }

        private sealed class SectionSeed
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string Detail { get; set; }
            public int RawTokens { get; set; }
            public int Count { get; set; }
            public List<PromptContextItemDto> Items { get; set; }

            public SectionSeed()
            {
                Items = new List<PromptContextItemDto>();
            }
        }
    }
}

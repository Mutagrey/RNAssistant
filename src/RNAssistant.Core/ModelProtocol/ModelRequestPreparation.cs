using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.ModelProtocol
{
    // Host owners prepare authority and domain prompts; this binds one callable
    // surface to its schema, wire projection, budget and outgoing request.
    public sealed class ModelRequestPreparation
    {
        private readonly AppSettings _settings;
        private readonly ModelProtocolCallContext _context;
        private readonly Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolCatalogEntry>, string, IReadOnlyList<ChatMessage>> _project;
        public IReadOnlyList<ToolCatalogEntry> Tools { get; private set; }
        public LlmRequestOptions Options { get; private set; }

        public ModelRequestPreparation(AppSettings settings, IReadOnlyList<ToolCatalogEntry> tools,
            ModelProtocolCallContext context,
            Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolCatalogEntry>, string, IReadOnlyList<ChatMessage>> project)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Tools = Array.AsReadOnly((tools ?? throw new ArgumentNullException(nameof(tools))).ToArray());
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _project = project ?? throw new ArgumentNullException(nameof(project));
            Options = ModelProtocolWire.CreateRequestOptions(settings.AgentResponseMode, Tools, context);
        }

        public IReadOnlyList<ChatMessage> ProjectMessages(IReadOnlyList<ChatMessage> messages)
        {
            var strict = Options.ResponseFormat == LlmResponseFormats.JsonSchema;
            return ModelReferenceProjection.Project(ModelProtocolWire.ProjectGuidance(
                strict ? ProjectSchemaMessages(messages) : messages, strict, _context.ReflectionEnabled));
        }

        private IReadOnlyList<ChatMessage> ProjectSchemaMessages(IReadOnlyList<ChatMessage> messages)
        { return _project(messages, Tools, Options.ResponseSchemaJson); }

        public int MessageBudget(IReadOnlyList<ChatMessage> required)
        {
            var fixedTokens = ModelContextBudget.EstimateRequestOptionsTokens(Options, _settings) +
                ModelProtocolClient.EstimateFormatRepairOverheadTokens(_settings) +
                ModelContextBudget.ContinuationReserveTokens(_settings);
            var budget = Math.Max(1, ModelContextBudget.InputBudgetTokens(_settings) - fixedTokens);
            return ModelContextBudget.IncludeProjectedMessageSavings(budget, required, ProjectMessages(required), _settings);
        }

        public int EstimateAdmittedTokens(IReadOnlyList<ChatMessage> messages)
        {
            return ModelContextBudget.EstimateAdmittedRequestTokens(ProjectMessages(messages), Options, _settings,
                ModelProtocolClient.EstimateFormatRepairOverheadTokens(_settings),
                ModelContextBudget.ContinuationReserveTokens(_settings));
        }

        public ModelProtocolRequest CreateRequest(ModelContextSnapshot snapshot,
            IReadOnlyList<ToolCatalogEntry> runnableCatalog,
            Func<ChatMessage, IReadOnlyList<ChatMessage>> compileRepair)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var exposed = new HashSet<string>(snapshot.Receipt.Arsenal.Tools.Select(tool => tool.Id), StringComparer.Ordinal);
            if (!exposed.SetEquals(Tools.Select(tool => tool.Id)))
                throw new InvalidOperationException("Prepared request tools do not match the compiled snapshot arsenal.");
            return new ModelProtocolRequest
            {
                Settings = _settings, AcceptedMessages = snapshot.Messages, ContextSnapshot = snapshot,
                CallableTools = Tools, RunnableCatalog = runnableCatalog, CallContext = _context, Options = Options,
                CompileRepair = compileRepair, ProjectJsonSchemaMessages = ProjectSchemaMessages,
                ProjectReferences = ModelReferenceProjection.Project
            };
        }
    }
}

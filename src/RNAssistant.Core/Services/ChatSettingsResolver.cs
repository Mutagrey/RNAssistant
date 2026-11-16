using System;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ChatSettingsResolver
    {
        public static AppSettings Resolve(AppSettings settings, ChatSession session)
        {
            return ResolveModel(settings, session?.Model);
        }

        public static AppSettings ResolveAcceptedRun(AppSettings settings, ChatSession session)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var accepted = session?.LastRun?.ModelConfiguration;
            var model = accepted?.Model;
            if (string.IsNullOrWhiteSpace(model)) model = session?.Model;
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("The accepted run has no recorded model.");
            return ResolveModel(settings, model);
        }

        public static ModelRunMetadata CaptureRunConfiguration(AppSettings settings, ChatSession session)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (session == null) throw new ArgumentNullException(nameof(session));
            return new ModelRunMetadata
            {
                Model = settings.Model,
                EndpointSha256 = EndpointSha256(settings.BaseUrl),
                ContextWindowTokens = ModelContextBudget.ContextWindowTokens(settings),
                AgentResponseMode = settings.AgentResponseMode,
                SystemPromptRole = settings.SystemPromptRole,
                ToolResultRole = settings.ToolResultRole,
                ReasoningRequestMode = settings.ReasoningRequestMode,
                ReasoningEnabled = session.ReasoningEnabled
            };
        }

        public static void ValidateAcceptedRun(ChatSession session, AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var accepted = session?.LastRun?.ModelConfiguration;
            if (accepted == null) return;
            if (settings.Model != accepted.Model ||
                !string.IsNullOrEmpty(accepted.EndpointSha256) && EndpointSha256(settings.BaseUrl) != accepted.EndpointSha256 ||
                accepted.ContextWindowTokens > 0 && ModelContextBudget.ContextWindowTokens(settings) != accepted.ContextWindowTokens ||
                settings.AgentResponseMode != accepted.AgentResponseMode ||
                settings.SystemPromptRole != accepted.SystemPromptRole ||
                settings.ToolResultRole != accepted.ToolResultRole ||
                settings.ReasoningRequestMode != accepted.ReasoningRequestMode ||
                session.ReasoningEnabled != accepted.ReasoningEnabled)
                throw new InvalidOperationException("Continuation must keep the accepted endpoint, model, context and protocol roles/settings.");
        }

        public static void RequireEditablePreferences(ChatSession session)
        {
            ConversationRunPolicy.RequireCurrentSession(session);
            var lifecycle = session.LastRun?.KernelState?.Summary?.Lifecycle;
            if ((lifecycle == RunLifecycle.Running || lifecycle == RunLifecycle.Waiting) || lifecycle == RunLifecycle.AwaitingConfirmation ||
                string.Equals(session.LastRun?.Status, "running", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Stop or resolve the active run before changing chat preferences.");
        }

        private static AppSettings ResolveModel(AppSettings settings, string model)
        {
            var effective = (settings ?? new AppSettings()).Clone();
            if (!string.IsNullOrWhiteSpace(model)) effective.Model = model.Trim();
            return effective;
        }

        private static string EndpointSha256(string baseUrl)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(baseUrl ?? string.Empty));
                var result = new System.Text.StringBuilder(bytes.Length * 2);
                foreach (var value in bytes) result.Append(value.ToString("x2"));
                return result.ToString();
            }
        }
    }
}

using System;
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
            var model = session?.LastRun?.ModelConfiguration?.Model;
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("The accepted run has no recorded model.");
            return ResolveModel(settings, model);
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
    }
}

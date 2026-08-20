using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class AgentRouteResolver
    {
        public RoutedTask Route(OfficeSnapshot snapshot, ChatSession session)
        {
            var route = Route(snapshot);
            if (session == null || !session.HtmlModeEnabled)
            {
                return route;
            }

            var existingWorkspace = HasHtmlWorkspaceContent(session);
            route.Mode = "html_workspace";
            route.TaskType = "html";
            route.Phase = existingWorkspace ? AgentPhases.ReadOnly : AgentPhases.Mutation;
            route.RequiresTool = true;
            route.RequiresInspection = existingWorkspace;
            route.DecisionReason = "explicit_html_mode";
            return route;
        }

        public RoutedTask Route(OfficeSnapshot snapshot)
        {
            return new RoutedTask
            {
                App = AgentText.FirstNonEmpty(snapshot == null ? null : snapshot.Host, "Office"),
                Mode = "agent",
                TaskType = "agent",
                Phase = AgentPhases.Decision,
                RequiresTool = false,
                RequiresInspection = false,
                DecisionReason = "model_decision"
            };
        }

        private static bool HasHtmlWorkspaceContent(ChatSession session)
        {
            return session != null &&
                session.HtmlWorkspace != null &&
                ((session.HtmlWorkspace.Files != null && session.HtmlWorkspace.Files.Count > 0) ||
                 (session.HtmlWorkspace.DataSources != null && session.HtmlWorkspace.DataSources.Count > 0));
        }
    }
}

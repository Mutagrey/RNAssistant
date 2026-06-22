using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal sealed class HtmlArtifactToolExecutor
    {
        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return new ToolDefinition
            {
                Id = "common.render_html",
                Host = "Common",
                Name = "render_html",
                Description = "Experimental: render a raw HTML component in the chat. The HTML may include scripts, but RNAssistant shows it only when unsafe HTML artifacts are enabled in Settings and renders it in a sandboxed iframe without Office bridge access.",
                ArgumentSchemaJson = "{\"title\":\"Component title\",\"html\":\"<html or fragment>\",\"height\":360}",
                BuiltIn = true,
                Enabled = true,
                MutatesDocument = false,
                AgentCanRun = true
            };
        }

        public bool IsControllerTool(string toolId)
        {
            return string.Equals(toolId, "common.render_html", StringComparison.OrdinalIgnoreCase);
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, AppSettings settings)
        {
            settings = settings ?? new AppSettings();
            if (!settings.AllowUnsafeHtmlArtifacts)
            {
                return ToolResult.Fail("Unsafe HTML artifacts are disabled. Enable them in Settings > Interface before using common.render_html.");
            }

            var html = ToolArgumentReader.String(command.Arguments, "html", string.Empty);
            if (string.IsNullOrWhiteSpace(html))
            {
                return ToolResult.Fail("html is required.");
            }
            if (html.Length > 200000)
            {
                return ToolResult.Fail("HTML artifact is too large. Limit is 200000 characters.");
            }

            var height = Math.Max(180, Math.Min(900, ToolArgumentReader.Int32(command.Arguments, "height", 360)));
            var title = ToolArgumentReader.String(command.Arguments, "title", "HTML component");
            return ToolResult.Ok("HTML artifact created: " + title, JsonConvert.SerializeObject(new
            {
                type = "rnassistant.html",
                version = 1,
                title = title,
                html = html,
                height = height
            }));
        }
    }
}

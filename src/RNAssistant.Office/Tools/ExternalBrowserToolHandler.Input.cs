using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class ExternalBrowserToolHandler
    {
        private static void AddPointerSchema(Dictionary<string, object> properties)
        {
            properties["button"] = new { type = "string", description = "Native pointer button.", @enum = new[] { "left", "right", "middle" } };
            properties["count"] = new { type = "integer", description = "One click or one native double-click sequence.", minimum = 1, maximum = 2 };
            properties["durationMs"] = new { type = "integer", description = "Bounded hold duration; the same call releases the button.", minimum = 0, maximum = 1500 };
            properties["modifiers"] = new { type = "array", description = "Bounded native pointer modifiers; no held state survives this call.", maxItems = 3,
                items = new { type = "string", @enum = new[] { "Alt", "Control", "Shift" } } };
        }
        private static IEnumerable<ToolCatalogEntry> InputCatalog()
        {
            foreach (var id in new[] { Drag, Hover, Scroll, PressKey, Select, Check })
            {
                var properties = new Dictionary<string, object> {
                    ["target"] = new { type = "string", description = "Exact live Browser page from core.find/read in this conversation.", minLength = 1, maxLength = 240 },
                    ["name"] = new { type = "string", description = "Exact observed element or scroll region name; viewport only if delivered by the page observation.", minLength = 1, maxLength = 160 }
                };
                var required = new List<string> { "target", "name" };
                if (id == Drag) { AddPointerSchema(properties); properties["toName"] = new { type = "string", description = "Exact observed destination element.", minLength = 1, maxLength = 160 }; required.Add("toName"); }
                if (id == Hover) properties["durationMs"] = new { type = "integer", description = "Maximum hover duration in this call.", minimum = 0, maximum = 1500 };
                if (id == Scroll) {
                    properties["deltaX"] = new { type = "integer", description = "Horizontal CSS-pixel wheel delta.", minimum = -2000, maximum = 2000 };
                    properties["deltaY"] = new { type = "integer", description = "Vertical CSS-pixel wheel delta.", minimum = -2000, maximum = 2000 }; required.Add("deltaY");
                }
                if (id == PressKey) {
                    properties["key"] = new { type = "string", description = "Allowlisted in-page key/chord. OS, browser chrome and file-dialog shortcuts are forbidden.",
                        @enum = new[] { "Enter", "Escape", "Tab", "Shift+Tab", "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End", "PageUp", "PageDown", "Backspace", "Delete", "Space", "Control+A", "Shift+Home", "Shift+End" } };
                    properties["count"] = new { type = "integer", description = "Bounded repeat count; keys are released in the same call.", minimum = 1, maximum = 5 }; required.Add("key");
                }
                if (id == Select) { properties["value"] = new { type = "string", description = "Exact observed option name to select.", minLength = 1, maxLength = 160 }; required.Add("value"); }
                if (id == Check) { properties["checked"] = new { type = "boolean", description = "Desired observed checkbox/radio state." }; required.Add("checked"); }
                var descriptor = new ToolDescriptor(id, "Bounded qualified native input on one owned local page. Requires a fresh complete delivered observation, exact control/layout revisions and attributable physical input. " +
                    "Read-back must prove the requested state change; dispatch alone is unknown. All held input releases in finally. Unqualified native isolation/input is refused, never replaced with synthetic DOM events.",
                    JsonConvert.SerializeObject(new { type = "object", properties, required, additionalProperties = false }));
                yield return ControllerToolCatalogEntry.CreateTypedProjection(descriptor,
                    new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, true, false, new[] { "agent" }, 1), BindingFor(id), scope: "session", mutatesLocalState: true);
            }
        }
        private static BrowserActionRequest InputRequest(ToolHandlerContext context, string name, string locator)
        {
            var json = JObject.FromObject(context.Arguments);
            var modifiers = json["modifiers"]?.ToObject<string[]>() ?? new string[0];
            if (modifiers.Distinct(StringComparer.Ordinal).Count() != modifiers.Length) throw Error("Duplicate native modifiers are not allowed.", "browser_action_invalid");
            return new BrowserActionRequest { Action = context.Execution.Call.Name.Substring("browser.".Length), Name = name,
                Locator = locator, Value = (string)json["value"], ExpectedText = (string)json["expectedText"], ToName = (string)json["toName"],
                Button = (string)json["button"] ?? "left", Count = (int?)json["count"] ?? 1, DurationMs = (int?)json["durationMs"] ?? 0,
                Modifiers = modifiers, DeltaX = (int?)json["deltaX"] ?? 0,
                DeltaY = (int?)json["deltaY"] ?? 0, Key = (string)json["key"], Checked = (bool?)json["checked"] };
        }
    }
}

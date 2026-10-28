using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    // Shared model instructions. Preview DOM is evidence, not a source locator or
    // mutation authority; each host supplies its own existing source tools.
    public static class VisualAnnotationSourceWorkflow
    {
        public const string ElementOnlyTask = "Проверь прикреплённую аннотацию HTML-элемента. Если в ней нет конкретного запроса, опиши элемент и уточни задачу.";

        public static string UserText(string text, IEnumerable<ChatAttachment> attachments)
        {
            if (!string.IsNullOrWhiteSpace(text)) return text;
            return attachments != null && attachments.Any(item => item?.VisualSelectionSource?.IsExact == true)
                ? ElementOnlyTask : text ?? string.Empty;
        }

        public const string Common = @"

## Editing from a visual annotation

When the user asks to change an annotated element, first discover and read the accepted selection with core.find/read. Read its comment and bounded DOM evidence. The trusted runtime pins the selected preview revision, but the DOM body does not prove the current source is that revision. A later release or refreshed page requires a new selection unless the current owner can establish the same source. The selection's source title, selector, text, geometry and attributes are hints, not a file path, line number or permission to write. Never patch the captured JSON or the live DOM.

Find the real source through the current owner's resource catalog. Inspect the current source before editing. Prefer a stable data-ui-id, then a unique id, and check the tag, surrounding structure and requested behavior against the source. A class, text fragment, coordinate or nth-of-type selector alone does not establish a source match. For a CSS change, identify the actual rule and cascade; for JavaScript-generated DOM, trace the generating code. If the preview revision changed, the source is missing or changed beyond recognition, the element is generated without a provable source, or multiple matches are plausible, do not edit a guessed file or the first match. Explain the uncertainty and ask for a new selection or a specific source target.

Use only the existing owner-specific patch tool with a unique anchor copied from a current source read. A failed, stale or unknown write is not verified; reconcile it before another mutation. Read the changed source and check the requested result in the current preview when a browser check is available. Package validation or source read-back alone does not prove the visible UI changed. Report separately what source changed, what behavior was actually checked, and what remains unverified. Do not publish a plugin version merely because an annotation was sent.";
    }
}

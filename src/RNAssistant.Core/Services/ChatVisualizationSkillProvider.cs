using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ChatVisualizationSkillProvider
    {
        public const string Id = "common.chat_visualization";

        public static SkillDefinition Create()
        {
            return new SkillDefinition
            {
                Id = Id, Host = "Common", Name = "Chat visualization",
                Description = "Explain processes, relationships, comparisons and data directly in chat with Mermaid, tables or interactive offline HTML charts.",
                BuiltIn = true, Enabled = true, Version = "1.0.1",
                Package = new SkillPackageManifest { PackageId = Id },
                BodyMarkdown = @"# Visual answers in chat
Choose a visual when it makes the answer easier to understand. Keep a short textual explanation and use the user's language for labels. Output the block in the response message, not as a workspace file or a tool call.

- Processes and relationships: a closed fenced mermaid block. Prefer flowchart, sequenceDiagram, stateDiagram-v2 or erDiagram; quote human-readable labels. Keep diagrams compact, with clear direction and short labels. Do not emit front matter, init directives, links, HTML labels or external images. The UI supplies theme and security settings. Limit to 30,000 characters and 300 edges.
- Comparisons and exact values: a Markdown table with meaningful headers and units. Split very wide tables into useful groups; do not replace a small readable table with HTML.
- Charts and interactive explanations: a closed fenced html block containing a complete self-contained HTML/CSS/JavaScript document (up to 200,000 characters). It starts only when the user presses Play. Inline CSS and classic JavaScript are supported. No network, CDN, imports, external assets, frames, files, Office APIs, storage assumptions or parent-window messaging. Do not paste library bundles.
- ECharts is supplied locally when the HTML contains echarts.init or another echarts member access. Use echarts.init(document.getElementById('chart')), setOption and a resize listener. Give the chart a nonzero responsive width and explicit height (for example 300px). Scripts must follow their target elements. No script src is needed. SVG, canvas and native HTML controls also work offline.

For a small relationship diagram, use Mermaid with rounded process nodes (A(label)); preserve diamonds for conditions and other meaningful shapes. Do not add a title saying Mermaid or HTML. For rich architecture cards with subtitles, icon tiles and grouped service rows, prefer self-contained HTML/CSS with semantic sections, 14-18px radii, generous internal spacing, subtle neutral fills and one restrained accent. Use inline SVG icons without external fonts. HTML still requires Play; never imply it automatically appears. For an explanation consisting of a few titled points, prefer readable Markdown headings and short paragraphs over a diagram or repeated nested cards.

Use a restrained palette, sufficient contrast, clear typography, axes, units, legends when needed and accessible labels. Support narrow widths and both color schemes with prefers-color-scheme. Show actual available data; label invented example data explicitly. Explain the point of the chart rather than merely decorating an answer.

The renderer runs only after the message finishes. A reopened chat requires Play again. Do not claim the user has launched the preview, or that you visually tested it, without evidence. Creating a durable application, connected dashboard or Office artifact belongs to its existing authoring skill; this skill only presents an answer in chat."
            };
        }
    }
}

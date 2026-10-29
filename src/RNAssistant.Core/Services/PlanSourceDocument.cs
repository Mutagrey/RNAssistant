using System;
using System.IO;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    // Editable source view of the existing Plan record; not a second stored document.
    public sealed class PlanSourceDocument
    {
        public const string Instructions = "Plans use target=plan: <unique title> with core.write for creation/complete overwrite and core.patch for edits. Read representation=source first; it starts with four lines: ---, title: <JSON-quoted title>, status: draft|ready, ---, then the exact Markdown body. Patch status to mark ready. Use a new title/target for a separate task. Lifecycle uses resources.delete/restore; Task List keeps task.list.set. ";
        public string Title { get; set; }
        public string Status { get; set; }
        public string Markdown { get; set; }

        public static bool IsTarget(string target)
        { return target != null && target.StartsWith("plan: ", StringComparison.OrdinalIgnoreCase); }

        public static string Target(string title) { return "plan: " + title; }

        public static string Serialize(ChatArtifact artifact)
        {
            return Serialize(new PlanSourceDocument { Title = artifact.Title,
                Status = PlanDocumentService.Status(artifact), Markdown = artifact.InlineText });
        }

        public static string Serialize(PlanSourceDocument source)
        {
            Validate(source);
            return "---\ntitle: " + JsonConvert.SerializeObject(source.Title) +
                "\nstatus: " + source.Status + "\n---\n" + source.Markdown;
        }

        public static PlanSourceDocument Parse(string text)
        {
            if (text == null) throw new InvalidDataException("Plan source is required.");
            var offset = 0;
            if (Line(text, ref offset) != "---") throw Invalid();
            var title = Line(text, ref offset);
            var status = Line(text, ref offset);
            if (!title.StartsWith("title: ", StringComparison.Ordinal) ||
                !status.StartsWith("status: ", StringComparison.Ordinal) || Line(text, ref offset) != "---") throw Invalid();
            string name;
            try
            {
                var token = Newtonsoft.Json.Linq.JToken.Parse(title.Substring(7));
                if (token.Type != Newtonsoft.Json.Linq.JTokenType.String) throw Invalid();
                name = (string)token;
            }
            catch (JsonException) { throw Invalid(); }
            var result = new PlanSourceDocument { Title = name, Status = status.Substring(8), Markdown = text.Substring(offset) };
            Validate(result);
            return result;
        }

        private static string Line(string text, ref int offset)
        {
            var end = text.IndexOf('\n', offset);
            if (end < 0) throw Invalid();
            var line = text.Substring(offset, end - offset).TrimEnd('\r');
            offset = end + 1;
            return line;
        }

        private static void Validate(PlanSourceDocument source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.Title) || source.Title.Length > 200 ||
                source.Title != source.Title.Trim() || source.Title.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 ||
                (source.Status != "draft" && source.Status != "ready") || string.IsNullOrWhiteSpace(source.Markdown)) throw Invalid();
        }

        private static InvalidDataException Invalid()
        { return new InvalidDataException("Plan source must start with --- followed by title: <JSON-quoted title>, status: draft|ready and ---, each on its own line, then nonempty Markdown. Read representation=source before editing."); }
    }

    // Runtime-only command, pinned by preparation and interpreted by the existing Plan owner.
    public sealed class PlanResourceCommand
    {
        public ResourceRef Source { get; set; }
        public string PlanId { get; set; }
        public string ArtifactId { get; set; }
        public PlanSourceDocument Content { get; set; }
        public int RestoreVersion { get; set; }
        public string RestoreTitle { get; set; }
    }
}

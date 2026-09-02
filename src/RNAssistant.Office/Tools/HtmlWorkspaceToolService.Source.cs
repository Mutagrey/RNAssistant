using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class HtmlWorkspaceToolService
    {
        internal static string WriteFileSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["path"] = new JObject { ["type"] = "string", ["description"] = "Workspace-relative .html, .css, or .js file path.", ["minLength"] = 1, ["maxLength"] = 260 },
                    ["content"] = new JObject { ["type"] = "string", ["description"] = "Complete exact file source after JSON decoding. Preserve line breaks and backslashes; in the outer response JSON encode a real line break as \\n and one literal source backslash as \\\\.", ["maxLength"] = MaxHtmlChars }
                },
                ["required"] = new JArray("path", "content"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        internal static string WriteDataSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["name"] = new JObject { ["type"] = "string", ["description"] = "Stable name exposed through window.RNAssistantData.", ["minLength"] = 1, ["maxLength"] = 128 },
                    ["json"] = new JObject { ["type"] = "string", ["description"] = "Complete exact valid JSON value serialized as text. Escape its quotes and backslashes for the outer response JSON; runtime validates and stores the decoded text unchanged.", ["minLength"] = 1, ["maxLength"] = MaxDataChars }
                },
                ["required"] = new JArray("name", "json"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        internal static string ApplyPatchSchema()
        {
            var find = new JObject
            {
                ["type"] = "string",
                ["description"] = "Non-empty exact decoded source text or unique insertion anchor. Preserve line breaks and backslashes; outer response JSON escaping applies.",
                ["minLength"] = 1,
                ["maxLength"] = MaxHtmlChars
            };
            var text = new JObject
            {
                ["type"] = "string",
                ["description"] = "Exact decoded replacement or inserted source text; preserve line breaks and backslashes. Empty text is valid for replacement/deletion.",
                ["maxLength"] = MaxHtmlChars
            };
            var operations = new JArray
            {
                PatchOperationSchema("replace", "Replace exactly one occurrence; ambiguity is rejected.",
                    new JObject { ["find"] = find.DeepClone(), ["text"] = text.DeepClone() }, "find", "text"),
                PatchOperationSchema("replaceAll", "Replace every exact occurrence explicitly.",
                    new JObject { ["find"] = find.DeepClone(), ["text"] = text.DeepClone() }, "find", "text"),
                PatchOperationSchema("insertBefore", "Insert non-empty text before one unique exact anchor.",
                    new JObject
                    {
                        ["find"] = find.DeepClone(),
                        ["text"] = new JObject { ["type"] = "string", ["description"] = "Non-empty exact decoded source text to insert; preserve line breaks and backslashes.", ["minLength"] = 1, ["maxLength"] = MaxHtmlChars }
                    }, "find", "text"),
                PatchOperationSchema("insertAfter", "Insert non-empty text after one unique exact anchor.",
                    new JObject
                    {
                        ["find"] = find.DeepClone(),
                        ["text"] = new JObject { ["type"] = "string", ["description"] = "Non-empty exact decoded source text to insert; preserve line breaks and backslashes.", ["minLength"] = 1, ["maxLength"] = MaxHtmlChars }
                    }, "find", "text"),
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["path"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Existing workspace-relative HTML, CSS, or JavaScript file path.",
                        ["minLength"] = 1,
                        ["maxLength"] = 260
                    },
                    ["patch"] = new JObject
                    {
                        ["type"] = "array",
                        ["description"] = "Native JSON array of ordered operations applied atomically to current file source.",
                        ["minItems"] = 1,
                        ["maxItems"] = 100,
                        ["items"] = new JObject { ["anyOf"] = operations }
                    }
                },
                ["required"] = new JArray("path", "patch"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static JObject PatchOperationSchema(string operation, string description, JObject properties, params string[] required)
        {
            properties = properties ?? new JObject();
            properties.AddFirst(new JProperty("op", new JObject
            {
                ["type"] = "string",
                ["description"] = description,
                ["enum"] = new JArray(operation)
            }));
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray(new[] { "op" }.Concat(required ?? new string[0])),
                ["additionalProperties"] = false
            };
        }

        private static HtmlWorkspaceToolOutcome ApplyWorkspacePatch(
            ChatSession session,
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = ToolArgumentReader.String(
                    arguments, "path", string.Empty);
                var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
                var file = FindFile(workspace, name, false);
                var before = file.Content ?? string.Empty;
                var patch = StructuredTextPatchEngine.Apply(
                    before, ReadPatchOperations(arguments), MaxHtmlChars);
                ValidateFile(file.Path, file.Kind, patch.Text);
                ValidateWorkspaceCapacity(workspace, file.Id, patch.Text, null, null);
                var changed = !string.Equals(before, patch.Text, StringComparison.Ordinal);
                var preview = new JObject
                {
                    ["type"] = "rnassistant.htmlWorkspacePatch",
                    ["version"] = 1,
                    ["path"] = file.Path,
                    ["kind"] = file.Kind,
                    ["saved"] = changed,
                    ["changed"] = changed,
                    ["oldCharacters"] = before.Length,
                    ["newCharacters"] = patch.Text.Length,
                    ["oldLineCount"] = SourceLineCount(before),
                    ["newLineCount"] = SourceLineCount(patch.Text),
                    ["previousContentSha256"] = TextPatternEngine.Sha256(before),
                    ["contentSha256"] = TextPatternEngine.Sha256(patch.Text),
                    ["operations"] = new JArray(patch.Steps.Select(item => new JObject
                    {
                        ["op"] = item.Op,
                        ["matchCount"] = item.MatchCount,
                        ["message"] = item.Message
                    }))
                };
                if (!changed)
                {
                    preview["revisionArtifactId"] = session.ActiveHtmlArtifactId;
                    return HtmlWorkspaceToolOutcome.Ok(
                        "HTML workspace patch made no content changes: " +
                        file.Path + ".",
                        AddWorkspaceResourceRefs(session, preview)
                            .ToString(Formatting.None),
                        HtmlWorkspaceEffect.VerifiedNoChange);
                }

                markDispatchPossible();
                var saved = UpsertFile(session, file.Path, file.Kind, patch.Text, false);
                preview["revisionArtifactId"] = session.ActiveHtmlArtifactId;
                return HtmlWorkspaceToolOutcome.Ok(
                    "HTML workspace patch applied: " + saved.Path + ".",
                    AddWorkspaceResourceRefs(session, preview)
                        .ToString(Formatting.None),
                    HtmlWorkspaceEffect.VerifiedChange);
            }
            catch (StructuredTextPatchException ex)
            {
                return HtmlWorkspaceToolOutcome.Error(
                    ex.Message, null, ex.ErrorCode, true);
            }
            catch (JsonException ex)
            {
                return HtmlWorkspaceToolOutcome.Error(
                    "Invalid HTML workspace patch: " + ex.Message, null,
                    "text_patch_invalid", true);
            }
        }

        private static List<StructuredTextPatchOperation> ReadPatchOperations(
            IDictionary<string, object> arguments)
        {
            object raw;
            if (arguments == null ||
                !arguments.TryGetValue("patch", out raw) || raw == null)
            {
                return new List<StructuredTextPatchOperation>();
            }
            var token = raw as JToken;
            var array = token as JArray;
            if (array == null)
            {
                var text = raw as string;
                array = text == null ? JArray.FromObject(raw) : JArray.Parse(text);
            }

            var result = new List<StructuredTextPatchOperation>();
            foreach (var tokenItem in array)
            {
                var item = tokenItem as JObject;
                if (item == null)
                {
                    throw new JsonException("Each HTML patch operation must be an object.");
                }

                var unsupported = item.Properties().FirstOrDefault(property =>
                    property.Name != "op" && property.Name != "find" &&
                    property.Name != "text");
                if (unsupported != null)
                {
                    throw new JsonException(
                        "Unsupported HTML patch property: " + unsupported.Name + ".");
                }

                var op = (string)item["op"];
                if (op != "replace" && op != "replaceAll" &&
                    op != "insertBefore" && op != "insertAfter")
                {
                    throw new JsonException("Unsupported HTML patch operation: " +
                        (op ?? "<missing>") + ".");
                }

                result.Add(new StructuredTextPatchOperation
                {
                    Op = op,
                    Find = (string)item["find"],
                    Text = (string)item["text"]
                });
            }
            return result;
        }

        private static int SourceLineCount(string source)
        {
            return SourceLineStarts(source).Count;
        }

        private static List<int> SourceLineStarts(string source)
        {
            source = source ?? string.Empty;
            var starts = new List<int> { 0 };
            for (var index = 0; index < source.Length; index++)
            {
                if (source[index] == '\r')
                {
                    if (index + 1 < source.Length && source[index + 1] == '\n') index++;
                    starts.Add(index + 1);
                }
                else if (source[index] == '\n')
                {
                    starts.Add(index + 1);
                }
            }
            return starts;
        }

        private static void SourcePosition(List<int> lineStarts, int sourceIndex, out int line, out int column)
        {
            lineStarts = lineStarts ?? new List<int> { 0 };
            var found = lineStarts.BinarySearch(Math.Max(0, sourceIndex));
            var lineIndex = found >= 0 ? found : Math.Max(0, ~found - 1);
            line = lineIndex + 1;
            column = Math.Max(1, sourceIndex - lineStarts[lineIndex] + 1);
        }

        private static string CurrentSourceNewLine(string source)
        {
            source = source ?? string.Empty;
            return source.IndexOf("\r\n", StringComparison.Ordinal) >= 0
                ? "\r\n"
                : source.IndexOf('\r') >= 0 ? "\r" : "\n";
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Domains.Outlook;

namespace RNAssistant.Office.Tools
{
    internal sealed class OutlookToolAdapter
    {
        private readonly OutlookService _service;

        internal OutlookToolAdapter(IOutlookBackend backend)
        {
            _service = new OutlookService(
                backend ?? throw new ArgumentNullException(nameof(backend)));
        }

        internal OutlookOutcome Execute(
            string toolId,
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            arguments = arguments ??
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.Equals(
                toolId, OutlookToolIds.SearchMail, StringComparison.Ordinal))
                return _service.SearchMail(new OutlookSearchMailRequest
                {
                    Query = ToolArgumentReader.String(
                        arguments, "query", string.Empty),
                    Mode = ToolArgumentReader.String(
                        arguments, "mode", "literal"),
                    MatchCase = ToolArgumentReader.Boolean(
                        arguments, "matchCase", false),
                    WholeWord = ToolArgumentReader.Boolean(
                        arguments, "wholeWord", false),
                    Fields = ToolArgumentReader.String(
                        arguments, "fields", "subject,sender,body"),
                    MaxItems = ToolArgumentReader.Int32(
                        arguments, "maxItems", 100),
                    MaxResults = ToolArgumentReader.Int32(
                        arguments, "maxResults", 50),
                    MaxBodyChars = ToolArgumentReader.Int32(
                        arguments, "maxBodyChars", 1000),
                    ContextChars = ToolArgumentReader.Int32(
                        arguments, "contextChars", 80)
                }, cancellationToken);
            if (string.Equals(
                toolId, OutlookToolIds.CreateDraft, StringComparison.Ordinal))
                return _service.CreateDraft(new OutlookCreateDraftRequest
                {
                    Kind = ToolArgumentReader.String(
                        arguments, "kind", string.Empty),
                    To = ToolArgumentReader.String(
                        arguments, "to", string.Empty),
                    Cc = ToolArgumentReader.String(
                        arguments, "cc", string.Empty),
                    Bcc = ToolArgumentReader.String(
                        arguments, "bcc", string.Empty),
                    Subject = ToolArgumentReader.String(
                        arguments, "subject", string.Empty),
                    Body = ToolArgumentReader.String(
                        arguments, "body", string.Empty)
                }, markDispatchPossible, cancellationToken);
            if (string.Equals(
                toolId, OutlookToolIds.UpdateMail, StringComparison.Ordinal))
                return _service.UpdateMail(new OutlookUpdateMailRequest
                {
                    Kind = ToolArgumentReader.String(
                        arguments, "kind", string.Empty),
                    HasCategories = arguments.ContainsKey("categories"),
                    Categories = ToolArgumentReader.String(
                        arguments, "categories", string.Empty)
                }, markDispatchPossible, cancellationToken);
            if (string.Equals(
                toolId, OutlookToolIds.CollectMail, StringComparison.Ordinal))
            {
                var grouped = string.Equals(
                    ToolArgumentReader.String(arguments, "groupBy", "none"),
                    "month", StringComparison.OrdinalIgnoreCase);
                return _service.CollectMail(new OutlookCollectMailRequest
                {
                    GroupBy = grouped ? "month" : ToolArgumentReader.String(
                        arguments, "groupBy", "none"),
                    MaxItems = ToolArgumentReader.Int32(
                        arguments, "maxItems", grouped ? 500 : 100),
                    MaxBodyChars = ToolArgumentReader.Int32(
                        arguments, "maxBodyChars", grouped ? 500 : 1000)
                }, cancellationToken);
            }
            return OutlookOutcome.Error(
                "Unsupported Outlook tool: " + toolId,
                new JObject
                {
                    ["code"] = "unknown_tool",
                    ["retryable"] = false
                }.ToString(Formatting.None),
                "unknown_tool", false);
        }

    }
}

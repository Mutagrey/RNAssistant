using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ExternalBrowserPrepared
    {
        public ExternalBrowserReadTarget Target { get; set; }
        public ResourceRef Observation { get; set; }
        public string PreparationId { get; set; }
    }

    // Semantic admission and existing runtime evidence; native Page owns element bindings/effects.
    internal sealed class ExternalBrowserToolHandler : IPreparableToolHandler, IManagedMutationToolHandler
    {
        internal const string Fill = "browser.fill", Click = "browser.click";
        internal static bool Owns(string id) { return id == Fill || id == Click; }
        internal static ToolBinding BindingFor(string id) { return new ToolBinding(id + ".probe.v1"); }
        private readonly ChatSession _session;
        private readonly Func<IExternalBrowserReadSource> _source;
        private readonly Func<ChatSession, IDisposable> _documentAccess;

        internal ExternalBrowserToolHandler(ChatSession session, Func<IExternalBrowserReadSource> source,
            Func<ChatSession, IDisposable> documentAccess)
        { _session = session; _source = source; _documentAccess = documentAccess; }

        internal static IEnumerable<ToolCatalogEntry> Catalog()
        {
            foreach (var id in new[] { Fill, Click })
            {
                var properties = new Dictionary<string, object> {
                    ["target"] = new { type = "string", description = "The browser page read in this conversation.", @enum = new[] { ExternalBrowserResourceProvider.Target } },
                    ["name"] = new { type = "string", description = "Exact observed textbox or button name; must identify one visible element.", minLength = 1, maxLength = 160 }
                };
                var value = id == Fill ? "value" : "expectedText";
                properties[value] = new { type = "string", description = id == Fill ? "Replacement textbox value; read-back compares it internally." :
                    "Exact new semantic text expected after the click; it must be absent before dispatch.", minLength = id == Fill ? 0 : 1, maxLength = id == Fill ? 1024 : 160 };
                var schema = JsonConvert.SerializeObject(new { type = "object", properties,
                    required = new[] { "target", "name", value }, additionalProperties = false });
                var descriptor = new ToolDescriptor(id,
                    "Local browser probe only. Requires a fresh core.read of the page delivered to this conversation before each action. " +
                    (id == Fill ? "Fill one visible textbox with the exact observed name; verify its value internally. " :
                    "Click one visible button with the exact observed name; expectedText must newly appear as a unique semantic entry. ") +
                    "Rejects stale, ambiguous, disabled or user-edited targets. Unknown effects must not be retried; inspect the page. External websites are unsupported.", schema);
                yield return ControllerToolCatalogEntry.CreateTypedProjection(descriptor,
                    new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, true, false, new[] { "agent" }, 1),
                    BindingFor(id), scope: "session", mutatesLocalState: true);
            }
        }

        public Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken token)
        {
            return Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var source = _source?.Invoke() as IExternalBrowserActionSource;
                    var target = source?.TargetFor(_session?.Id);
                    if (target == null) throw Error("Open the local browser probe from this chat first.", "browser_page_unavailable");
                    var accepted = _session.Messages.SingleOrDefault(message => message.Role == "assistant" &&
                        message.ToolCallId == context.Execution.Call.Id && message.RunId == context.Execution.RunId &&
                        message.AcceptedCallOrigin?.StepId == context.Execution.StepId);
                    var uri = ResourceUri.Create("browser", target.ChatId, target.SessionId);
                    var observation = accepted?.ResourceEvidence?.LastOrDefault(evidence => evidence.Resource?.Uri == uri &&
                        evidence.Resource.IsExact && evidence.View == "text" && evidence.Payload != null &&
                        evidence.Payload.Sha256 == evidence.ContentSha256 && evidence.Coverage?.Start == 0 &&
                        evidence.ReadMetadata?.RepresentationComplete == true &&
                        evidence.ReadMetadata.RequestedCoverage?.Kind == ResourceCoverageKinds.Whole)?.Resource;
                    if (observation == null) throw Error("Read the current browser page in this conversation before acting; an omitted read does not authorize an action.", "browser_observation_required");
                    using (DocumentAccessGate.BeginOperation()) using (_documentAccess(_session)) { }
                    var prepared = new ExternalBrowserPrepared { Target = target, Observation = observation,
                        PreparationId = Guid.NewGuid().ToString("N") };
                    source.PrepareAsync(target, observation.Revision, prepared.PreparationId,
                        new ExternalBrowserActionRequest { Action = context.Execution.Call.Name == Fill ? "fill" : "click",
                            Name = ToolArgumentReader.String(context.Arguments, "name", ""),
                            Value = ToolArgumentReader.String(context.Arguments, "value", null),
                            ExpectedText = ToolArgumentReader.String(context.Arguments, "expectedText", null) }, token).GetAwaiter().GetResult();
                    if (source != _source() || !target.Matches(source.TargetFor(_session.Id))) throw Error("The browser owner changed.", "browser_document_stale");
                    using (DocumentAccessGate.BeginOperation()) using (_documentAccess(_session)) { }
                    return new ToolPreparationResult(RuntimeResult.Ok("Local browser action prepared; its exact element will be checked again before dispatch."), JsonConvert.SerializeObject(prepared));
                }
                catch (ResourceRequestException error)
                {
                    return new ToolPreparationResult(RuntimeResult.Error(error.Message, Code(error.ErrorCode)), recovery:
                        new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan));
                }
            }, token);
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
        { return Task.Run(() => ExecuteBound(context, token), token); }

        private ToolHandlerResult ExecuteBound(ToolHandlerContext context, CancellationToken token)
        {
            // Confirmed resumption can arrive on the UI thread too. Keep the synchronous document
            // gate on one worker through dispatch, read-back and terminal publication.
            token.ThrowIfCancellationRequested();
            using (DocumentAccessGate.BeginOperation())
            using (_documentAccess(_session))
            {
                ToolHandlerResult completed;
                ExternalBrowserPrepared prepared = null;
                var visualEffect = ExternalBrowserEffect.Rejected;
                var source = _source?.Invoke() as IExternalBrowserActionSource;
                try
                {
                    prepared = JsonConvert.DeserializeObject<ExternalBrowserPrepared>(context.PreparedStateJson);
                    if (prepared?.Target == null || source == null || prepared.Target.ChatId != _session.Id ||
                        !prepared.Target.Matches(source.TargetFor(_session.Id))) throw Error("The prepared browser owner changed.", "browser_document_stale");
                    var native = source.ActAsync(prepared.Target, prepared.PreparationId, context.MarkDispatchPossible, token).GetAwaiter().GetResult();
                    visualEffect = native.Effect;
                    if (source != _source() || !prepared.Target.Matches(source.TargetFor(_session.Id))) throw Error("The browser owner changed after dispatch.", "browser_effect_unknown");
                    using (_documentAccess(_session)) { } // User/host changes outside our gate must also fail closed.
                    var verified = native.Effect == ExternalBrowserEffect.VerifiedChange || native.Effect == ExternalBrowserEffect.VerifiedNoChange;
                    var data = Code(ExternalBrowserPolicy.SafeActionCode(native.Code));
                    var result = verified ? RuntimeResult.Ok("The local browser action passed its read-back check. Read the page again before another action.", data)
                        : native.Effect == ExternalBrowserEffect.Rejected ? RuntimeResult.Error("The element changed or is not actionable. Read the page again; no effect occurred.", data)
                        : RuntimeResult.Unknown("The browser effect is unknown. Inspect the page; do not repeat the action. Further mutations require manually closing and reopening the page.", data);
                    var effect = native.Effect == ExternalBrowserEffect.VerifiedChange ? ToolEffectEvidence.VerifiedChange
                        : native.Effect == ExternalBrowserEffect.Unknown ? ToolEffectEvidence.Unknown : ToolEffectEvidence.VerifiedNoChange;
                    completed = new ToolHandlerResult(result, effect);
                }
                catch (Exception error)
                {
                    if (context.MayHaveDispatched) source?.BlockActions(prepared?.Target);
                    try { context.CompleteFailure(error); }
                    finally { source?.PresentResult(prepared?.Target, prepared?.PreparationId,
                        context.MayHaveDispatched ? ExternalBrowserEffect.Unknown : ExternalBrowserEffect.Rejected); }
                    throw;
                }
                // A renderer read-back alone must not paint success before runtime accepts its terminal.
                try
                {
                    var terminal = context.Complete(completed);
                    source.PresentResult(prepared.Target, prepared.PreparationId, visualEffect);
                    return terminal;
                }
                catch
                {
                    source.BlockActions(prepared.Target);
                    source.PresentResult(prepared.Target, prepared.PreparationId, ExternalBrowserEffect.Unknown);
                    throw;
                }
            }
        }

        private sealed class Failure { [JsonProperty("code")] public string Code { get; set; } }
        private static string Code(string value) { return JsonConvert.SerializeObject(new Failure { Code = value }); }
        private static ResourceRequestException Error(string message, string code) { return ExternalBrowserResourceProvider.Error(message, code); }
    }
}

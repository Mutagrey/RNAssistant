using RNAssistant.Application.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        public BrowserPageHandle Target { get; set; }
        public ResourceRef Observation { get; set; }
        public string PreparationId { get; set; }
        public BrowserAgentOpenRequest Open { get; set; }
    }

    // Semantic admission and existing runtime evidence; native Page owns element bindings/effects.
    internal sealed partial class ExternalBrowserToolHandler : IPreparableToolHandler, IManagedMutationToolHandler
    {
        internal const string Open = "browser.open", Fill = "browser.fill", Click = "browser.click", Navigate = "browser.navigate", Drag = "browser.drag", Hover = "browser.hover", Scroll = "browser.scroll", PressKey = "browser.press_key", Select = "browser.select", Check = "browser.check";
        internal static bool Owns(string id) { return id == Open || id == Fill || id == Click || id == Navigate || id == Drag || id == Hover || id == Scroll || id == PressKey || id == Select || id == Check; }
        internal static ToolBinding BindingFor(string id) { return new ToolBinding(id + ".v5"); }
        private readonly ChatSession _session;
        private readonly Func<IBrowserReadSource> _source;
        private readonly Func<ChatSession, IDisposable> _documentAccess;

        internal ExternalBrowserToolHandler(ChatSession session, Func<IBrowserReadSource> source,
            Func<ChatSession, IDisposable> documentAccess)
        { _session = session; _source = source; _documentAccess = documentAccess; }

        internal static IEnumerable<ToolCatalogEntry> Catalog()
        {
            yield return OpenCatalog();
            foreach (var entry in InputCatalog()) yield return entry;
            foreach (var id in new[] { Fill, Click, Navigate })
            {
                var properties = new Dictionary<string, object> {
                    ["target"] = new { type = "string", description = "Exact Browser page target returned by core.find/read in this conversation.", minLength = 1, maxLength = 240 },
                    ["name"] = new { type = "string", description = "Exact observed textbox or button name; must identify one visible element.", minLength = 1, maxLength = 160 }
                };
                var value = id == Fill ? "value" : "expectedText";
                properties[value] = new { type = "string", description = id == Fill ? "Replacement textbox value; read-back compares it internally." :
                    "Exact new semantic text expected after the click; it must be absent before dispatch.", minLength = id == Fill ? 0 : 1, maxLength = id == Fill ? 1024 : 160 };
                if (id == Navigate) {
                    properties.Remove("name"); properties.Remove(value);
                    properties["locator"] = new { type = "string", description = "Exact local locator from current Browser discovery. Network URLs are refused.", minLength = 1, maxLength = 2048 };
                    properties["linkName"] = new { type = "string", description = "Exact observed name of one visible HTML link. Follow its bound href in this tab. Provide either locator or linkName, never both.", minLength = 1, maxLength = 160 };
                }
                if (id == Click) AddPointerSchema(properties);
                var schema = JsonConvert.SerializeObject(new { type = "object", properties,
                    required = id == Navigate ? new[] { "target" } : new[] { "target", "name", value }, additionalProperties = false });
                if (id == Navigate) {
                    var shape = JObject.Parse(schema);
                    shape["anyOf"] = new JArray(new JObject { ["required"] = new JArray("locator") }, new JObject { ["required"] = new JArray("linkName") });
                    schema = shape.ToString(Formatting.None);
                }
                var descriptor = new ToolDescriptor(id,
                    id == Navigate ? "Navigate an existing owned Browser page to a discovered local locator or observed local link. Supply exactly one of locator/linkName. Requires a fresh delivered core.read first. " +
                    "Requires qualified native execution and verifies committed local navigation; read the new page before further actions. Manual control blocks navigation; unknown effects must not be retried. Does not open a new tab." :
                    "On a qualified owned local HTML/plugin page, requires a fresh core.read delivered to this conversation before each action. " +
                    (id == Fill ? "Fill one visible textbox with the exact observed name; verify its value internally. " :
                    "Click one visible in-page button; expectedText must newly appear as a unique semantic entry within one second. For links use browser.navigate with linkName. ") +
                    "Rejects stale, ambiguous, disabled, sensitive, user-edited or incompletely observed targets. A changed document or missing read-back is unknown and must not be retried.", schema);
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
                    if (context.Execution.Call.Name == Open) return PrepareOpen(context, token);
                    var source = _source?.Invoke() as IBrowserActionSource;
                    var name = ToolArgumentReader.String(context.Arguments, "target", "");
                    var matches = source?.TargetsFor(_session?.Id).Where(item => item.Target == name).ToArray() ?? new BrowserPageHandle[0];
                    if (matches.Length > 1) throw Error("The browser page is ambiguous. Select an exact page from discovery.", "resource_target_ambiguous");
                    var target = matches.SingleOrDefault();
                    if (target == null) throw Error(source is IBrowserOpenSource ? "Native Browser execution/input is unavailable. A source-only tab does not grant page actions." :
                        "Open the Browser page from this chat first.", source is IBrowserOpenSource ? "browser_execution_unavailable" : "browser_page_unavailable");
                    var accepted = _session.Messages.SingleOrDefault(message => message.Role == "assistant" &&
                        message.ToolCallId == context.Execution.Call.Id && message.RunId == context.Execution.RunId &&
                        message.AcceptedCallOrigin?.StepId == context.Execution.StepId);
                    var uri = ResourceUri.Create("browser", target.ChatId, target.SessionId);
                    var observation = accepted?.ResourceEvidence?.Where(evidence => evidence.Immutable &&
                        evidence.ReadMetadata?.DescriptorAtRead?.Kind == ExternalBrowserResourceProvider.Kind &&
                        evidence.Resource.IsExact && evidence.View == "text" && evidence.Payload != null &&
                        evidence.Payload.Sha256 == evidence.ContentSha256 && evidence.Coverage?.Start == 0 &&
                        evidence.ReadMetadata?.RepresentationComplete == true &&
                        evidence.ReadMetadata.RequestedCoverage?.Kind == ResourceCoverageKinds.Whole)
                        .SelectMany(evidence => evidence.Dependencies).LastOrDefault(dependency =>
                            dependency.Kind == "immutable-snapshot" && dependency.View == "text" &&
                            dependency.Resource.Uri == uri && dependency.Resource.IsExact)?.Resource;
                    if (observation == null) throw Error("Read the current browser page in this conversation before acting; an omitted read does not authorize an action.", "browser_observation_required");
                    using (DocumentAccessGate.BeginOperation()) using (_documentAccess?.Invoke(_session)) { }
                    var prepared = new ExternalBrowserPrepared { Target = target, Observation = observation,
                        PreparationId = Guid.NewGuid().ToString("N") };
                    var navigate = context.Execution.Call.Name == Navigate;
                    var url = ToolArgumentReader.String(context.Arguments, "locator", null);
                    var link = ToolArgumentReader.String(context.Arguments, "linkName", null);
                    if (navigate && ((url == null) == (link == null)))
                        throw Error("Provide exactly one of locator or linkName.", "browser_action_invalid");
                    RNAssistant.Application.BrowserLocalLocator local = null;
                    if (navigate && url != null && !RNAssistant.Application.BrowserLocalLocator.TryParse(url, out local))
                        throw Error("Browser navigation accepts local locators only.", "browser_action_invalid");
                    if (navigate && url != null && !((_source() as IBrowserOpenSource)?.OpenableTargetsFor(ExternalBrowserResourceProvider.Owner(_session))
                        .Any(item => item.Target.DisplayLocator == local.ContentLocator) ?? false))
                        throw Error("Select a local destination issued by the current source catalog.", "browser_source_unavailable");
                    var request = InputRequest(context, navigate ? link : ToolArgumentReader.String(context.Arguments, "name", ""), url);
                    source.PrepareAsync(target, observation.Revision, prepared.PreparationId, request, token).GetAwaiter().GetResult();
                    if (source != _source() || !source.TargetsFor(_session.Id).Any(target.Matches)) throw Error("The browser owner changed.", "browser_document_stale");
                    using (DocumentAccessGate.BeginOperation()) using (_documentAccess?.Invoke(_session)) { }
                    return new ToolPreparationResult(RuntimeResult.Ok("Browser action prepared; its exact target and user control will be checked again before dispatch."), JsonConvert.SerializeObject(prepared));
                }
                catch (ResourceRequestException error)
                {
                    return new ToolPreparationResult(RuntimeResult.Error(error.Message, Code(error.ErrorCode)), recovery:
                        new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan));
                }
            }, token);
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
        { return Task.Run(() => context.Execution.Call.Name == Open ? ExecuteOpen(context, token) : ExecuteBound(context, token), token); }

        private ToolHandlerResult ExecuteBound(ToolHandlerContext context, CancellationToken token)
        {
            // Confirmed resumption can arrive on the UI thread too. Keep the synchronous document
            // gate on one worker through dispatch, read-back and terminal publication.
            token.ThrowIfCancellationRequested();
            using (DocumentAccessGate.BeginOperation())
            using (_documentAccess?.Invoke(_session))
            {
                ToolHandlerResult completed;
                ExternalBrowserPrepared prepared = null;
                var visualEffect = BrowserActionEffect.Rejected;
                var source = _source?.Invoke() as IBrowserActionSource;
                try
                {
                    prepared = JsonConvert.DeserializeObject<ExternalBrowserPrepared>(context.PreparedStateJson);
                    if (prepared?.Target == null || source == null || prepared.Target.ChatId != _session.Id ||
                        !source.TargetsFor(_session.Id).Any(prepared.Target.Matches)) throw Error("The prepared browser owner changed.", "browser_document_stale");
                    var navigation = context.Execution.Call.Name == Navigate;
                    var native = source.ActAsync(prepared.Target, prepared.PreparationId, context.MarkDispatchPossible, token, navigation).GetAwaiter().GetResult();
                    visualEffect = native.Effect;
                    if (navigation && native.Effect == BrowserActionEffect.VerifiedChange &&
                        (native.NavigationTarget == null || native.NavigationTarget.DocumentGeneration <= prepared.Target.DocumentGeneration))
                        throw Error("Navigation has no fresh document evidence.", "browser_effect_unknown");
                    var after = navigation && native.NavigationTarget != null ? native.NavigationTarget : prepared.Target;
                    if (source != _source() || after.ChatId != prepared.Target.ChatId || after.SessionId != prepared.Target.SessionId ||
                        !source.TargetsFor(_session.Id).Any(after.Matches)) throw Error("The browser owner changed after dispatch.", "browser_effect_unknown");
                    using (_documentAccess?.Invoke(_session)) { } // User/host changes outside our gate must also fail closed.
                    var verified = native.Effect == BrowserActionEffect.VerifiedChange || native.Effect == BrowserActionEffect.VerifiedNoChange;
                    var data = Code(ExternalBrowserPolicy.SafeActionCode(native.Code));
                    var result = verified ? RuntimeResult.Ok("The browser action passed its read-back check. Read the page again before another action.", data)
                        : native.Effect == BrowserActionEffect.Rejected ? RuntimeResult.Error(native.Code == "browser_user_control"
                            ? "The user controls this page. Wait for explicit Resume agent; do not retry browser actions."
                            : "The element changed or is not actionable. Read the page again; no effect occurred.", data)
                        : RuntimeResult.Unknown("The browser effect is unknown. Inspect the page; do not repeat the action. Further mutations require manually closing and reopening the page.", data);
                    var effect = native.Effect == BrowserActionEffect.VerifiedChange ? ToolEffectEvidence.VerifiedChange
                        : native.Effect == BrowserActionEffect.Unknown ? ToolEffectEvidence.Unknown : ToolEffectEvidence.VerifiedNoChange;
                    completed = new ToolHandlerResult(result, effect);
                }
                catch (Exception error)
                {
                    if (context.MayHaveDispatched) source?.BlockActions(prepared?.Target);
                    try { context.CompleteFailure(error); }
                    finally { source?.PresentResult(prepared?.Target, prepared?.PreparationId,
                        context.MayHaveDispatched ? BrowserActionEffect.Unknown : BrowserActionEffect.Rejected); }
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
                    source.PresentResult(prepared.Target, prepared.PreparationId, BrowserActionEffect.Unknown);
                    throw;
                }
            }
        }

        private sealed class Failure { [JsonProperty("code")] public string Code { get; set; } }
        private static string Code(string value) { return JsonConvert.SerializeObject(new Failure { Code = value }); }
        private static ResourceRequestException Error(string message, string code) { return ExternalBrowserResourceProvider.Error(message, code); }
    }
}

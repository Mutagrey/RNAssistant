using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Tools;
using Result = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Services
{
    internal static class ExcelPowerQuerySyntaxService
    {
        internal static async Task<ToolHandlerResult> ValidateAsync(ResourceGatewayService gateway,
            HostRuntime host, ChatSession session, string query, CancellationToken token)
        {
            var target = ExcelPowerQueryToolSupport.Target(session);
            // Capture through the existing CAS/resource owner once. Parsing runs
            // after this document gate is released and cannot evaluate M.
            var capture = host.ReadDocument(target, token, () => gateway.ReadWhole(session,
                new ResourceRef(ExcelResourceProvider.QueryIdentity(session.DocumentAuthorityId, query).Uri),
                ResourceRepresentations.Source, token));
            var source = capture.Result.Text;
            if (source == null || !capture.Result.Complete || capture.Result.Truncated ||
                capture.Result.ContentSha256 != TextPatternEngine.Sha256(source))
                throw new ResourceRequestException("An exact complete M snapshot is required for syntax analysis.",
                    "excel_query_syntax_snapshot_unavailable", false);
            var view = new PowerQuerySourceCheckView { Target = ExcelResourceProvider.QueryPrefix + query,
                SourceCharacters = source.Length };
            if (source.Length > PowerQuerySyntaxRequest.MaximumSourceCharacters)
            {
                view.Reason = "source_exceeds_analysis_limit";
            }
            else
            {
                var request = new PowerQuerySyntaxRequest(Guid.NewGuid().ToString("N"), source, capture.Result.ContentSha256);
                var analyzer = gateway.PowerQuerySyntaxAnalyzer;
                PowerQuerySyntaxAnalysis analysis;
                try
                {
                    analysis = analyzer == null || !analyzer.IsAvailable
                        ? PowerQuerySyntaxAnalysis.Unavailable(request, "analyzer_unavailable")
                        : await analyzer.AnalyzeAsync(request, token).ConfigureAwait(false);
                    if (analysis == null) throw new InvalidOperationException("Parser returned no result.");
                    analysis.RequireMatches(request);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                { analysis = PowerQuerySyntaxAnalysis.Unavailable(request, "analysis_cancelled_or_timed_out"); }
                catch (Exception)
                {
                    // Infrastructure/transport/parser failures are not M syntax
                    // errors. Never substitute a homemade or lexical heuristic.
                    analysis = PowerQuerySyntaxAnalysis.Unavailable(request, "analysis_unavailable_or_invalid_result");
                }
                view.Syntax = analysis.Syntax;
                view.ParserVersion = analysis.ParserVersion;
                view.Reason = analysis.Reason;
                view.Diagnostics = analysis.Diagnostics.Select(diagnostic => Locate(source, diagnostic)).ToList();
            }
            token.ThrowIfCancellationRequested();
            try
            {
                view.SourceMatchesCurrent = host.ReadDocument(target, token, () => {
                    var current = ExcelPowerQueryToolSupport.Backend(gateway).ReadQuery(query);
                    return current != null && current.QueryName == query &&
                        string.Equals(current.Formula, source, StringComparison.Ordinal);
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is OfficeDocumentGuardException || ex is HostRuntime.MutationLockException ||
                ex is ExcelPowerQueryBackendException || ex is ResourceRequestException ||
                ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException)
            { view.CurrentSourceCheck = "unavailable"; }
            var message = view.Syntax == PowerQuerySyntaxStatus.Passed ? "M syntax passed for the captured source snapshot."
                : view.Syntax == PowerQuerySyntaxStatus.Failed ? "M syntax errors were found in the captured source snapshot."
                : "M syntax was not checked; inspect the reported analysis limit or availability reason.";
            if (view.SourceMatchesCurrent == false) message += " Live M changed after capture; read the current source before applying this result.";
            else if (!view.SourceMatchesCurrent.HasValue) message += " Current live source could not be checked.";
            message += " Name resolution, dependencies, source access and execution remain separate checks.";
            // Retain source provenance without granting a model whole-source
            // overwrite observation: source bytes were not delivered by this tool.
            return new ToolHandlerResult(Result.Ok(message, JsonConvert.SerializeObject(view), capture.ResourceRefs), ToolEffectEvidence.None);
        }

        private static PowerQuerySourceDiagnostic Locate(string source, PowerQuerySyntaxDiagnostic diagnostic)
        {
            var result = new PowerQuerySourceDiagnostic { Code = diagnostic.Code, Message = diagnostic.Message,
                StartOffset = diagnostic.StartOffset, EndOffset = diagnostic.EndOffset };
            if (!diagnostic.StartOffset.HasValue) return result;
            var line = 1; var column = 1;
            for (var index = 0; index < diagnostic.StartOffset.Value; index++)
            {
                if (source[index] == '\r')
                {
                    line++; column = 1;
                    if (index + 1 < diagnostic.StartOffset.Value && source[index + 1] == '\n') index++;
                }
                else if (source[index] == '\n') { line++; column = 1; }
                else column++;
            }
            result.Line = line; result.Column = column;
            return result;
        }
    }

    internal sealed class PowerQuerySourceCheckView
    {
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("syntax")] public PowerQuerySyntaxStatus Syntax { get; set; } = PowerQuerySyntaxStatus.NotChecked;
        [JsonProperty("parser")] public string Parser { get { return "@microsoft/powerquery-parser"; } }
        [JsonProperty("parserVersion")] public string ParserVersion { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
        [JsonProperty("sourceCharacters")] public int SourceCharacters { get; set; }
        [JsonProperty("analysisCharacterLimit")] public int AnalysisCharacterLimit { get { return PowerQuerySyntaxRequest.MaximumSourceCharacters; } }
        [JsonProperty("sourceMatchesCurrent")] public bool? SourceMatchesCurrent { get; set; }
        [JsonProperty("currentSourceCheck")] public string CurrentSourceCheck { get; set; } = "observed_after_analysis";
        [JsonProperty("diagnostics")] public List<PowerQuerySourceDiagnostic> Diagnostics { get; set; } = new List<PowerQuerySourceDiagnostic>();
        [JsonProperty("diagnosticCoverage")] public string DiagnosticCoverage { get { return "parser_stops_at_first_error"; } }
        [JsonProperty("positionUnits")] public string PositionUnits { get { return "UTF-16 offsets: zero-based; line/column: one-based"; } }
        [JsonProperty("nameResolution")] public string NameResolution { get { return "not_checked"; } }
        [JsonProperty("dependencies")] public string Dependencies { get { return "not_checked"; } }
        [JsonProperty("sourceAccess")] public string SourceAccess { get { return "not_checked"; } }
        [JsonProperty("execution")] public string Execution { get { return "not_executed"; } }
    }

    internal sealed class PowerQuerySourceDiagnostic
    {
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("startOffset")] public int? StartOffset { get; set; }
        [JsonProperty("endOffset")] public int? EndOffset { get; set; }
        [JsonProperty("line")] public int? Line { get; set; }
        [JsonProperty("column")] public int? Column { get; set; }
    }
}

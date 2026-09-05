using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Vba;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class VbaPackageToolHandler : IToolHandler
    {
        internal const string HandlerId = "vba.custom.package.execute.v1";

        private readonly ToolPackageSource _source;
        private readonly VbaToolExecutor _executor;
        private readonly HostRuntime _runtime;
        private readonly ChatSession _session;
        private readonly bool _dryRun;

        internal VbaPackageToolHandler(ToolPackageSource source,
            VbaToolExecutor executor, HostRuntime runtime,
            ChatSession session, bool dryRun)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _session = session;
            _dryRun = dryRun;
        }

        internal static bool IsDefinition(ToolCatalogEntry definition)
        {
            return definition != null && !definition.BuiltIn &&
                string.Equals(definition.Executor, "vba",
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static bool Owns(ToolRegistration registration)
        {
            return registration != null && registration.Binding != null &&
                string.Equals(registration.Binding.HandlerId, HandlerId,
                    StringComparison.Ordinal);
        }

        internal static ToolBinding BindingFor(ToolCatalogEntry definition)
        {
            if (!IsDefinition(definition)) return null;
            return new ToolBinding(HandlerId, definition.EntryPoint,
                definition.Scope, definition.Host);
        }

        internal static ToolPolicy PolicyFor(ToolCatalogEntry definition)
        {
            if (!IsDefinition(definition)) return null;
            return new ToolPolicy(
                ToolEffect.Write,
                ToolVerification.None,
                true,
                false,
                new[] { "agent" },
                Math.Max(1, definition.RiskLevel));
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                var arguments = JObject.FromObject(context.Arguments);
                var result = _dryRun
                    ? _runtime.ReadDocument(Target(_session), cancellationToken,
                        delegate
                        {
                            return _executor.ExecuteCustomPackage(
                                _source, arguments, true, context.Execution,
                                _session, context.MarkDispatchPossible,
                                cancellationToken);
                        })
                    : _runtime.ExecuteDocumentMutation(
                        Target(_session), cancellationToken, delegate
                        {
                            return _executor.ExecuteCustomPackage(
                                _source, arguments, false, context.Execution,
                                _session, context.MarkDispatchPossible,
                                cancellationToken);
                        }, terminalOutcome => context.Complete(new ToolHandlerResult(Result(terminalOutcome), Effect(terminalOutcome))), context.CompleteFailure);
                if (result == null)
                    throw new InvalidOperationException(
                        "VBA package handler returned no typed result.");
                return Task.FromResult(new ToolHandlerResult(
                    Result(result), Effect(result)));
            }
            catch (OfficeDocumentGuardException ex)
                when (!context.MayHaveDispatched)
            {
                return Failure(ex.Message, ex.ErrorCode, ex.Retryable);
            }
            catch (HostRuntime.MutationLockException ex)
                when (!context.MayHaveDispatched)
            {
                return Failure(ex.Message,
                    ex.Retryable ? "tool_mutation_busy" :
                        "tool_mutation_lock_unavailable",
                    ex.Retryable);
            }
        }

        internal static RuntimeResult Result(VbaPackageResult result)
        {
            if (result.Status == VbaMutationOutcomeStatus.Ok)
                return RuntimeResult.Ok(result.Message, result.DataJson);
            if (result.Status == VbaMutationOutcomeStatus.Unknown)
                return RuntimeResult.Unknown(result.Message, result.DataJson);
            return RuntimeResult.Error(result.Message, result.DataJson);
        }

        internal static ToolEffectEvidence Effect(VbaPackageResult result)
        {
            return result.Effect == VbaPackageEffectEvidence.VerifiedChange
                ? ToolEffectEvidence.VerifiedChange
                : result.Effect == VbaPackageEffectEvidence.VerifiedNoChange
                    ? ToolEffectEvidence.VerifiedNoChange
                    : result.Effect == VbaPackageEffectEvidence.Unknown
                        ? ToolEffectEvidence.Unknown
                        : ToolEffectEvidence.None;
        }

        private static OfficeDocumentExecutionExpectation Target(
            ChatSession session)
        {
            if (session == null) return null;
            return new OfficeDocumentExecutionExpectation
            {
                Host = session.Host,
                DocumentKey = session.DocumentKey,
                RuntimeDocumentKey = session.LastRun == null
                    ? string.Empty : session.LastRun.DocumentRuntimeKey
            };
        }

        private static Task<ToolHandlerResult> Failure(
            string message, string code, bool retryable)
        {
            return Task.FromResult(new ToolHandlerResult(
                RuntimeResult.Error(message, new JObject
                {
                    ["code"] = code,
                    ["retryable"] = retryable
                }.ToString()), ToolEffectEvidence.None));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Qualification;
using RNAssistant.OfficeHosts.Qualification;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    public sealed partial class ExcelAdapter : IQualificationHostPort
    {
        private const string Wq0Capability = "qualification.excel.wq0.v1";
        private readonly Dictionary<string, ExcelWq0State> _wq0Runs =
            new Dictionary<string, ExcelWq0State>(StringComparer.Ordinal);

        public IReadOnlyList<string> QualificationCapabilities
        {
            get
            {
                string reason;
                return ExcelIdentityHelperClient.IsSupported(out reason)
                    ? (IReadOnlyList<string>)Array.AsReadOnly(new[]
                    {
                        Wq0Capability, "windows-x64", "office-x64", "independent-client-helper"
                    })
                    : new string[0];
            }
        }

        public bool SupportsQualificationAction(QualificationStep step)
        {
            if (step == null) return false;
            return step.Action == "excel.wq0.preflight" ||
                step.Action == "excel.wq0.fixture.create" ||
                step.Action == "excel.wq0.capture.baseline" ||
                step.Action == "excel.wq0.capture.switch" ||
                step.Action == "excel.wq0.capture.save-as" ||
                step.Action == "excel.wq0.capture.second-window" ||
                step.Action == "excel.wq0.rotate-client" ||
                step.Action == "excel.wq0.rebind-reopened" ||
                step.Action == "excel.wq0.capture.other-process" ||
                step.Action == "excel.wq0.cleanup";
        }

        public QualificationActionResult ExecuteQualificationAction(
            QualificationStepExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (context.Step.Action)
                {
                    case "excel.wq0.preflight": return PreflightWq0();
                    case "excel.wq0.fixture.create": return CreateWq0Fixture(context.RunId);
                    case "excel.wq0.capture.baseline": return CaptureWq0(context.RunId, "baseline", true);
                    case "excel.wq0.capture.switch": return CaptureWq0(context.RunId, "after-switch", false);
                    case "excel.wq0.capture.save-as": return CaptureWq0(context.RunId, "after-save-as", false);
                    case "excel.wq0.capture.second-window": return CaptureWq0(context.RunId, "second-window", false);
                    case "excel.wq0.rotate-client": return RotateWq0Client(context.RunId);
                    case "excel.wq0.rebind-reopened": return RebindWq0(context.RunId);
                    case "excel.wq0.capture.other-process": return CaptureOtherProcess(context.RunId);
                    case "excel.wq0.cleanup": return CleanupWq0(context.RunId);
                    default:
                        return QualificationActionResult.Blocked(
                            "action_not_allowlisted", "Excel WQ0 action is not allowlisted.");
                }
            }
            catch (PlatformNotSupportedException ex)
            {
                return QualificationActionResult.Blocked("windows_x64_required", ex.Message);
            }
            catch (Exception ex)
            {
                return QualificationActionResult.Failed(
                    "excel_wq0_action_failed", Bound(ex.Message, 1000));
            }
        }

        public bool SupportsQualificationAssertion(QualificationStep step)
        {
            return step != null && step.Assertion == "excel.wq0.identity-matrix";
        }

        public QualificationVerificationResult VerifyQualificationAssertion(
            QualificationStepExecutionContext context,
            QualificationEvidenceSnapshot evidence,
            CancellationToken cancellationToken)
        {
            return ExcelWq0EvidenceVerifier.Verify(evidence, cancellationToken);
        }

        public void ReleaseQualificationResources()
        {
            foreach (var runId in _wq0Runs.Keys.ToArray())
            {
                try { CleanupWq0(runId); } catch { }
            }
        }

        private QualificationActionResult PreflightWq0()
        {
            string reason;
            if (!ExcelIdentityHelperClient.IsSupported(out reason))
                return QualificationActionResult.Blocked("helper_unavailable", reason);
            ComIdentityLease.RequireWindowsSta();
            var source = RequireWorkbook();
            if (source == null)
                return QualificationActionResult.Blocked("source_workbook_required",
                    "Open a control workbook before starting Excel WQ0.");
            return QualificationActionResult.Passed(new JObject
            {
                ["windows"] = true,
                ["process64Bit"] = Environment.Is64BitProcess,
                ["apartment"] = Thread.CurrentThread.GetApartmentState().ToString(),
                ["helper"] = ExcelIdentityHelperClient.ExecutableName,
                ["ownerCallSite"] = QualificationOwnerLabel(),
                ["ownerAssemblyMvid"] = typeof(ComIdentitySample).Assembly.ManifestModule.ModuleVersionId.ToString("D")
            }.ToString(Formatting.None), "Excel WQ0 preflight completed.");
        }

        private QualificationActionResult CreateWq0Fixture(string runId)
        {
            if (_wq0Runs.ContainsKey(runId))
                return QualificationActionResult.Blocked("fixture_exists", "This run already owns an Excel fixture.");
            var source = RequireWorkbook();
            var state = new ExcelWq0State
            {
                Source = source,
                SourceSavedBefore = source == null ? (bool?)null : source.Saved,
                Directory = Path.Combine(Path.GetTempPath(), "RNAssistant-WQ0", SafeRunId(runId))
            };
            _wq0Runs.Add(runId, state);
            Directory.CreateDirectory(state.Directory);
            state.MarkerPath = Path.Combine(state.Directory, ".rna-wq0-owner");
            File.WriteAllText(state.MarkerPath, runId);
            var initialDirectory = Path.Combine(state.Directory, "initial");
            var saveAsDirectory = Path.Combine(state.Directory, "save-as");
            Directory.CreateDirectory(initialDirectory);
            Directory.CreateDirectory(saveAsDirectory);
            state.InitialPath = Path.Combine(initialDirectory, "Identity-WQ0.xlsx");
            state.ExpectedSaveAsPath = Path.Combine(saveAsDirectory, "Identity-WQ0.xlsx");
            state.Fixture = _application.Workbooks.Add();
            state.Fixture.SaveAs(state.InitialPath, Excel.XlFileFormat.xlOpenXMLWorkbook);
            state.CurrentPath = state.InitialPath;
            return QualificationActionResult.Passed(new JObject
            {
                ["ownership"] = "runner-owned",
                ["fixtureName"] = state.Fixture.Name,
                ["initialPath"] = state.InitialPath,
                ["expectedSaveAsPath"] = state.ExpectedSaveAsPath,
                ["sourceSavedBefore"] = state.SourceSavedBefore
            }.ToString(Formatting.None), "Runner-owned Excel WQ0 fixture created.", "verified_change");
        }

        private QualificationActionResult CaptureWq0(string runId, string scenario, bool startClients)
        {
            var state = RequireState(runId);
            if (startClients)
            {
                if (state.Clients.Count != 0)
                    return QualificationActionResult.Blocked("clients_already_started", "Independent clients already exist.");
                var target = Target(state.Fixture);
                state.Clients["client-A"] = ExcelIdentityHelperClient.Start(target.Hwnd, target.Index, "client-A");
                state.Clients["client-B"] = ExcelIdentityHelperClient.Start(target.Hwnd, target.Index, "client-B");
            }
            var observation = Observe(state, scenario);
            if (scenario == "after-save-as") state.CurrentPath = state.Fixture.FullName;
            return QualificationActionResult.Passed(observation.ToString(Formatting.None),
                "Excel identity observation captured.");
        }

        private QualificationActionResult RotateWq0Client(string runId)
        {
            var state = RequireState(runId);
            ExcelIdentityHelperClient first;
            if (!state.Clients.TryGetValue("client-A", out first))
                return QualificationActionResult.Blocked("client_a_missing", "Client A is not attached.");
            first.Release();
            state.Clients.Remove("client-A");
            var target = Target(state.Fixture);
            state.Clients["client-C"] = ExcelIdentityHelperClient.Start(target.Hwnd, target.Index, "client-C");
            return QualificationActionResult.Passed(Observe(state, "rotated-client").ToString(Formatting.None),
                "Client A released and independent client C attached.");
        }

        private QualificationActionResult RebindWq0(string runId)
        {
            var state = RequireState(runId);
            var oldClients = new JArray(state.Clients.Values.Select(client => HelperEvidence(client.Observe("after-close"))));
            var reopened = FindWorkbook(state.CurrentPath);
            if (reopened == null)
                return QualificationActionResult.Blocked("fixture_not_reopened",
                    "Reopen the runner-owned fixture from its Save As path before continuing.",
                    new JObject { ["oldClients"] = oldClients }.ToString(Formatting.None));
            foreach (var client in state.Clients.Values) client.Dispose();
            state.Clients.Clear();
            state.Fixture = reopened;
            var target = Target(reopened);
            state.Clients["client-D"] = ExcelIdentityHelperClient.Start(target.Hwnd, target.Index, "client-D");
            state.Clients["client-E"] = ExcelIdentityHelperClient.Start(target.Hwnd, target.Index, "client-E");
            return QualificationActionResult.Passed(new JObject
            {
                ["oldClients"] = oldClients,
                ["newObservation"] = Observe(state, "reopened")
            }.ToString(Formatting.None), "Closed target and reopened lifetime were observed.");
        }

        private QualificationActionResult CaptureOtherProcess(string runId)
        {
            var state = RequireState(runId);
            var currentProcess = ProcessIdForApplication();
            var expectedName = state.Fixture.Name;
            foreach (var hwnd in ExcelProbeTarget.ListTopLevelWindows())
            {
                if (ExcelProbeTarget.ProcessId(hwnd) == currentProcess) continue;
                var target = ExcelIdentityHelperClient.ListWorkbooks(hwnd)
                    .FirstOrDefault(item => string.Equals(item.Name, expectedName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(item.FullName, state.CurrentPath, StringComparison.OrdinalIgnoreCase));
                if (target == null) continue;
                using (var client = ExcelIdentityHelperClient.Start(hwnd, target.Index, "other-process"))
                {
                    return QualificationActionResult.Passed(new JObject
                    {
                        ["expectedName"] = expectedName,
                        ["foreign"] = HelperEvidence(client.Initial)
                    }.ToString(Formatting.None), "Same-name workbook in another Excel process was observed.");
                }
            }
            return QualificationActionResult.Blocked("other_process_target_missing",
                "Open a same-name disposable workbook in a different Excel process before continuing.");
        }

        private QualificationActionResult CleanupWq0(string runId)
        {
            ExcelWq0State state;
            if (!_wq0Runs.TryGetValue(runId, out state))
                return QualificationActionResult.Passed("{\"released\":true,\"fixtureOwned\":false}",
                    "No Excel WQ0 fixture remained.");
            var failures = new List<string>();
            foreach (var client in state.Clients.Values)
            {
                try { client.Release(); }
                catch (Exception ex) { failures.Add(Bound(ex.Message, 300)); }
            }
            state.Clients.Clear();
            try
            {
                var open = state.Fixture != null && FindWorkbook(state.CurrentPath) != null;
                if (open) state.Fixture.Close(false);
            }
            catch (Exception ex) { failures.Add(Bound(ex.Message, 300)); }
            try
            {
                if (File.Exists(state.MarkerPath) &&
                    string.Equals(File.ReadAllText(state.MarkerPath), runId, StringComparison.Ordinal))
                    Directory.Delete(state.Directory, true);
                else failures.Add("Fixture ownership marker is missing or does not match.");
            }
            catch (Exception ex) { failures.Add(Bound(ex.Message, 300)); }
            bool? sourceSavedAfter = null;
            try { sourceSavedAfter = state.Source == null ? (bool?)null : state.Source.Saved; }
            catch { failures.Add("Control workbook state is unavailable during cleanup."); }
            _wq0Runs.Remove(runId);
            var actual = new JObject
            {
                ["released"] = failures.Count == 0,
                ["sourceSavedBefore"] = state.SourceSavedBefore,
                ["sourceSavedAfter"] = sourceSavedAfter,
                ["sourceUnchanged"] = state.SourceSavedBefore == sourceSavedAfter,
                ["errors"] = new JArray(failures)
            }.ToString(Formatting.None);
            return failures.Count == 0 && state.SourceSavedBefore == sourceSavedAfter
                ? QualificationActionResult.Passed(actual, "Excel WQ0 clients and fixture were released.", "verified_no_change")
                : QualificationActionResult.Failed("cleanup_failed", "Excel WQ0 cleanup was incomplete.", actual);
        }

        private JObject Observe(ExcelWq0State state, string scenario)
        {
            var result = new JObject
            {
                ["scenario"] = scenario,
                ["inProcess"] = InProcessObservation(state.Fixture, scenario),
                ["clients"] = new JArray(state.Clients.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => HelperEvidence(item.Value.Observe(scenario))))
            };
            return result;
        }

        private JObject InProcessObservation(Excel.Workbook workbook, string scenario)
        {
            var before = workbook.Saved;
            using (var lease = ComIdentityLease.Create(workbook))
            {
                var sample = lease.ReadAgain();
                return new JObject
                {
                    ["label"] = QualificationOwnerLabel(),
                    ["scenario"] = scenario,
                    ["status"] = "observed",
                    ["excelProcessId"] = ProcessIdForApplication(),
                    ["excelProcessStartUtc"] = Process.GetProcessById((int)ProcessIdForApplication())
                        .StartTime.ToUniversalTime().ToString("o"),
                    ["candidate"] = sample.Candidate,
                    ["oxid"] = sample.Oxid,
                    ["oid"] = sample.Oid,
                    ["ipid"] = sample.Ipid,
                    ["name"] = workbook.Name,
                    ["fullName"] = workbook.FullName,
                    ["savedBeforeRead"] = before,
                    ["savedAfterRead"] = workbook.Saved,
                    ["windowCount"] = workbook.Windows.Count
                };
            }
        }

        private static JObject HelperEvidence(ExcelIdentityHelperResponse response)
        {
            var result = JObject.FromObject(response);
            result.Remove("nonce");
            return result;
        }

        private ExcelWq0Target Target(Excel.Workbook workbook)
        {
            var index = 0;
            var current = 0;
            foreach (Excel.Workbook candidate in _application.Workbooks)
            {
                current++;
                if (ExcelProbeTarget.SameLocalObject(workbook, candidate)) { index = current; break; }
            }
            if (index == 0) throw new InvalidOperationException("Runner-owned workbook is not open.");
            return new ExcelWq0Target { Hwnd = _application.Hwnd, Index = index };
        }

        private Excel.Workbook FindWorkbook(string fullName)
        {
            foreach (Excel.Workbook workbook in _application.Workbooks)
            {
                try
                {
                    if (string.Equals(workbook.FullName, fullName, StringComparison.OrdinalIgnoreCase))
                        return workbook;
                }
                catch { }
            }
            return null;
        }

        private uint ProcessIdForApplication()
        {
            return ExcelProbeTarget.ProcessId(_application.Hwnd);
        }

        private string QualificationOwnerLabel()
        {
            return _qualificationOwnerLabel;
        }

        private ExcelWq0State RequireState(string runId)
        {
            ExcelWq0State state;
            if (!_wq0Runs.TryGetValue(runId, out state))
                throw new InvalidOperationException("Excel WQ0 runner-owned fixture is unavailable.");
            return state;
        }

        private static string SafeRunId(string runId)
        {
            var result = new string((runId ?? string.Empty).Where(char.IsLetterOrDigit).Take(48).ToArray());
            return result.Length == 0 ? Guid.NewGuid().ToString("N") : result;
        }

        private static string Bound(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximum) return value;
            return value.Substring(0, maximum);
        }

        private sealed class ExcelWq0Target
        {
            internal long Hwnd;
            internal int Index;
        }

        private sealed class ExcelWq0State
        {
            internal Excel.Workbook Source;
            internal bool? SourceSavedBefore;
            internal Excel.Workbook Fixture;
            internal string Directory;
            internal string MarkerPath;
            internal string InitialPath;
            internal string ExpectedSaveAsPath;
            internal string CurrentPath;
            internal readonly Dictionary<string, ExcelIdentityHelperClient> Clients =
                new Dictionary<string, ExcelIdentityHelperClient>(StringComparer.Ordinal);
        }
    }
}

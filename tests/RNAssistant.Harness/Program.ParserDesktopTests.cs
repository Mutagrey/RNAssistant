using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Qualification;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;
using RNAssistant.OfficeHosts.Identity;
using RNAssistant.OfficeHosts.Qualification;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ParsesOfficeTargetJsonDescriptor()
        {
            var target = OfficeTargetDescriptor.FromJson("{\"Host\":\"Excel\",\"Hwnd\":123456,\"ProcessId\":4321,\"FullName\":\"C:\\\\Docs\\\\Book.xlsx\",\"Name\":\"Book.xlsx\",\"Selection\":\"Sheet1!A1:B2\"}");
            AssertEqual("Excel", target.Host, "host");
            AssertEqual(123456L, target.Hwnd, "hwnd");
            AssertEqual(4321, target.ProcessId, "process id");
            AssertEqual("C:\\Docs\\Book.xlsx", target.FullName, "full name");
            AssertEqual("Book.xlsx", target.Name, "name");
            AssertEqual("Sheet1!A1:B2", target.Selection, "selection");
            AssertTrue(target.HasDocumentIdentity, "has identity");
        }

        private static void ParsesOfficeTargetBase64Descriptor()
        {
            var json = "{\"Host\":\"Outlook\",\"EntryId\":\"abc123\",\"Name\":\"Mail\"}";
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            var target = OfficeTargetDescriptor.FromBase64Json(base64);
            AssertEqual("Outlook", target.Host, "host");
            AssertEqual("abc123", target.EntryId, "entry id");
            AssertEqual("Mail", target.Name, "name");
            AssertTrue(target.HasDocumentIdentity, "has identity");
        }

        private static void OfficeTargetIgnoresUtf8Bom()
        {
            var json = "\uFEFF{\"Host\":\"Word\",\"FullName\":\"C:\\\\Docs\\\\Doc.docx\"}";
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            var target = OfficeTargetDescriptor.FromBase64Json(base64);
            AssertEqual("Word", target.Host, "host");
            AssertEqual("C:\\Docs\\Doc.docx", target.FullName, "full name");
        }

        private static void TargetRegistryManualModeKeepsSelection()
        {
            var registry = new OfficeTargetRegistry();
            registry.Mode = TargetSelectionMode.Manual;
            var first = registry.Select(new OfficeTargetDescriptor { Host = "Excel", Hwnd = 1, FullName = "C:\\Docs\\A.xlsx", Name = "A.xlsx" });
            var second = registry.Upsert(new OfficeTargetDescriptor { Host = "Word", Hwnd = 2, FullName = "C:\\Docs\\B.docx", Name = "B.docx" });

            AssertEqual(TargetSelectionMode.Manual, registry.Mode, "manual mode");
            AssertEqual(first.Id, registry.SelectedTargetId, "manual selected id");
            AssertEqual("A.xlsx", registry.SelectedTarget.Target.Name, "manual selected target");
            AssertTrue(second != null, "second target added");
            AssertEqual(2, registry.Targets.Count, "registry count");
        }

        private static void TargetRegistryAutoModeCanSwitchSelection()
        {
            var registry = new OfficeTargetRegistry();
            registry.Mode = TargetSelectionMode.AutoFollow;
            registry.Select(new OfficeTargetDescriptor { Host = "Excel", Hwnd = 1, FullName = "C:\\Docs\\A.xlsx", Name = "A.xlsx" });
            var second = registry.Select(new OfficeTargetDescriptor { Host = "Word", Hwnd = 2, FullName = "C:\\Docs\\B.docx", Name = "B.docx" });

            AssertEqual(TargetSelectionMode.AutoFollow, registry.Mode, "mode");
            AssertEqual(second.Id, registry.SelectedTargetId, "auto selected id");
            AssertEqual("B.docx", registry.SelectedTarget.Target.Name, "auto selected target");
            AssertEqual(1, registry.ForHost("Word").Count, "word count");
        }

        private static void OfficeStaDispatcherRunsSta()
        {
            using (var dispatcher = new OfficeStaDispatcher())
            {
                var firstThreadId = dispatcher.Invoke(delegate { return Thread.CurrentThread.ManagedThreadId; });
                var secondThreadId = dispatcher.Invoke(delegate { return Thread.CurrentThread.ManagedThreadId; });

                AssertEqual(firstThreadId, secondThreadId, "dispatcher thread id");
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    var apartment = dispatcher.Invoke(delegate { return Thread.CurrentThread.GetApartmentState(); });
                    AssertEqual(ApartmentState.STA, apartment, "dispatcher apartment");
                }
            }
        }

        private static void DispatchedAdapterDelegatesCalls()
        {
            var identityObject = new object();
            AssertEqual(
                DocumentIdentity.RuntimeKey("Excel", identityObject),
                DocumentIdentity.RuntimeKey("Excel", identityObject),
                "runtime identity is stable for the same object");

            var createdOnThread = 0;
            var executeOnThread = 0;
            var adapter = new FakeOfficeAdapter();

            using (var dispatched = new DispatchedOfficeApplicationAdapter(delegate(IOfficeStaDispatcher ignored)
            {
                createdOnThread = Thread.CurrentThread.ManagedThreadId;
                return new ThreadRecordingOfficeAdapter(adapter, delegate
                {
                    executeOnThread = Thread.CurrentThread.ManagedThreadId;
                });
            }))
            {
                AssertEqual("Excel", dispatched.HostName, "host name");
                var result = dispatched.ExecuteTool(GuardProbeCommand(adapter));
                AssertTrue(result.Success, "tool success");
                AssertEqual(1, adapter.Executed.Count, "executed count");
            }

            AssertTrue(createdOnThread != 0, "created thread");
            AssertEqual(createdOnThread, executeOnThread, "execute thread");

            foreach (var host in new[] { "Excel", "Word", "PowerPoint", "Outlook" })
            {
                var guardedAdapter = FakeOfficeAdapter.ForHost(host);
                var probe = GuardProbeCommand(guardedAdapter);
                using (var dispatched = new DispatchedOfficeApplicationAdapter(
                    delegate(IOfficeStaDispatcher ignored) { return guardedAdapter; }))
                {
                    var originalDocumentKey = dispatched.DocumentKey;
                    var originalRuntimeKey = dispatched.RuntimeDocumentKey;
                    using (((IOfficeDocumentExecutionGuard)dispatched).BeginExpectedDocument(
                        host, originalDocumentKey, originalRuntimeKey))
                    {
                        guardedAdapter.RuntimeDocumentKeyValue = originalRuntimeKey + "-new-proxy";
                        var sameDocument = dispatched.ExecuteTool(probe);
                        AssertTrue(sameDocument.Success,
                            host + " guard accepts a stable document key when COM runtime identity changes");
                    }

                    guardedAdapter.RuntimeDocumentKeyValue = originalRuntimeKey;
                    using (((IOfficeDocumentExecutionGuard)dispatched).BeginExpectedDocument(
                        host, originalDocumentKey, originalRuntimeKey))
                    {
                        guardedAdapter.DocumentKeyValue = originalDocumentKey + "-saved";
                        var migratedDocument = dispatched.ExecuteTool(probe);
                        AssertTrue(migratedDocument.Success,
                            host + " guard accepts the same runtime document after identity migration");
                    }

                    using (((IOfficeDocumentExecutionGuard)dispatched).BeginExpectedDocument(
                        host, guardedAdapter.DocumentKey, guardedAdapter.RuntimeDocumentKey))
                    {
                        guardedAdapter.DocumentKeyValue += "-other";
                        guardedAdapter.RuntimeDocumentKeyValue += "-other";
                        var blocked = dispatched.ExecuteTool(probe);
                        AssertEqual("active_document_changed", blocked.ErrorCode,
                            host + " guard blocks a different Office document");
                        var readBlocked = false;
                        try
                        {
                            dispatched.GetDocumentSnapshot(128);
                        }
                        catch (OfficeDocumentGuardException ex)
                        {
                            readBlocked = string.Equals(
                                ex.ErrorCode,
                                "active_document_changed",
                                StringComparison.Ordinal);
                        }
                        AssertTrue(readBlocked,
                            host + " guard also blocks live document reads after dispatch");
                        AssertEqual(2, guardedAdapter.Executed.Count,
                            host + " blocked tool never reaches Office adapter");
                    }
                }
            }
        }

        private static ToolCommand GuardProbeCommand(FakeOfficeAdapter adapter)
        {
            var definition = adapter.GetBuiltInTools().First(tool =>
                !ExcelReadToolIds.Owns(tool.Id) && !ExcelWriteToolIds.Owns(tool.Id));
            return new ToolCommand { ToolId = definition.Id };
        }

        private static void HostRuntimeCancelsQueuedMutationAndReleasesAccess()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                var target = new OfficeDocumentExecutionExpectation
                {
                    Host = adapter.HostName,
                    DocumentKey = adapter.DocumentKey,
                    RuntimeDocumentKey = adapter.RuntimeDocumentKey
                };
                var owner = new HostRuntime(adapter, paths);
                var waiter = new HostRuntime(adapter, paths);
                var actionCalls = 0;
                using (var started = new ManualResetEventSlim(false))
                using (var cancellation = new CancellationTokenSource())
                {
                    var lease = owner.BeginDocumentAccess(target);
                    Task<bool> pending = null;
                    try
                    {
                        pending = Task.Run(() =>
                        {
                            started.Set();
                            try
                            {
                                waiter.ExecuteMutation(target, false, true, cancellation.Token, () =>
                                {
                                    Interlocked.Increment(ref actionCalls);
                                    return ToolResult.Ok("unexpected dispatch");
                                });
                                return false;
                            }
                            catch (OperationCanceledException)
                            {
                                return true;
                            }
                        });
                        AssertTrue(started.Wait(5000), "competing runtime starts access");
                        AssertTrue(!pending.Wait(150), "another runtime cannot bypass the held document gate");
                        cancellation.Cancel();
                        AssertTrue(pending.Wait(5000), "queued mutation observes cancellation before owner releases access");
                        AssertTrue(pending.GetAwaiter().GetResult(), "pre-dispatch cancellation remains cancellation");
                        AssertEqual(0, actionCalls, "cancelled waiter never enters its mutation action");
                    }
                    finally
                    {
                        cancellation.Cancel();
                        lease.Dispose();
                        if (pending != null)
                            AssertTrue(pending.Wait(5000), "queued mutation completes after access cleanup");
                    }
                }

                var uncertain = waiter.ExecuteMutation(target, false, true, CancellationToken.None, () =>
                {
                    throw new OperationCanceledException("test cancellation after mutation action entry");
                });
                AssertEqual("tool_effect_uncertain", uncertain.ErrorCode,
                    "cancellation after action entry preserves possible document effect");
                AssertEqual(false, uncertain.Retryable, "uncertain mutation is not automatically retryable");

                var appliedEffects = 0;
                var postActionFailures = new Exception[]
                {
                    new HostRuntime.MutationLockException("gate failure after effect", true),
                    new OfficeDocumentGuardException(ToolResult.Fail("guard failure after effect", null, "active_document_changed", false))
                };
                foreach (var failure in postActionFailures)
                {
                    var failed = waiter.ExecuteForExpectedDocument(target, true, () =>
                        waiter.ExecuteMutation(target, false, true, CancellationToken.None, () =>
                        {
                            appliedEffects++;
                            throw failure;
                        }));
                    AssertEqual("tool_effect_uncertain", failed.ErrorCode, "post-action guard/gate failure retains possible effect");
                    AssertEqual(false, failed.Retryable, "post-action failure is not reported as a safe retry");
                }
                AssertEqual(postActionFailures.Length, appliedEffects, "each simulated effect precedes its failure");

                var next = new HostRuntime(adapter, paths);
                var result = next.ExecuteMutation(target, false, true, CancellationToken.None,
                    () => ToolResult.Ok("next mutation"));
                AssertTrue(result.Success, "a later runtime acquires access after pre- and post-action cancellation");
            });
        }

        private static void HostRuntimeReusesNestedReadAccessAndReleasesOnFailure()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                var target = new OfficeDocumentExecutionExpectation
                {
                    Host = adapter.HostName,
                    DocumentKey = adapter.DocumentKey,
                    RuntimeDocumentKey = adapter.RuntimeDocumentKey
                };
                var runtime = new HostRuntime(adapter, paths);
                var reader = new HostRuntime(adapter, paths);
                Task read = null;
                using (var readStarted = new ManualResetEventSlim(false))
                {
                    try
                    {
                        var result = runtime.ExecuteMutation(target, false, true, CancellationToken.None, () =>
                        {
                            using (runtime.BeginDocumentAccess(target))
                            using (runtime.BeginDocumentAccess(target))
                            {
                                read = Task.Run(() =>
                                {
                                    readStarted.Set();
                                    using (reader.BeginDocumentAccess(target)) { }
                                });
                                AssertTrue(readStarted.Wait(5000), "another runtime starts its document read");
                                AssertTrue(!read.Wait(150), "nested access does not let another runtime bypass the gate");
                                return ToolResult.Ok("nested read");
                            }
                        });
                        AssertTrue(result.Success, "nested reads reuse the owning mutation access");
                    }
                    finally
                    {
                        if (read != null)
                            AssertTrue(read.Wait(5000), "another runtime reads after mutation access is released");
                    }
                }

                var failure = new InvalidOperationException("test read failure");
                try
                {
                    runtime.ExecuteMutation(target, false, true, CancellationToken.None, () =>
                    {
                        using (runtime.BeginDocumentAccess(target))
                        {
                            throw failure;
                        }
                    });
                    throw new InvalidOperationException("mutation swallowed the read failure");
                }
                catch (InvalidOperationException ex)
                {
                    AssertTrue(ReferenceEquals(failure, ex), "nested action failure propagates unchanged");
                }

                var next = new HostRuntime(adapter, paths);
                var recovered = next.ExecuteMutation(target, false, true, CancellationToken.None,
                    () => ToolResult.Ok("mutation after failed read"));
                AssertTrue(recovered.Success, "failed mutation releases document access for another runtime");
            });
        }

        private static void HostRuntimeBoundSessionPreservesTargetAcrossSaveAsAndRejectsReopen()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument { StableId = "Excel:Path:original.xlsx", IsAlive = true };
                    var session = new BoundTestOfficeSession(dispatcher, document, "bound-lifetime-one", new object());
                    var adapter = new BoundTestOfficeAdapter(session);
                    var runtime = new HostRuntime(adapter, paths);
                    var target = BoundTestTarget(session);
                    var actionCalls = 0;
                    Func<ToolResult> action = () =>
                    {
                        actionCalls++;
                        AssertTrue(dispatcher.CheckAccess, "bound action executes on its owner dispatcher");
                        AssertTrue(ReferenceEquals(document, session.BoundDocumentObject), "action retains the exact bound object");
                        return ToolResult.Ok("bound access");
                    };

                    AssertTrue(runtime.ExecuteForExpectedDocument(target, true, action).Success, "initial bound access succeeds");
                    dispatcher.Invoke(() => document.StableId = "Excel:Path:saved-as.xlsx");
                    AssertTrue(runtime.ExecuteForExpectedDocument(target, true, action).Success,
                        "Save As does not invalidate the matching runtime lifetime");

                    var savedTarget = BoundTestTarget(session);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closed = runtime.ExecuteForExpectedDocument(savedTarget, true, action);
                    AssertTrue(!closed.Success, "a closed bound session refuses access");

                    var reopened = new BoundTestDocument { StableId = document.StableId, IsAlive = true };
                    var reopenedAdapter = new BoundTestOfficeAdapter(
                        new BoundTestOfficeSession(dispatcher, reopened, "bound-lifetime-two", new object()));
                    var replaced = new HostRuntime(reopenedAdapter, paths).ExecuteForExpectedDocument(savedTarget, true, action);
                    AssertTrue(!replaced.Success, "matching saved path cannot override a different runtime lifetime");
                    AssertEqual(2, actionCalls, "neither closed nor reopened targets reach the action");
                }
            });
        }

        private static void HostRuntimeBoundOwnerStaBusyAndNestedReads()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                using (var held = new ManualResetEventSlim(false))
                using (var release = new ManualResetEventSlim(false))
                {
                    var document = new BoundTestDocument { StableId = "Excel:Path:before-save.xlsx", IsAlive = true };
                    var session = new BoundTestOfficeSession(dispatcher, document, "bound-shared-lifetime", new object());
                    var adapter = new BoundTestOfficeAdapter(session);
                    var owner = new HostRuntime(adapter, paths);
                    var contender = new HostRuntime(adapter, paths);
                    var target = BoundTestTarget(session);
                    var unexpectedCalls = 0;
                    Task<ToolResult> attempted = null;
                    var worker = Task.Run(() =>
                    {
                        using (owner.BeginDocumentAccess(target))
                        {
                            held.Set();
                            AssertTrue(release.Wait(10000), "worker receives access-release signal");
                        }
                    });
                    OfficeDocumentExecutionExpectation savedTarget = null;
                    try
                    {
                        AssertTrue(held.Wait(5000), "worker holds the bound document gate");
                        dispatcher.Invoke(() => document.StableId = "Excel:Path:after-save.xlsx");
                        savedTarget = BoundTestTarget(session);
                        attempted = Task.Run(() => dispatcher.Invoke(() =>
                            contender.ExecuteForExpectedDocument(savedTarget, true, () =>
                            {
                                Interlocked.Increment(ref unexpectedCalls);
                                return ToolResult.Ok("unexpected owner-thread dispatch");
                            })));
                        AssertTrue(attempted.Wait(3000), "owner dispatcher reports busy without waiting for worker release");
                        var busy = attempted.GetAwaiter().GetResult();
                        AssertEqual("tool_mutation_busy", busy.ErrorCode, "Save As retains the occupied runtime gate");
                        AssertEqual(true, busy.Retryable, "owner-thread contention is an explicit retryable result");
                        AssertEqual(0, unexpectedCalls, "busy owner-thread request never enters its action");
                    }
                    finally
                    {
                        release.Set();
                        AssertTrue(worker.Wait(5000), "worker releases the document gate");
                        if (attempted != null) AssertTrue(attempted.Wait(5000), "owner-thread attempt completes after cleanup");
                    }

                    var nestedCalls = 0;
                    var nested = contender.ExecuteForExpectedDocument(target, true, () =>
                    {
                        using (contender.BeginDocumentAccess(savedTarget))
                        {
                            return contender.ExecuteMutation(savedTarget, false, true, CancellationToken.None, () =>
                            {
                                using (contender.BeginDocumentAccess(savedTarget))
                                {
                                    nestedCalls++;
                                    return ToolResult.Ok("nested bound read");
                                }
                            });
                        }
                    });
                    AssertTrue(nested.Success, "same-operation nested mutation/read reuses access on the owner dispatcher");
                    AssertEqual(1, nestedCalls, "nested action executes exactly once after contention ends");
                }
            });
        }

        private static void HostRuntimeBoundOperationDoesNotLeakAccess()
        {
            WithTempPaths(paths =>
            {
                using (var dispatched = CreateBoundTestDispatchedAdapter(
                    new BoundTestDocument { StableId = "Excel:Path:first.xlsx", IsAlive = true }, "bound-first"))
                using (var childStarted = new ManualResetEventSlim(false))
                using (var cancellation = new CancellationTokenSource())
                {
                    var first = (BoundTestOfficeSession)dispatched.DocumentSession;
                    var second = new BoundTestOfficeSession(dispatched.StaDispatcher,
                        new BoundTestDocument { StableId = "Excel:Path:second.xlsx", IsAlive = true }, "bound-second", new object());
                    var runtime = new HostRuntime(dispatched, paths);
                    var other = new HostRuntime(new BoundTestOfficeAdapter(second), paths);
                    var target = BoundTestTarget(first);
                    var otherTarget = BoundTestTarget(second);
                    var unexpectedCalls = 0;
                    Task<bool> child = null;
                    try
                    {
                        var result = runtime.ExecuteForExpectedDocument(target, true, () =>
                        {
                            var nestedRoot = runtime.ExecuteForExpectedDocument(target, true, () =>
                            {
                                unexpectedCalls++;
                                return ToolResult.Ok("unexpected new-root dispatch");
                            });
                            AssertEqual("tool_mutation_busy", nestedRoot.ErrorCode, "a new root on the same thread cannot borrow the current operation");

                            var differentTargetBlocked = false;
                            try
                            {
                                using (other.BeginDocumentAccess(otherTarget)) { unexpectedCalls++; }
                            }
                            catch (HostRuntime.MutationLockException ex)
                            {
                                differentTargetBlocked = ex.Retryable;
                            }
                            AssertTrue(differentTargetBlocked, "nested access cannot treat another target as the held document");

                            child = Task.Run(() =>
                            {
                                childStarted.Set();
                                try
                                {
                                    runtime.ExecuteForExpectedDocument(target, true, cancellation.Token, () =>
                                    {
                                        Interlocked.Increment(ref unexpectedCalls);
                                        return ToolResult.Ok("unexpected child dispatch");
                                    });
                                    return false;
                                }
                                catch (OperationCanceledException) { return true; }
                            });
                            AssertTrue(childStarted.Wait(5000), "child task starts a separate operation");
                            AssertTrue(!child.Wait(150), "child task cannot bypass its parent's occupied document gate");
                            cancellation.Cancel();
                            AssertTrue(child.Wait(5000), "cached wrapper metadata lets queued child cancel while the owner dispatcher is occupied");
                            AssertTrue(child.GetAwaiter().GetResult(), "child cancellation occurs before dispatch");
                            using (runtime.BeginDocumentAccess(target)) { }
                            return ToolResult.Ok("parent retains access");
                        });
                        AssertTrue(result.Success, "blocked nested attempts preserve the parent operation's access");
                    }
                    finally
                    {
                        cancellation.Cancel();
                        if (child != null) AssertTrue(child.Wait(5000), "child exits after parent access cleanup");
                    }
                    AssertEqual(0, unexpectedCalls, "new root, different target and child never enter unauthorized actions");
                }
            });
        }

        private static void HostRuntimeBoundQueuedCancellationSkipsActionAndReleasesGate()
        {
            WithTempPaths(paths =>
            {
                using (var owner = new OfficeStaDispatcher())
                using (var queued = new ManualResetEventSlim(false))
                using (var admit = new ManualResetEventSlim(false))
                using (var cancellation = new CancellationTokenSource())
                {
                    var dispatcher = new BoundTestQueuedDispatcher(owner, queued, admit);
                    var session = new BoundTestOfficeSession(dispatcher,
                        new BoundTestDocument { StableId = "Excel:Path:queued.xlsx", IsAlive = true }, "bound-queued", new object());
                    var runtime = new HostRuntime(new BoundTestOfficeAdapter(session), paths);
                    var target = BoundTestTarget(session);
                    var actionCalls = 0;
                    dispatcher.PauseNextDispatch();
                    var pending = Task.Run(() =>
                    {
                        try
                        {
                            runtime.ExecuteForExpectedDocument(target, true, cancellation.Token, () =>
                            {
                                Interlocked.Increment(ref actionCalls);
                                return ToolResult.Ok("unexpected queued action");
                            });
                            return false;
                        }
                        catch (OperationCanceledException) { return true; }
                    });
                    try
                    {
                        AssertTrue(queued.Wait(5000), "bound callback reaches the owner admission boundary after gate acquisition");
                        cancellation.Cancel();
                        admit.Set();
                        AssertTrue(pending.Wait(5000), "cancelled callback leaves its owner queue");
                        AssertTrue(pending.GetAwaiter().GetResult(), "cancellation before owner execution remains cancellation");
                        AssertEqual(0, actionCalls, "cancelled callback never starts its action");
                    }
                    finally
                    {
                        cancellation.Cancel();
                        admit.Set();
                        AssertTrue(pending.Wait(5000), "queued callback completes after admission cleanup");
                    }
                    var next = runtime.ExecuteForExpectedDocument(target, true, () => ToolResult.Ok("next bound action"));
                    AssertTrue(next.Success, "cancelled queued callback releases its document gate");
                }
            });
        }

        private static void HostRuntimeGateOrderAndFailedAcquisitionCleanup()
        {
            WithTempPaths(paths =>
            {
                var documentKey = "order_document|" + paths.Root;
                var sharedKey = "order_shared|" + paths.Root;
                var documentToken = new object();
                using (DocumentAccessGate.BeginOperation())
                using (DocumentAccessGate.Enter(documentKey, documentToken, null, null, false, CancellationToken.None))
                using (DocumentAccessGate.Enter(sharedKey, null, null, null, false, CancellationToken.None, false))
                using (DocumentAccessGate.Enter(documentKey, documentToken, null, null, false, CancellationToken.None))
                {
                    // Document -> shared -> already-held document is safe reentry, not reverse acquisition.
                }

                var reverseBlocked = false;
                using (DocumentAccessGate.BeginOperation())
                using (DocumentAccessGate.Enter(sharedKey, null, null, null, false, CancellationToken.None, false))
                {
                    try
                    {
                        using (DocumentAccessGate.Enter(documentKey, documentToken, null, null, false, CancellationToken.None)) { }
                    }
                    catch (HostRuntime.MutationLockException ex) { reverseBlocked = ex.Retryable; }
                }
                AssertTrue(reverseBlocked, "shared state cannot acquire a new document gate in reverse order");

                var inconsistentBlocked = false;
                using (DocumentAccessGate.BeginOperation())
                using (DocumentAccessGate.Enter(documentKey, documentToken, null, null, false, CancellationToken.None))
                {
                    try
                    {
                        using (DocumentAccessGate.Enter(documentKey, new object(), null, null, false, CancellationToken.None)) { }
                    }
                    catch (HostRuntime.MutationLockException ex) { inconsistentBlocked = !ex.Retryable; }
                }
                AssertTrue(inconsistentBlocked, "conflicting bound tokens cannot share one runtime identity");

                var validDirectory = Path.Combine(paths.Root, "gate-cleanup");
                var invalidDirectory = Path.Combine(paths.Root, "not-a-directory");
                Directory.CreateDirectory(validDirectory);
                File.WriteAllText(invalidDirectory, "blocks directory creation");
                foreach (var fileContention in new[] { false, true })
                {
                    var key = "failed_acquire|" + paths.Root + "|" + fileContention;
                    var directory = fileContention ? validDirectory : invalidDirectory;
                    var rejected = false;
                    using (fileContention
                        ? new FileStream(Path.Combine(validDirectory, "blocked.lck"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                        : null)
                    using (DocumentAccessGate.BeginOperation())
                    {
                        try
                        {
                            using (DocumentAccessGate.Enter(key, new object(), directory, "blocked", false, CancellationToken.None)) { }
                        }
                        catch (HostRuntime.MutationLockException ex) { rejected = ex.Retryable == fileContention; }
                    }
                    AssertTrue(rejected, "directory/file lock failure retains its failure classification");
                    using (DocumentAccessGate.BeginOperation())
                    using (DocumentAccessGate.Enter(key, new object(), validDirectory, "blocked", false, CancellationToken.None))
                    {
                        // A new token can succeed only after the failed entry and semaphore were released.
                    }
                }
            });
        }

        private static DispatchedOfficeApplicationAdapter CreateBoundTestDispatchedAdapter(BoundTestDocument document, string runtimeId)
        {
            DispatchedOfficeApplicationAdapter adapter = null;
            adapter = new DispatchedOfficeApplicationAdapter(dispatcher => new BoundTestOfficeAdapter(
                new BoundTestOfficeSession(dispatcher, document, runtimeId, new object())));
            return adapter;
        }

        private static OfficeDocumentExecutionExpectation BoundTestTarget(BoundTestOfficeSession session)
        {
            return session.StaDispatcher.Invoke(() => new OfficeDocumentExecutionExpectation
            {
                Host = session.Host,
                DocumentKey = session.StableDocumentId,
                RuntimeDocumentKey = session.RuntimeDocumentId
            });
        }

        private static void DocumentCatalogActivatesSelectedDocument()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var catalog = (IOfficeDocumentCatalog)adapter;
            var before = catalog.ListOpenDocuments();

            AssertEqual(2, before.Count, "open document count");
            AssertTrue(before.Any(item => item.DocumentKey == "forecast-doc" && !item.IsActive), "forecast initially inactive");
            AssertTrue(catalog.ActivateDocument("forecast-doc"), "forecast activation succeeds");
            AssertEqual("forecast-doc", adapter.DocumentKey, "active document key");
            AssertEqual("Forecast.xlsx", adapter.DocumentTitle, "active document title");
            AssertTrue(catalog.ListOpenDocuments().Any(item => item.DocumentKey == "forecast-doc" && item.IsActive), "forecast marked active");
        }

        private static void DocumentOpenServiceRecognizesWebPaths()
        {
            AssertTrue(DocumentOpenService.IsAvailable("https://example.sharepoint.com/Documents/Book.xlsx"), "https document path");
            AssertTrue(DocumentOpenService.IsAvailable("http://example.test/Book.xlsx"), "http document path");
            AssertTrue(!DocumentOpenService.IsAvailable(string.Empty), "empty document path");
            AssertTrue(DocumentOpenService.SamePath("C:\\Docs\\Book.xlsx", "c:/docs/book.xlsx"),
                "Windows paths compare case-insensitively");
            AssertTrue(DocumentOpenService.SamePath(
                "https://example.sharepoint.com/Documents/Book%20One.xlsx",
                "https://EXAMPLE.sharepoint.com/Documents/Book One.xlsx"),
                "SharePoint URLs compare canonically");
            AssertTrue(!DocumentOpenService.SamePath("C:\\Docs\\One.xlsx", "C:\\Docs\\Two.xlsx"),
                "different full paths stay distinct");
        }

        private static void UnsavedDocumentIdentityUsesRuntimeKey()
        {
            var properties = new FakeDocumentProperties();
            var key = DocumentIdentity.ForOfficeDocument("Excel", string.Empty, "Excel:Runtime:first", delegate { return properties; });

            AssertEqual("Excel:Runtime:first", key, "unsaved document runtime identity");
            AssertEqual(0, properties.Count, "identity lookup does not dirty unsaved document");
        }

        private static void SavedDocumentIdentityUsesFullPathOrLegacyId()
        {
            var properties = new FakeDocumentProperties();
            var first = DocumentIdentity.ForOfficeDocument(
                "Excel",
                "C:\\Docs\\One.xlsx",
                "Excel:Runtime:first",
                delegate { return properties; });
            var second = DocumentIdentity.ForOfficeDocument(
                "Excel",
                "C:\\Docs\\Two.xlsx",
                "Excel:Runtime:second",
                delegate { return properties; });

            AssertEqual("Excel:Path:C:\\Docs\\One.xlsx", first, "saved document full path identity");
            AssertEqual("Excel:Path:C:\\Docs\\Two.xlsx", second, "same-folder documents stay distinct");
            AssertEqual(0, properties.Count, "identity lookup does not add a hidden property");

            properties.Add(DocumentIdentity.PropertyName, false, 4, "legacy-id");
            var legacy = DocumentIdentity.ForOfficeDocument(
                "Excel",
                "C:\\Docs\\One.xlsx",
                "Excel:Runtime:first",
                delegate { return properties; });
            AssertEqual("Excel:DocumentId:legacy-id", legacy, "existing persisted identity remains supported");
        }

        private static void HostRuntimeDirectReadsPreserveRootAndTarget()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument { StableId = "doc", IsAlive = true };
                    var session = new BoundTestOfficeSession(dispatcher, document, "direct-read", new object());
                    var runtime = new HostRuntime(new BoundTestOfficeAdapter(session), paths);
                    var target = BoundTestTarget(session);
                    var calls = 0;
                    AssertEqual("read", runtime.ReadDocument(target, () =>
                    {
                        AssertTrue(dispatcher.CheckAccess, "typed read runs on owner STA");
                        using (runtime.BeginDocumentAccess(target)) { calls++; }
                        AssertDirectReadBusy(runtime);
                        return "read";
                    }), "typed result preserved");
                    try
                    {
                        runtime.ReadDocument<int>(target, () => { throw new InvalidOperationException("read failure"); });
                        throw new Exception("read exception was swallowed");
                    }
                    catch (InvalidOperationException ex) { AssertEqual("read failure", ex.Message, "read failure propagated"); }
                    AssertEqual(2, runtime.ReadDocument(target, () => ++calls), "failed read releases gate");
                    target.RuntimeDocumentKey = "different-lifetime";
                    try
                    {
                        runtime.ReadDocument(target, () => ++calls);
                        throw new Exception("mismatched target was accepted");
                    }
                    catch (OfficeDocumentGuardException) { }
                    dispatcher.Invoke(() => document.IsAlive = false);
                    try
                    {
                        runtime.ReadDocument(null, () => ++calls);
                        throw new Exception("closed document was accepted");
                    }
                    catch (OfficeDocumentGuardException) { }
                    AssertEqual(2, calls, "rejected reads never enter action");
                }
            });
        }

        private static void DirectContextReadsShareDocumentAccess()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var session = new BoundTestOfficeSession(dispatcher,
                        new BoundTestDocument { StableId = "doc", IsAlive = true }, "context-read", new object());
                    var adapter = new BoundTestOfficeAdapter(session);
                    var runtime = new HostRuntime(adapter, paths);
                    var capture = new OfficeContextCaptureService(adapter, runtime);
                    var target = BoundTestTarget(session);
                    var calls = new List<string>();
                    adapter.BeforeRead = kind =>
                    {
                        AssertTrue(dispatcher.CheckAccess, "context/catalog callback on owner STA");
                        AssertDirectReadBusy(runtime);
                        calls.Add(kind);
                    };
                    var note = capture.CaptureSelection(target, "selection", 2000);
                    AssertTrue(note != null && !string.IsNullOrWhiteSpace(note.Text), "selection returned");
                    AssertTrue(calls.SequenceEqual(new[] { "prepare", "selection" }), "preparation and capture both gated");
                    AssertTrue(capture.CaptureOfficeContext() != null, "UI context capture succeeds");
                    AssertTrue(calls.Contains("context"), "UI context provider reached under gate");
                    calls.Clear();
                    var held = runtime.ExecuteForExpectedDocument(BoundTestTarget(session), true, () =>
                    {
                        AssertTrue(capture.CaptureOfficeContext() == null, "reentered UI context omitted on busy");
                        try
                        {
                            capture.CaptureSelection(target, "selection", 2000);
                            throw new Exception("reentered selection borrowed mutation access");
                        }
                        catch (HostRuntime.MutationLockException) { }
                        return ToolResult.Ok("held");
                    });
                    AssertTrue(held.Success, "outer operation survives reentrant reads");
                    AssertEqual(0, calls.Count, "busy reads do not call Office");
                    adapter.BeforeRead = kind =>
                    {
                        if (kind == "prepare") throw new OfficeDocumentGuardException(
                            ToolResult.Fail("closed during prepare", null, "active_document_changed", false));
                        calls.Add(kind);
                    };
                    try
                    {
                        capture.CaptureSelection(target, "selection", 2000);
                        throw new Exception("preparation guard was swallowed");
                    }
                    catch (OfficeDocumentGuardException) { }
                    AssertEqual(0, calls.Count, "capture skipped after preparation guard failure");
                    adapter.BeforeRead = null;
                    AssertTrue(capture.CaptureOfficeContext() != null, "read gate released after preparation failure");
                }
            });
        }

        private static void DirectVbaCatalogReadsShareDocumentAccess()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument { StableId = "doc", IsAlive = true };
                    var session = new BoundTestOfficeSession(dispatcher, document, "catalog-read", new object());
                    var fake = new FakeOfficeAdapter();
                    var package = BuildVbaPackageToolForTest();
                    foreach (var component in package.Components)
                        fake.SetVbaModule(component.Name, component.Code, component.Type);
                    var adapter = new BoundTestOfficeAdapter(session, fake);
                    var runtime = new HostRuntime(adapter, paths);
                    var store = new ToolStore(paths);
                    var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), store, paths: paths);
                    var catalog = new ToolCatalogService(adapter, executor, store);
                    adapter.BeforeRead = kind =>
                    {
                        AssertTrue(dispatcher.CheckAccess, "VBA list and module read on owner STA");
                        AssertDirectReadBusy(runtime);
                    };
                    Func<bool> hasPackage = () => catalog.GetVisibleTools().Any(tool => tool.Id == package.Id);
                    AssertTrue(runtime.ExecuteForExpectedDocument(BoundTestTarget(session), true, () =>
                    {
                        AssertTrue(!hasPackage(), "busy catalog excludes unavailable document tools");
                        AssertEqual(0, fake.Executed.Count, "busy catalog never reaches Office");
                        return ToolResult.Ok("held");
                    }).Success, "busy catalog preserves outer operation");
                    AssertTrue(hasPackage(), "busy failure was not cached as empty discovery");
                    AssertTrue(fake.Executed.Count(command => command.ToolId == "excel.vba_read_module") >= 2,
                        "manifest and declared components read under the same gate");
                    var readCount = fake.Executed.Count;
                    AssertTrue(hasPackage(), "cache remains available with valid access");
                    AssertEqual(readCount, fake.Executed.Count, "cache avoids repeated COM reads");

                    foreach (var failComponent in new[] { false, true })
                    foreach (var failureKind in new[] { "result", "gate", "exception" })
                    {
                        catalog.InvalidateDocumentVbaTools();
                        var attempts = 0;
                        var matchingReads = 0;
                        fake.BeforeExecuteTool = command =>
                        {
                            attempts++;
                            var failedTool = failComponent ? "excel.vba_read_module" : "excel.vba_list_project_components_internal";
                            if (command.ToolId != failedTool || ++matchingReads != (failComponent ? 2 : 1)) return;
                            if (failureKind == "gate") throw new HostRuntime.MutationLockException("transient catalog access failure", true);
                            if (failureKind == "exception") throw new InvalidOperationException("transient backend read failure");
                            fake.QueueResult(command.ToolId, ToolResult.Fail(
                                "transient document guard failure", null, "active_document_changed", false));
                        };
                        AssertTrue(!catalog.GetVisibleTools().Any(tool => tool.Scope == "document"),
                            "failed discovery publishes neither empty-cache success nor a partial package: " + failureKind);
                        AssertEqual(failComponent ? 3 : 1, attempts, "failed access ends this load without retry");
                        fake.BeforeExecuteTool = null;
                        readCount = fake.Executed.Count;
                        AssertTrue(hasPackage(), "next independent load recovers after " + failureKind);
                        AssertTrue(fake.Executed.Count > readCount, "failed load was not cached");
                    }

                    foreach (var malformedProject in new[] { "{}", "{\"modules\":\"invalid\"}" })
                    {
                        catalog.InvalidateDocumentVbaTools();
                        fake.QueueResult(
                            "excel.vba_list_project_components_internal",
                            ToolResult.Ok("malformed project", malformedProject));
                        AssertTrue(!hasPackage(), "malformed project snapshot publishes no document tools");
                        readCount = fake.Executed.Count;
                        AssertTrue(hasPackage(), "malformed project snapshot is not cached as an empty project");
                        AssertTrue(fake.Executed.Count > readCount, "project snapshot is reread after malformed data");
                    }

                    catalog.InvalidateDocumentVbaTools();
                    fake.QueueResult("excel.vba_read_module", ToolResult.Ok("malformed module", "{}"));
                    AssertTrue(!hasPackage(), "malformed module snapshot publishes no partial package");
                    readCount = fake.Executed.Count;
                    AssertTrue(hasPackage(), "malformed module snapshot is not cached as an unavailable package");
                    AssertTrue(fake.Executed.Count > readCount, "module snapshot is reread after malformed data");

                    catalog.InvalidateDocumentVbaTools();
                    fake.QueueResult("excel.vba_list_project_components_internal", ToolResult.Ok("empty project", "{\"modules\":[]}"));
                    AssertTrue(!hasPackage(), "successfully empty project has no document tools");
                    readCount = fake.Executed.Count;
                    AssertTrue(!hasPackage(), "successful empty catalog remains cached");
                    AssertEqual(readCount, fake.Executed.Count, "successful empty discovery avoids repeated reads");
                    catalog.InvalidateDocumentVbaTools();
                    AssertTrue(hasPackage(), "explicit invalidation refreshes the successful empty cache");
                    readCount = fake.Executed.Count;
                    dispatcher.Invoke(() => document.IsAlive = false);
                    AssertTrue(!hasPackage(), "closed session cannot reuse document catalog cache");
                    AssertEqual(readCount, fake.Executed.Count, "closed session cannot access Office");
                }
            });
        }

        private static void AssertDirectReadBusy(HostRuntime runtime)
        {
            try
            {
                runtime.ReadDocument<int>(null, () => { throw new Exception("independent read borrowed access"); });
                throw new Exception("independent read unexpectedly succeeded");
            }
            catch (HostRuntime.MutationLockException ex) { AssertTrue(ex.Retryable, "independent read reports busy"); }
        }

        private static void ExcelIdentityProbeParsesObjectIdentity()
        {
            var packet = ExcelIdentityProbePacket();
            var parsed = ComIdentitySample.Parse(packet);
            AssertEqual("fedcba9876543210:0123456789abcdef", parsed.Candidate, "unsigned little-endian OXID/OID decoded");
            // Interface id and ref-count metadata are not document identity.
            packet[24] = 32;
            packet[28] = 17;
            packet[48] = 99;
            var otherInterface = ComIdentitySample.Parse(packet);
            AssertEqual(parsed.Candidate, otherInterface.Candidate, "IPID and reference counts do not alter candidate");
            AssertTrue(parsed.Ipid != otherInterface.Ipid, "interface observations remain distinguishable");
            packet[40] ^= 1;
            AssertTrue(parsed.Candidate != ComIdentitySample.Parse(packet).Candidate,
                "different object in same exporter has a different candidate");
        }

        private static void ExcelIdentityProbeRejectsInvalidPackets()
        {
            var samples = new List<byte[]> { null, new byte[0], new byte[65537] };
            var packet = ExcelIdentityProbePacket();
            for (var length = 1; length < packet.Length; length++) samples.Add(packet.Take(length).ToArray());
            foreach (var format in new byte[] { 0, 2, 3, 4, 8, 255 })
            {
                var unsupported = (byte[])packet.Clone();
                unsupported[4] = format;
                samples.Add(unsupported);
            }
            foreach (var offset in new[] { 0, 8, 64, 66, 70, 74 })
            {
                var malformed = (byte[])packet.Clone();
                malformed[offset] = 255;
                samples.Add(malformed);
            }
            foreach (var offset in new[] { 32, 40, 48 })
            {
                var emptyIdentity = (byte[])packet.Clone();
                Array.Clear(emptyIdentity, offset, offset == 48 ? 16 : 8);
                samples.Add(emptyIdentity);
            }
            samples.Add(packet.Concat(new byte[] { 0 }).ToArray());
            foreach (var sample in samples)
            {
                try
                {
                    ComIdentitySample.Parse(sample);
                    throw new Exception("invalid OBJREF acquired a candidate identity");
                }
                catch (InvalidDataException) { }
            }
        }

        private static void ExcelIdentityProbeRejectsNonWindowsAccess()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) return;
            try
            {
                ComIdentityLease.Create(new object());
                throw new Exception("probe reached native work outside Windows");
            }
            catch (PlatformNotSupportedException) { }
            try
            {
                ExcelProbeTarget.ResolveApplication(1);
                throw new Exception("resolver reached native work outside Windows");
            }
            catch (PlatformNotSupportedException) { }
        }

        private static void ExcelIdentityHelperProtocolIsBounded()
        {
            var request = new ExcelIdentityHelperRequest
            {
                SchemaVersion = ExcelIdentityHelperProtocol.SchemaVersion,
                Nonce = new string('a', 64),
                Operation = "bind",
                Hwnd = 42,
                WorkbookIndex = 1,
                Label = "client-A",
                Scenario = "initial",
                OwnerAssemblyMvid = new Guid("11111111-2222-3333-4444-555555555555").ToString("D")
            };
            var json = ExcelIdentityHelperProtocol.SerializeRequest(request);
            var parsed = ExcelIdentityHelperProtocol.ParseRequest(json);
            AssertEqual("bind", parsed.Operation, "helper operation remains typed");
            AssertEqual(42L, parsed.Hwnd, "helper target is an explicit HWND");
            AssertTrue(!json.Contains("command") && !json.Contains("script") && !json.Contains("url"),
                "helper request has no generic execution field");

            RuntimeThrows<InvalidDataException>(() => ExcelIdentityHelperProtocol.ParseRequest(
                json.TrimEnd('}') + ",\"command\":\"cmd.exe\"}"));
            RuntimeThrows<InvalidDataException>(() => ExcelIdentityHelperProtocol.ReadBoundedLine(
                new StringReader(new string('x', ExcelIdentityHelperProtocol.MaximumMessageChars + 1) + "\n")));
            var response = new ExcelIdentityHelperResponse
            {
                SchemaVersion = ExcelIdentityHelperProtocol.SchemaVersion,
                Nonce = request.Nonce,
                Type = "released",
                Status = "released",
                OwnerAssemblyMvid = request.OwnerAssemblyMvid
            };
            var responseJson = ExcelIdentityHelperProtocol.SerializeResponse(response);
            RuntimeThrows<InvalidDataException>(() => ExcelIdentityHelperProtocol.ParseResponse(
                responseJson, new string('b', 64)));
        }

        private static void ExcelWq0VerifierRequiresFullMatrix()
        {
            const string started = "2026-08-31T10:00:00.0000000Z";
            var baseline = ExcelWq0ObservationSet("C:\\initial\\Identity-WQ0.xlsx", 1,
                new[] { "client-A", "client-B" }, 1, 10, started, "0000000000000001", "0000000000000002");
            var switched = ExcelWq0ObservationSet("C:\\initial\\Identity-WQ0.xlsx", 1,
                new[] { "client-A", "client-B" }, 1, 10, started, "0000000000000001", "0000000000000002");
            var savedAs = ExcelWq0ObservationSet("C:\\save-as\\Identity-WQ0.xlsx", 1,
                new[] { "client-A", "client-B" }, 1, 10, started, "0000000000000001", "0000000000000002");
            var secondWindow = ExcelWq0ObservationSet("C:\\save-as\\Identity-WQ0.xlsx", 2,
                new[] { "client-A", "client-B" }, 1, 10, started, "0000000000000001", "0000000000000002");
            var rotated = ExcelWq0ObservationSet("C:\\save-as\\Identity-WQ0.xlsx", 2,
                new[] { "client-B", "client-C" }, 1, 10, started, "0000000000000001", "0000000000000002");
            var reopened = ExcelWq0ObservationSet("C:\\save-as\\Identity-WQ0.xlsx", 1,
                new[] { "client-D", "client-E" }, 1, 10, started, "0000000000000001", "0000000000000099");
            var rebound = new JObject
            {
                ["oldClients"] = new JArray(new JObject { ["status"] = "closed" },
                    new JObject { ["status"] = "closed" }),
                ["newObservation"] = reopened
            };
            var foreign = ExcelWq0Observation("other-process", "C:\\other\\Identity-WQ0.xlsx", 1,
                20, "2026-08-31T10:01:00.0000000Z", "0000000000000100", "0000000000000200");
            var evidence = ExcelWq0Evidence(baseline, switched, savedAs, secondWindow, rotated,
                rebound, new JObject { ["foreign"] = foreign });
            var passed = ExcelWq0EvidenceVerifier.Verify(evidence, CancellationToken.None);
            AssertEqual(QualificationStepOutcome.Passed, passed.Outcome,
                "complete independent-client matrix passes typed verifier");

            foreign["excelProcessId"] = 10;
            foreign["excelProcessStartUtc"] = started;
            foreign["oxid"] = "0000000000000001";
            foreign["oid"] = "0000000000000002";
            var failed = ExcelWq0EvidenceVerifier.Verify(ExcelWq0Evidence(baseline, switched, savedAs,
                secondWindow, rotated, rebound, new JObject { ["foreign"] = foreign }), CancellationToken.None);
            AssertEqual(QualificationStepOutcome.Failed, failed.Outcome,
                "same scoped identity in foreign scenario cannot pass");
            AssertEqual(QualificationStepOutcome.Blocked,
                ExcelWq0EvidenceVerifier.Verify(new QualificationEvidenceSnapshot(null), CancellationToken.None).Outcome,
                "missing persisted matrix blocks rather than passes");
        }

        private static QualificationEvidenceSnapshot ExcelWq0Evidence(JObject baseline, JObject switched,
            JObject savedAs, JObject secondWindow, JObject rotated, JObject rebound, JObject other)
        {
            var values = new[]
            {
                new { Id = "baseline", Value = baseline },
                new { Id = "after-switch", Value = switched },
                new { Id = "after-save-as", Value = savedAs },
                new { Id = "second-window", Value = secondWindow },
                new { Id = "rotate-client", Value = rotated },
                new { Id = "rebind-reopened", Value = rebound },
                new { Id = "other-process", Value = other }
            };
            return new QualificationEvidenceSnapshot(values.Select(item => new QualificationRecordedStep(
                item.Id, QualificationStepOutcome.Passed, QualificationEvidenceStrength.None,
                item.Value.ToString(Formatting.None))));
        }

        private static JObject ExcelWq0ObservationSet(string path, int windowCount, string[] labels,
            int saved, int processId, string processStart, string oxid, string oid)
        {
            return new JObject
            {
                ["inProcess"] = ExcelWq0Observation("host-owner", path, windowCount,
                    processId, processStart, oxid, oid, saved != 0),
                ["clients"] = new JArray(labels.Select(label => ExcelWq0Observation(label, path,
                    windowCount, processId, processStart, oxid, oid, saved != 0)))
            };
        }

        private static JObject ExcelWq0Observation(string label, string path, int windowCount,
            int processId, string processStart, string oxid, string oid, bool saved = true)
        {
            return new JObject
            {
                ["label"] = label,
                ["status"] = "observed",
                ["excelProcessId"] = processId,
                ["excelProcessStartUtc"] = processStart,
                ["oxid"] = oxid,
                ["oid"] = oid,
                ["name"] = "Identity-WQ0.xlsx",
                ["fullName"] = path,
                ["savedBeforeRead"] = saved,
                ["savedAfterRead"] = saved,
                ["windowCount"] = windowCount
            };
        }

        private static byte[] ExcelIdentityProbePacket()
        {
            using (var buffer = new MemoryStream())
            using (var writer = new BinaryWriter(buffer))
            {
                writer.Write(0x574f454du);
                writer.Write(1u);
                writer.Write(new Guid("00000000-0000-0000-C000-000000000046").ToByteArray());
                writer.Write(0u);
                writer.Write(1u);
                writer.Write(0xfedcba9876543210ul);
                writer.Write(0x0123456789abcdeful);
                writer.Write(new Guid("01234567-89ab-cdef-0123-456789abcdef").ToByteArray());
                writer.Write((ushort)4);
                writer.Write((ushort)2);
                writer.Write(new byte[8]);
                return buffer.ToArray();
            }
        }

        // Contract-only fixtures: caller-supplied identities do not simulate Excel COM identity resolution.
        private sealed class BoundTestDocument
        {
            public string StableId { get; set; }
            public bool IsAlive { get; set; }
        }

        private sealed class BoundTestOfficeSession : IOfficeDocumentSession
        {
            private readonly BoundTestDocument _document;

            public BoundTestOfficeSession(IOfficeStaDispatcher dispatcher, BoundTestDocument document, string runtimeId, object gate)
            {
                StaDispatcher = dispatcher;
                _document = document;
                RuntimeDocumentId = runtimeId;
                MutationGate = gate;
            }

            public string Host { get { return "Excel"; } }
            public string StableDocumentId
            {
                get
                {
                    AssertTrue(StaDispatcher.CheckAccess, "stable document identity is inspected on its owner dispatcher");
                    return _document.StableId;
                }
            }
            public string RuntimeDocumentId { get; private set; }
            public IOfficeStaDispatcher StaDispatcher { get; private set; }
            public object MutationGate { get; private set; }

            public object BoundDocumentObject
            {
                get
                {
                    AssertTrue(StaDispatcher.CheckAccess, "bound object is inspected on its owner dispatcher");
                    return _document;
                }
            }

            public bool IsAlive
            {
                get
                {
                    AssertTrue(StaDispatcher.CheckAccess, "liveness is inspected on its owner dispatcher");
                    return _document.IsAlive;
                }
            }
        }

        private sealed class BoundTestOfficeAdapter : IOfficeApplicationAdapter, IOfficeDocumentSessionProvider, IOfficeDispatcherProvider, IOfficeContextProvider, IExcelBackendProvider, IExcelReadBackend, IExcelWriteBackend, IExcelFindReplaceBackend
        {
            private readonly FakeOfficeAdapter _inner;

            public BoundTestOfficeAdapter(BoundTestOfficeSession session, FakeOfficeAdapter inner = null)
            {
                Session = session;
                _inner = inner ?? new FakeOfficeAdapter();
            }
            public Action<string> BeforeRead { get; set; }
            public BoundTestOfficeSession Session { get; private set; }
            public IOfficeDocumentSession DocumentSession { get { return Session; } }
            public IOfficeStaDispatcher StaDispatcher { get { return Session.StaDispatcher; } }
            public IExcelReadBackend ExcelReadBackend { get { return this; } }
            public IExcelWriteBackend ExcelWriteBackend { get { return this; } }
            public IExcelFindReplaceBackend ExcelFindReplaceBackend { get { return this; } }
            public string HostName { get { return Session.Host; } }
            public string DocumentKey { get { return StaDispatcher.Invoke(() => Session.StableDocumentId); } }
            public string RuntimeDocumentKey { get { return Session.RuntimeDocumentId; } }
            public string DocumentTitle { get { return "Bound test document"; } }
            public string GetDocumentSnapshot(int maxChars) { return _inner.GetDocumentSnapshot(maxChars); }
            public void PrepareForContextCapture() { BeforeRead?.Invoke("prepare"); _inner.PrepareForContextCapture(); }
            public ContextNote CaptureSelectionContext(string mode, int maxChars) { BeforeRead?.Invoke("selection"); return _inner.CaptureSelectionContext(mode, maxChars); }
            public OfficeContext GetOfficeContext() { BeforeRead?.Invoke("context"); return _inner.GetOfficeContext(); }
            public IEnumerable<ToolDefinition> GetBuiltInTools() { return _inner.GetBuiltInTools(); }
            public ToolResult ExecuteTool(ToolCommand command) { BeforeRead?.Invoke(command.ToolId); return _inner.ExecuteTool(command); }
            public ExcelInspectSnapshot Inspect(ExcelInspectRequest request) { BeforeRead?.Invoke(FakeOfficeAdapter.ExcelInspectOperation); return _inner.Inspect(request); }
            public ExcelRangeSnapshot ReadRange(ExcelRangeReadRequest request) { BeforeRead?.Invoke(FakeOfficeAdapter.ExcelRangeReadOperation); return _inner.ReadRange(request); }
            public ExcelWriteSnapshot Read(ExcelWriteReadRequest request) { BeforeRead?.Invoke(FakeOfficeAdapter.ExcelWriteReadOperation); return _inner.Read(request); }
            public void Apply(ExcelWriteApplyRequest request, Action markDispatchPossible) { BeforeRead?.Invoke(FakeOfficeAdapter.ExcelWriteApplyOperation); _inner.Apply(request, markDispatchPossible); }
            public void ReadScope(ExcelCellScopeRequest request, Action<ExcelCellSnapshot> visit) { BeforeRead?.Invoke(FakeOfficeAdapter.ExcelFindScopeReadOperation); _inner.ReadScope(request, visit); }
            public void Apply(ExcelReplaceApplyRequest request, Action markDispatchPossible) { BeforeRead?.Invoke(FakeOfficeAdapter.ExcelReplaceApplyOperation); _inner.Apply(request, markDispatchPossible); }
        }

        private sealed class BoundTestQueuedDispatcher : IOfficeStaDispatcher
        {
            private readonly IOfficeStaDispatcher _owner;
            private readonly ManualResetEventSlim _queued;
            private readonly ManualResetEventSlim _admit;
            private int _pauseNext;

            public BoundTestQueuedDispatcher(IOfficeStaDispatcher owner, ManualResetEventSlim queued, ManualResetEventSlim admit)
            {
                _owner = owner;
                _queued = queued;
                _admit = admit;
            }

            public bool CheckAccess { get { return _owner.CheckAccess; } }
            public void PauseNextDispatch() { Interlocked.Exchange(ref _pauseNext, 1); }

            public T Invoke<T>(Func<T> action)
            {
                var pause = !CheckAccess && Interlocked.Exchange(ref _pauseNext, 0) != 0;
                return _owner.Invoke(() =>
                {
                    if (pause)
                    {
                        _queued.Set();
                        AssertTrue(_admit.Wait(10000), "owner admits the queued bound callback");
                    }
                    return action();
                });
            }
        }

        public sealed class FakeDocumentProperties
        {
            private readonly Dictionary<string, FakeDocumentProperty> _values =
                new Dictionary<string, FakeDocumentProperty>(StringComparer.OrdinalIgnoreCase);

            public int Count { get { return _values.Count; } }

            public FakeDocumentProperty this[string name]
            {
                get
                {
                    FakeDocumentProperty property;
                    if (!_values.TryGetValue(name, out property))
                    {
                        throw new KeyNotFoundException();
                    }
                    return property;
                }
            }

            public void Add(string name, bool linkToContent, int propertyType, string value)
            {
                _values[name] = new FakeDocumentProperty { Value = value };
            }
        }

        public sealed class FakeDocumentProperty
        {
            public string Value { get; set; }
        }
    }
}

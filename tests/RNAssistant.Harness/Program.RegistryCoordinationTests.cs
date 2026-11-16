using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void StorageCoordinationAllowsNativeMaintenance()
        {
            WithTempPaths(paths => {
                var office = ConversationLeaseService.ForOffice(paths);
                var equivalentPaths = AppDataPaths.CreateForRoot(Path.Combine(paths.Root, "."));
                var scope = "document:" + DocumentAuthorityId.Create().Id;
                var scoped = ConversationLeaseService.ForScope(equivalentPaths, scope);
                IConversationStore store = new ConversationJournal(paths, scope);
                var resources = new ResourceJournal(paths);
                var files = new WorkspaceFileService(paths, resources, resources, resources);
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "maintenance-project"));
                using (office.AcquireMaintenance())
                using (scoped.AcquireMaintenance())
                {
                    using (RuntimeStorageActivity.Acquire(equivalentPaths))
                        AssertTrue(office.AnyHeld(), "nested storage publication still has a visible activity lease");
                    AssertTrue(!office.AnyHeld(), "completed nested activity is released while maintenance remains held");
                    var chat = store.CreateTransient("Excel", "fixture", "Document", "Native maintenance");
                    var input = new ChatMessage { Role = "user", Content = "Retain this message." };
                    chat.Messages.Add(input); store.Commit(chat, ConversationCommit.UserInput(input));
                    AssertEqual(input.Content, store.Load(chat.Id).Messages[0].Content,
                        "native chat commit/read can enter the maintenance owner's storage protocol");
                    files.CreateText(workspace, "source.txt", "native resource");
                    AssertEqual("native resource", File.ReadAllText(Path.Combine(workspace.RootPath, "source.txt")),
                        "resource journal and CAS publication can share the same maintenance owner");
                    AssertTrue(store.Delete(chat.Host, chat.DocumentKey, chat.Id), "native deletion keeps the exclusive maintenance scope");
                    AssertTrue(!office.AnyHeld(), "nested durable operations release every activity marker");
                }
                using (new FileStream(Path.Combine(paths.Root, "locks", "run-registry.lck"),
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                using (scoped.Reserve("exact-chat"))
                    RuntimeThrows<IOException>(() => ConversationLeaseService.ForScope(paths, scope).Reserve("exact-chat"));
            });
        }

        private static void StorageCoordinationDoesNotFlowToWorkers()
        {
            WithTempPaths(paths => {
                var office = ConversationLeaseService.ForOffice(paths);
                using (var attempted = new ManualResetEventSlim())
                using (var entered = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                {
                    Task worker = null;
                    try
                    {
                        using (office.AcquireMaintenance())
                        {
                            worker = Task.Run(() => {
                                attempted.Set();
                                using (RuntimeStorageActivity.Acquire(paths))
                                {
                                    entered.Set();
                                    if (!release.Wait(5000)) throw new TimeoutException("Test worker was not released.");
                                }
                            });
                            AssertTrue(attempted.Wait(5000), "worker attempts independent storage admission");
                            AssertTrue(!entered.Wait(100), "Task.Run never inherits the caller's coordination reentry");
                        }
                        AssertTrue(entered.Wait(5000), "worker enters after maintenance releases coordination");
                        AssertTrue(office.AnyHeld(), "worker activity still blocks destructive maintenance");
                    }
                    finally
                    {
                        release.Set();
                        if (worker != null) worker.GetAwaiter().GetResult();
                    }
                    AssertTrue(!office.AnyHeld(), "worker activity releases without leaving a false active run");
                }
            });
        }

        private static void StorageCoordinationWaiterDoesNotBlockOwner()
        {
            WithTempPaths(paths => {
                var scope = "document:" + DocumentAuthorityId.Create().Id;
                var owner = ConversationLeaseService.ForScope(paths, scope);
                var waiterOwner = ConversationLeaseService.ForScope(paths, scope);
                IConversationStore store = new ConversationJournal(paths, scope);
                var chat = store.CreateTransient("Excel", "fixture", "Document", "Lease owner");
                store.Commit(chat, new ConversationCommit());
                using (var attempted = new ManualResetEventSlim())
                using (var acquired = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                using (var cancel = new CancellationTokenSource())
                {
                    IDisposable exact = owner.Reserve(chat.Id);
                    Task waiter = null;
                    try
                    {
                        waiter = Task.Run(() => {
                            attempted.Set();
                            using (waiterOwner.Reserve(chat.Id, wait: true, cancellationToken: cancel.Token))
                            {
                                acquired.Set();
                                if (!release.Wait(5000)) throw new TimeoutException("Test waiter was not released.");
                            }
                        });
                        AssertTrue(attempted.Wait(5000) && !waiter.Wait(100), "second owner waits for the occupied exact lease");
                        using (var publication = new CancellationTokenSource(3000))
                        using (RuntimeStorageActivity.Acquire(paths, publication.Token))
                        {
                            var input = new ChatMessage { Role = "user", Content = "Owner can finish." };
                            chat.Messages.Add(input); store.Commit(chat, ConversationCommit.UserInput(input));
                        }
                        AssertTrue(!acquired.IsSet, "publication does not release or bypass exact ownership");
                        exact.Dispose(); exact = null;
                        AssertTrue(acquired.Wait(5000), "waiter acquires the exact lease after its owner releases it");
                    }
                    finally
                    {
                        exact?.Dispose(); release.Set(); cancel.Cancel();
                        if (waiter != null)
                            try { waiter.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                    }
                    AssertTrue(!owner.AnyHeld(), "the waiter leaves no held run lease");
                }
                using (owner.Reserve(chat.Id))
                using (var cancel = new CancellationTokenSource())
                {
                    cancel.Cancel();
                    RuntimeThrows<OperationCanceledException>(() => waiterOwner.Reserve(chat.Id, true, cancel.Token));
                }
            });
        }
    }
}

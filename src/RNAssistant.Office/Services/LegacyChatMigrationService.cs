using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // Host-owned background transfer of retained legacy chats. The job holds one
    // storage activity marker, so catalog reads remain available while destructive
    // maintenance and runtime clear wait for the transfer to finish.
    internal sealed class LegacyChatMigrationService : IDisposable
    {
        private const int PageSize = 25;
        private const int MaxReportedIssues = 50;
        private readonly object _sync = new object();
        private readonly AppDataPaths _paths;
        private readonly ChatStore _source;
        private readonly ConversationJournalRouter _destination;
        private readonly DocumentAuthorityRegistry _identities;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private MigrationState _state = new MigrationState { Status = "idle" };
        private Task _worker = Task.CompletedTask;
        private bool _cancelRequested;
        private bool _disposed;

        internal LegacyChatMigrationService(AppDataPaths paths, ChatStore source,
            ConversationJournalRouter destination, DocumentAuthorityRegistry identities)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _destination = destination ?? throw new ArgumentNullException(nameof(destination));
            _identities = identities ?? throw new ArgumentNullException(nameof(identities));
        }

        internal LegacyChatMigrationStatusDto Start()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_state.Running) return SnapshotLocked();
                _cancelRequested = false;
                _state = new MigrationState
                {
                    Status = "running",
                    Running = true,
                    Message = "Подготавливаю фоновый перенос прежних чатов…"
                };
                var token = _lifetime.Token;
                _worker = Task.Run(() => Run(token));
                return SnapshotLocked();
            }
        }

        internal LegacyChatMigrationStatusDto Status()
        {
            lock (_sync) return SnapshotLocked();
        }

        internal LegacyChatMigrationStatusDto Cancel()
        {
            lock (_sync)
            {
                if (_state.Running && !_cancelRequested)
                {
                    _cancelRequested = true;
                    _state.Cancelling = true;
                    _state.Message = "Останавливаю после текущего чата…";
                }
                return SnapshotLocked();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _cancelRequested = true;
            }
            try { _lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
            // The worker owns its activity lease and releases it on cancellation.
            // Do not wait on an Office UI thread during panel shutdown.
        }

        private void Run(CancellationToken token)
        {
            IDisposable activity = null;
            var stop = false;
            try
            {
                Update(state => state.Message = "Ожидаю доступа к хранилищу…");
                activity = RuntimeStorageActivity.Acquire(_paths, token);
                var offset = 0;
                while (!stop && !token.IsCancellationRequested && !CancellationRequested())
                {
                    LegacyChatRecoveryPage page;
                    Update(state => state.Message = "Читаю каталог прежних чатов…");
                    try { page = _source.ReadLegacyRecoveryChats(offset, PageSize, token); }
                    catch (OperationCanceledException) { SetTerminal("cancelled", "Перенос остановлен."); return; }
                    catch (Exception error)
                    {
                        SetTerminal("failed", "Не удалось прочитать прежние чаты: " + error.Message);
                        return;
                    }

                    Update(state => {
                        if (page.Total > state.Total) state.Total = page.Total;
                        state.CurrentTitle = null;
                        state.Message = page.Total == 0 ? "Прежних чатов не найдено." : "Переношу прежние чаты…";
                    });
                    if (page.Total == 0) { SetTerminal("completed", "Прежних чатов не найдено."); return; }

                    foreach (var item in page.Items ?? new LegacyChatRecoveryItem[0])
                    {
                        if (token.IsCancellationRequested || CancellationRequested()) { stop = true; break; }
                        Update(state => {
                            state.Processed++;
                            state.CurrentTitle = FirstNonEmpty(item.Title, item.ChatId, item.SourceId);
                            state.Message = "Переношу прежние чаты…";
                        });

                        if (!string.Equals(item.Status, "readable", StringComparison.Ordinal) || !item.CanImport)
                        {
                            Update(state => state.Skipped++);
                            AddIssue(new LegacyChatMigrationIssueDto
                            {
                                SourceId = item.SourceId, SourceRevision = item.Revision,
                                Title = FirstNonEmpty(item.Title, item.ChatId), Status = "skipped",
                                Message = FirstNonEmpty(item.Error, item.Status, "Источник недоступен для переноса.")
                            });
                            continue;
                        }

                        try
                        {
                            _source.ImportLegacyRecoveryChat(item.SourceId, item.Revision, _destination, _identities, token);
                            Update(state => state.Imported++);
                        }
                        catch (OperationCanceledException)
                        {
                            SetTerminal("cancelled", "Перенос остановлен.");
                            return;
                        }
                        catch (LegacyChatImportException error)
                        {
                            Update(state => state.Failed++);
                            AddIssue(new LegacyChatMigrationIssueDto
                            {
                                SourceId = item.SourceId, SourceRevision = item.Revision,
                                Title = FirstNonEmpty(item.Title, item.ChatId), Status = "failed",
                                Message = error.Message
                            });
                        }
                        catch (Exception error)
                        {
                            // A non-typed failure can follow publication. Keep the exact
                            // source/revision issue visible and stop instead of replaying.
                            Update(state => state.Unknown++);
                            AddIssue(new LegacyChatMigrationIssueDto
                            {
                                SourceId = item.SourceId, SourceRevision = item.Revision,
                                Title = FirstNonEmpty(item.Title, item.ChatId), Status = "unknown",
                                Message = error.Message
                            });
                            stop = true;
                            break;
                        }
                    }

                    if (stop || token.IsCancellationRequested || CancellationRequested()) break;
                    if (!page.NextOffset.HasValue) break;
                    if (page.NextOffset.Value <= offset)
                    {
                        SetTerminal("failed", "Каталог прежних чатов вернул повторяющуюся страницу.");
                        return;
                    }
                    offset = page.NextOffset.Value;
                }
            }
            catch (OperationCanceledException)
            {
                SetTerminal("cancelled", "Перенос остановлен.");
                return;
            }
            catch (IOException error)
            {
                SetTerminal("failed", "Хранилище занято другой операцией. Дождитесь завершения проверки, копии или индексации и повторите перенос. " + error.Message);
                return;
            }
            catch (UnauthorizedAccessException error)
            {
                SetTerminal("failed", "Нет доступа к хранилищу прежних чатов: " + error.Message);
                return;
            }
            catch (Exception error)
            {
                SetTerminal("failed", "Фоновый перенос остановлен: " + error.Message);
                return;
            }
            finally
            {
                if (activity != null) activity.Dispose();
            }

            string terminalStatus;
            string message;
            lock (_sync)
            {
                if (_state.Unknown != 0) terminalStatus = "unknown";
                else if (_cancelRequested) terminalStatus = "cancelled";
                else terminalStatus = "completed";
                message = Summary(_state, terminalStatus);
            }
            SetTerminal(terminalStatus, message);
        }

        private bool CancellationRequested()
        {
            lock (_sync) return _cancelRequested || _disposed;
        }

        private void Update(Action<MigrationState> change)
        {
            lock (_sync)
            {
                if (_state.Running) change(_state);
            }
        }

        private void AddIssue(LegacyChatMigrationIssueDto issue)
        {
            lock (_sync)
            {
                if (_state.Issues.Count >= MaxReportedIssues) return;
                _state.Issues.Add(issue ?? throw new ArgumentNullException(nameof(issue)));
            }
        }

        private void SetTerminal(string status, string message)
        {
            lock (_sync)
            {
                _state.Status = status;
                _state.Running = false;
                _state.Cancelling = false;
                _state.CurrentTitle = null;
                _state.Message = message ?? string.Empty;
            }
        }

        private LegacyChatMigrationStatusDto SnapshotLocked()
        {
            return new LegacyChatMigrationStatusDto
            {
                Status = _state.Status,
                Running = _state.Running,
                Cancelling = _state.Cancelling,
                Total = _state.Total,
                Processed = _state.Processed,
                Imported = _state.Imported,
                Failed = _state.Failed,
                Unknown = _state.Unknown,
                Skipped = _state.Skipped,
                CurrentTitle = _state.CurrentTitle,
                Message = _state.Message,
                Issues = _state.Issues.ToArray()
            };
        }

        private static string Summary(MigrationState state, string status)
        {
            if (status == "cancelled")
                return "Перенос остановлен после " + state.Processed + " из " + state.Total + ".";
            if (status == "unknown")
                return "Результат одного переноса неизвестен; перенос остановлен. Используйте «Проверить перенос» для точной исходной записи.";
            return "Перенос завершён: подтверждено " + state.Imported + ", отказов " +
                state.Failed + ", пропущено " + state.Skipped + ".";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values) if (!string.IsNullOrWhiteSpace(value)) return value;
            return string.Empty;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LegacyChatMigrationService));
        }

        private sealed class MigrationState
        {
            internal string Status;
            internal bool Running;
            internal bool Cancelling;
            internal int Total;
            internal int Processed;
            internal int Imported;
            internal int Failed;
            internal int Unknown;
            internal int Skipped;
            internal string CurrentTitle;
            internal string Message;
            internal readonly List<LegacyChatMigrationIssueDto> Issues = new List<LegacyChatMigrationIssueDto>();
        }
    }
}

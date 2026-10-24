using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Tools;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Runtime
{
    internal sealed class WorkspaceRunPreviewRead
    {
        public bool ReachedOwner { get; private set; }
        public WorkspaceModelProgress Progress { get; private set; }

        public WorkspaceRunPreviewRead(bool reachedOwner, WorkspaceModelProgress progress)
        { ReachedOwner = reachedOwner; Progress = progress; }
    }

    // Transient local signal to the process that owns the session lease. Only
    // its kernel writes the terminal outcome; no cancellation mailbox/store.
    internal sealed class WorkspaceRunControl : IDisposable
    {
        private const int PreviewMaximumBytes = 512 * 1024;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly CancellationTokenSource _run;
        private readonly Task _listener;
        public CancellationToken Token { get { return _run.Token; } }

        public WorkspaceRunControl(string scope, string sessionId, string runId, Func<string, bool> matches,
            Func<WorkspaceModelProgress> preview, CancellationToken cancellationToken)
        {
            _run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var name = Name(scope, sessionId, runId);
            var pipe = Server(name); // Bind before returning the application owner.
            _listener = ListenAsync(name, pipe, matches, preview);
        }

        private static string Name(string scope, string sessionId, string runId)
        { return "rna-" + TextPatternEngine.Sha256(Path.GetFullPath(scope) + "\n" + sessionId + "\n" + runId).Substring(0, 24); }

        private static NamedPipeServerStream Server(string name)
        {
#if NET8_0_OR_GREATER
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#else
            // .NET Framework has no CurrentUserOnly flag. Preserve the same
            // boundary using the Windows pipe ACL; never create a public pipe.
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false); security.SetOwner(user);
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.ReadPermissions,
                System.Security.AccessControl.AccessControlType.Allow));
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, security);
#endif
        }
        private static PipeOptions ClientOptions
        {
            get {
#if NET8_0_OR_GREATER
                return PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
#else
                return PipeOptions.Asynchronous;
#endif
            }
        }

        private static void RequireCurrentUser(NamedPipeClientStream pipe)
        {
#if !NET8_0_OR_GREATER
            if (!pipe.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier)).Equals(
                System.Security.Principal.WindowsIdentity.GetCurrent().User))
                throw new IOException("Workspace pipe belongs to another user.");
#endif
        }

        private static async Task<bool> ReadExactlyAsync(Stream pipe, byte[] bytes, CancellationToken token)
        {
            for (var offset = 0; offset < bytes.Length;)
            {
                var read = await pipe.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }

        public static async Task<WorkspaceRunPreviewRead> ReadPreviewAsync(string scope, string sessionId, string runId)
        {
            using (var deadline = new CancellationTokenSource(250))
            using (var pipe = new NamedPipeClientStream(".", Name(scope, sessionId, runId),
                PipeDirection.InOut, ClientOptions))
            {
                try
                {
                    await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                    RequireCurrentUser(pipe);
                    var request = Encoding.ASCII.GetBytes("preview\n");
                    await pipe.WriteAsync(request, 0, request.Length, deadline.Token).ConfigureAwait(false);
                    var length = new byte[4];
                    if (!await ReadExactlyAsync(pipe, length, deadline.Token).ConfigureAwait(false))
                        return new WorkspaceRunPreviewRead(false, null);
                    var count = BitConverter.ToInt32(length, 0);
                    if (count == 0) return new WorkspaceRunPreviewRead(true, null);
                    if (count < 0 || count > PreviewMaximumBytes) return new WorkspaceRunPreviewRead(false, null);
                    var bytes = new byte[count];
                    if (!await ReadExactlyAsync(pipe, bytes, deadline.Token).ConfigureAwait(false))
                        return new WorkspaceRunPreviewRead(false, null);
                    var result = JsonConvert.DeserializeObject<WorkspaceModelProgress>(Encoding.UTF8.GetString(bytes));
                    return result?.RunId == runId && result.Sequence > 0 ?
                        new WorkspaceRunPreviewRead(true, result) : new WorkspaceRunPreviewRead(false, null);
                }
                catch (OperationCanceledException) { return new WorkspaceRunPreviewRead(false, null); }
                catch (IOException) { return new WorkspaceRunPreviewRead(false, null); }
                catch (JsonException) { return new WorkspaceRunPreviewRead(false, null); }
                catch (UnauthorizedAccessException) { return new WorkspaceRunPreviewRead(false, null); }
            }
        }

        private async Task ListenAsync(string name, NamedPipeServerStream first, Func<string, bool> matches,
            Func<WorkspaceModelProgress> preview)
        {
            var pipe = first;
            while (true)
            {
                using (pipe)
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                        {
                            deadline.CancelAfter(2000);
                            var bytes = new byte[513];
                            var length = 0;
                            while (length < bytes.Length)
                            {
                                var read = await pipe.ReadAsync(bytes, length, 1, deadline.Token).ConfigureAwait(false);
                                if (read == 0 || bytes[length] == (byte)'\n') break;
                                length++;
                            }
                            var request = length > 0 && length <= 512 && bytes[length] == (byte)'\n' ?
                                Encoding.ASCII.GetString(bytes, 0, length) : null;
                            if (request == "preview")
                            {
                                var value = preview?.Invoke();
                                var content = value == null ? new byte[0] :
                                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
                                if (content.Length > PreviewMaximumBytes) content = new byte[0];
                                var size = BitConverter.GetBytes(content.Length);
                                await pipe.WriteAsync(size, 0, size.Length, deadline.Token).ConfigureAwait(false);
                                if (content.Length > 0)
                                    await pipe.WriteAsync(content, 0, content.Length, deadline.Token).ConfigureAwait(false);
                            }
                            else
                            {
                                var accepted = request != null && matches(request);
                                try
                                { await pipe.WriteAsync(new[] { accepted ? (byte)1 : (byte)2 }, 0, 1, deadline.Token).ConfigureAwait(false); }
                                finally { if (accepted) _run.Cancel(); }
                            }
                        }
                    }
                    catch (OperationCanceledException) { if (_lifetime.IsCancellationRequested) return; }
                    catch (IOException) { if (_lifetime.IsCancellationRequested) return; }
                    catch (JsonException) { if (_lifetime.IsCancellationRequested) return; }
                }
                if (_lifetime.IsCancellationRequested) return;
                pipe = Server(name);
            }
        }

        public static async Task<WorkspaceCancellationStatus> SendAsync(string scope, WorkspaceRunControlRequest request,
            CancellationToken cancellationToken)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var pipe = new NamedPipeClientStream(".", Name(scope, request.SessionId, request.RunId), PipeDirection.InOut,
                ClientOptions))
            {
                deadline.CancelAfter(2000);
                try
                {
                    await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                    RequireCurrentUser(pipe);

                    var bytes = Encoding.ASCII.GetBytes(request.Cursor + "\n");
                    await pipe.WriteAsync(bytes, 0, bytes.Length, deadline.Token).ConfigureAwait(false);
                    var reply = new byte[1];
                    if (await pipe.ReadAsync(reply, 0, 1, deadline.Token).ConfigureAwait(false) != 1)
                        return WorkspaceCancellationStatus.Unconfirmed;
                    return reply[0] == 1 ? WorkspaceCancellationStatus.Requested :
                        reply[0] == 2 ? WorkspaceCancellationStatus.StaleState : WorkspaceCancellationStatus.Unconfirmed;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { return WorkspaceCancellationStatus.Unconfirmed; }
                catch (IOException) { return WorkspaceCancellationStatus.Unconfirmed; }
            }
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            try { _listener.GetAwaiter().GetResult(); }
            finally { _lifetime.Dispose(); _run.Dispose(); }
        }
    }
}

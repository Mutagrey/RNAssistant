using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;

namespace RNAssistant.OfficeHosts.Qualification
{
    public sealed class ExcelIdentityHelperClient : IDisposable
    {
        public const string ExecutableName = "RNAssistant.ExcelIdentityHelper.exe";
        private const int ConnectTimeoutMilliseconds = 10000;
        private readonly string _nonce;
        private readonly NamedPipeServerStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly Process _process;
        private readonly string _ownerMvid;
        private bool _released;

        private ExcelIdentityHelperClient(string nonce, NamedPipeServerStream pipe, StreamReader reader,
            StreamWriter writer, Process process, string ownerMvid)
        {
            _nonce = nonce;
            _pipe = pipe;
            _reader = reader;
            _writer = writer;
            _process = process;
            _ownerMvid = ownerMvid;
        }

        public ExcelIdentityHelperResponse Initial { get; private set; }

        public static bool IsSupported(out string reason)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Environment.Is64BitProcess)
            {
                reason = "Excel identity helper requires Windows x64.";
                return false;
            }
            var path = ResolveExecutablePath();
            if (!File.Exists(path))
            {
                reason = "Same-build Excel identity helper is missing.";
                return false;
            }
            reason = null;
            return true;
        }

        public static ExcelIdentityHelperClient Start(long hwnd, int workbookIndex, string label)
        {
            string reason;
            if (!IsSupported(out reason)) throw new PlatformNotSupportedException(reason);
            var channel = "RNAssistant.WQ0." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N");
            var nonce = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(channel, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096);
            Process process = null;
            StreamReader reader = null;
            StreamWriter writer = null;
            try
            {
                process = StartProcess(channel, nonce);
                WaitForConnection(pipe);
                reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
                writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };
                var ownerMvid = OwnerMvid();
                var client = new ExcelIdentityHelperClient(nonce, pipe, reader, writer, process, ownerMvid);
                client.Send(new ExcelIdentityHelperRequest
                {
                    SchemaVersion = ExcelIdentityHelperProtocol.SchemaVersion,
                    Nonce = nonce,
                    Operation = "bind",
                    Hwnd = hwnd,
                    WorkbookIndex = workbookIndex,
                    Label = label,
                    Scenario = "initial",
                    OwnerAssemblyMvid = ownerMvid
                });
                client.Initial = client.Receive("observation");
                client.ValidateOwner(client.Initial);
                return client;
            }
            catch
            {
                if (writer != null) writer.Dispose();
                if (reader != null) reader.Dispose();
                pipe.Dispose();
                Terminate(process);
                throw;
            }
        }

        public static IReadOnlyList<ExcelIdentityWorkbookTarget> ListWorkbooks(long hwnd)
        {
            string reason;
            if (!IsSupported(out reason)) throw new PlatformNotSupportedException(reason);
            var channel = "RNAssistant.WQ0." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N");
            var nonce = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            using (var pipe = new NamedPipeServerStream(channel, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096))
            {
                var process = StartProcess(channel, nonce);
                try
                {
                    WaitForConnection(pipe);
                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, true)
                    {
                        AutoFlush = true,
                        NewLine = "\n"
                    })
                    {
                        var request = new ExcelIdentityHelperRequest
                        {
                            SchemaVersion = ExcelIdentityHelperProtocol.SchemaVersion,
                            Nonce = nonce,
                            Operation = "list",
                            Hwnd = hwnd,
                            OwnerAssemblyMvid = OwnerMvid()
                        };
                        writer.WriteLine(ExcelIdentityHelperProtocol.SerializeRequest(request));
                        var response = ExcelIdentityHelperProtocol.ParseResponse(
                            ExcelIdentityHelperProtocol.ReadBoundedLine(reader), nonce);
                        if (!string.Equals(response.Type, "workbooks", StringComparison.Ordinal) ||
                            !string.Equals(response.Status, "listed", StringComparison.Ordinal))
                            throw new InvalidDataException(response.Message ?? "Identity helper did not return a workbook list.");
                        ValidateOwner(response, OwnerMvid());
                        return Array.AsReadOnly((response.Workbooks ?? new ExcelIdentityWorkbookTarget[0]).ToArray());
                    }
                }
                finally
                {
                    WaitOrTerminate(process);
                }
            }
        }

        public ExcelIdentityHelperResponse Observe(string scenario)
        {
            ThrowIfReleased();
            Send(new ExcelIdentityHelperRequest
            {
                SchemaVersion = ExcelIdentityHelperProtocol.SchemaVersion,
                Nonce = _nonce,
                Operation = "observe",
                Scenario = scenario,
                OwnerAssemblyMvid = _ownerMvid
            });
            var response = Receive("observation");
            ValidateOwner(response);
            return response;
        }

        public ExcelIdentityHelperResponse Release()
        {
            if (_released) return null;
            _released = true;
            try
            {
                Send(new ExcelIdentityHelperRequest
                {
                    SchemaVersion = ExcelIdentityHelperProtocol.SchemaVersion,
                    Nonce = _nonce,
                    Operation = "release",
                    OwnerAssemblyMvid = _ownerMvid
                });
                var response = Receive("released");
                ValidateOwner(response);
                return response;
            }
            finally
            {
                _writer.Dispose();
                _reader.Dispose();
                _pipe.Dispose();
                WaitOrTerminate(_process);
            }
        }

        public void Dispose()
        {
            if (_released) return;
            try { Release(); }
            catch
            {
                _released = true;
                try { _writer.Dispose(); } catch { }
                try { _reader.Dispose(); } catch { }
                try { _pipe.Dispose(); } catch { }
                Terminate(_process);
            }
        }

        private void Send(ExcelIdentityHelperRequest request)
        {
            _writer.WriteLine(ExcelIdentityHelperProtocol.SerializeRequest(request));
        }

        private ExcelIdentityHelperResponse Receive(string expectedType)
        {
            var response = ExcelIdentityHelperProtocol.ParseResponse(
                ExcelIdentityHelperProtocol.ReadBoundedLine(_reader), _nonce);
            if (string.Equals(response.Type, "error", StringComparison.Ordinal))
                throw new InvalidOperationException((response.Code ?? "helper_error") + ": " +
                    (response.Message ?? "Excel identity helper failed."));
            if (!string.Equals(response.Type, expectedType, StringComparison.Ordinal))
                throw new InvalidDataException("Excel identity helper returned an unexpected response type.");
            return response;
        }

        private void ValidateOwner(ExcelIdentityHelperResponse response)
        {
            ValidateOwner(response, _ownerMvid);
        }

        private static void ValidateOwner(ExcelIdentityHelperResponse response, string expected)
        {
            if (!string.Equals(response.OwnerAssemblyMvid, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Excel identity helper does not use the same owner assembly build.");
        }

        private void ThrowIfReleased()
        {
            if (_released) throw new ObjectDisposedException("ExcelIdentityHelperClient");
        }

        private static Process StartProcess(string channel, string nonce)
        {
            var start = new ProcessStartInfo
            {
                FileName = ResolveExecutablePath(),
                Arguments = "--pipe " + channel + " --nonce " + nonce,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };
            return Process.Start(start) ?? throw new InvalidOperationException("Excel identity helper did not start.");
        }

        private static void WaitForConnection(NamedPipeServerStream pipe)
        {
            var pending = pipe.BeginWaitForConnection(null, null);
            if (!pending.AsyncWaitHandle.WaitOne(ConnectTimeoutMilliseconds))
                throw new TimeoutException("Excel identity helper did not connect in time.");
            pipe.EndWaitForConnection(pending);
        }

        private static string ResolveExecutablePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ExecutableName);
        }

        private static string OwnerMvid()
        {
            return typeof(ComIdentitySample).Assembly.ManifestModule.ModuleVersionId.ToString("D");
        }

        private static void WaitOrTerminate(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.WaitForExit(3000)) Terminate(process);
            }
            finally { process.Dispose(); }
        }

        private static void Terminate(Process process)
        {
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(); } catch { }
            try { process.Dispose(); } catch { }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using RNAssistant.OfficeHosts.Qualification;

namespace RNAssistant.ExcelIdentityHelper
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string channel;
            string nonce;
            if (!TryReadArguments(args, out channel, out nonce)) return 2;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Environment.Is64BitProcess ||
                Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) return 3;
            using (var pipe = new NamedPipeClientStream(".", channel, PipeDirection.InOut, PipeOptions.None))
            {
                pipe.Connect(10000);
                using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                })
                {
                    return Serve(reader, writer, nonce);
                }
            }
        }

        private static int Serve(TextReader reader, TextWriter writer, string nonce)
        {
            object application = null;
            object workbook = null;
            ComIdentityLease lease = null;
            bool? savedBeforeBind = null;
            string label = null;
            try
            {
                while (true)
                {
                    var line = ExcelIdentityHelperProtocol.ReadBoundedLine(reader);
                    if (line == null) return 4;
                    ExcelIdentityHelperRequest request;
                    try
                    {
                        request = ExcelIdentityHelperProtocol.ParseRequest(line);
                        if (!string.Equals(request.Nonce, nonce, StringComparison.Ordinal))
                            throw new InvalidDataException("Request nonce mismatch.");
                        if (!string.Equals(request.OwnerAssemblyMvid, OwnerMvid(), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Owner assembly build mismatch.");
                    }
                    catch (Exception ex)
                    {
                        Write(writer, Error(nonce, "invalid_request", ex.Message));
                        return 5;
                    }

                    try
                    {
                        if (request.Operation == "list")
                        {
                            application = ExcelProbeTarget.ResolveApplication(request.Hwnd);
                            Write(writer, WorkbookList(nonce, application));
                            return 0;
                        }
                        if (request.Operation == "bind")
                        {
                            if (lease != null) throw new InvalidOperationException("Helper is already bound.");
                            application = ExcelProbeTarget.ResolveApplication(request.Hwnd);
                            workbook = WorkbookAt(application, request.WorkbookIndex);
                            savedBeforeBind = ReadBoolean(workbook, "Saved");
                            label = request.Label;
                            lease = ComIdentityLease.Create(workbook);
                            Write(writer, Observation(nonce, label, request.Scenario, application, workbook,
                                lease, savedBeforeBind));
                            continue;
                        }
                        if (request.Operation == "observe")
                        {
                            if (lease == null) throw new InvalidOperationException("Helper is not bound.");
                            Write(writer, Observation(nonce, label, request.Scenario, application, workbook,
                                lease, savedBeforeBind));
                            continue;
                        }
                        if (request.Operation == "release")
                        {
                            if (lease != null)
                            {
                                lease.Dispose();
                                lease = null;
                            }
                            Write(writer, new ExcelIdentityHelperResponse
                            {
                                SchemaVersion = ExcelIdentityHelperProtocol.SchemaVersion,
                                Nonce = nonce,
                                Type = "released",
                                Status = "released",
                                Label = label,
                                ClientProcessId = Process.GetCurrentProcess().Id,
                                OwnerThreadId = Thread.CurrentThread.ManagedThreadId,
                                OwnerAssemblyMvid = OwnerMvid()
                            });
                            return 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        Write(writer, Error(nonce, "operation_failed", ex.Message));
                        return 6;
                    }
                }
            }
            finally
            {
                if (lease != null) lease.Dispose();
                GC.KeepAlive(workbook);
                GC.KeepAlive(application);
            }
        }

        private static ExcelIdentityHelperResponse Observation(string nonce, string label, string scenario,
            object application, object workbook, ComIdentityLease lease, bool? savedBeforeBind)
        {
            var appHwnd = Convert.ToInt64(Read(application, "Hwnd"));
            var processId = unchecked((int)ExcelProbeTarget.ProcessId(appHwnd));
            if (!ContainsWorkbook(application, workbook))
            {
                return new ExcelIdentityHelperResponse
                {
                    SchemaVersion = ExcelIdentityHelperProtocol.SchemaVersion,
                    Nonce = nonce,
                    Type = "observation",
                    Status = "closed",
                    Label = label,
                    Scenario = scenario,
                    ClientProcessId = Process.GetCurrentProcess().Id,
                    OwnerThreadId = Thread.CurrentThread.ManagedThreadId,
                    ExcelProcessId = processId,
                    ExcelProcessStartUtc = Process.GetProcessById(processId).StartTime.ToUniversalTime().ToString("o"),
                    ExcelVersion = Convert.ToString(Read(application, "Version")),
                    OwnerAssemblyMvid = OwnerMvid(),
                    Candidate = lease.Initial.Candidate,
                    Oxid = lease.Initial.Oxid,
                    Oid = lease.Initial.Oid,
                    Ipid = lease.Initial.Ipid,
                    SavedBeforeBind = savedBeforeBind
                };
            }
            var before = ReadBoolean(workbook, "Saved");
            var sample = lease.ReadAgain();
            var after = ReadBoolean(workbook, "Saved");
            return new ExcelIdentityHelperResponse
            {
                SchemaVersion = ExcelIdentityHelperProtocol.SchemaVersion,
                Nonce = nonce,
                Type = "observation",
                Status = "observed",
                Label = label,
                Scenario = scenario,
                ClientProcessId = Process.GetCurrentProcess().Id,
                OwnerThreadId = Thread.CurrentThread.ManagedThreadId,
                ExcelProcessId = processId,
                ExcelProcessStartUtc = Process.GetProcessById(processId).StartTime.ToUniversalTime().ToString("o"),
                ExcelVersion = Convert.ToString(Read(application, "Version")),
                OwnerAssemblyMvid = OwnerMvid(),
                Candidate = sample.Candidate,
                Oxid = sample.Oxid,
                Oid = sample.Oid,
                Ipid = sample.Ipid,
                Name = Convert.ToString(Read(workbook, "Name")),
                FullName = Convert.ToString(Read(workbook, "FullName")),
                SavedBeforeBind = savedBeforeBind,
                SavedBeforeRead = before,
                SavedAfterRead = after,
                WindowCount = CollectionCount(Read(workbook, "Windows"))
            };
        }

        private static ExcelIdentityHelperResponse WorkbookList(string nonce, object application)
        {
            var workbooks = Read(application, "Workbooks");
            var count = Math.Min(CollectionCount(workbooks), 256);
            var items = new List<ExcelIdentityWorkbookTarget>(count);
            for (var index = 1; index <= count; index++)
            {
                var workbook = CollectionItem(workbooks, index);
                items.Add(new ExcelIdentityWorkbookTarget
                {
                    Index = index,
                    Name = Convert.ToString(Read(workbook, "Name")),
                    FullName = Convert.ToString(Read(workbook, "FullName"))
                });
            }
            return new ExcelIdentityHelperResponse
            {
                SchemaVersion = ExcelIdentityHelperProtocol.SchemaVersion,
                Nonce = nonce,
                Type = "workbooks",
                Status = "listed",
                ClientProcessId = Process.GetCurrentProcess().Id,
                OwnerThreadId = Thread.CurrentThread.ManagedThreadId,
                OwnerAssemblyMvid = OwnerMvid(),
                Workbooks = items.AsReadOnly()
            };
        }

        private static ExcelIdentityHelperResponse Error(string nonce, string code, string message)
        {
            return new ExcelIdentityHelperResponse
            {
                SchemaVersion = ExcelIdentityHelperProtocol.SchemaVersion,
                Nonce = nonce,
                Type = "error",
                Status = "failed",
                Code = code,
                Message = Bound(message, 1000),
                ClientProcessId = Process.GetCurrentProcess().Id,
                OwnerThreadId = Thread.CurrentThread.ManagedThreadId,
                OwnerAssemblyMvid = OwnerMvid()
            };
        }

        private static void Write(TextWriter writer, ExcelIdentityHelperResponse response)
        {
            writer.WriteLine(ExcelIdentityHelperProtocol.SerializeResponse(response));
            writer.Flush();
        }

        private static object WorkbookAt(object application, int index)
        {
            var workbooks = Read(application, "Workbooks");
            var count = CollectionCount(workbooks);
            if (index < 1 || index > count) throw new InvalidOperationException("Workbook index is not open.");
            return CollectionItem(workbooks, index);
        }

        private static bool ContainsWorkbook(object application, object workbook)
        {
            var workbooks = Read(application, "Workbooks");
            var count = CollectionCount(workbooks);
            for (var index = 1; index <= count; index++)
                if (ExcelProbeTarget.SameLocalObject(workbook, CollectionItem(workbooks, index))) return true;
            return false;
        }

        private static object Read(object target, string property)
        {
            if (target == null) throw new InvalidOperationException("COM target is unavailable.");
            return target.GetType().InvokeMember(property, BindingFlags.GetProperty,
                null, target, null);
        }

        private static int CollectionCount(object collection)
        {
            return Convert.ToInt32(Read(collection, "Count"));
        }

        private static object CollectionItem(object collection, int index)
        {
            return collection.GetType().InvokeMember("Item", BindingFlags.GetProperty,
                null, collection, new object[] { index });
        }

        private static bool ReadBoolean(object target, string property)
        {
            return Convert.ToBoolean(Read(target, property));
        }

        private static string OwnerMvid()
        {
            return typeof(ComIdentitySample).Assembly.ManifestModule.ModuleVersionId.ToString("D");
        }

        private static string Bound(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximum) return value;
            return value.Substring(0, maximum);
        }

        private static bool TryReadArguments(string[] args, out string channel, out string nonce)
        {
            channel = null;
            nonce = null;
            if (args == null || args.Length != 4) return false;
            for (var index = 0; index < args.Length; index += 2)
            {
                if (args[index] == "--pipe") channel = args[index + 1];
                else if (args[index] == "--nonce") nonce = args[index + 1];
                else return false;
            }
            return IsToken(channel, 128) && IsToken(nonce, 64);
        }

        private static bool IsToken(string value, int maximum)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) return false;
            foreach (var item in value)
                if (!char.IsLetterOrDigit(item) && item != '.') return false;
            return true;
        }
    }
}

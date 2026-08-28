using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace RNAssistant.ExcelIdentityProbe
{
    // Keeps the original marshal reference until explicit disposal on its creating STA.
    // No finalizer: CoReleaseMarshalData must not run on the finalizer apartment.
    public sealed class ComIdentityLease : IDisposable
    {
        private readonly int _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        private object _comObject;
        private IStream _stream;
        private bool _marshaled;
        private bool _comInitialized;
        public ComIdentitySample Initial { get; private set; }

        private ComIdentityLease(object comObject) { _comObject = comObject; }

        public static ComIdentityLease Create(object comObject)
        {
            RequireWindowsSta();
            if (comObject == null || !Marshal.IsComObject(comObject))
                throw new ArgumentException("A live COM workbook on the calling STA is required.", "comObject");
            var lease = new ComIdentityLease(comObject);
            try
            {
                Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 2));
                lease._comInitialized = true;
                Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out lease._stream));
                var iid = ComIdentitySample.UnknownInterface;
                var unknown = Marshal.GetIUnknownForObject(comObject);
                try
                {
                    // MSHCTX_LOCAL, MSHLFLAGS_NORMAL. The packet is never unmarshaled;
                    // its reference is retained and explicitly released by this lease.
                    Marshal.ThrowExceptionForHR(CoMarshalInterface(lease._stream, ref iid, unknown, 0, IntPtr.Zero, 0));
                    lease._marshaled = true;
                }
                finally { Marshal.Release(unknown); }
                lease._stream.Stat(out var stat, 1);
                if (stat.cbSize < 76 || stat.cbSize > ComIdentitySample.MaximumPacketBytes)
                    throw new InvalidDataException("Marshaled packet exceeds probe bounds or is incomplete.");
                var bytes = new byte[(int)stat.cbSize];
                var count = Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    lease._stream.Seek(0, 0, IntPtr.Zero);
                    Marshal.WriteInt32(count, 0);
                    lease._stream.Read(bytes, bytes.Length, count);
                    if (Marshal.ReadInt32(count) != bytes.Length)
                        throw new InvalidDataException("Marshaled packet read was incomplete.");
                    lease.Initial = ComIdentitySample.Parse(bytes);
                }
                finally
                {
                    Array.Clear(bytes, 0, bytes.Length);
                    Marshal.FreeCoTaskMem(count);
                }
                return lease;
            }
            catch (Exception failure)
            {
                try { lease.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException("Probe failed and marshal cleanup failed.", failure, cleanup); }
                throw;
            }
        }

        public ComIdentitySample ReadAgain()
        {
            CheckOwner();
            if (_stream == null) throw new ObjectDisposedException("ComIdentityLease");
            using (var sample = Create(_comObject)) return sample.Initial;
        }

        public void Dispose()
        {
            CheckOwner();
            var stream = _stream;
            _stream = null;
            try
            {
                if (stream != null)
                {
                    try
                    {
                        if (_marshaled)
                        {
                            stream.Seek(0, 0, IntPtr.Zero);
                            Marshal.ThrowExceptionForHR(CoReleaseMarshalData(stream));
                        }
                    }
                    finally { Marshal.ReleaseComObject(stream); }
                }
            }
            finally
            {
                GC.KeepAlive(_comObject);
                _comObject = null;
                if (_comInitialized)
                {
                    _comInitialized = false;
                    CoUninitialize();
                }
            }
        }

        internal static void RequireWindowsSta()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("Probe requires a Windows x64 process.");
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Run the probe on the workbook's owner STA.");
        }

        private void CheckOwner()
        {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("Read/dispose must run on the creating STA.");
        }

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();
        [DllImport("ole32.dll")]
        private static extern int CreateStreamOnHGlobal(IntPtr global, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out IStream stream);
        [DllImport("ole32.dll")]
        private static extern int CoMarshalInterface(IStream stream, ref Guid iid, IntPtr unknown, uint context, IntPtr reserved, uint flags);
        [DllImport("ole32.dll")]
        private static extern int CoReleaseMarshalData(IStream stream);
    }
}

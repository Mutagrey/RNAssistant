using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace RNAssistant.Desktop
{
    internal sealed class SingleInstanceManager : IDisposable
    {
        private const string MutexName = "Local\\RNAssistant.Desktop";
        private const string PipeName = "RNAssistant.Desktop.Activation";
        private readonly Mutex _mutex;
        private bool _ownsMutex;
        private bool _disposed;

        public SingleInstanceManager()
        {
            _mutex = new Mutex(true, MutexName, out _ownsMutex);
        }

        public bool IsFirstInstance { get { return _ownsMutex; } }

        public static void SendActivation(string[] args)
        {
            using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
            {
                client.Connect(2000);
                using (var writer = new StreamWriter(client, Encoding.UTF8))
                {
                    writer.Write(JsonConvert.SerializeObject(args ?? new string[0]));
                }
            }
        }

        public void StartServer(Action<string[]> activate)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                while (!_disposed)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                        {
                            server.WaitForConnection();
                            using (var reader = new StreamReader(server, Encoding.UTF8))
                            {
                                var json = reader.ReadToEnd();
                                var args = JsonConvert.DeserializeObject<string[]>(json) ?? new string[0];
                                activate(args);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            });
        }

        public void Dispose()
        {
            _disposed = true;
            if (_ownsMutex)
            {
                _mutex.ReleaseMutex();
                _ownsMutex = false;
            }
            _mutex.Dispose();
        }
    }
}

using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace RNAssistant.Desktop
{
    internal sealed class SingleInstanceManager : IDisposable
    {
        private const string MutexName = "Local\\RNAssistant.Desktop";
        private static readonly string PipeName = "RNAssistant.Desktop.Activation." + SafeUserName();
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
                    writer.Write(JsonConvert.SerializeObject(new ActivationPipeMessage
                    {
                        Type = "activate",
                        Args = args ?? new string[0]
                    }));
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
                                var args = ParseActivationArgs(json);
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

        private static string[] ParseActivationArgs(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new string[0];
            }

            var token = JToken.Parse(json);
            if (token.Type == JTokenType.Array)
            {
                return token.ToObject<string[]>() ?? new string[0];
            }

            var message = token.ToObject<ActivationPipeMessage>();
            return message == null || message.Args == null ? new string[0] : message.Args;
        }

        private static string SafeUserName()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var name = identity == null ? Environment.UserName : identity.Name;
                var builder = new StringBuilder();
                foreach (var ch in name ?? string.Empty)
                {
                    builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
                }
                return builder.Length == 0 ? "user" : builder.ToString();
            }
            catch
            {
                return "user";
            }
        }

        private sealed class ActivationPipeMessage
        {
            public string Type { get; set; }
            public string[] Args { get; set; }
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace RNAssistant.BrowserHost
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            try { await OwnBrowserAsync(args).ConfigureAwait(false); return 0; }
            catch (Exception ex)
            {
                // Browser output is drained separately; only this bounded owner error crosses back.
                var message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
                try { Console.Error.WriteLine(message.Substring(0, Math.Min(700, message.Length))); }
                catch (IOException) { } // Caller may already have gone away.
                return 5;
            }
        }

        private static async Task OwnBrowserAsync(string[] args)
        {
            int seconds;
            if (!Console.IsInputRedirected || args.Length != 4 ||
                (args[1] != "headless" && args[1] != "window") ||
                (args[2] != "preview" && args[2] != "verify") ||
                !int.TryParse(args[3], out seconds) || seconds < 1 || seconds > 3660)
                throw new ArgumentException("Browser lifetime requires its caller pipe and bounded launch options.");

            var interrupted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Console.CancelKeyPress += (sender, e) => { e.Cancel = true; interrupted.TrySetResult(true); };
            // Read on a background thread: standard console streams may read synchronously.
            var ownerClosed = Task.Run(() => Console.OpenStandardInput().ReadByte());
            if (ownerClosed.IsCompleted) return;
            var profile = Path.Combine(Path.GetTempPath(), "rna-browser-" + Guid.NewGuid().ToString("N"));
            Process browser = null;
            try
            {
                Directory.CreateDirectory(profile);
                var start = new ProcessStartInfo(args[0]) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if (args[1] == "headless") start.ArgumentList.Add("--headless=new");
                if (args[2] == "preview") start.ArgumentList.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
                foreach (var option in new[] { "--disable-gpu", "--no-first-run",
                    "--no-default-browser-check", "--disable-background-networking", "--disable-extensions",
                    "--disable-sync", "--remote-debugging-port=0", "--user-data-dir=" + profile,
                    "--proxy-server=http://127.0.0.1:9", "about:blank" }) start.ArgumentList.Add(option);
                browser = Process.Start(start);
                if (browser == null) throw new IOException("Browser process could not start.");
                browser.StandardInput.Close(); // Never pass the ownership pipe to Chromium.
                _ = browser.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                _ = browser.StandardError.BaseStream.CopyToAsync(Stream.Null);
                Console.WriteLine(profile);
                Console.Out.Flush();
                var exited = browser.WaitForExitAsync();
                var finished = await Task.WhenAny(ownerClosed, interrupted.Task, exited,
                    Task.Delay(TimeSpan.FromSeconds(seconds))).ConfigureAwait(false);
                if (finished == exited) throw new IOException("Browser exited before its caller released it.");
                if (finished != ownerClosed && finished != interrupted.Task)
                    throw new IOException("Browser lifetime safety deadline expired.");
            }
            finally
            {
                try
                {
                    if (browser != null)
                    {
                        try
                        {
                            if (!browser.HasExited) browser.Kill(entireProcessTree: true);
                            if (!browser.WaitForExit(3000)) throw new IOException("Browser did not stop.");
                        }
                        finally { browser.Dispose(); }
                    }
                }
                finally { await RemoveProfileAsync(profile).ConfigureAwait(false); }
            }
        }

        private static async Task RemoveProfileAsync(string profile)
        {
            // Descendants can release files shortly after their parent exits. No
            // global temp scan and no path/PID received from a previous process.
            for (var attempt = 0; ; attempt++)
            {
                try { if (Directory.Exists(profile)) Directory.Delete(profile, true); return; }
                catch (Exception ex) when (attempt < 20 && (ex is IOException || ex is UnauthorizedAccessException))
                { await Task.Delay(100).ConfigureAwait(false); }
            }
        }
    }
}

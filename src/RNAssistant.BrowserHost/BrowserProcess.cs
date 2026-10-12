using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RNAssistant.BrowserHost
{
    // A pipe, not a persisted PID, owns the lifetime. The helper is the browser's
    // parent and creates/removes its own profile even when the caller is killed.
    public sealed class BrowserProcess
    {
        private readonly Process _owner;
        private readonly Task<string> _error;
        private bool _stopped;
        public string ProfilePath { get; private set; }
        public bool HasExited { get { return _owner.HasExited; } }

        private BrowserProcess(Process owner)
        { _owner = owner; _error = owner.StandardError.ReadToEndAsync(); }

        public static async Task<BrowserProcess> StartAsync(string executable, bool headless,
            bool preview, int lifetimeSeconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var processPath = Environment.ProcessPath;
            // The development host is framework-dependent. Preserve the selected
            // dotnet when invoked through it; apphost callers use dotnet on PATH.
            var host = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet",
                StringComparison.OrdinalIgnoreCase) ? processPath : "dotnet";
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(BrowserProcess).Assembly.Location);
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add(headless ? "headless" : "window");
            start.ArgumentList.Add(preview ? "preview" : "verify");
            start.ArgumentList.Add(lifetimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var process = Process.Start(start);
            if (process == null) throw new IOException("Browser lifetime owner could not start.");
            var browser = new BrowserProcess(process);
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    browser.ProfilePath = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                }
                if (string.IsNullOrEmpty(browser.ProfilePath))
                    throw new IOException("Browser lifetime owner did not report a profile.");
                return browser;
            }
            catch
            {
                await browser.StopAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task StopAsync()
        {
            if (_stopped) return;
            _stopped = true;
            try
            {
                _owner.StandardInput.Close(); // EOF also happens automatically on caller process loss.
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
                {
                    try { await _owner.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    { throw new IOException("Browser lifetime cleanup did not finish within 12 seconds."); }
                }
                if (_owner.ExitCode != 0)
                    throw new IOException("Browser lifetime failed: " + (await _error.ConfigureAwait(false)).Trim());
            }
            finally { _owner.Dispose(); }
        }
    }
}

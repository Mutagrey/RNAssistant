using System;
using System.Windows.Forms;

namespace RNAssistant.Desktop
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            DesktopLog.Info("Desktop start. Args=" + string.Join(" ", args ?? new string[0]));
            using (var instance = new SingleInstanceManager())
            {
                if (!instance.IsFirstInstance)
                {
                    try
                    {
                        SingleInstanceManager.SendActivation(args);
                        return;
                    }
                    catch (Exception ex)
                    {
                        DesktopLog.Error("Could not send activation to existing instance.", ex);
                    }
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var form = new MainForm();
                if (instance.IsFirstInstance)
                {
                    instance.StartServer(form.ApplyActivation);
                }
                if (args != null && args.Length > 0)
                {
                    form.ApplyActivation(args);
                }

                Application.Run(form);
            }
        }
    }
}

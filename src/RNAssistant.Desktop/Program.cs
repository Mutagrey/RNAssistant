using System;
using System.Windows.Forms;

namespace RNAssistant.Desktop
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            using (var instance = new SingleInstanceManager())
            {
                if (!instance.IsFirstInstance)
                {
                    try
                    {
                        SingleInstanceManager.SendActivation(args);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "RN Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var form = new MainForm();
                instance.StartServer(form.ApplyActivation);
                if (args != null && args.Length > 0)
                {
                    form.ApplyActivation(args);
                }

                Application.Run(form);
            }
        }
    }
}

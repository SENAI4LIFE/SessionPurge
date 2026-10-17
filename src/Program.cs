using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;

[assembly: AssemblyTitle("SessionPurge")]
[assembly: AssemblyDescription("Offline local conversation history manager")]
[assembly: AssemblyProduct("SessionPurge")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

namespace SessionPurge
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
        [STAThread]
        private static void Main()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    MessageBox.Show("Run SessionPurge from a standard, non-elevated Windows session.", "SessionPurge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate { MessageBox.Show("The operation stopped. Close the application and rescan before retrying.", "SessionPurge", MessageBoxButtons.OK, MessageBoxIcon.Warning); };
            Application.Run(new MainWindow());
        }
    }
}

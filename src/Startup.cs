using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows.Forms;

namespace EcoGPU
{
    /// <summary>
    /// Start with Windows via a scheduled task. The app needs admin rights, and a
    /// "Run" registry entry can't elevate without a UAC prompt; a task with
    /// "highest privileges" can.
    /// </summary>
    public static class Startup
    {
        const string TaskName = "EcoGPU";

        public static bool IsEnabled() => RunSchtasks("/Query /TN \"" + TaskName + "\"") == 0;

        public static bool Enable()
        {
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Starts EcoGPU at logon.</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT5S</Delay></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>{SecurityElement.Escape(Application.ExecutablePath)}</Command><Arguments>--startup</Arguments></Exec>
  </Actions>
</Task>";
            string tmp = Path.Combine(Path.GetTempPath(), "EcoGPU-task.xml");
            File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            try
            {
                return RunSchtasks("/Create /F /TN \"" + TaskName + "\" /XML \"" + tmp + "\"") == 0;
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        public static bool Disable() => RunSchtasks("/Delete /F /TN \"" + TaskName + "\"") == 0;

        static int RunSchtasks(string args)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }
    }
}

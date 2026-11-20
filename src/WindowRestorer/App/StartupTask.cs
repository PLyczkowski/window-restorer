using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using WindowRestorer.Core;

namespace WindowRestorer.App;

/// <summary>
/// "Start with Windows" via a Task Scheduler task. Unlike the Startup folder, a task can start the
/// app as administrator without a security prompt at every login. Creating it needs admin once.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "WindowRestorer";

    /// <summary>Creates or removes the login task. Returns false (and logs why) if it didn't work.</summary>
    public static bool Apply(bool enabled, bool runAsAdmin, IEnumerable<string> appArgs)
    {
        if (!enabled) return RunSchtasks(["/Delete", "/TN", TaskName, "/F"], "remove the login task", allowMissing: true);

        var xmlPath = Path.Combine(Path.GetTempPath(), $"WindowRestorer-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildXml(Environment.ProcessPath!, ["--logon", .. appArgs], runAsAdmin), Encoding.Unicode);
            return RunSchtasks(["/Create", "/TN", TaskName, "/XML", xmlPath, "/F"], "create the login task", allowMissing: false);
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    private static bool RunSchtasks(string[] args, string what, bool allowMissing)
    {
        Process? process;
        if (Elevation.IsElevated)
        {
            var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            process = Process.Start(psi)!;
        }
        else
        {
            // Admin tasks can only be created/removed as admin: one security prompt.
            process = Elevation.StartElevated("schtasks.exe", args);
            if (process == null)
            {
                Log.Warn($"Couldn't {what}: admin prompt declined");
                return false;
            }
        }
        using (process)
        {
            var output = process.StartInfo.RedirectStandardOutput ? process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd() : "";
            process.WaitForExit();
            if (process.ExitCode == 0 || allowMissing)
            {
                Log.Info($"Startup task: {what} (exit {process.ExitCode})");
                return true;
            }
            Log.Error($"Couldn't {what}: schtasks exit code {process.ExitCode} {output.Trim()}");
            return false;
        }
    }

    private static string BuildXml(string exe, IEnumerable<string> args, bool runAsAdmin)
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        // No battery conditions and no time limit: the default task settings would stop the app after 3 days or on battery.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Window Restorer: restores window positions after login.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>{(runAsAdmin ? "HighestAvailable" : "LeastPrivilege")}</RunLevel>
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
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>{SecurityElement.Escape(Elevation.JoinArgs(args))}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}

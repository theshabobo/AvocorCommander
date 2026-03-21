using AvocorCommander.Core;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AvocorCommander.ViewModels;

public sealed class AboutViewModel : BaseViewModel
{
    public string AppName        => "Avocor Commander";
    public string AppVersion     => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "—");
    public string Description    =>
        "Avocor Commander is a professional control application for managing Avocor display " +
        "products over RS-232 serial and TCP/IP network connections. Send commands, schedule " +
        "timed actions, run macros, and monitor connected devices across all supported Avocor series.";

    // ── System Info ───────────────────────────────────────────────────────────

    public string OsVersion      => Environment.OSVersion.VersionString;
    public string OsPlatform     => RuntimeInformation.OSDescription;
    public string DotNetVersion  => RuntimeInformation.FrameworkDescription;
    public string Architecture   => RuntimeInformation.ProcessArchitecture.ToString();
    public string MachineName    => Environment.MachineName;
    public string UserName       => Environment.UserName;

    // ── Copyright ─────────────────────────────────────────────────────────────

    public string Copyright      => $"© {DateTime.Now.Year} Avocor. All rights reserved.";
}

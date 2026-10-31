namespace HackWizard.Core.Logging;

public static class AppPaths
{
    public static string Root
    {
        get
        {
            var o = Environment.GetEnvironmentVariable("HACKWIZARD_HOME");
            return string.IsNullOrWhiteSpace(o)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HackWizard")
                : o;
        }
    }
    public static string Logs => Ensure(Path.Combine(Root, "logs"));
    public static string Cache => Ensure(Path.Combine(Root, "cache"));
    public static string Rules => Ensure(Path.Combine(Root, "Rules"));
    public static string Work => Ensure(Path.Combine(Root, "work"));
    public static string Reports => Ensure(Path.Combine(Root, "reports"));
    public static string Ensure(string p) { Directory.CreateDirectory(p); return p; }
}

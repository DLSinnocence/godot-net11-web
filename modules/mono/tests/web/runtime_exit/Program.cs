using System;
using System.Runtime.InteropServices.JavaScript;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

public static partial class RuntimeExitProbe
{
    public static void Main() => Console.WriteLine("PROBE:READY");

    [JSImport("requestExit", "godot:runtime")]
    private static partial void RequestRuntimeExit(int code);

    [JSExport]
    public static void Quit(bool requestLoaderExit)
    {
        Console.WriteLine("PROBE:MANAGED_CLEANUP");
        try
        {
            if (requestLoaderExit)
            {
                RequestRuntimeExit(0);
            }
        }
        finally
        {
            Environment.Exit(0);
        }
    }
}

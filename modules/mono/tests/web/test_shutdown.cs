using System;
using System.IO;

namespace GodotWebShutdownTests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Usage: test_shutdown <LibGodotMain.cs>");
                return 2;
            }

            string source = File.ReadAllText(args[0]);

            AssertAbsent(source, "emscripten_force_exit");
            AssertOrdered(
                source,
                "private static void ExitCallback()",
                "Volatile.Read(ref shutdownSyncComplete)",
                "Interlocked.Exchange(ref runtimeExitStarted, 1)",
                "emscripten_cancel_main_loop();",
                "instance = null;",
                "currentInstance?.Dispose();",
                "Environment.Exit(Environment.ExitCode);");
            AssertOrdered(
                source,
                "private static unsafe void SetupExit()",
                "Interlocked.Exchange(ref shutdownStarted, 1)",
                "emscripten_cancel_main_loop();",
                "emscripten_set_main_loop",
                "godot_js_os_finish_async");
            AssertOrdered(
                source,
                "private static void MainLoopCallback()",
                "Volatile.Read(ref shutdownStarted)",
                "libgodot_web_iteration()");

            Console.WriteLine("LibGodot Web shutdown contract passed.");
            return 0;
        }

        private static void AssertAbsent(string source, string text)
        {
            if (source.Contains(text, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected shutdown API: {text}");
            }
        }

        private static void AssertOrdered(string source, params string[] fragments)
        {
            int position = 0;
            foreach (string fragment in fragments)
            {
                int next = source.IndexOf(fragment, position, StringComparison.Ordinal);
                if (next < 0)
                {
                    throw new InvalidOperationException($"Missing or out-of-order shutdown fragment: {fragment}");
                }

                position = next + fragment.Length;
            }
        }
    }
}

namespace GodotPlugins.Game
{
    public sealed class GodotInstance : IDisposable
    {
        public void Dispose() { }

        public bool Start() => true;
    }

    public static class LibGodot
    {
        public static GodotInstance? CreateGodotInstance(string[] args) => new();
    }
}

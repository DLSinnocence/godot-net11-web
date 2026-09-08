using System;
using System.Runtime.InteropServices;

namespace GodotPlugins.Game
{
    internal static partial class Initializer
    {
        // NativeFuncs signatures are registered by UnmanagedCallbacksGenerator in GodotSharp.
        // This additional signature belongs to the LibGodot class database interface.

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr classdb_get_method_bind_sig(IntPtr _1, IntPtr _2, long _3);
    }
}

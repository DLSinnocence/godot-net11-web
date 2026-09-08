using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Godot.NativeInterop;
using Xunit;

namespace Godot.SourceGenerators.Tests;

public class NativeInteropAbiTests
{
    private static readonly Type CallbacksType = typeof(NativeFuncs)
        .GetNestedType("UnmanagedCallbacks", BindingFlags.NonPublic)!;

    [Fact]
    public void StructReturnsUseTrailingOutputPointers()
    {
        var callbacks = CallbacksType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var methods = typeof(NativeFuncs).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        int structReturns = 0;

        foreach (var callback in callbacks)
        {
            var returnType = callback.FieldType.GetFunctionPointerReturnType();
            Assert.False(IsStruct(returnType), $"{callback.Name} still returns a struct across the native boundary.");

            var method = Assert.Single(methods, m => m.Name == callback.Name &&
                (m.MethodImplementationFlags & MethodImplAttributes.AggressiveInlining) != 0);
            if (!IsStruct(method.ReturnType))
                continue;

            structReturns++;
            Assert.Equal(typeof(void), returnType);
            var parameters = callback.FieldType.GetFunctionPointerParameterTypes();
            Assert.Equal(method.GetParameters().Length + 1, parameters.Length);
            Assert.Equal(method.ReturnType.MakePointerType(), parameters[^1]);
        }

        Assert.True(structReturns > 0, "The test must exercise real Godot struct-return callbacks.");
    }

    [Fact]
    public void InitializationRejectsLegacyCallbackTable()
    {
        int legacySize = Marshal.SizeOf(CallbacksType) - IntPtr.Size;
        var exception = Assert.Throws<ArgumentException>(() => NativeFuncs.Initialize(IntPtr.Zero, legacySize));
        Assert.Contains("Rebuild the Godot engine and GodotSharp together", exception.Message);
        AssertNotInitialized();
    }

    [Theory]
    [InlineData("godotsharp_stack_info_vector_resize")]
    [InlineData("godotsharp_internal_signal_awaiter_connect")]
    [InlineData("godotsharp_array_resize")]
    public void NativeErrorCodesReturnInt32(string name)
    {
        var field = CallbacksType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(int), field.FieldType.GetFunctionPointerReturnType());
        Assert.Equal(typeof(long), Enum.GetUnderlyingType(typeof(Error)));
    }

    [Fact]
    public void EveryCallbackRegistersItsLoweredWasmSignature()
    {
        var callbacks = CallbacksType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var callback in callbacks)
        {
            var trampoline = CallbacksType.GetNestedType(callback.Name + "Trampoline", BindingFlags.NonPublic)!;
            Assert.NotNull(trampoline);
            Assert.NotNull(trampoline.GetCustomAttribute<UnmanagedFunctionPointerAttribute>());
            var invoke = trampoline.GetMethod("Invoke")!;
            Assert.Equal(TrampolineType(callback.FieldType.GetFunctionPointerReturnType()), invoke.ReturnType);
            Assert.Equal(callback.FieldType.GetFunctionPointerParameterTypes().Select(TrampolineType),
                invoke.GetParameters().Select(p => p.ParameterType));
        }
    }

    [Fact]
    public void AbiVersionUsesFixedFirstSlot()
    {
        Assert.Equal(IntPtr.Zero, Marshal.OffsetOf(CallbacksType, "godotsharp_get_runtime_interop_abi_version"));
    }

    [Fact]
    public void InitializationRejectsNullCallbackTable()
    {
        Assert.Throws<ArgumentNullException>(() => NativeFuncs.Initialize(IntPtr.Zero, Marshal.SizeOf(CallbacksType)));
        AssertNotInitialized();
    }

    [Fact]
    public void InitializationRejectsMissingAbiVersion()
    {
        int size = Marshal.SizeOf(CallbacksType);
        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            for (int offset = 0; offset < size; offset += IntPtr.Size)
                Marshal.WriteIntPtr(table, offset, IntPtr.Zero);

            Assert.Throws<InvalidOperationException>(() => NativeFuncs.Initialize(table, size));
            AssertNotInitialized();
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    [Fact]
    public void InitializationRejectsWrongAbiVersion()
    {
        int size = Marshal.SizeOf(CallbacksType);
        IntPtr table = Marshal.AllocHGlobal(size);
        AbiVersionCallback getVersion = () => 0;
        try
        {
            for (int offset = 0; offset < size; offset += IntPtr.Size)
                Marshal.WriteIntPtr(table, offset, IntPtr.Zero);

            int versionOffset = Marshal.OffsetOf(CallbacksType, "godotsharp_get_runtime_interop_abi_version").ToInt32();
            Marshal.WriteIntPtr(table, versionOffset, Marshal.GetFunctionPointerForDelegate(getVersion));

            var exception = Assert.Throws<InvalidOperationException>(() => NativeFuncs.Initialize(table, size));
            Assert.Contains("Runtime interop ABI version mismatch", exception.Message);
            AssertNotInitialized();
        }
        finally
        {
            GC.KeepAlive(getVersion);
            Marshal.FreeHGlobal(table);
        }
    }

    private static bool IsStruct(Type type) => type.IsValueType && !type.IsPrimitive && !type.IsEnum && type != typeof(void);

    private static Type TrampolineType(Type type) => type.IsPointer || type.IsFunctionPointer ? typeof(IntPtr) : type;

    private static void AssertNotInitialized() => Assert.False((bool)typeof(NativeFuncs)
        .GetField("initialized", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AbiVersionCallback();
}

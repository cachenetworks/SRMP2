using System;

// Slime Rancher 2's generated IL2CPP assemblies expose compiler-services metadata
// that can shadow the .NET 6 NullableAttribute definition during Roslyn overload
// resolution. Supplying the compiler-required constructors in the mod assembly
// keeps normal .NET Task APIs compilable without coupling networking code to
// IL2CPP's core library implementation.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(
        AttributeTargets.Class |
        AttributeTargets.Event |
        AttributeTargets.Field |
        AttributeTargets.GenericParameter |
        AttributeTargets.Module |
        AttributeTargets.Parameter |
        AttributeTargets.Property |
        AttributeTargets.ReturnValue,
        AllowMultiple = false,
        Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;

        public NullableAttribute(byte value)
        {
            NullableFlags = new[] { value };
        }

        public NullableAttribute(byte[] value)
        {
            NullableFlags = value;
        }
    }

    [AttributeUsage(
        AttributeTargets.Class |
        AttributeTargets.Delegate |
        AttributeTargets.Interface |
        AttributeTargets.Method |
        AttributeTargets.Struct,
        AllowMultiple = false,
        Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;

        public NullableContextAttribute(byte flag)
        {
            Flag = flag;
        }
    }
}

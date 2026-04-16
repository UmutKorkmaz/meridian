// Polyfill required for `init`-only setters and `record` types when
// targeting netstandard2.0. Roslyn looks up this exact type by name when
// emitting init-setter metadata. Visible only inside the generator
// assembly and never shipped to consumers.
//
// See:
// https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/IsExternalInit.cs
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }

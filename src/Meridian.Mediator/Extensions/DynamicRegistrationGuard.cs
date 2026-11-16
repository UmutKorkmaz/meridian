namespace Meridian.Mediator.Extensions;

/// <summary>
/// Shared failure for legacy registration paths that need run-time reflection or generic instantiation.
/// Callers test <c>RuntimeFeature.IsDynamicCodeSupported</c> directly so the Native AOT compiler can remove the
/// reflective branch; dynamic-code runtimes never reach this type.
/// </summary>
internal static class DynamicRegistrationGuard
{
    internal static NotSupportedException Unsupported(string operation) =>
        new($"{operation} needs run-time reflection or code generation, which this runtime does not support (Native AOT). " +
            "Declare closed handlers, behaviors and processors in a generated registry ([GenerateMediatorRegistry]), register it with AddGeneratedRegistry<TRegistry>() and set RequireGeneratedDispatch = true.");
}

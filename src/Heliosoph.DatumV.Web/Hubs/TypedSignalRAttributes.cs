// Local definitions of marker attributes expected by
// TypedSignalR.Client.TypeScript.Generator (`dotnet-tsrts`). The codegen tool
// resolves these by fully-qualified metadata name, so identity does not have
// to come from the upstream packages.
//
// Defining them locally avoids pulling in TypedSignalR.Client + Tapper, whose
// source generators emit C# client-side HubConnection extensions that need
// Microsoft.AspNetCore.SignalR.Client — irrelevant for a server project that
// hosts hubs rather than consuming them.

namespace TypedSignalR.Client
{
    [AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
    internal sealed class HubAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
    internal sealed class ReceiverAttribute : Attribute;
}

namespace Tapper
{
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct | AttributeTargets.Enum,
        Inherited = false,
        AllowMultiple = false)]
    internal sealed class TranspilationSourceAttribute : Attribute;
}

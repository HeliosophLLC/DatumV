using System.Reflection;
using DatumIngest.Model;
using DatumIngest.Tests;
using Xunit.Sdk;

[assembly: ScopedReferenceStore]

namespace DatumIngest.Tests;

/// <summary>
/// Automatically wraps every test method with a fresh <see cref="ReferenceStore"/> scope
/// so that reference-backed <see cref="DataValue"/> instances can be created without
/// manual scope management. Applied at the assembly level.
/// </summary>
internal sealed class ScopedReferenceStoreAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest)
    {
        ReferenceStore.BeginQueryScope();
    }

    public override void After(MethodInfo methodUnderTest)
    {
        ReferenceStore.EndQueryScope();
    }
}

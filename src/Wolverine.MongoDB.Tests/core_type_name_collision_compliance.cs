using System.Reflection;
using Wolverine.ComplianceTests;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// Upstream <c>CoreTypeNameCollisionCompliance</c> (GH-3907): no public type in this integration may
/// share a simple name with a type in Wolverine core, or every file importing both namespaces gets
/// CS0104. Several of this library's Internals types are public because the generated handler code
/// references them by name, so the invariant matters here as much as for the in-tree stores.
/// </summary>
public class core_type_name_collision_compliance : CoreTypeNameCollisionCompliance
{
    protected override Assembly StoreAssembly => typeof(WolverineMongoDbExtensions).Assembly;
}

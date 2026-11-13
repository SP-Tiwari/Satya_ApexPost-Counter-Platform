using System.Reflection;
using ApexPost.Courier.Domain;
using ApexPost.Courier.Offline.Persistence;

namespace ApexPost.Courier.Offline.Tests;

// The brief: domain classes must not reference any Couchbase types, and the
// Week 3 SQL Server code must stay untouched. Since the Week 4 review the
// domain is its own assembly, ApexPost.Courier.Domain, with no package or
// project references, so the compiler enforces the rule; these tests guard
// against a reference being added back.
public sealed class ArchitectureTests
{
	private static readonly Assembly DomainAssembly = typeof(ShipmentScan).Assembly;
	private static readonly Assembly Library = typeof(CourierDatabase).Assembly;

	[Fact]
	public void Domain_IsASeparateAssemblyFromThePersistenceLibrary()
	{
		Assert.Equal("ApexPost.Courier.Domain", DomainAssembly.GetName().Name);
		Assert.NotEqual(DomainAssembly, Library);
		Assert.DoesNotContain(Library.GetTypes(), type => type.Namespace == "ApexPost.Courier.Domain");
	}

	[Fact]
	public void DomainAssembly_ReferencesOnlyTheFramework()
	{
		var references = DomainAssembly.GetReferencedAssemblies()
			.Select(name => name.Name!)
			.ToList();

		Assert.All(references, name => Assert.True(
			name.StartsWith("System", StringComparison.Ordinal)
				|| name.Equals("netstandard", StringComparison.Ordinal)
				|| name.Equals("mscorlib", StringComparison.Ordinal),
			$"The courier domain must reference only the framework, but references {name}."));
	}

	[Fact]
	public void DomainTypes_DoNotReferenceCouchbase()
	{
		var domainTypes = DomainAssembly.GetTypes()
			.Where(type => type.Namespace == "ApexPost.Courier.Domain")
			.ToList();
		Assert.NotEmpty(domainTypes);

		var offenders = domainTypes
			.SelectMany(type => ReferencedTypes(type).Select(used => (type, used)))
			.Where(pair => pair.used.Assembly.GetName().Name!.StartsWith("Couchbase", StringComparison.Ordinal))
			.Select(pair => $"{pair.type.Name} -> {pair.used.FullName}")
			.ToList();

		Assert.Empty(offenders);
	}

	[Fact]
	public void ApplicationTypes_DoNotReferenceCouchbase()
	{
		var offenders = Library.GetTypes()
			.Where(type => type.Namespace == "ApexPost.Courier.Offline.Application")
			.SelectMany(ReferencedTypes)
			.Where(used => used.Assembly.GetName().Name!.StartsWith("Couchbase", StringComparison.Ordinal))
			.ToList();

		Assert.Empty(offenders);
	}

	[Fact]
	public void Library_DoesNotDependOnTheWeek3Projects()
	{
		var references = Library.GetReferencedAssemblies().Select(name => name.Name).ToList();

		// The courier domain is the only ApexPost assembly the library may use.
		Assert.DoesNotContain(references, name =>
			name!.StartsWith("ApexPost.", StringComparison.Ordinal)
			&& name != "ApexPost.Courier.Domain");
		Assert.DoesNotContain(references, name => name!.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
	}

	[Fact]
	public void PublicPersistenceApi_ExposesNoCouchbaseTypes()
	{
		var offenders = new[] { typeof(CourierDatabase), typeof(CouchbaseScanRepository) }
			.SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
			.SelectMany(MemberTypes)
			.Where(used => used.Assembly.GetName().Name!.StartsWith("Couchbase", StringComparison.Ordinal))
			.ToList();

		Assert.Empty(offenders);
	}

	private static IEnumerable<Type> ReferencedTypes(Type type)
	{
		const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
			| BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

		if (type.BaseType is not null)
			yield return type.BaseType;
		foreach (var implemented in type.GetInterfaces())
			yield return implemented;
		foreach (var used in type.GetMembers(all).SelectMany(MemberTypes))
			yield return used;
	}

	private static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
	{
		FieldInfo field => [field.FieldType],
		PropertyInfo property => [property.PropertyType],
		MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
			.Append(method.ReturnType),
		ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
		EventInfo @event => [@event.EventHandlerType!],
		_ => []
	};
}

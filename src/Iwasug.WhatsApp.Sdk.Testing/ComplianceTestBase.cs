using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AwesomeAssertions;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Testing;

/// <summary>
/// Completeness backstop for the contract kit (EF Core's ComplianceTestBase trick): the
/// suite can be green only when every abstract contract base class in this package has at
/// least one concrete subclass in the running test assemblies. A contract class added
/// later without a binding fails the run, listing the unsubclassed base(s) by name.
/// </summary>
/// <remarks>
/// Abstract by design: hosts (and this repo's own test project) subclass it with an empty
/// concrete type so the <c>[Fact]</c> below is discovered and executed by their runner —
/// the kit assembly itself is a plain library and never runs tests.
/// The default discovery scans the current AppDomain's loaded assemblies; hosts with a
/// custom assembly-loading layout can override <see cref="GetContractBaseClasses"/> /
/// <see cref="GetConcreteSubclasses"/>.
/// </remarks>
public abstract class ComplianceTestBase
{
    /// <summary>
    /// Every contract base class of this kit: abstract classes in the kit's
    /// <c>StoreContract</c> namespace that declare <c>[Fact]</c> members.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Test-kit completeness discovery runs in a non-trimmed test host; the kit is executed against consumer test assemblies, never inside trimmed production apps.")]
    protected virtual IReadOnlyCollection<Type> GetContractBaseClasses()
    {
        var kitAssembly = typeof(ComplianceTestBase).Assembly;
        var storeContractNamespace = typeof(ComplianceTestBase).Namespace + ".StoreContract";

        return kitAssembly
            .GetTypes()
            .Where(type => type.IsClass && type.IsAbstract && !type.IsGenericTypeDefinition)
            .Where(type => string.Equals(type.Namespace, storeContractNamespace, StringComparison.Ordinal))
            .Where(DeclaresFacts)
            .ToArray();
    }

    /// <summary>
    /// Concrete (non-abstract) subclasses of <paramref name="contractBase"/> found among
    /// the assemblies loaded in the current AppDomain.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Test-kit completeness discovery runs in a non-trimmed test host; the kit is executed against consumer test assemblies, never inside trimmed production apps.")]
    protected virtual IReadOnlyCollection<Type> GetConcreteSubclasses(Type contractBase)
    {
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(GetLoadableTypes)
            .Where(type => type.IsClass && !type.IsAbstract && contractBase.IsAssignableFrom(type))
            .ToArray();
    }

    [Fact]
    public void Every_contract_class_has_a_concrete_subclass()
    {
        var contractBases = GetContractBaseClasses();

        // Floor assertion (IN-06): reflection discovery can silently return empty in
        // a trimmed host — fail loudly instead of passing vacuously. Five contract
        // classes ship with the kit (identity, pre-key, recent-message-cache,
        // safety-state, signal-session); raise the floor when a sixth is added.
        contractBases.Count.Should().BeGreaterThanOrEqualTo(5,
            "reflection discovery returned fewer contract classes than the kit ships — "
            + "the completeness backstop would otherwise pass vacuously");

        var missing = contractBases
            .Where(contractBase => GetConcreteSubclasses(contractBase).Count == 0)
            .Select(contractBase => contractBase.FullName ?? contractBase.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        missing.Should().BeEmpty(
            "every kit contract class must have a concrete binding in the test assemblies, otherwise its tests silently do not run; unsubclassed: {0}",
            string.Join(", ", missing));
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "The Type instances arrive from Assembly.GetTypes() during test-time completeness discovery; no trimming occurs in a test host.")]
    private static bool DeclaresFacts(Type type)
    {
        return type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(method => method.GetCustomAttributes(inherit: true).Any(attr => attr is FactAttribute));
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Test-kit completeness discovery runs in a non-trimmed test host; the kit is executed against consumer test assemblies, never inside trimmed production apps.")]
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null)!;
        }
        catch (Exception)
        {
            // Unloadable/host assemblies contribute nothing to completeness discovery.
            return Array.Empty<Type>();
        }
    }
}

using Xunit;
using Xunit.Abstractions;

namespace SaasusSdk.Contract;

/// <summary>
/// xunit entry point for the contract test — the stable
/// <c>dotnet test tests/Contract/SaaSusSdk.Contract.Tests.csproj</c> surface.
/// </summary>
/// <remarks>
/// Each (operation, call form) is its own theory case, so both appear in the
/// test report and failures are counted individually; the work is delegated to
/// <see cref="ContractEngine"/> and every case must round-trip.
/// <para>Modules with no configured Prism endpoint contribute no case, so the
/// suite runs one module locally and all of them in CI. With no case at all —
/// no mock reachable or no spec directory — xunit fails the theory for missing
/// data, which is intended: the run proved nothing.</para>
/// <para>Synthetic data only; no response body, token or PII is logged.</para>
/// </remarks>
public class ContractTest
{
    private readonly ITestOutputHelper _output;

    public ContractTest(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Cases() =>
        ContractEngine.Cases.Keys.OrderBy(id => id, StringComparer.Ordinal).Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Contract(string caseId) => ContractEngine.Cases[caseId].InvokeAsync();

    /// <summary>
    /// The module list must reach the suite. It is declared once, in the shared
    /// fetch-openapi-spec action, and arrives in <c>CONTRACT_MODULES</c>;
    /// without it no module is enumerated and the run would prove nothing.
    /// </summary>
    [Fact]
    public void ModuleListIsProvided()
    {
        Assert.True(ContractEngine.Modules.Length > 0,
            "CONTRACT_MODULES is empty: it carries the module list from the fetch-openapi-spec action"
            + " (locally: CONTRACT_MODULES=\"auth\" dotnet test tests/Contract)");
    }

    /// <summary>
    /// Every module the SDK generates must be under test. A module added to
    /// generate.sh but not to the shared module list would contribute no case,
    /// so all of its operations would go unverified.
    /// </summary>
    [Fact]
    public void EveryGeneratedModuleIsUnderTest()
    {
        var untested = ContractEngine.GeneratedModules()
            .Except(ContractEngine.Modules, StringComparer.Ordinal)
            .ToList();
        Assert.True(untested.Count == 0, "modules generated into the SDK but absent from CONTRACT_MODULES"
            + " (their operations would go unverified): " + string.Join(", ", untested));
    }

    /// <summary>
    /// A module whose Prism mock is configured must have a spec to exercise it
    /// with. Without this, such a module would contribute no case and the run
    /// would pass with none of its operations verified.
    /// </summary>
    [Fact]
    public void ConfiguredModulesHaveSpec()
    {
        var missing = ContractEngine.ModulesMissingSpec().ToList();
        Assert.True(missing.Count == 0, "modules with a Prism mock but no spec in CONTRACT_SPEC_DIR"
            + " (their operations would go unverified): " + string.Join(", ", missing));
    }

    /// <summary>
    /// Every spec operation of a configured module must map to a generated SDK
    /// method. Enforced for the latest SDK (<c>CONTRACT_REQUIRE_COMPLETE=true</c>
    /// in workflow step B); an older SDK (C) legitimately lacks newer
    /// operations, so there the check only reports that it is not enforced.
    /// </summary>
    [Fact]
    public void AllOperationsEnumerated()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CONTRACT_REQUIRE_COMPLETE"), "true",
                StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine("CONTRACT_REQUIRE_COMPLETE is not set:"
                + " completeness is only enforced for the latest SDK");
            return;
        }
        var missing = ContractEngine.Modules
            .Where(module => PrismEndpoints.UrlForOrNull(module) != null)
            .SelectMany(ContractEngine.UnmappedOperations)
            .ToList();
        Assert.True(missing.Count == 0, "spec operations not enumerated in SDK: " + string.Join(", ", missing));
    }
}

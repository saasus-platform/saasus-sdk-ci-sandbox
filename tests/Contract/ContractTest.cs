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

using System.Reflection;
using Xunit;

namespace SaasusSdk.Contract;

/// <summary>
/// Covers the input synthesis and the argument binding on their own, without a
/// mock: the specs in use declare neither header parameters nor anyOf bodies,
/// so <see cref="ContractTest"/> would not exercise either path, and the
/// forward-compatibility shape below only occurs against a released SDK.
/// </summary>
public class SynthesisTests
{
    private const string ParameterSpec = """
        openapi: 3.0.0
        paths:
          /tenants/{tenant_id}/users:
            parameters:
              - name: tenant_id
                in: path
                schema:
                  type: string
                example: tenant-1
            get:
              operationId: GetUsers
              parameters:
                - name: limit
                  in: query
                  schema:
                    type: integer
                  example: 10
                - name: X-Saasus-Referer
                  in: header
                  schema:
                    type: string
                  example: referer-1
              responses:
                200:
                  description: ok
        """;

    private const string BodySpec = """
        openapi: 3.0.0
        paths:
          /units:
            post:
              operationId: SaveUnit
              requestBody:
                content:
                  application/json:
                    schema:
                      $ref: '#/components/schemas/UnitForSave'
              responses:
                200:
                  description: ok
        components:
          schemas:
            UnitForSave:
              {0}:
                - $ref: '#/components/schemas/FixedUnit'
                - $ref: '#/components/schemas/UsageUnit'
              discriminator:
                propertyName: type
                mapping:
                  fixed: '#/components/schemas/FixedUnit'
                  usage: '#/components/schemas/UsageUnit'
            FixedUnit:
              type: object
              properties:
                name:
                  type: string
                  example: fixed-unit
            UsageUnit:
              type: object
              properties:
                name:
                  type: string
                  example: usage-unit
        """;

    /// <summary>
    /// Path, query and header parameters all reach the argument of the matching
    /// name, and the generator's own <c>operationIndex</c> argument keeps its
    /// default. A header parameter that was dropped would leave the request
    /// incomplete without any case failing.
    /// </summary>
    [Fact]
    public void BindsPathQueryAndHeaderParametersByName()
    {
        var (spec, operation) = Load(ParameterSpec, "GetUsers");

        var args = ContractEngine.BuildArgs(Method(nameof(CurrentSignature)), spec, operation, 0);

        Assert.Equal(new object?[] { "tenant-1", 10, "referer-1", 0 }, args);
    }

    /// <summary>
    /// When the spec adds a parameter, an SDK generated before it does not
    /// declare the argument. That backward-compatible change must not fail
    /// before the request is sent: the parameter is simply not supplied.
    /// </summary>
    [Fact]
    public void ToleratesParameterMissingFromOlderSdk()
    {
        var (spec, operation) = Load(ParameterSpec, "GetUsers");

        var args = ContractEngine.BuildArgs(Method(nameof(OlderSignature)), spec, operation, 0);

        Assert.Equal(new object?[] { "tenant-1", "referer-1", 0 }, args);
    }

    /// <summary>
    /// An argument that matches no spec parameter and is not the request body
    /// cannot be filled from the spec. Supplying a constant would send a value
    /// the spec never declared, so binding fails instead.
    /// </summary>
    [Fact]
    public void FailsOnArgumentTheSpecDoesNotExplain()
    {
        var (spec, operation) = Load(ParameterSpec, "GetUsers");

        Assert.Throws<ContractFailure>(() =>
            ContractEngine.BuildArgs(Method(nameof(UnexplainedSignature)), spec, operation, 0));
    }

    /// <summary>
    /// Every member of a composite request body is sent, for anyOf as well as
    /// oneOf. A body whose members are only reachable through anyOf would
    /// otherwise be synthesized as an empty object.
    /// </summary>
    [Theory]
    [InlineData("oneOf")]
    [InlineData("anyOf")]
    public void CoversEveryRequestBodyVariant(string keyword)
    {
        var (spec, operation) = Load(BodySpec.Replace("{0}", keyword), "SaveUnit");

        Assert.Equal(new[] { "fixed", "usage" }, spec.BodyVariantLabels(operation));

        var body = Assert.IsType<Dictionary<string, object?>>(spec.BodyValue(operation, 1));
        Assert.Equal("usage-unit", body["name"]);
        Assert.Equal("usage", body["type"]);
    }

    /// <summary>
    /// Two body variants never share a case id. The cases are keyed by id, so a
    /// shared id drops one variant, and the label alone does not distinguish
    /// them: inline oneOf/anyOf members have no <c>$ref</c> to be named after
    /// and all fall back to the same label.
    /// </summary>
    [Fact]
    public void GivesEveryBodyVariantItsOwnCaseId()
    {
        var (spec, operation) = Load(BodySpec.Replace("{0}", "oneOf"), "SaveUnit");

        var ids = Enumerable.Range(0, 2)
            .Select(index => new ContractCase("pricing", typeof(SynthesisTests), spec, operation, index,
                "same-label", CallForm.Sync, "http://127.0.0.1:4010").Id)
            .ToList();

        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Writes a spec document and loads it through the suite's own entry point.</summary>
    private static (SpecDocument Spec, SpecOperation Operation) Load(string document, string operationId)
    {
        var directory = Directory.CreateTempSubdirectory("contract-spec");
        File.WriteAllText(Path.Combine(directory.FullName, "test.yml"), document);
        var spec = SpecDocument.LoadOrNull(directory.FullName, "test");
        Assert.NotNull(spec);
        return (spec!, spec!.Operations.Single(o => o.OperationId == operationId));
    }

    private static MethodInfo Method(string name) =>
        typeof(SynthesisTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    // Stand-ins for the generated signatures. Only their parameter names and
    // types matter; they are never invoked.
    private static void CurrentSignature(string tenantId, int? limit, string xSaasusReferer, int operationIndex = 0)
    {
    }

    private static void OlderSignature(string tenantId, string xSaasusReferer, int operationIndex = 0)
    {
    }

    private static void UnexplainedSignature(string tenantId, int? limit, string xSaasusReferer, string surprise,
        int operationIndex = 0)
    {
    }
}

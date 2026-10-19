using System.Net;
using System.Reflection;
using Newtonsoft.Json;

namespace SaasusSdk.Contract;

/// <summary>
/// Reflective contract engine behind the xunit suite (<see cref="ContractTest"/>).
/// </summary>
/// <remarks>
/// <para><b>Enumeration</b> — operations are discovered at run time by matching
/// each spec operationId against the generated <c>&lt;module&gt;api.Api.*Api</c>
/// classes. Nothing is hardcoded, so an operation an SDK lacks is simply not
/// enumerated — which is what lets the same suite run a previously released SDK
/// (forward compatibility).</para>
/// <para><b>Call forms</b> — <c>WithHttpInfo</c> (sync) and
/// <c>WithHttpInfoAsync</c>. The plain <c>Op</c> / <c>OpAsync</c> forms only
/// call them and return <c>.Data</c>, so they add no logic and observe less:
/// RestSharp reports a response that fails to deserialize as <c>ErrorText</c>
/// with a null <c>Data</c> rather than an exception, and only the
/// <c>WithHttpInfo</c> result (<see cref="ApiResponseView"/>) exposes that and
/// the HTTP status.</para>
/// <para><b>Inputs</b> — parameters and request bodies come from
/// <see cref="SpecDocument"/>.</para>
/// <para>Synthetic data only: synthetic credentials, request values from the
/// spec, and no response body, credential or PII reported.</para>
/// </remarks>
internal static class ContractEngine
{
    /// <summary>All SDK modules. A module is tested only if it is generated AND has a Prism endpoint.</summary>
    internal static readonly string[] Modules =
        { "auth", "pricing", "billing", "awsmarketplace", "integration", "apilog", "communication" };

    /// <summary>The single assembly all generated modules are compiled into.</summary>
    private static readonly Assembly SdkAssembly = typeof(authapi.Client.Configuration).Assembly;

    private static readonly Lazy<IReadOnlyDictionary<string, ContractCase>> LazyCases =
        new(BuildCases);

    static ContractEngine()
    {
        // The generated ApiClient signs every request with SaaSus SigV1 from
        // these variables and throws when any is unset
        // (generated/<module>api/Expansion/ExpansionApiClient.cs), so the mock
        // is unreachable without them; it does not validate the signature.
        // Overwritten rather than defaulted so that a developer's real
        // credentials are never signed into a request sent to the mock.
        // Verifying the authentication layer itself is out of scope here.
        Environment.SetEnvironmentVariable("SAASUS_SECRET_KEY", "synthetic-secret-key");
        Environment.SetEnvironmentVariable("SAASUS_API_KEY", "synthetic-api-key");
        Environment.SetEnvironmentVariable("SAASUS_SAAS_ID", "synthetic-saas-id");
    }

    /// <summary>Every case, keyed by its reported id.</summary>
    internal static IReadOnlyDictionary<string, ContractCase> Cases => LazyCases.Value;

    /// <summary>
    /// Spec operations of a configured module with no generated SDK method.
    /// Asserted only for the latest SDK: an older one legitimately lacks newer
    /// operations.
    /// </summary>
    internal static IEnumerable<string> UnmappedOperations(string module)
    {
        var spec = SpecDocument.LoadOrNull(SpecDir, module);
        if (spec == null)
        {
            yield break;
        }
        foreach (var operation in spec.Operations)
        {
            if (FindApiType(module, operation.OperationId) == null)
            {
                yield return module + "." + operation.OperationId;
            }
        }
    }

    /// <summary>
    /// Modules whose Prism mock is configured but whose spec is absent from
    /// <c>CONTRACT_SPEC_DIR</c>.
    /// </summary>
    /// <remarks>
    /// Such a module contributes no case, so without this check the run would
    /// pass with none of its operations verified. No other spec is substituted:
    /// a mock must be exercised with the document it was started from, or a
    /// request the mock rejects and one the spec never declared become
    /// indistinguishable.
    /// </remarks>
    internal static IEnumerable<string> ModulesMissingSpec() =>
        Modules.Where(module => PrismEndpoints.UrlForOrNull(module) != null
            && SpecDocument.LoadOrNull(SpecDir, module) == null);

    private static string? SpecDir => Environment.GetEnvironmentVariable("CONTRACT_SPEC_DIR");

    private static IReadOnlyDictionary<string, ContractCase> BuildCases()
    {
        var cases = new Dictionary<string, ContractCase>(StringComparer.Ordinal);
        foreach (var module in Modules)
        {
            var baseUrl = PrismEndpoints.UrlForOrNull(module);
            var spec = SpecDocument.LoadOrNull(SpecDir, module);
            if (baseUrl == null || spec == null)
            {
                continue;
            }
            foreach (var operation in spec.Operations)
            {
                var apiType = FindApiType(module, operation.OperationId);
                if (apiType == null)
                {
                    continue;
                }
                // A oneOf/anyOf body yields one case per variant, otherwise one case.
                var labels = spec.BodyVariantLabels(operation);
                for (var index = 0; index < Math.Max(labels.Count, 1); index++)
                {
                    var label = index < labels.Count ? labels[index] : null;
                    foreach (var form in new[] { CallForm.Sync, CallForm.Async })
                    {
                        var contractCase = new ContractCase(module, apiType, spec, operation, index, label, form, baseUrl);
                        // Cases are keyed by id, so a collision would silently
                        // drop one. The variant index in the id makes that
                        // unreachable; if it happens anyway, fail loudly
                        // rather than leave a case unverified.
                        if (!cases.TryAdd(contractCase.Id, contractCase))
                        {
                            throw new ContractFailure($"duplicate contract case id: {contractCase.Id}");
                        }
                    }
                }
            }
        }
        return cases;
    }

    /// <summary>
    /// The generated API class holding the operation, or <c>null</c> when this
    /// SDK has none. Matching on the method name rather than the spec's tag
    /// keeps enumeration independent of the generator's tag-to-class naming;
    /// the <c>WithHttpInfo</c> suffix makes the match unambiguous (no accessor
    /// of the API class is named that way).
    /// </summary>
    private static Type? FindApiType(string module, string operationId)
    {
        foreach (var type in ApiTypes(module))
        {
            if (SyncMethod(type, operationId) != null && AsyncMethod(type, operationId) != null)
            {
                return type;
            }
        }
        return null;
    }

    private static IEnumerable<Type> ApiTypes(string module) =>
        SdkAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == module + "api.Api")
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    internal static MethodInfo? SyncMethod(Type apiType, string operationId) =>
        DeclaredMethod(apiType, operationId + "WithHttpInfo");

    internal static MethodInfo? AsyncMethod(Type apiType, string operationId) =>
        DeclaredMethod(apiType, operationId + "WithHttpInfoAsync");

    private static MethodInfo? DeclaredMethod(Type apiType, string name) =>
        apiType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == name && !m.IsSpecialName);

    /// <summary>
    /// A generated API client pointed at the mock. The hand-written client
    /// configuration wrappers (<c>generated/modules</c>) are deliberately
    /// bypassed: this suite verifies the generated client against the spec.
    /// </summary>
    internal static object CreateApi(string module, Type apiType, string baseUrl)
    {
        var configurationType = SdkAssembly.GetType(module + "api.Client.Configuration")
            ?? throw new InvalidOperationException($"{module}: no generated Configuration type");
        var configuration = Activator.CreateInstance(configurationType)!;
        configurationType.GetProperty("BasePath")!.SetValue(configuration, baseUrl);
        return Activator.CreateInstance(apiType, configuration)!;
    }

    /// <summary>
    /// Fills the generated argument list, binding each argument to the spec
    /// parameter of the same name.
    /// </summary>
    /// <remarks>
    /// By name, not by position: an SDK generated from another spec revision
    /// (the forward-compatibility case, where a parameter was inserted in the
    /// middle) still gets each value in the right argument. A spec parameter
    /// the method does not declare is simply not supplied — the same change
    /// seen from the other side.
    /// <para>The request body is the only argument with no spec parameter, so
    /// exactly one argument is unbound when the operation has a body and none
    /// otherwise. Any other unbound argument cannot be explained from the spec,
    /// and a constant would send a value the spec never declared, so binding
    /// fails instead.</para>
    /// </remarks>
    internal static object?[] BuildArgs(MethodInfo method, SpecDocument spec, SpecOperation operation, int variantIndex)
    {
        var parameters = method.GetParameters();
        var bound = new SpecParam?[parameters.Length];
        var unbound = new List<int>();
        for (var i = 0; i < parameters.Length; i++)
        {
            bound[i] = operation.Parameters.FirstOrDefault(p => Matches(p.Name, parameters[i].Name));
            if (bound[i] == null && !IsGeneratorArgument(parameters[i]))
            {
                unbound.Add(i);
            }
        }

        var expected = operation.BodySchema != null ? 1 : 0;
        if (unbound.Count != expected)
        {
            throw new ContractFailure($"{operation.OperationId}: parameter binding failed — expected {expected}"
                + $" unbound argument(s) (the request body), got {unbound.Count} of {parameters.Length}");
        }

        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (bound[i] is { } specParam)
            {
                args[i] = Coerce(spec.ParamValue(specParam), parameter.ParameterType);
            }
            else if (unbound.Count > 0 && i == unbound[0])
            {
                args[i] = Coerce(spec.BodyValue(operation, variantIndex), parameter.ParameterType);
            }
            else
            {
                // operationIndex / cancellationToken: the generator's own
                // arguments, left at their declared defaults.
                args[i] = parameter.HasDefaultValue ? parameter.DefaultValue : Default(parameter.ParameterType);
            }
        }
        return args;
    }

    /// <summary>
    /// True for the arguments the generator adds to every operation, which no
    /// spec parameter corresponds to.
    /// </summary>
    private static bool IsGeneratorArgument(ParameterInfo parameter) =>
        parameter.ParameterType == typeof(CancellationToken)
        || (parameter.Name == "operationIndex" && parameter.ParameterType == typeof(int));

    /// <summary>
    /// Compares a spec parameter name with a generated argument name, ignoring
    /// the separators and the casing the generator changes
    /// (<c>created_date</c> -&gt; <c>createdDate</c>,
    /// <c>X-Saasus-Referer</c> -&gt; <c>xSaasusReferer</c>).
    /// </summary>
    private static bool Matches(string specName, string? argumentName) =>
        argumentName != null && Flatten(specName) == Flatten(argumentName);

    private static string Flatten(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Converts a spec-derived value into the generated argument type through
    /// JSON, the same conversion the SDK applies to a request body. The
    /// generated models tag each property with its spec name, so an object keyed
    /// by spec names binds by name, not position.
    /// </summary>
    private static object? Coerce(object? value, Type target)
    {
        if (value == null)
        {
            return Default(target);
        }
        return JsonConvert.DeserializeObject(JsonConvert.SerializeObject(value), target);
    }

    private static object? Default(Type target) =>
        target.IsValueType ? Activator.CreateInstance(target) : null;

    /// <summary>
    /// The HTTP status carried by a generated <c>ApiException</c>, or 0 when the
    /// error is not one.
    /// </summary>
    internal static int HttpStatusOf(Exception error) =>
        error.GetType().GetProperty("ErrorCode")?.GetValue(error) as int? ?? 0;
}

/// <summary>The call forms exercised per operation.</summary>
internal enum CallForm
{
    Sync,
    Async
}

/// <summary>
/// A contract failure. Carries only what this harness composed itself, never a
/// response body.
/// </summary>
internal sealed class ContractFailure : Xunit.Sdk.XunitException
{
    internal ContractFailure(string message) : base(message)
    {
    }
}

/// <summary>One contract case: an operation exercised through one call form.</summary>
internal sealed class ContractCase
{
    private readonly string _module;
    private readonly Type _apiType;
    private readonly SpecDocument _spec;
    private readonly SpecOperation _operation;
    private readonly int _variantIndex;
    private readonly string _baseUrl;

    internal ContractCase(string module, Type apiType, SpecDocument spec, SpecOperation operation,
        int variantIndex, string? variantLabel, CallForm form, string baseUrl)
    {
        _module = module;
        _apiType = apiType;
        _spec = spec;
        _operation = operation;
        _variantIndex = variantIndex;
        _baseUrl = baseUrl;
        Form = form;
        // The id carries the variant index, not just the label: two oneOf/anyOf
        // members can share a label (inline members have no $ref to name them),
        // and cases are keyed by id, so a shared id would leave one variant
        // unsent.
        Id = $"{module}.{apiType.Name}.{operation.OperationId}"
            + (variantLabel == null ? string.Empty : $"(#{variantIndex} {variantLabel})")
            + $" [{form}]";
    }

    /// <summary>Stable, human-readable id, e.g. <c>pricing.PricingUnitsApi.CreatePricingUnit(#0 fixed) [Sync]</c>.</summary>
    internal string Id { get; }

    internal CallForm Form { get; }

    /// <summary>
    /// Invokes the call form against the mock and asserts the round trip: the
    /// request is accepted and the response deserializes into the SDK types.
    /// </summary>
    internal async Task InvokeAsync()
    {
        try
        {
            var api = ContractEngine.CreateApi(_module, _apiType, _baseUrl);
            var method = Form == CallForm.Sync
                ? ContractEngine.SyncMethod(_apiType, _operation.OperationId)!
                : ContractEngine.AsyncMethod(_apiType, _operation.OperationId)!;
            var args = ContractEngine.BuildArgs(method, _spec, _operation, _variantIndex);

            var result = method.Invoke(api, args);
            if (Form == CallForm.Async)
            {
                var task = (Task)result!;
                await task.ConfigureAwait(false);
                result = task.GetType().GetProperty("Result")!.GetValue(task);
            }
            AssertRoundTrip(new ApiResponseView(result!));
        }
        catch (ContractFailure)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Unwrap so the declared-error check sees the real ApiException.
            var error = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            // For an operation declaring no 2xx response (e.g.
            // ReturnInternalServerError), surfacing the declared error IS the
            // check, so a spec-declared status passes. Everything else fails: a
            // 4xx means the mock rejected a request built from the same spec it
            // validates against, so the SDK or the spec is wrong; a 5xx means
            // Prism (static mode) could not produce the declared response, a
            // spec defect. Skipping either would leave that operation's
            // response deserialization unverified.
            var status = ContractEngine.HttpStatusOf(error);
            if (_operation.HasSuccess || status == 0 || !_operation.DeclaredStatuses.Contains(status))
            {
                throw Failure(error);
            }
        }
    }

    private void AssertRoundTrip(ApiResponseView response)
    {
        if (response.Status is < 200 or >= 300)
        {
            // The generated exception factory normally raises these; reaching
            // here means it was disabled or the status is a 1xx/3xx.
            if (_operation.HasSuccess || !_operation.DeclaredStatuses.Contains(response.Status))
            {
                throw new ContractFailure($"{Id} failed: unexpected HTTP {response.Status}");
            }
            return;
        }
        // RestSharp reports a response that could not be deserialized as an
        // error on the response rather than as an exception. The text is not
        // quoted: the generated codec puts the response body into it.
        if (!string.IsNullOrEmpty(response.ErrorText))
        {
            throw new ContractFailure($"{Id} failed: HTTP {response.Status} response could not be deserialized"
                + " — response bodies are deliberately not reported; re-run locally for the detail");
        }
        // An operation that declares a response body must deliver one; the
        // generator types a body-less operation as ApiResponse<Object>.
        if (response.PayloadType != typeof(object) && response.Data == null)
        {
            throw new ContractFailure($"{Id} failed: HTTP {response.Status} response deserialized to null");
        }
    }

    /// <summary>
    /// The reported failure, built from the operation, the call form, the
    /// exception type and the HTTP status only.
    /// </summary>
    /// <remarks>
    /// Generated models put the response body in their deserialization message
    /// (<c>PricingUnit</c>: "The JSON string `...` cannot be deserialized"),
    /// and the runner prints that message and the whole inner-exception chain
    /// into the uploaded report. The original exception is therefore neither
    /// chained nor quoted, keeping bodies out of the logs and artifacts as the
    /// issue requires; diagnosis needs a local re-run.
    /// </remarks>
    private ContractFailure Failure(Exception error)
    {
        var status = ContractEngine.HttpStatusOf(error);
        return new ContractFailure($"{Id} failed: {error.GetType().FullName}"
            + (status != 0 ? $" (HTTP {status})" : string.Empty)
            + " — response bodies are deliberately not reported; re-run locally for the detail");
    }
}

/// <summary>
/// Reads the generated <c>ApiResponse&lt;T&gt;</c>, whose <c>T</c> differs per
/// operation.
/// </summary>
internal sealed class ApiResponseView
{
    internal ApiResponseView(object response)
    {
        var type = response.GetType();
        Status = (int)(HttpStatusCode)type.GetProperty("StatusCode")!.GetValue(response)!;
        ErrorText = type.GetProperty("ErrorText")!.GetValue(response) as string;
        Data = type.GetProperty("Data")!.GetValue(response);
        PayloadType = type.GetGenericArguments()[0];
    }

    internal int Status { get; }

    internal string? ErrorText { get; }

    internal object? Data { get; }

    internal Type PayloadType { get; }
}

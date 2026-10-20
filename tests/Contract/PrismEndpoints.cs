namespace SaasusSdk.Contract;

/// <summary>
/// Resolves the base URL of the Prism mock for a module, or <c>null</c> when
/// the module has no mock configured (it then contributes no case).
/// </summary>
/// <remarks>
/// The single source is the environment variable <c>PRISM_URL_&lt;MODULE&gt;</c>,
/// exported by the setup-prism action in CI and set by hand for a local run
/// (<c>CONTRACT_MODULES=auth PRISM_URL_AUTH=http://127.0.0.1:4010 dotnet test tests/Contract</c>).
/// </remarks>
internal static class PrismEndpoints
{
    internal static string? UrlForOrNull(string module)
    {
        var url = Environment.GetEnvironmentVariable("PRISM_URL_" + module.ToUpperInvariant());
        return string.IsNullOrEmpty(url) ? null : url;
    }
}

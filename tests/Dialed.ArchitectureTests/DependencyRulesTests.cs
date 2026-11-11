using System.Reflection;
using Dialed.AI;
using Dialed.Client;
using Dialed.Domain;
using Dialed.Images;
using Dialed.UI;

namespace Dialed.ArchitectureTests;

/// <summary>
/// Guards the project boundaries from the architecture doc. Most importantly, Dialed.UI and Dialed.Client
/// stay free of browser-only APIs so a MAUI Blazor Hybrid host can reuse them later (ADR-0009).
/// </summary>
public sealed class DependencyRulesTests
{
    private static readonly string[] BrowserOnly =
    [
        "Microsoft.JSInterop",
        "Microsoft.AspNetCore.Components.WebAssembly",
    ];

    private static readonly string[] HostsAndServer = ["Dialed.Web", "Dialed.Api", "Dialed.Functions"];

    [Fact]
    public void Domain_references_no_other_Dialed_project_and_no_AspNetCore()
    {
        var references = ReferencedAssemblyNames(typeof(DomainAssembly));

        references.ShouldNotContain(name => name.StartsWith("Dialed.", StringComparison.Ordinal));
        references.ShouldNotContain(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Client_uses_no_browser_only_APIs_and_no_hosts()
    {
        var references = ReferencedAssemblyNames(typeof(ClientAssembly));

        references.ShouldNotContain(name => StartsWithAny(name, BrowserOnly));
        references.ShouldNotContain(name => StartsWithAny(name, HostsAndServer));
        references.ShouldNotContain("Dialed.UI");
    }

    [Fact]
    public void Client_defines_every_platform_service_as_an_interface()
    {
        string[] expected =
        [
            "IPhotoCapture", "ILocalFileStore", "IConnectivity", "IShareService", "IPasskeyClient",
            "IStoragePersistence", "IWakeLock", "ISoundCue", "IAppLifecycle", "IFileExport", "IErrorSink", "IDeviceInfo", "IPreferenceStore",
            "ICrossTabLock", "IBarcodeScanner", "IPushNotifications", "IImageRenderer",
        ];

        var platform = typeof(ClientAssembly).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.Namespace == "Dialed.Client.Platform")
            .Select(type => type.Name);

        platform.ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public void UI_uses_no_browser_only_APIs_and_no_hosts()
    {
        var references = ReferencedAssemblyNames(typeof(Routes));

        references.ShouldNotContain(name => StartsWithAny(name, BrowserOnly));
        references.ShouldNotContain(name => StartsWithAny(name, HostsAndServer));
    }

    [Fact]
    public void AI_references_no_client_UI_or_hosts()
    {
        var references = ReferencedAssemblyNames(typeof(AIAssembly));
        string[] forbidden = ["Dialed.UI", "Dialed.Client", .. HostsAndServer];

        references.ShouldNotContain(name => name.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal));
        references.ShouldNotContain(name => StartsWithAny(name, forbidden));
    }

    [Fact]
    public void Images_references_only_Domain_so_the_API_and_a_Function_can_both_host_it()
    {
        var references = ReferencedAssemblyNames(typeof(ImagesAssembly));
        string[] forbidden = ["Dialed.UI", "Dialed.Client", "Dialed.AI", .. HostsAndServer];

        references.ShouldNotContain(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        references.ShouldNotContain(name => StartsWithAny(name, forbidden));
    }

    private static List<string> ReferencedAssemblyNames(Type marker) =>
        [.. marker.Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty)];

    private static bool StartsWithAny(string name, string[] prefixes) =>
        prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
}

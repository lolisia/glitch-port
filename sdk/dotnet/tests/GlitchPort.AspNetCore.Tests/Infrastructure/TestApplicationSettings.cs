using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace GlitchPort.AspNetCore.Tests.Infrastructure;

/// <summary>
/// Carries the per-test settings into the test entry point before
/// <c>AddGlitchPort</c> runs.
/// </summary>
/// <remarks>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> applies its
/// <c>ConfigureAppConfiguration</c> callbacks after the entry point has called
/// <c>AddGlitchPort</c>, which bounds the configuration eagerly. The test
/// assembly therefore publishes its settings before the host is started and the
/// entry point adds them to <c>builder.Configuration</c>. Tests in this assembly
/// run sequentially (see AssemblyInfo.cs), so one settings snapshot at a time is
/// enough.
/// </remarks>
internal static class TestApplicationSettings
{
    private static readonly object Gate = new object();
    private static Dictionary<string, string?> _settings = new Dictionary<string, string?>(StringComparer.Ordinal);

    public static void Configure(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (Gate)
        {
            _settings = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var setting in settings)
            {
                _settings[setting.Key] = setting.Value;
            }
        }
    }

    public static void Apply(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Dictionary<string, string?> snapshot;
        lock (Gate)
        {
            snapshot = new Dictionary<string, string?>(_settings, StringComparer.Ordinal);
        }

        builder.Configuration.AddInMemoryCollection(snapshot);
    }
}

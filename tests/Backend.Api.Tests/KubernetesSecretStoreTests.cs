using Backend.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Api.Tests;

public sealed class KubernetesSecretStoreTests
{
    private sealed class FakeEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Backend.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class RecordingLogger : ILogger<KubernetesSecretStore>
    {
        public List<string> Warnings { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Kubernetes:Namespace"] = "sdlc" })
        .Build();

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task DevelopmentAndTestingFallBackToTheDefaultKubeconfigAndLogAWarning(string environmentName)
    {
        var logger = new RecordingLogger();
        var store = new KubernetesSecretStore(Configuration(), new FakeEnvironment(environmentName), logger);

        // Outside a real cluster InClusterConfig() always fails, so the fallback attempt also fails
        // here (no kubeconfig present in this environment either) - what this proves is that the
        // fallback was attempted (the warning), not that it succeeded.
        await Assert.ThrowsAnyAsync<Exception>(() => store.StoreAsync("key", "value", CancellationToken.None));
        Assert.Contains(logger.Warnings, w => w.Contains("falling back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProductionFailsClosedWithoutFallingBackToADefaultKubeconfig()
    {
        // Security finding on PR #14: falling back to BuildDefaultConfig() outside Development/Testing
        // could load whatever kubeconfig happens to be on the process's environment and write tokens to
        // an unreviewed cluster/context. Production must fail on InClusterConfig() alone.
        var logger = new RecordingLogger();
        var store = new KubernetesSecretStore(Configuration(), new FakeEnvironment("Production"), logger);

        await Assert.ThrowsAnyAsync<Exception>(() => store.StoreAsync("key", "value", CancellationToken.None));
        Assert.Empty(logger.Warnings);
    }
}

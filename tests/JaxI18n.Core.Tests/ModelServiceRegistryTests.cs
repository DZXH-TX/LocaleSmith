using JaxI18n.Core.Abstractions;
using JaxI18n.Core.Models;
using JaxI18n.Core.Services;

namespace JaxI18n.Core.Tests;

public sealed class ModelServiceRegistryTests
{
    [Fact]
    public void ModelSourceRejectsRemotePlaintextEndpointWithoutCredential()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ModelSource(
            "remote-http",
            "Remote HTTP",
            ModelProviderKind.Ollama,
            new Uri("http://models.example.test:11434"),
            "llama3"));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstSourceIsSelectedAndSelectionCanChangeAtRuntime()
    {
        using var registry = new ModelServiceRegistry();
        var local = new StubModelService(CreateSource("local", "Local", ModelProviderKind.Ollama));
        var cloud = new StubModelService(CreateSource("cloud", "Cloud", ModelProviderKind.OpenAiCompatible));
        registry.AddOrUpdate(local);
        registry.AddOrUpdate(cloud);

        Assert.Equal("local", registry.SelectedSource?.Id);
        Assert.True(registry.SelectSource("cloud"));
        Assert.Same(cloud, registry.GetSelected());
        Assert.False(registry.SelectSource("missing"));
    }

    [Fact]
    public void RemovingSelectionChoosesDeterministicReplacement()
    {
        using var registry = new ModelServiceRegistry();
        registry.AddOrUpdate(new StubModelService(CreateSource("z", "Zulu", ModelProviderKind.Ollama)));
        registry.AddOrUpdate(new StubModelService(CreateSource("a", "Alpha", ModelProviderKind.Anthropic)));

        Assert.True(registry.Remove("z"));

        Assert.Equal("a", registry.SelectedSource?.Id);
    }

    [Fact]
    public void UpdatingSourceDoesNotRequireRestart()
    {
        using var registry = new ModelServiceRegistry();
        var original = new StubModelService(CreateSource("same", "Before", ModelProviderKind.Ollama));
        var replacement = new StubModelService(CreateSource("same", "After", ModelProviderKind.Ollama));
        ModelSelectionChangedEventArgs? change = null;
        registry.AddOrUpdate(original);
        registry.SelectionChanged += (_, args) => change = args;

        registry.AddOrUpdate(replacement);

        Assert.Same(replacement, registry.GetSelected());
        Assert.Equal("After", registry.SelectedSource?.DisplayName);
        Assert.Equal("Before", change?.Previous?.DisplayName);
        Assert.Equal("After", change?.Current?.DisplayName);
    }

    private static ModelSource CreateSource(string id, string name, ModelProviderKind kind) =>
        new(id, name, kind, new Uri("http://127.0.0.1:11434"), "test-model", kind == ModelProviderKind.Ollama ? null : $"keys/{id}");

    private sealed class StubModelService(ModelSource source) : IModelService
    {
        public ModelSource Source { get; } = source;

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelResponse("ok"));
    }
}

using System.Text.Json;
using LocaleSmith.Core.Models;

namespace LocaleSmith.Core.Tests;

public sealed class ModelToolContractTests
{
    [Fact]
    public void RequestClonesSchemasAndRequiresUniqueProviderSafeNames()
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""");
        var tool = new ModelToolDefinition("system_context", "Read safe context.", document.RootElement);
        var request = new ModelRequest(
            [new ModelMessage(ModelMessageRole.User, "Inspect the environment.")],
            tools: [tool]);

        Assert.Equal("object", request.Tools[0].InputSchema.GetProperty("type").GetString());
        Assert.Throws<ArgumentException>(() => new ModelRequest(
            [new ModelMessage(ModelMessageRole.User, "x")],
            tools: [tool, tool]));
        Assert.Throws<ArgumentException>(() => new ModelToolDefinition(
            "system.context",
            "Invalid provider function name.",
            request.Tools[0].InputSchema));
    }

    [Fact]
    public void ToolConversationCarriesAssistantCallsAndCorrelatedResults()
    {
        using var document = JsonDocument.Parse("""{"path":"demo.jar"}""");
        var call = new ModelToolCall("call-1", "inspect_archive", document.RootElement);
        const string reasoningContent = "  opaque reasoning\n保留原样  ";
        var assistant = new ModelMessage(
            ModelMessageRole.Assistant,
            string.Empty,
            [call],
            reasoningContent: reasoningContent);
        var result = new ModelMessage(
            ModelMessageRole.Tool,
            "ok",
            toolCallId: call.Id,
            toolName: call.Name);
        var response = new ModelResponse(
            string.Empty,
            toolCalls: [call],
            reasoningContent: reasoningContent);

        Assert.Same(call, Assert.Single(assistant.ToolCalls));
        Assert.Equal(reasoningContent, assistant.ReasoningContent);
        Assert.Equal("call-1", result.ToolCallId);
        Assert.Same(call, Assert.Single(response.ToolCalls));
        Assert.Equal(reasoningContent, response.ReasoningContent);
        Assert.Throws<ArgumentNullException>(() => new ModelMessage(ModelMessageRole.Tool, "orphan"));
        Assert.Throws<ArgumentException>(() => new ModelMessage(
            ModelMessageRole.User,
            "not provider state",
            reasoningContent: reasoningContent));
        Assert.Throws<ArgumentException>(() => new ModelResponse(string.Empty));
    }
}

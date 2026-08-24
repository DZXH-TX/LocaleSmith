using System.Net;
using System.Text;
using System.Text.Json;
using LocaleSmith.Core.Models;
using LocaleSmith.Infrastructure.Models;
using LocaleSmith.Infrastructure.Security;

namespace LocaleSmith.Infrastructure.Tests;

public sealed class ModelAdapterTests
{
    [Fact]
    public async Task OllamaUsesLocalChatApiWithoutCredential()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Uri? requestedUri = null;
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestedUri = request.RequestUri;
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Null(request.Headers.Authorization);
            return JsonResponse("""{"model":"llama3","message":{"content":"你好"},"prompt_eval_count":7,"eval_count":2}""");
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        var source = new ModelSource(
            "ollama",
            "Local Ollama",
            ModelProviderKind.Ollama,
            new Uri("http://127.0.0.1:11434"),
            "llama3");
        var service = new OllamaModelService(client, source, secrets);

        var response = await service.CompleteAsync(CreateRequest(), cancellationToken);

        Assert.Equal("http://127.0.0.1:11434/api/chat", requestedUri?.AbsoluteUri);
        Assert.Contains("\"stream\":false", requestJson, StringComparison.Ordinal);
        Assert.Equal("你好", response.Content);
        Assert.Equal(7L, response.InputTokens);
        Assert.Equal(2L, response.OutputTokens);
        Assert.Equal(9L, response.TotalTokens);
        Assert.True(response.Usage!.IsComplete);
    }

    [Fact]
    public async Task OllamaListsInstalledModelsFromTagsEndpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal("http://127.0.0.1:11434/api/tags", request.RequestUri?.AbsoluteUri);
            return Task.FromResult(JsonResponse(
                """
                {"models":[
                  {"name":"llama3:latest","modified_at":"2026-01-02T03:04:05Z","size":1234,"digest":"abc","details":{"family":"llama","parameter_size":"8B","quantization_level":"Q4_K_M"}},
                  {"model":"gemma3"}
                ]}
                """));
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        var service = new OllamaModelService(
            client,
            new ModelSource(
                "ollama",
                "Local Ollama",
                ModelProviderKind.Ollama,
                new Uri("http://127.0.0.1:11434"),
                "llama3:latest"),
            secrets);

        var models = await service.ListModelsAsync(cancellationToken);

        Assert.Equal(["gemma3", "llama3:latest"], models.Select(static model => model.Name));
        Assert.Equal("8B", models[1].ParameterSize);
        Assert.Equal(1234, models[1].SizeBytes);
    }

    [Fact]
    public async Task OpenAiCompatibleResolvesCredentialPerRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        string? authorization = null;
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Equal("https://models.example/v1/chat/completions", request.RequestUri?.AbsoluteUri);
            return JsonResponse("""{"model":"demo","choices":[{"message":{"content":"translated"}}],"usage":{"prompt_tokens":3000000000,"completion_tokens":4000000000,"total_tokens":9000000000}}""");
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/cloud", "cloud-secret".AsMemory(), cancellationToken);
        var source = new ModelSource(
            "cloud",
            "Cloud",
            ModelProviderKind.OpenAiCompatible,
            new Uri("https://models.example/v1"),
            "demo",
            "providers/cloud");
        var service = new OpenAiCompatibleModelService(client, source, secrets);

        var response = await service.CompleteAsync(CreateRequest(), cancellationToken);

        Assert.Equal("Bearer cloud-secret", authorization);
        Assert.Contains("\"max_tokens\":128", requestJson, StringComparison.Ordinal);
        Assert.Equal("translated", response.Content);
        Assert.Equal(3_000_000_000L, response.InputTokens);
        Assert.Equal(4_000_000_000L, response.OutputTokens);
        Assert.Equal(9_000_000_000L, response.TotalTokens);
        Assert.Equal(1, response.Usage!.ProviderCallCount);
        Assert.Equal(1, response.Usage.CallsWithUsage);
        Assert.Equal(1, response.Usage.CallsWithCompleteUsage);
    }

    [Theory]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/chat/completions")]
    [InlineData("https://api.deepseek.com/", "https://api.deepseek.com/chat/completions")]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/chat/completions", "https://api.deepseek.com/chat/completions")]
    [InlineData("https://api.deepseek.com/v1/chat/completions/", "https://api.deepseek.com/v1/chat/completions")]
    public async Task OpenAiCompatibleNormalizesCommonDeepSeekEndpointInputs(
        string configuredEndpoint,
        string expectedRequestEndpoint)
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(expectedRequestEndpoint, request.RequestUri?.AbsoluteUri);
            return Task.FromResult(JsonResponse(
                """{"model":"deepseek-chat","choices":[{"message":{"content":"OK"}}]}"""));
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            "providers/deepseek",
            "deepseek-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var service = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "deepseek",
                "DeepSeek",
                ModelProviderKind.OpenAiCompatible,
                new Uri(configuredEndpoint),
                "deepseek-chat",
                "providers/deepseek"),
            secrets);

        var response = await service.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("OK", response.Content);
        Assert.Null(response.Usage);
    }

    [Theory]
    [InlineData(ModelProviderPresets.OpenAiId, "max_completion_tokens", "max_tokens")]
    [InlineData(ModelProviderPresets.XiaomiMimoId, "max_completion_tokens", "max_tokens")]
    [InlineData(ModelProviderPresets.KimiId, "max_completion_tokens", "max_tokens")]
    [InlineData(ModelProviderPresets.MiniMaxId, "max_completion_tokens", "max_tokens")]
    [InlineData(ModelProviderPresets.DeepSeekId, "max_tokens", "max_completion_tokens")]
    [InlineData(ModelProviderPresets.QwenId, "max_tokens", "max_completion_tokens")]
    [InlineData(ModelProviderPresets.DoubaoId, "max_tokens", "max_completion_tokens")]
    [InlineData(ModelProviderPresets.ZhipuGlmId, "max_tokens", "max_completion_tokens")]
    [InlineData(ModelProviderPresets.CustomId, "max_tokens", "max_completion_tokens")]
    public async Task OpenAiCompatibleUsesPresetSpecificTokenLimitParameter(
        string presetId,
        string expectedParameter,
        string unexpectedParameter)
    {
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""{"choices":[{"message":{"content":"OK"}}]}""");
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/preset", "secret".AsMemory(), TestContext.Current.CancellationToken);
        var preset = ModelProviderPresets.ResolveOrCustom(presetId);
        var service = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "preset",
                "Preset",
                ModelProviderKind.OpenAiCompatible,
                preset.DefaultEndpoint ?? new Uri("https://models.example/v1"),
                "editable-model",
                "providers/preset",
                presetId),
            secrets);

        await service.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Contains($"\"{expectedParameter}\":128", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{unexpectedParameter}\"", requestJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ModelProviderPresets.DeepSeekId, true)]
    [InlineData(ModelProviderPresets.QwenId, true)]
    [InlineData(ModelProviderPresets.XiaomiMimoId, false)]
    [InlineData(ModelProviderPresets.MiniMaxId, true)]
    [InlineData(ModelProviderPresets.DoubaoId, true)]
    [InlineData(ModelProviderPresets.ZhipuGlmId, true)]
    [InlineData(ModelProviderPresets.KimiId, false)]
    [InlineData(ModelProviderPresets.OpenAiId, true)]
    [InlineData(ModelProviderPresets.CustomId, true)]
    public async Task OpenAiCompatibleSendsTemperatureOnlyWhenPresetSupportsCustomization(
        string presetId,
        bool expectsTemperature)
    {
        var sendCount = 0;
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            sendCount++;
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""{"choices":[{"message":{"content":"OK"}}]}""");
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/preset", "secret".AsMemory(), TestContext.Current.CancellationToken);
        var preset = ModelProviderPresets.ResolveOrCustom(presetId);
        var service = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "preset",
                "Preset",
                ModelProviderKind.OpenAiCompatible,
                preset.DefaultEndpoint ?? new Uri("https://models.example/v1"),
                "editable-model",
                "providers/preset",
                presetId),
            secrets);

        await service.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(1, sendCount);
        if (expectsTemperature)
        {
            Assert.Contains("\"temperature\":0.2", requestJson, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("\"temperature\"", requestJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task KimiK3UsesCurrentTokenFieldAndOmitsFixedTemperature()
    {
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""{"choices":[{"message":{"content":"OK"}}]}""");
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/kimi", "secret".AsMemory(), TestContext.Current.CancellationToken);
        var service = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "kimi",
                "Kimi",
                ModelProviderKind.OpenAiCompatible,
                ModelProviderPresets.Kimi.DefaultEndpoint!,
                ModelProviderPresets.Kimi.DefaultModelName!,
                "providers/kimi",
                ModelProviderPresets.KimiId),
            secrets);

        await service.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Contains("\"model\":\"kimi-k3\"", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"max_completion_tokens\":128", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"temperature\"", requestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomOpenAiCompatibleCanOverrideTokenLimitParameterWithoutRetry()
    {
        var sendCount = 0;
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            sendCount++;
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""{"choices":[{"message":{"content":"OK"}}]}""");
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/custom", "secret".AsMemory(), TestContext.Current.CancellationToken);
        var service = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "custom",
                "Custom",
                ModelProviderKind.OpenAiCompatible,
                new Uri("https://models.example/v1"),
                "editable-model",
                "providers/custom",
                ModelProviderPresets.CustomId,
                OpenAiTokenLimitParameter.MaxCompletionTokens),
            secrets);

        await service.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(1, sendCount);
        Assert.Contains("\"max_completion_tokens\":128", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"max_tokens\"", requestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiCompatibleCanOmitTokenLimitParameterWhenModelUsesProviderDefault()
    {
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""{"choices":[{"message":{"content":"OK"}}]}""");
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/custom", "secret".AsMemory(), TestContext.Current.CancellationToken);
        var service = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "custom",
                "Custom",
                ModelProviderKind.OpenAiCompatible,
                new Uri("https://models.example/v1"),
                "model-with-provider-default",
                "providers/custom",
                ModelProviderPresets.CustomId,
                OpenAiTokenLimitParameter.Omit),
            secrets);

        await service.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("\"max_tokens\"", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"max_completion_tokens\"", requestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicUsesMessagesApiAndSeparateSystemPrompt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.True(request.Headers.TryGetValues("x-api-key", out var keys));
            Assert.Equal("anthropic-secret", Assert.Single(keys));
            Assert.True(request.Headers.Contains("anthropic-version"));
            Assert.Equal("https://api.anthropic.test/v1/messages", request.RequestUri?.AbsoluteUri);
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""{"model":"claude-test","content":[{"type":"text","text":"part one"},{"type":"text","text":" + part two"}],"usage":{"input_tokens":9,"output_tokens":4}}""");
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/anthropic", "anthropic-secret".AsMemory(), cancellationToken);
        var source = new ModelSource(
            "anthropic",
            "Anthropic",
            ModelProviderKind.Anthropic,
            new Uri("https://api.anthropic.test"),
            "claude-test",
            "providers/anthropic");
        var service = new AnthropicModelService(client, source, secrets);

        var response = await service.CompleteAsync(CreateRequest(), cancellationToken);

        Assert.Contains("\"system\":\"system prompt\"", requestJson, StringComparison.Ordinal);
        Assert.Equal("part one + part two", response.Content);
        Assert.Equal(9L, response.InputTokens);
        Assert.Equal(4L, response.OutputTokens);
        Assert.Equal(13L, response.TotalTokens);
    }

    [Fact]
    public async Task OpenAiCompatibleRoundTripsToolCalls()
    {
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(
                """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"system_context","arguments":"{\"detail\":\"safe\"}"}}]}}]}""");
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/cloud", "secret".AsMemory(), TestContext.Current.CancellationToken);
        var service = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "cloud",
                "Cloud",
                ModelProviderKind.OpenAiCompatible,
                new Uri("https://models.example/v1"),
                "demo",
                "providers/cloud"),
            secrets);

        ModelResponse response = await service.CompleteAsync(
            CreateToolRequest(),
            TestContext.Current.CancellationToken);

        ModelToolCall call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("safe", call.Arguments.GetProperty("detail").GetString());
        Assert.Contains("\"tools\":[{\"type\":\"function\"", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"system_context\"", requestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiCompatibleOmitsPrivateReasoningForOtherProvidersOrMissingState()
    {
        var requestBodies = new List<string>();
        var responseIndex = 0;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            responseIndex++;
            return responseIndex == 1
                ? JsonResponse(
                    """{"choices":[{"message":{"content":"normal","reasoning_content":"ignored vendor extension"}}]}""")
                : JsonResponse("""{"choices":[{"message":{"content":"kimi without reasoning"}}]}""");
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/reasoning", "secret".AsMemory(), TestContext.Current.CancellationToken);
        var deepSeek = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "deepseek",
                "DeepSeek",
                ModelProviderKind.OpenAiCompatible,
                new Uri("https://api.deepseek.com"),
                "editable-model",
                "providers/reasoning",
                ModelProviderPresets.DeepSeekId),
            secrets);
        var kimi = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "kimi",
                "Kimi",
                ModelProviderKind.OpenAiCompatible,
                ModelProviderPresets.Kimi.DefaultEndpoint!,
                ModelProviderPresets.Kimi.DefaultModelName!,
                "providers/reasoning",
                ModelProviderPresets.KimiId),
            secrets);
        var messageWithPrivateState = new ModelMessage(
            ModelMessageRole.Assistant,
            "prior visible answer",
            reasoningContent: "must not cross provider boundary");

        ModelResponse deepSeekResponse = await deepSeek.CompleteAsync(
            new ModelRequest([messageWithPrivateState]),
            TestContext.Current.CancellationToken);
        ModelResponse kimiResponse = await kimi.CompleteAsync(
            new ModelRequest([new ModelMessage(ModelMessageRole.User, "no prior reasoning")]),
            TestContext.Current.CancellationToken);

        Assert.Null(deepSeekResponse.ReasoningContent);
        Assert.Null(kimiResponse.ReasoningContent);
        Assert.Equal(2, requestBodies.Count);
        Assert.All(requestBodies, static body =>
            Assert.DoesNotContain("reasoning_content", body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnthropicRoundTripsToolUseAndToolResultBlocks()
    {
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(
                """{"content":[{"type":"tool_use","id":"toolu_1","name":"system_context","input":{}}],"usage":{"input_tokens":2,"output_tokens":1}}""");
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/anthropic", "secret".AsMemory(), TestContext.Current.CancellationToken);
        var service = new AnthropicModelService(
            client,
            new ModelSource(
                "anthropic",
                "Anthropic",
                ModelProviderKind.Anthropic,
                new Uri("https://api.anthropic.test"),
                "claude-test",
                "providers/anthropic"),
            secrets);

        ModelResponse response = await service.CompleteAsync(
            CreateToolRequest(includePriorToolResult: true),
            TestContext.Current.CancellationToken);

        Assert.Equal("toolu_1", Assert.Single(response.ToolCalls).Id);
        Assert.Contains("\"input_schema\":{\"type\":\"object\"", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"tool_result\"", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"tool_use_id\":\"previous-call\"", requestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OllamaRoundTripsToolCallsAndToolNames()
    {
        string? requestJson = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(
                """{"message":{"role":"assistant","content":"","tool_calls":[{"type":"function","function":{"name":"system_context","arguments":{}}}]}}""");
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        var service = new OllamaModelService(
            client,
            new ModelSource(
                "ollama",
                "Local Ollama",
                ModelProviderKind.Ollama,
                new Uri("http://127.0.0.1:11434"),
                "llama3"),
            secrets);

        ModelResponse response = await service.CompleteAsync(
            CreateToolRequest(includePriorToolResult: true),
            TestContext.Current.CancellationToken);

        Assert.Equal("system_context", Assert.Single(response.ToolCalls).Name);
        Assert.Contains("\"tool_name\":\"system_context\"", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"parameters\":{\"type\":\"object\"", requestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OllamaSynthesizesFreshCorrelationIdsForEveryResponse()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(
            """{"message":{"role":"assistant","content":"","tool_calls":[{"type":"function","function":{"name":"system_context","arguments":{}}}]}}""")));
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        var service = new OllamaModelService(
            client,
            new ModelSource(
                "ollama",
                "Local Ollama",
                ModelProviderKind.Ollama,
                new Uri("http://127.0.0.1:11434"),
                "llama3"),
            secrets);

        ModelResponse first = await service.CompleteAsync(
            CreateToolRequest(),
            TestContext.Current.CancellationToken);
        ModelResponse second = await service.CompleteAsync(
            CreateToolRequest(),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(Assert.Single(first.ToolCalls).Id, Assert.Single(second.ToolCalls).Id);
    }

    [Fact]
    public async Task MalformedProviderToolArgumentsAreRejected()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(
            """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_1","function":{"name":"system_context","arguments":"[]"}}]}}]}""")));
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/cloud", "secret".AsMemory(), TestContext.Current.CancellationToken);
        var service = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "cloud",
                "Cloud",
                ModelProviderKind.OpenAiCompatible,
                new Uri("https://models.example/v1"),
                "demo",
                "providers/cloud"),
            secrets);

        ModelServiceException exception = await Assert.ThrowsAsync<ModelServiceException>(() =>
            service.CompleteAsync(CreateToolRequest(), TestContext.Current.CancellationToken));

        Assert.Contains("must be a JSON object", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderFailureReturnsTypedStatusAndBoundedBody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("invalid credential", Encoding.UTF8, "text/plain")
            }));
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/cloud", "wrong".AsMemory(), cancellationToken);
        var service = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "cloud",
                "Cloud",
                ModelProviderKind.OpenAiCompatible,
                new Uri("https://models.example/v1"),
                "demo",
                "providers/cloud"),
            secrets);

        var exception = await Assert.ThrowsAsync<ModelServiceException>(
            () => service.CompleteAsync(CreateRequest(), cancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("invalid credential", exception.ResponseBody);
    }

    [Fact]
    public async Task OpenAiCompatibleFailureRedactsCredentialAndIncludesProviderRequestId()
    {
        const string apiKey = "deepseek-plain-secret";
        var sendCount = 0;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            sendCount++;
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"Authentication failed for " + apiKey +
                    "\",\"api_key\":\"" + apiKey + "\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
            response.Headers.Add("x-request-id", "deepseek-request-123");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            "providers/deepseek",
            apiKey.AsMemory(),
            TestContext.Current.CancellationToken);
        var service = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "deepseek",
                "DeepSeek",
                ModelProviderKind.OpenAiCompatible,
                new Uri("https://api.deepseek.com/v1"),
                "deepseek-chat",
                "providers/deepseek"),
            secrets);

        var exception = await Assert.ThrowsAsync<ModelServiceException>(() =>
            service.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("deepseek-request-123", exception.RequestId);
        Assert.Contains("HTTP 401", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Authentication failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("deepseek-request-123", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, exception.ResponseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, sendCount);
    }

    [Fact]
    public async Task OpenAiCompatibleNetworkExceptionCannotEchoResolvedCredential()
    {
        const string apiKey = "network-exception-plain-secret";
        using var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException($"Proxy echoed Authorization: Bearer {apiKey}"));
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            "providers/deepseek",
            apiKey.AsMemory(),
            TestContext.Current.CancellationToken);
        var service = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "deepseek",
                "DeepSeek",
                ModelProviderKind.OpenAiCompatible,
                new Uri("https://api.deepseek.com"),
                "deepseek-v4-pro",
                "providers/deepseek",
                ModelProviderPresets.DeepSeekId),
            secrets);

        var exception = await Assert.ThrowsAsync<ModelServiceException>(() =>
            service.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Contains("network request failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task AnthropicFailureAlsoRedactsResolvedCredential()
    {
        const string apiKey = "anthropic-plain-secret";
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"Rejected " + apiKey + "\"}}",
                    Encoding.UTF8,
                    "application/json")
            }));
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            "providers/anthropic",
            apiKey.AsMemory(),
            TestContext.Current.CancellationToken);
        var service = new AnthropicModelService(
            client,
            new ModelSource(
                "anthropic",
                "Anthropic",
                ModelProviderKind.Anthropic,
                new Uri("https://api.anthropic.test"),
                "claude-test",
                "providers/anthropic"),
            secrets);

        var exception = await Assert.ThrowsAsync<ModelServiceException>(() =>
            service.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Contains("Rejected [REDACTED]", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, exception.ResponseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderErrorWithoutMessageDoesNotSurfaceCredentialFields()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"error\":{\"api_key\":\"server-echoed-secret\"}}",
                    Encoding.UTF8,
                    "application/json")
            }));
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync(
            "providers/cloud",
            "current-secret".AsMemory(),
            TestContext.Current.CancellationToken);
        var service = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "cloud",
                "Cloud",
                ModelProviderKind.OpenAiCompatible,
                new Uri("https://models.example/v1"),
                "demo",
                "providers/cloud"),
            secrets);

        var exception = await Assert.ThrowsAsync<ModelServiceException>(() =>
            service.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal("The provider returned a JSON error response without a message.", exception.ResponseBody);
        Assert.DoesNotContain("server-echoed-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("server-echoed-secret", exception.ResponseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossOriginRedirectResponseIsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://unexpected.example/chat/completions"),
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"must not be accepted"}}]}""",
                    Encoding.UTF8,
                    "application/json")
            }));
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        await secrets.SetAsync("providers/cloud", "secret".AsMemory(), cancellationToken);
        var service = new OpenAiCompatibleModelService(
            client,
            new ModelSource(
                "cloud",
                "Cloud",
                ModelProviderKind.OpenAiCompatible,
                new Uri("https://models.example/v1"),
                "demo",
                "providers/cloud"),
            secrets);

        var exception = await Assert.ThrowsAsync<ModelServiceException>(
            () => service.CompleteAsync(CreateRequest(), cancellationToken));

        Assert.Contains("different origin", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedSuccessfulResponseIsRejectedBeforeParsing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            var content = new ByteArrayContent(new byte[16 * 1024 * 1024 + 1]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = new HttpClient(handler);
        using var secrets = new InMemorySecretStore();
        var service = new OllamaModelService(
            client,
            new ModelSource(
                "ollama",
                "Local Ollama",
                ModelProviderKind.Ollama,
                new Uri("http://127.0.0.1:11434"),
                "llama3"),
            secrets);

        var exception = await Assert.ThrowsAsync<ModelServiceException>(
            () => service.CompleteAsync(CreateRequest(), cancellationToken));

        Assert.Contains("larger than", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialEndpointRequiresHttpsOutsideLoopback()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ModelSource(
            "cloud",
            "Cloud",
            ModelProviderKind.OpenAiCompatible,
            new Uri("http://models.example/v1"),
            "demo",
            "providers/cloud"));

        Assert.Equal("endpoint", exception.ParamName);
    }

    private static ModelRequest CreateRequest() => new(
        [
            new ModelMessage(ModelMessageRole.System, "system prompt"),
            new ModelMessage(ModelMessageRole.User, "translate me")
        ],
        temperature: 0.2,
        maxTokens: 128);

    private static ModelRequest CreateToolRequest(bool includePriorToolResult = false)
    {
        using var schema = JsonDocument.Parse("""{"type":"object","additionalProperties":false}""");
        var messages = new List<ModelMessage>
        {
            new(ModelMessageRole.User, "Read safe system context.")
        };
        if (includePriorToolResult)
        {
            messages.Add(new ModelMessage(
                ModelMessageRole.Tool,
                "safe context",
                toolCallId: "previous-call",
                toolName: "system_context"));
        }

        return new ModelRequest(
            messages,
            maxTokens: 128,
            tools: [new ModelToolDefinition("system_context", "Read safe system context.", schema.RootElement)]);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}

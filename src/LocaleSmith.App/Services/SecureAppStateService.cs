using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LocaleSmith.Core.Abstractions;
using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;
using LocaleSmith.Infrastructure.Models;
using LocaleSmith.Presentation.Abstractions;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Services;

public sealed partial class SecureAppStateService :
    IAppConfigurationService,
    IOnboardingService,
    IModelSourceCatalog,
    IModelSelectionService,
    IModelSelectionStateNotifier,
    IDisposable
{
    private readonly IConfigurationStore<AppConfiguration> _configurationStore;
    private readonly ISecretStore _secretStore;
    private readonly ModelServiceRegistry _registry;
    private readonly HttpClient _modelHttpClient;
    private readonly ICliSandboxRootManager _sandboxRootManager;
    private readonly IAppLanguagePreferenceWriter? _languagePreferenceWriter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppConfiguration _configuration = new();
    private bool _initialized;
    private bool _disposed;

    public SecureAppStateService(
        IConfigurationStore<AppConfiguration> configurationStore,
        ISecretStore secretStore,
        ModelServiceRegistry registry,
        HttpClient modelHttpClient,
        ICliSandboxRootManager sandboxRootManager,
        IAppLanguagePreferenceWriter? languagePreferenceWriter = null)
    {
        _configurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _modelHttpClient = modelHttpClient ?? throw new ArgumentNullException(nameof(modelHttpClient));
        _sandboxRootManager = sandboxRootManager ?? throw new ArgumentNullException(nameof(sandboxRootManager));
        _languagePreferenceWriter = languagePreferenceWriter;
    }

    public IReadOnlyList<ModelSource> Sources => _registry.Sources;

    public ModelSource? SelectedSource => _registry.SelectedSource;

    public event EventHandler<ModelSelectionStateChangedEventArgs>? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ModelSelectionStateChangedEventArgs? selectionStateChanged = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var loaded = await _configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false)
                ?? new AppConfiguration();
            var normalized = LegacyDefaultPathNormalizer.Normalize(loaded, out var defaultsChanged);
            ValidateConfiguration(normalized);
            if (defaultsChanged)
            {
                await _configurationStore.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
            }

            _configuration = normalized;
            ApplySandboxRoots(_configuration);
            selectionStateChanged = ReconcileRegistry(_configuration);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
            PublishModelSelectionState(selectionStateChanged);
        }
    }

    public async Task<AppConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _configuration with { ModelSources = _configuration.ModelSources.ToArray() };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        AppConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var normalized = NormalizeConfiguration(configuration);
        ValidateConfiguration(normalized);
        if (!string.IsNullOrWhiteSpace(normalized.SandboxPath))
        {
            Directory.CreateDirectory(normalized.SandboxPath);
        }
        if (!string.IsNullOrWhiteSpace(normalized.LogDirectoryPath))
        {
            Directory.CreateDirectory(normalized.LogDirectoryPath);
        }
        ModelSelectionStateChangedEventArgs? selectionStateChanged = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _configurationStore.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
            _configuration = normalized with { ModelSources = normalized.ModelSources.ToArray() };
            ApplySandboxRoots(_configuration);
            selectionStateChanged = ReconcileRegistry(_configuration);
            _languagePreferenceWriter?.Save(_configuration.Language);
        }
        finally
        {
            _gate.Release();
            PublishModelSelectionState(selectionStateChanged);
        }
    }

    public async Task SaveSettingsAsync(
        AppSettingsUpdate settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var workspacePath = EnsureUserDirectoryAllowed(settings.WorkspacePath, nameof(settings));
        var sandboxPath = EnsureUserDirectoryAllowed(settings.SandboxPath, nameof(settings));
        Directory.CreateDirectory(sandboxPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var logDirectoryPath = EnsureLogDirectoryAllowed(
                settings.LogDirectoryPath ?? _configuration.LogDirectoryPath,
                nameof(settings));
            Directory.CreateDirectory(logDirectoryPath);
            var updated = _configuration with
            {
                Language = settings.Language,
                Theme = settings.Theme,
                ForceAppAnimations = settings.ForceAppAnimations,
                WorkspacePath = workspacePath,
                SandboxPath = sandboxPath,
                LogDirectoryPath = logDirectoryPath
            };
            ValidateConfiguration(updated);
            await _configurationStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _configuration = updated with { ModelSources = updated.ModelSources.ToArray() };
            ApplySandboxRoots(_configuration);
            _languagePreferenceWriter?.Save(_configuration.Language);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteAsync(
        OnboardingSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var hasNetworkConfiguration =
            !string.IsNullOrWhiteSpace(submission.NetworkPresetId) ||
            submission.NetworkEndpoint is not null ||
            !string.IsNullOrWhiteSpace(submission.NetworkModelName) ||
            !submission.NetworkApiKey.IsEmpty ||
            submission.NetworkTokenLimitParameter is not null;
        if (submission.ConfigureOllama && hasNetworkConfiguration)
        {
            throw new ArgumentException(
                "Choose either local Ollama or one network model source during onboarding.",
                nameof(submission));
        }

        ModelProviderPreset? networkPreset = null;
        OpenAiTokenLimitParameter? networkTokenLimitParameter = null;
        if (hasNetworkConfiguration)
        {
            if (!ModelProviderPresets.TryGet(submission.NetworkPresetId, out networkPreset) ||
                networkPreset.Protocol != ModelProviderKind.OpenAiCompatible)
            {
                throw new ArgumentException("Choose a supported network provider preset.", nameof(submission));
            }

            if (submission.NetworkEndpoint is null ||
                string.IsNullOrWhiteSpace(submission.NetworkModelName) ||
                submission.NetworkApiKey.IsEmpty)
            {
                throw new ArgumentException(
                    "The network endpoint, model name, and API key are required.",
                    nameof(submission));
            }

            networkTokenLimitParameter = NormalizeTokenLimitParameter(
                networkPreset.Protocol,
                networkPreset.Id,
                submission.NetworkTokenLimitParameter);
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var workspace = EnsureUserDirectoryAllowed(submission.WorkspacePath, nameof(submission));
        var sandbox = EnsureUserDirectoryAllowed(submission.SandboxPath, nameof(submission));
        var logDirectory = EnsureLogDirectoryAllowed(
            submission.LogDirectoryPath ?? AppConfiguration.GetDefaultLogDirectoryPath(),
            nameof(submission));
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(sandbox);
        Directory.CreateDirectory(logDirectory);

        ModelSelectionStateChangedEventArgs? selectionStateChanged = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        char[]? previousNetworkSecret = null;
        string? networkCredentialReference = null;
        var networkCredentialChangeAttempted = false;
        try
        {
            var sources = _configuration.ModelSources.ToList();
            string? selectedId = _configuration.SelectedModelSourceId;
            if (submission.ConfigureOllama)
            {
                const string localId = "ollama-local";
                var local = new ModelSourceProfile
                {
                    Id = localId,
                    DisplayName = "Local Ollama",
                    Provider = ModelProviderKind.Ollama,
                    Endpoint = submission.OllamaEndpoint.AbsoluteUri,
                    ModelName = submission.OllamaModelName.Trim()
                };
                sources.RemoveAll(static source => source.Id == localId);
                sources.Add(local);
                selectedId ??= localId;
            }

            if (networkPreset is not null)
            {
                var networkId = $"preset-{networkPreset.Id}";
                var existing = sources.FirstOrDefault(source => source.Id == networkId);
                networkCredentialReference = existing?.CredentialReference ??
                    $"model-sources/{networkId}/api-key";
                var network = new ModelSourceProfile
                {
                    Id = networkId,
                    DisplayName = networkPreset.DisplayName,
                    Provider = networkPreset.Protocol,
                    PresetId = networkPreset.Id,
                    TokenLimitParameter = networkTokenLimitParameter,
                    Endpoint = submission.NetworkEndpoint!.AbsoluteUri,
                    ModelName = submission.NetworkModelName!.Trim(),
                    CredentialReference = networkCredentialReference,
                    CredentialFingerprint = ComputeCredentialFingerprint(submission.NetworkApiKey.Span)
                };
                _ = CreateModelService(network, _secretStore);
                sources.RemoveAll(source => source.Id == networkId);
                sources.Add(network);
                selectedId ??= networkId;
            }

            var updated = _configuration with
            {
                IsOnboardingComplete = true,
                WorkspacePath = workspace,
                SandboxPath = sandbox,
                LogDirectoryPath = logDirectory,
                SelectedModelSourceId = selectedId,
                ModelSources = sources.ToArray()
            };
            ValidateConfiguration(updated);
            try
            {
                if (networkCredentialReference is not null)
                {
                    using var previous = await _secretStore
                        .ResolveAsync(networkCredentialReference, cancellationToken)
                        .ConfigureAwait(false);
                    previousNetworkSecret = previous is null ? null : CopySecret(previous);
                    networkCredentialChangeAttempted = true;
                    await _secretStore
                        .SetAsync(networkCredentialReference, submission.NetworkApiKey, cancellationToken)
                        .ConfigureAwait(false);
                }

                // This call creates the Credential Manager master key first, then atomically writes AES-256-GCM data.
                await _configurationStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception transactionException)
            {
                if (networkCredentialChangeAttempted && networkCredentialReference is not null)
                {
                    try
                    {
                        if (previousNetworkSecret is null)
                        {
                            await _secretStore
                                .DeleteAsync(networkCredentialReference, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await _secretStore
                                .SetAsync(networkCredentialReference, previousNetworkSecret, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception compensationException)
                    {
                        throw CreateTransactionAggregateException(
                            transactionException,
                            [CreateCredentialCompensationException(
                                networkCredentialReference,
                                "restore the onboarding credential",
                                compensationException)]);
                    }
                }

                throw;
            }

            _configuration = updated;
            ApplySandboxRoots(updated);
            selectionStateChanged = ReconcileRegistry(updated);
        }
        finally
        {
            if (previousNetworkSecret is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(previousNetworkSecret.AsSpan()));
            }

            _gate.Release();
            PublishModelSelectionState(selectionStateChanged);
        }
    }

    public async Task<IReadOnlyList<ModelSourceProfile>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var configuration = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return configuration.ModelSources
            .OrderBy(static source => source.DisplayName, StringComparer.CurrentCulture)
            .ToArray();
    }

    public async Task<ModelSourceProfile> SaveAsync(
        ModelSourceDraft source,
        ReadOnlyMemory<char> apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        ModelSelectionStateChangedEventArgs? selectionStateChanged = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        char[]? previousSecret = null;
        char[]? removedCredentialSecret = null;
        string? changedCredentialReference = null;
        string? removedCredentialReference = null;
        var credentialChangeAttempted = false;
        var credentialRemovalAttempted = false;
        try
        {
            ModelSourceProfile profile;
            AppConfiguration updated;
            try
            {
                var id = source.Id ?? $"model-{Guid.NewGuid():N}";
                if (!IdentifierPattern().IsMatch(id))
                {
                    throw new ArgumentException("The model source identifier is invalid.", nameof(source));
                }

                var normalizedPresetId = NormalizePresetId(source.Provider, source.PresetId);
                var normalizedTokenLimitParameter = NormalizeTokenLimitParameter(
                    source.Provider,
                    normalizedPresetId,
                    source.TokenLimitParameter);

                var existing = _configuration.ModelSources.FirstOrDefault(item => item.Id == id);
                var credentialReference = source.Provider == ModelProviderKind.Ollama
                    ? null
                    : existing?.CredentialReference ?? $"model-sources/{id}/api-key";
                if (source.Provider == ModelProviderKind.Ollama &&
                    existing?.CredentialReference is { } obsoleteReference)
                {
                    using var obsolete = await _secretStore
                        .ResolveAsync(obsoleteReference, cancellationToken)
                        .ConfigureAwait(false);
                    removedCredentialSecret = obsolete is null ? null : CopySecret(obsolete);
                    removedCredentialReference = obsoleteReference;
                    credentialRemovalAttempted = true;
                    var removed = await _secretStore
                        .DeleteAsync(obsoleteReference, cancellationToken)
                        .ConfigureAwait(false);
                    if (removedCredentialSecret is not null && !removed)
                    {
                        throw new InvalidOperationException(
                            "Credential deletion did not remove the value that was read for this model source.");
                    }
                }

                if (credentialReference is not null && !apiKey.IsEmpty)
                {
                    using var oldValue = await _secretStore
                        .ResolveAsync(credentialReference, cancellationToken)
                        .ConfigureAwait(false);
                    previousSecret = oldValue is null
                        ? null
                        : CopySecret(oldValue);
                    changedCredentialReference = credentialReference;
                    credentialChangeAttempted = true;
                    await _secretStore.SetAsync(credentialReference, apiKey, cancellationToken).ConfigureAwait(false);
                }
                else if (source.Provider != ModelProviderKind.Ollama && credentialReference is not null)
                {
                    using var existingSecret = await _secretStore
                        .ResolveAsync(credentialReference, cancellationToken)
                        .ConfigureAwait(false);
                    if (existingSecret is null)
                    {
                        throw new InvalidOperationException("An API key is required for this provider.");
                    }
                }

                profile = new ModelSourceProfile
                {
                    Id = id,
                    DisplayName = source.DisplayName,
                    Provider = source.Provider,
                    PresetId = normalizedPresetId,
                    TokenLimitParameter = normalizedTokenLimitParameter,
                    Endpoint = source.Endpoint.AbsoluteUri,
                    ModelName = source.ModelName,
                    CredentialReference = credentialReference,
                    CredentialFingerprint = source.Provider == ModelProviderKind.Ollama
                    ? null
                    : apiKey.IsEmpty
                        ? existing?.CredentialFingerprint
                        : ComputeCredentialFingerprint(apiKey.Span)
                };
                _ = CreateModelService(profile, _secretStore);

                var profiles = _configuration.ModelSources.Where(item => item.Id != id).Append(profile).ToArray();
                updated = _configuration with
                {
                    ModelSources = profiles,
                    SelectedModelSourceId = _configuration.SelectedModelSourceId ?? id
                };
                await _configurationStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception transactionException)
            {
                var compensationFailures = new List<Exception>();
                if (credentialChangeAttempted && changedCredentialReference is not null)
                {
                    try
                    {
                        if (previousSecret is not null)
                        {
                            await _secretStore.SetAsync(
                                changedCredentialReference,
                                previousSecret,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        else
                        {
                            await _secretStore.DeleteAsync(
                                changedCredentialReference,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception compensationException)
                    {
                        compensationFailures.Add(CreateCredentialCompensationException(
                            changedCredentialReference,
                            "restore the replaced credential",
                            compensationException));
                    }
                }

                if (credentialRemovalAttempted &&
                    removedCredentialReference is not null &&
                    removedCredentialSecret is not null)
                {
                    try
                    {
                        await _secretStore.SetAsync(
                            removedCredentialReference,
                            removedCredentialSecret,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception compensationException)
                    {
                        compensationFailures.Add(CreateCredentialCompensationException(
                            removedCredentialReference,
                            "restore the removed credential",
                            compensationException));
                    }
                }

                if (compensationFailures.Count != 0)
                {
                    throw CreateTransactionAggregateException(transactionException, compensationFailures);
                }

                throw;
            }

            _configuration = updated;
            try
            {
                selectionStateChanged = ReconcileRegistry(updated);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "The model source configuration and credential changes were committed, but the in-memory " +
                    "model registry could not be reconciled. Reload application state before continuing.",
                    exception);
            }

            return profile;
        }
        finally
        {
            if (previousSecret is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(previousSecret.AsSpan()));
            }

            if (removedCredentialSecret is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(removedCredentialSecret.AsSpan()));
            }

            _gate.Release();
            PublishModelSelectionState(selectionStateChanged);
        }
    }

    public async Task<bool> DeleteAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        ModelSelectionStateChangedEventArgs? selectionStateChanged = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        char[]? removedCredentialSecret = null;
        string? removedCredentialReference = null;
        var credentialRemovalAttempted = false;
        try
        {
            var profile = _configuration.ModelSources.FirstOrDefault(source => source.Id == sourceId);
            if (profile is null)
            {
                return false;
            }

            var remaining = _configuration.ModelSources.Where(source => source.Id != sourceId).ToArray();
            var selectedId = _configuration.SelectedModelSourceId == sourceId
                ? remaining.OrderBy(static source => source.DisplayName, StringComparer.CurrentCulture).FirstOrDefault()?.Id
                : _configuration.SelectedModelSourceId;
            var updated = _configuration with
            {
                ModelSources = remaining,
                SelectedModelSourceId = selectedId
            };
            if (profile.CredentialReference is not null)
            {
                removedCredentialReference = profile.CredentialReference;
                using var existingSecret = await _secretStore
                    .ResolveAsync(removedCredentialReference, cancellationToken)
                    .ConfigureAwait(false);
                removedCredentialSecret = existingSecret is null ? null : CopySecret(existingSecret);
            }

            try
            {
                if (removedCredentialReference is not null)
                {
                    credentialRemovalAttempted = true;
                    var removed = await _secretStore
                        .DeleteAsync(removedCredentialReference, cancellationToken)
                        .ConfigureAwait(false);
                    if (removedCredentialSecret is not null && !removed)
                    {
                        throw new InvalidOperationException(
                            "Credential deletion did not remove the value that was read for this model source.");
                    }
                }

                await _configurationStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception transactionException)
            {
                if (credentialRemovalAttempted &&
                    removedCredentialReference is not null &&
                    removedCredentialSecret is not null)
                {
                    try
                    {
                        await _secretStore.SetAsync(
                            removedCredentialReference,
                            removedCredentialSecret,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception compensationException)
                    {
                        throw CreateTransactionAggregateException(
                            transactionException,
                            [CreateCredentialCompensationException(
                                removedCredentialReference,
                                "restore the deleted credential",
                                compensationException)]);
                    }
                }

                throw;
            }

            _configuration = updated;
            try
            {
                selectionStateChanged = ReconcileRegistry(updated);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "The model source deletion and credential removal were committed, but the in-memory model " +
                    "registry could not be reconciled. Reload application state before continuing.",
                    exception);
            }

            return true;
        }
        finally
        {
            if (removedCredentialSecret is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(removedCredentialSecret.AsSpan()));
            }

            _gate.Release();
            PublishModelSelectionState(selectionStateChanged);
        }
    }

    public async Task<ModelConnectionResult> TestConnectionAsync(
        ModelSourceDraft source,
        ReadOnlyMemory<char> apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var profile = new ModelSourceProfile
        {
            Id = source.Id ?? $"connection-test-{Guid.NewGuid():N}",
            DisplayName = source.DisplayName,
            Provider = source.Provider,
            PresetId = NormalizePresetId(source.Provider, source.PresetId),
            TokenLimitParameter = NormalizeTokenLimitParameter(
                source.Provider,
                source.PresetId,
                source.TokenLimitParameter),
            Endpoint = source.Endpoint.AbsoluteUri,
            ModelName = source.ModelName,
            CredentialReference = source.Provider == ModelProviderKind.Ollama
                ? null
                : apiKey.IsEmpty ? source.CredentialReference : "connection-test/key"
        };

        using var ephemeral = apiKey.IsEmpty ? null : new EphemeralSecretResolver(apiKey.Span);
        var resolver = (ISecretResolver?)ephemeral ?? _secretStore;
        try
        {
            var service = CreateModelService(profile, resolver);
            var response = await service.CompleteAsync(
                new ModelRequest(
                    [new ModelMessage(ModelMessageRole.User, "Reply only with OK.")],
                    maxTokens: 64),
                cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(response.Content)
                ? ModelConnectionResult.Failure("The service responded without text. Check the model name.")
                : ModelConnectionResult.Success("Connection succeeded and the model returned a response.");
        }
        catch (ModelServiceException exception)
        {
            return ModelConnectionResult.Failure(exception.Message);
        }
        catch (HttpRequestException exception)
        {
            return ModelConnectionResult.Failure(
                $"Network request failed ({exception.HttpRequestError}): {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ModelConnectionResult.Failure("The model request timed out.");
        }
        catch (InvalidOperationException exception)
        {
            return ModelConnectionResult.Failure(exception.Message);
        }
    }

    public async Task<IReadOnlyList<AvailableModelInfo>> ListAvailableModelsAsync(
        ModelSourceDraft source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Provider != ModelProviderKind.Ollama)
        {
            return [];
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var profile = new ModelSourceProfile
        {
            Id = source.Id ?? $"catalog-{Guid.NewGuid():N}",
            DisplayName = source.DisplayName,
            Provider = ModelProviderKind.Ollama,
            Endpoint = source.Endpoint.AbsoluteUri,
            ModelName = source.ModelName
        };
        var service = CreateModelService(profile, _secretStore);
        if (service is not IModelCatalogService catalog)
        {
            return [];
        }

        return await catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SelectSourceAsync(
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        ModelSelectionStateChangedEventArgs? selectionStateChanged = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousId = _registry.SelectedSource?.Id;
            if (!_registry.SelectSource(sourceId))
            {
                return false;
            }

            var updated = _configuration with { SelectedModelSourceId = sourceId };
            try
            {
                await _configurationStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                _configuration = updated;
                selectionStateChanged = CaptureModelSelectionState();
                return true;
            }
            catch
            {
                if (previousId is not null)
                {
                    _registry.SelectSource(previousId);
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
            PublishModelSelectionState(selectionStateChanged);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Dispose();
        _disposed = true;
    }

    private ModelSelectionStateChangedEventArgs ReconcileRegistry(AppConfiguration configuration)
    {
        var expectedIds = configuration.ModelSources.Select(static source => source.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var source in _registry.Sources.Where(source => !expectedIds.Contains(source.Id)).ToArray())
        {
            _registry.Remove(source.Id);
        }

        foreach (var profile in configuration.ModelSources)
        {
            _registry.AddOrUpdate(CreateModelService(profile, _secretStore));
        }

        if (configuration.SelectedModelSourceId is not null)
        {
            _registry.SelectSource(configuration.SelectedModelSourceId);
        }

        return CaptureModelSelectionState();
    }

    private ModelSelectionStateChangedEventArgs CaptureModelSelectionState() => new(
        _registry.Sources,
        _registry.SelectedSource);

    private void PublishModelSelectionState(ModelSelectionStateChangedEventArgs? state)
    {
        var handlers = StateChanged;
        if (state is null || handlers is null)
        {
            return;
        }

        foreach (EventHandler<ModelSelectionStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, state);
            }
            catch
            {
                // A presentation subscriber must not fail an already-committed configuration transaction.
            }
        }
    }

    private void ApplySandboxRoots(AppConfiguration configuration)
    {
        var roots = string.IsNullOrWhiteSpace(configuration.SandboxPath)
            ? Array.Empty<string>()
            : new[] { Path.GetFullPath(configuration.SandboxPath) };
        _sandboxRootManager.ReplaceSandboxRoots(roots);
    }

    private IModelService CreateModelService(ModelSourceProfile profile, ISecretResolver secretResolver)
    {
        var source = new ModelSource(
            profile.Id,
            profile.DisplayName,
            profile.Provider,
            new Uri(profile.Endpoint, UriKind.Absolute),
            profile.ModelName,
            profile.CredentialReference,
            profile.PresetId,
            profile.TokenLimitParameter);
        return profile.Provider switch
        {
            ModelProviderKind.Ollama => new OllamaModelService(_modelHttpClient, source, secretResolver),
            ModelProviderKind.OpenAiCompatible => new OpenAiCompatibleModelService(_modelHttpClient, source, secretResolver),
            ModelProviderKind.Anthropic => new AnthropicModelService(_modelHttpClient, source, secretResolver),
            _ => throw new NotSupportedException($"Model provider '{profile.Provider}' is not supported.")
        };
    }

    private static void ValidateConfiguration(AppConfiguration configuration)
    {
        if (configuration.SchemaVersion != AppConfiguration.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported settings schema {configuration.SchemaVersion}.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in configuration.ModelSources)
        {
            if (!IdentifierPattern().IsMatch(profile.Id) || !ids.Add(profile.Id))
            {
                throw new InvalidDataException("Model source identifiers must be valid and unique.");
            }

            _ = new ModelSource(
                profile.Id,
                profile.DisplayName,
                profile.Provider,
                new Uri(profile.Endpoint, UriKind.Absolute),
                profile.ModelName,
                profile.CredentialReference,
                profile.PresetId,
                profile.TokenLimitParameter);
        }

        if (configuration.SelectedModelSourceId is not null && !ids.Contains(configuration.SelectedModelSourceId))
        {
            throw new InvalidDataException("The selected model source does not exist.");
        }

        if (configuration.IsOnboardingComplete)
        {
            if (string.IsNullOrWhiteSpace(configuration.WorkspacePath) ||
                string.IsNullOrWhiteSpace(configuration.SandboxPath) ||
                string.IsNullOrWhiteSpace(configuration.LogDirectoryPath))
            {
                throw new InvalidDataException(
                    "Completed settings require workspace, sandbox, and log directory paths.");
            }

            _ = EnsureUserDirectoryAllowed(configuration.WorkspacePath, nameof(configuration));
            _ = EnsureUserDirectoryAllowed(configuration.SandboxPath, nameof(configuration));
            _ = EnsureLogDirectoryAllowed(configuration.LogDirectoryPath, nameof(configuration));
        }
    }

    private static AppConfiguration NormalizeConfiguration(AppConfiguration configuration) => configuration with
    {
        WorkspacePath = string.IsNullOrWhiteSpace(configuration.WorkspacePath)
            ? string.Empty
            : EnsureUserDirectoryAllowed(configuration.WorkspacePath, nameof(configuration)),
        SandboxPath = string.IsNullOrWhiteSpace(configuration.SandboxPath)
            ? string.Empty
            : EnsureUserDirectoryAllowed(configuration.SandboxPath, nameof(configuration)),
        LogDirectoryPath = string.IsNullOrWhiteSpace(configuration.LogDirectoryPath)
            ? AppConfiguration.GetDefaultLogDirectoryPath()
            : EnsureLogDirectoryAllowed(configuration.LogDirectoryPath, nameof(configuration)),
        ModelSources = configuration.ModelSources.ToArray()
    };

    private static string NormalizePresetId(ModelProviderKind provider, string? presetId) =>
        provider == ModelProviderKind.OpenAiCompatible
            ? ModelProviderPresets.ResolveOrCustom(presetId).Id
            : ModelProviderPresets.CustomId;

    private static OpenAiTokenLimitParameter? NormalizeTokenLimitParameter(
        ModelProviderKind provider,
        string? presetId,
        OpenAiTokenLimitParameter? parameter)
    {
        if (parameter is { } explicitParameter && !Enum.IsDefined(explicitParameter))
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameter),
                parameter,
                "Unknown OpenAI-compatible token-limit parameter.");
        }

        return provider == ModelProviderKind.OpenAiCompatible
            ? parameter ?? ModelProviderPresets.ResolveOrCustom(presetId).DefaultTokenLimitParameter
            : null;
    }

    private static string EnsureUserDirectoryAllowed(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("The path does not have a filesystem root.", parameterName);
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A drive root cannot be used as an application data directory.", parameterName);
        }

        if (File.Exists(fullPath))
        {
            throw new ArgumentException("Application data paths must be directories.", parameterName);
        }

        var protectedRoots = new[]
        {
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86)
        }.Where(static root => !string.IsNullOrWhiteSpace(root));
        if (protectedRoots.Any(root => IsWithin(fullPath, root)))
        {
            throw new ArgumentException("Workspace and sandbox paths cannot be protected system directories.", parameterName);
        }

        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    "Workspace and sandbox paths cannot traverse a symbolic link or junction.",
                    parameterName);
            }
        }

        return fullPath;
    }

    private static string EnsureLogDirectoryAllowed(string path, string parameterName) =>
        AppConfiguration.NormalizeLogDirectoryPath(
            EnsureUserDirectoryAllowed(path, parameterName));

    private static bool IsWithin(string candidate, string root)
    {
        candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static char[] CopySecret(SecretValue value)
    {
        var characters = new char[value.Length];
        value.CopyTo(characters);
        return characters;
    }

    private static InvalidOperationException CreateCredentialCompensationException(
        string credentialReference,
        string operation,
        Exception innerException) => new(
            $"Failed to {operation} for credential reference '{credentialReference}'. No credential value was logged.",
            innerException);

    private static AggregateException CreateTransactionAggregateException(
        Exception transactionException,
        IEnumerable<Exception> compensationFailures) => new(
            "The model source transaction failed before configuration commit, and credential compensation also " +
            "failed. Persisted configuration and the in-memory registry remain unchanged; credential state " +
            "requires repair.",
            new[] { transactionException }.Concat(compensationFailures));

    private static string ComputeCredentialFingerprint(ReadOnlySpan<char> secret)
    {
        var utf8 = new byte[Encoding.UTF8.GetByteCount(secret)];
        var digest = new byte[SHA256.HashSizeInBytes];
        try
        {
            Encoding.UTF8.GetBytes(secret, utf8);
            SHA256.HashData(utf8, digest);
            return $"sha256:…{Convert.ToHexString(digest.AsSpan(^4)).ToLowerInvariant()}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IdentifierPattern();

    private sealed class EphemeralSecretResolver : ISecretResolver, IDisposable
    {
        private char[]? _characters;

        public EphemeralSecretResolver(ReadOnlySpan<char> secret) => _characters = secret.ToArray();

        public ValueTask<SecretValue?> ResolveAsync(
            string reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_characters is null, this);
            return ValueTask.FromResult<SecretValue?>(new SecretValue(_characters));
        }

        public void Dispose()
        {
            var characters = Interlocked.Exchange(ref _characters, null);
            if (characters is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
            }
        }
    }
}

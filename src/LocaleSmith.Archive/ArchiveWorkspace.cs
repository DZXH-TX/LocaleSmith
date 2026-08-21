using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LocaleSmith.Application.Abstractions;
using LocaleSmith.Application.Models;
using LocaleSmith.Archive.ClassFile;
using LocaleSmith.Core.Models;
using LocaleSmith.Core.Services;
using LocaleSmith.NativeInterop;

namespace LocaleSmith.Archive;

internal sealed class ArchiveWorkspace : IArchiveWorkspace
{
    private const int MaximumTextResourceBytes = 16 * 1024 * 1024;
    private const int MaximumClassFileBytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly Guid _jobId;
    private readonly PipelineRequest _request;
    private readonly string _sourcePath;
    private readonly string _workspacePath;
    private readonly string _workspacesRoot;
    private readonly string _extractionPath;
    private readonly IArchiveScanner _scanner;
    private readonly ArchiveWorkspaceOptions _options;
    private readonly TransactionJournal _journal;
    private readonly bool _folderSource;
    private readonly List<string> _warnings = new();
    private readonly ReadOnlyCollection<string> _warningsView;
    private readonly List<ArchiveEntrySnapshot> _entries = new();
    private readonly Dictionary<string, SourceEntryDescriptor> _sourceEntries =
        new(StringComparer.Ordinal);
    private readonly Dictionary<TranslationStyle, Dictionary<string, byte[]>> _overrides = new();
    private readonly Dictionary<string, byte[]> _externalizationOverrides =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ExternalizedSourceDescriptor> _externalizedSources = new();
    private readonly Dictionary<TranslationStyle, Dictionary<string, string>> _expectedExternalizedTargetValues = new();
    private readonly Dictionary<string, IReadOnlyList<ClassFileRewriteSelection>> _appliedClassRewrites =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StagedArtifact> _stagedArtifacts = new();
    private readonly Dictionary<string, bool> _pendingPublishedArtifacts = new(StringComparer.OrdinalIgnoreCase);
    private FileStream? _sourceLock;
    private ArchiveScanManifest? _manifest;
    private ArchiveInspection? _inspection;
    private IReadOnlyList<TranslationEntry>? _translationEntries;
    private IReadOnlyList<HardcodedStringCandidate>? _hardcodedCandidates;
    private PackageVerification? _verification;
    private byte[] _archiveComment = Array.Empty<byte>();
    private bool _extracted;
    private bool _translationsApplied;
    private bool _committed;
    private bool _rolledBack;
    private bool _workspaceVerified;
    private bool _externalizationCompleted;
    private bool _disposed;
    private DirectoryMutationGuard? _workspaceGuard;

    public ArchiveWorkspace(
        Guid jobId,
        PipelineRequest request,
        string sourcePath,
        FileStream sourceLock,
        string workspacePath,
        string workspacesRoot,
        IArchiveScanner scanner,
        ArchiveWorkspaceOptions options,
        TransactionJournal journal,
        DirectoryMutationGuard workspaceGuard,
        bool folderSource = false)
    {
        _jobId = jobId;
        _request = request;
        _sourcePath = sourcePath;
        _sourceLock = sourceLock;
        _workspacePath = workspacePath;
        _workspacesRoot = workspacesRoot;
        _extractionPath = Path.Combine(workspacePath, "extracted");
        _scanner = scanner;
        _options = options;
        _journal = journal;
        _workspaceGuard = workspaceGuard;
        _folderSource = folderSource;
        _warningsView = _warnings.AsReadOnly();
        _workspaceVerified = VerifyWorkspaceRoot();
    }

    public Task<ArchiveInspection> InspectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_inspection is not null)
        {
            return Task.FromResult(_inspection);
        }

        try
        {
            _manifest = _scanner.ScanArchive(_sourcePath);
            MinecraftContentKind contentKind = ClassifyContentKind(_manifest);
            NativeSignatureEvidence signatures = _manifest.Archive.Signatures;
            ArchiveSignatureState signatureState = signatures.Status switch
            {
                "none" => ArchiveSignatureState.None,
                "present_unverified" or "incomplete_unverified" => ArchiveSignatureState.PresentUnverified,
                _ => ArchiveSignatureState.PresentUnverified
            };
            _warnings.AddRange(_manifest.Warnings);
            if (signatureState != ArchiveSignatureState.None &&
                _request.SignedArchiveHandling == SignedArchiveHandling.CreateUnsignedCopy)
            {
                AddWarning(
                    "The output will be an unsigned copy: JAR signature files and blocks will be removed because repacking invalidates them.");
            }

            if (_manifest.Archive.Entries.Any(static entry => entry.Comment is not null))
            {
                AddWarning("ZIP per-entry comments cannot be reproduced by the managed repacker and will be omitted.");
            }

            _inspection = new ArchiveInspection(
                CreatePackageIdentity(_manifest.ModMetadata),
                _manifest.ModMetadata.PrimaryModId,
                _manifest.ModMetadata.PrimaryLoader ?? "unknown",
                _manifest.ModMetadata.UsedFilenameFallback,
                signatureState,
                CanResign: false,
                _warningsView,
                contentKind);
            _journal.Write(
                "inspect",
                "ok",
                $"content={contentKind}; loader={_inspection.Loader}; modId={_inspection.ModId}; " +
                $"signature={signatureState}");
            return Task.FromResult(_inspection);
        }
        catch (Exception exception)
        {
            _journal.Write("inspect", "failed", exception.Message);
            throw;
        }
    }

    public async Task ExtractAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInspected();
        if (_extracted)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_extractionPath);
            using var extractionGuard = DirectoryMutationGuard.OpenDirectoryForMutation(_extractionPath);
            ArchivePathSafety.EnsureChildPath(_workspacePath, _extractionPath);
            RejectReparsePoint(_extractionPath);

            await using var input = new FileStream(
                _sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _archiveComment = await ReadArchiveCommentAsync(input, cancellationToken).ConfigureAwait(false);
            input.Position = 0;
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
            ValidateEntryInventory(archive);

            var collisionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            for (int index = 0; index < archive.Entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry entry = archive.Entries[index];
                NativeZipEntry nativeEntry = _manifest!.Archive.Entries[index];
                string archivePath = ArchivePathSafety.ValidateArchiveRelativePath(entry.FullName);
                if (!string.Equals(archivePath, nativeEntry.Path, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Native and managed ZIP inventories disagree at entry {index}.");
                }

                string collisionKey = archivePath.TrimEnd('/');
                if (!collisionKeys.Add(collisionKey))
                {
                    throw new InvalidDataException(
                        $"Archive entries collide under Windows path normalization: '{archivePath}'.");
                }

                if (nativeEntry.Encrypted)
                {
                    throw new InvalidDataException($"Encrypted archive entries are not supported: '{archivePath}'.");
                }

                if (ArchivePathSafety.IsSymbolicLink(entry.ExternalAttributes) ||
                    nativeEntry.UnixMode is uint unixMode && IsUnixSymbolicLink(unixMode))
                {
                    throw new InvalidDataException($"Symbolic-link archive entries are forbidden: '{archivePath}'.");
                }

                bool isDirectory = entry.Name.Length == 0 ||
                    string.Equals(nativeEntry.EntryType, "directory", StringComparison.OrdinalIgnoreCase);
                if (entry.Length < 0 || entry.Length > _options.MaximumEntryBytes)
                {
                    throw new InvalidDataException($"Archive entry exceeds the configured size limit: '{archivePath}'.");
                }

                totalBytes = checked(totalBytes + entry.Length);
                if (totalBytes > _options.MaximumTotalBytes)
                {
                    throw new InvalidDataException("Archive exceeds the configured total uncompressed size limit.");
                }

                string extractedPath = ArchivePathSafety.CombineArchivePath(_extractionPath, archivePath);
                if (isDirectory)
                {
                    using (OpenOrCreateGuardedDirectoryPath(_extractionPath, extractedPath))
                    {
                        // Each newly-created component is bound before traversal continues.
                    }
                }
                else
                {
                    string? parent = Path.GetDirectoryName(extractedPath);
                    if (parent is null)
                    {
                        throw new InvalidDataException($"Archive entry has no safe parent path: '{archivePath}'.");
                    }

                    using DirectoryMutationGuard? parentGuard =
                        OpenOrCreateGuardedDirectoryPath(_extractionPath, parent);
                    using var destinationGuard = DirectoryMutationGuard.CreateFileForMutation(extractedPath);
                    await ExtractFileAsync(entry, destinationGuard, cancellationToken).ConfigureAwait(false);
                    destinationGuard.ApplyLeafTimestamp(entry.LastWriteTime);
                }

                _entries.Add(new ArchiveEntrySnapshot(
                    index,
                    archivePath,
                    isDirectory,
                    entry.LastWriteTime,
                    entry.ExternalAttributes,
                    IsStored(nativeEntry.CompressionMethod) ? CompressionKind.Stored : CompressionKind.Compressed,
                    extractedPath));
            }

            foreach (ArchiveEntrySnapshot directory in _entries
                         .Where(static entry => entry.IsDirectory)
                         .OrderByDescending(static entry => entry.ArchivePath.Length))
            {
                DirectoryMutationGuard? directoryGuard =
                    DirectoryMutationGuard.OpenExistingChildDirectoryForMutation(
                        _extractionPath,
                        directory.ExtractedPath);
                if (directoryGuard is null)
                {
                    extractionGuard.ApplyLeafTimestamp(directory.LastWriteTime);
                    continue;
                }

                using (directoryGuard)
                {
                    directoryGuard.ApplyLeafTimestamp(directory.LastWriteTime);
                }
            }

            await PrepareUnsignedManifestOverrideAsync(cancellationToken).ConfigureAwait(false);
            _extracted = true;
            _journal.Write("extract", "ok", $"entries={_entries.Count}; bytes={totalBytes}");
        }
        catch (Exception exception)
        {
            _journal.Write("extract", "failed", exception.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<TranslationEntry>> ReadTranslatableEntriesAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureExtracted();
        if (_translationEntries is not null)
        {
            return _translationEntries;
        }

        try
        {
            var result = new List<TranslationEntry>();
            IEnumerable<NativeResourceEntry> languageResources = SelectLanguageResources();
            foreach (NativeResourceEntry resource in languageResources.Concat(SelectDocumentResources()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string extractedPath = GetExtractedFile(resource.Path);
                string text = await ReadTextResourceAsync(extractedPath, cancellationToken).ConfigureAwait(false);
                string targetPath = CreateTargetArchivePath(resource);
                switch (resource.Kind)
                {
                    case "language_json":
                        foreach ((string key, string value) in JsonResourceEditor.ReadLanguageEntries(text))
                        {
                            AddSourceEntry(result, resource.Path, key, value, TranslatableResourceKind.LanguageJson, targetPath);
                        }

                        break;
                    case "language_lang":
                        foreach ((string key, string value) in LangResourceEditor.ReadEntries(text))
                        {
                            AddSourceEntry(result, resource.Path, key, value, TranslatableResourceKind.LanguageLang, targetPath);
                        }

                        break;
                    case "pack_text":
                        AddSourceEntry(result, resource.Path, null, text, TranslatableResourceKind.PackText, targetPath);
                        break;
                    case "mcmeta":
                        foreach ((string pointer, string value) in JsonResourceEditor.ReadMcmetaDisplayEntries(
                                     text,
                                     resource.Path))
                        {
                            AddSourceEntry(result, resource.Path, pointer, value, TranslatableResourceKind.Mcmeta, targetPath);
                        }

                        break;
                }
            }

            foreach (ExternalizedSourceDescriptor externalized in _externalizedSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddSourceEntry(
                    result,
                    externalized.ClassArchivePath,
                    externalized.TranslationKey,
                    externalized.OriginalText,
                    TranslatableResourceKind.ExternalizedLanguageJson,
                    externalized.TargetArchivePath);
            }

            _translationEntries = result.AsReadOnly();
            _journal.Write("read_translatable_entries", "ok", $"entries={result.Count}");
            return _translationEntries;
        }
        catch (Exception exception)
        {
            _journal.Write("read_translatable_entries", "failed", exception.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<HardcodedStringCandidate>> ScanHardcodedStringsAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureExtracted();
        if (_hardcodedCandidates is not null)
        {
            return _hardcodedCandidates;
        }

        try
        {
            var candidates = new List<HardcodedStringCandidate>();
            var analyzedLocations = new HashSet<(
                string ArchivePath,
                string ClassName,
                string MethodName,
                string MethodDescriptor,
                int BytecodeOffset)>();
            foreach (ArchiveEntrySnapshot classEntry in _entries.Where(static entry =>
                         !entry.IsDirectory && entry.ArchivePath.EndsWith(".class", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    byte[] bytes = await ReadExtractedClassBytesAsync(classEntry, cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<ClassFileLiteralCandidate> analyzed =
                        MojangComponentLiteralClassFileRewriter.Analyze(
                            bytes,
                            _manifest!.ModMetadata.PrimaryModId);
                    foreach (ClassFileLiteralCandidate candidate in analyzed)
                    {
                        analyzedLocations.Add((
                            classEntry.ArchivePath,
                            candidate.ClassName,
                            candidate.MethodName,
                            candidate.MethodDescriptor,
                            candidate.BytecodeOffset));
                        candidates.Add(new HardcodedStringCandidate(
                            checked((ulong)classEntry.Index),
                            classEntry.ArchivePath,
                            candidate.ClassName,
                            candidate.MethodName,
                            candidate.MethodDescriptor,
                            candidate.BytecodeOffset,
                            candidate.Opcode,
                            candidate.ConstantPoolIndex,
                            candidate.Value,
                            candidate.SuggestedKey,
                            candidate.IsSafe));
                    }
                }
                catch (InvalidDataException exception)
                {
                    AddWarning(
                        $"Class '{classEntry.ArchivePath}' was rejected by the verified safe-subset analyzer and will not be modified.");
                    _journal.Write("class_safe_subset_scan_error", "rejected", $"{classEntry.ArchivePath}: {exception.Message}");
                }
            }

            NativeClassStringScan? nativeScan = _manifest!.ClassStringScan;
            if (nativeScan is not null)
            {
                foreach (NativeClassStringReference reference in nativeScan.References)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!reference.Candidate ||
                        reference.BytecodeOffset > int.MaxValue ||
                        analyzedLocations.Contains((
                            reference.ArchivePath,
                            reference.Class,
                            reference.Method,
                            reference.Descriptor,
                            checked((int)reference.BytecodeOffset))))
                    {
                        continue;
                    }

                    // Native scanning remains useful for read-only diagnostics, but only the
                    // managed analyzer above can establish the exact safe rewrite proof.
                    candidates.Add(MapHardcodedCandidate(reference, _manifest.ModMetadata.PrimaryModId));
                }

                if (nativeScan.Errors.Count > 0)
                {
                    AddWarning(
                        $"Native class string scanning skipped or rejected {nativeScan.Errors.Count} class files; see the transaction log for details.");
                    foreach (NativeClassScanError error in nativeScan.Errors)
                    {
                        _journal.Write("class_scan_error", "recorded", $"{error.ArchivePath}: {error.Error}");
                    }
                }
            }

            _hardcodedCandidates = candidates
                .OrderBy(static candidate => candidate.ArchiveIndex)
                .ThenBy(static candidate => candidate.BytecodeOffset)
                .ToArray();
            _journal.Write(
                "scan_hardcoded_strings",
                "ok",
                $"candidates={candidates.Count}; safe_subset={candidates.Count(static candidate => candidate.IsRecognizedSafePattern)}");
            return _hardcodedCandidates;
        }
        catch (Exception exception)
        {
            _journal.Write("scan_hardcoded_strings", "failed", exception.Message);
            throw;
        }
    }

    public async Task<ExternalizationReport> ExternalizeAsync(
        IReadOnlyList<HardcodedStringCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureExtracted();
        ArgumentNullException.ThrowIfNull(candidates);
        if (_externalizationCompleted)
        {
            throw new InvalidOperationException("Verified safe-subset externalization has already been completed.");
        }

        if (_translationEntries is not null)
        {
            throw new InvalidOperationException(
                "Verified safe-subset externalization must run before the translatable entry inventory is frozen.");
        }

        if (candidates.Any(static candidate => !candidate.IsRecognizedSafePattern))
        {
            throw new ArgumentException(
                "Only candidates recognized as safe bytecode patterns may be submitted for externalization.",
                nameof(candidates));
        }

        if (_hardcodedCandidates is null)
        {
            throw new InvalidOperationException("Hardcoded strings must be scanned before externalization.");
        }

        var authoritative = _hardcodedCandidates
            .Where(static candidate => candidate.IsRecognizedSafePattern)
            .ToDictionary(CreateCandidateIdentity, StringComparer.Ordinal);
        var submitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (HardcodedStringCandidate candidate in candidates)
        {
            string identity = CreateCandidateIdentity(candidate);
            if (!submitted.Add(identity) ||
                !authoritative.TryGetValue(identity, out HardcodedStringCandidate? analyzed) ||
                analyzed != candidate)
            {
                throw new InvalidDataException(
                    "An externalization selection is duplicated, stale, or does not exactly match the verified analysis.");
            }
        }

        if (candidates.Count == 0)
        {
            _externalizationCompleted = true;
            _journal.Write("externalize_safe_subset", "ok", "candidates=0; externalized=0");
            return new ExternalizationReport(0, 0, Array.Empty<string>());
        }

        try
        {
            var classOverrides = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var rewriteSelections = new Dictionary<string, IReadOnlyList<ClassFileRewriteSelection>>(
                StringComparer.OrdinalIgnoreCase);
            var externalizedSources = new List<ExternalizedSourceDescriptor>(candidates.Count);
            string targetPath = CreateExternalizedTargetPath();
            string fallbackPath = CreateExternalizedFallbackPath();
            bool targetIsFallback = string.Equals(
                targetPath,
                fallbackPath,
                StringComparison.OrdinalIgnoreCase);

            foreach (IGrouping<string, HardcodedStringCandidate> classGroup in candidates.GroupBy(
                         static candidate => candidate.ArchivePath,
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                HardcodedStringCandidate first = classGroup.First();
                ArchiveEntrySnapshot classEntry = _entries.SingleOrDefault(entry =>
                        entry.Index == checked((int)first.ArchiveIndex) &&
                        string.Equals(entry.ArchivePath, classGroup.Key, StringComparison.Ordinal) &&
                        !entry.IsDirectory)
                    ?? throw new InvalidDataException(
                        $"Analyzed class '{classGroup.Key}' is no longer present at its original archive index.");
                byte[] original = await ReadExtractedClassBytesAsync(classEntry, cancellationToken).ConfigureAwait(false);
                ClassFileRewriteSelection[] selections = classGroup
                    .Select(static candidate => new ClassFileRewriteSelection(
                        candidate.ClassName,
                        candidate.MethodName,
                        candidate.MethodDescriptor,
                        candidate.BytecodeOffset,
                        candidate.Value,
                        candidate.SuggestedKey))
                    .ToArray();
                ClassFileRewriteResult rewritten = MojangComponentLiteralClassFileRewriter.Rewrite(
                    original,
                    selections);
                if (rewritten.AppliedCandidates.Count != selections.Length)
                {
                    throw new InvalidDataException(
                        $"Class '{classGroup.Key}' did not apply every selected safe-subset rewrite.");
                }

                classOverrides.Add(classGroup.Key, rewritten.Bytes);
                rewriteSelections.Add(classGroup.Key, selections);
                externalizedSources.AddRange(classGroup.Select(candidate => new ExternalizedSourceDescriptor(
                    candidate.ArchivePath,
                    candidate.SuggestedKey,
                    candidate.Value,
                    targetPath)));
            }

            var fallbackValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ExternalizedSourceDescriptor source in externalizedSources)
            {
                if (!fallbackValues.TryAdd(source.TranslationKey, source.OriginalText))
                {
                    throw new InvalidDataException(
                        $"Verified analysis produced duplicate translation key '{source.TranslationKey}'.");
                }
            }

            string fallbackBase = FindEntry(fallbackPath) is null
                ? "{}"
                : await ReadTextResourceAsync(
                        GetExtractedFile(fallbackPath),
                        cancellationToken)
                    .ConfigureAwait(false);
            Dictionary<string, string> existingFallback = JsonResourceEditor
                .ReadLanguageEntries(fallbackBase)
                .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
            foreach ((string key, string originalText) in fallbackValues)
            {
                if (existingFallback.TryGetValue(key, out string? existingText) &&
                    !string.Equals(existingText, originalText, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Existing fallback key '{key}' conflicts with the verified original literal.");
                }
            }

            foreach ((string path, byte[] bytes) in classOverrides)
            {
                _externalizationOverrides.Add(path, bytes);
            }

            // For an English target, en_us is the translated target itself. The verified
            // translation becomes Minecraft's runtime fallback, so there is no second file in
            // which the original literal can be preserved. Conflict checks above still run to
            // prevent overwriting an unrelated existing key. Other targets keep the original
            // literal in en_us exactly as before.
            if (!targetIsFallback)
            {
                byte[] fallbackOverride = JsonResourceEditor.UpdateLanguage(fallbackBase, fallbackValues);
                _externalizationOverrides.Add(fallbackPath, fallbackOverride);
            }
            foreach ((string path, IReadOnlyList<ClassFileRewriteSelection> selections) in rewriteSelections)
            {
                _appliedClassRewrites.Add(path, selections);
            }

            _externalizedSources.AddRange(externalizedSources);
            _externalizationCompleted = true;
            const string safeSubsetNotice =
                "Only the verified Mojang Component.literal(String) -> translatable(String) bytecode subset was externalized; all other hardcoded patterns remain unchanged.";
            AddWarning(safeSubsetNotice);
            _journal.Write(
                "externalize_safe_subset",
                "ok",
                $"candidates={candidates.Count}; externalized={externalizedSources.Count}; classes={classOverrides.Count}");
            return new ExternalizationReport(
                candidates.Count,
                externalizedSources.Count,
                new[] { safeSubsetNotice });
        }
        catch (Exception exception)
        {
            _journal.Write("externalize_safe_subset", "failed", exception.Message);
            throw;
        }
    }

    public async Task ApplyTranslationsAsync(
        TranslationBatchResult translations,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureExtracted();
        ArgumentNullException.ThrowIfNull(translations);
        if (_translationEntries is null)
        {
            throw new InvalidOperationException("Translatable entries must be read before translations are applied.");
        }

        if (!string.Equals(
                NormalizeLocale(translations.TargetLanguage),
                NormalizeLocale(_request.TargetLanguage),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The translation batch target language does not match the pipeline request.");
        }

        try
        {
            _overrides.Clear();
            _expectedExternalizedTargetValues.Clear();
            foreach (TranslationStyle style in _request.Styles)
            {
                _overrides.Add(
                    style,
                    new Dictionary<string, byte[]>(_externalizationOverrides, StringComparer.OrdinalIgnoreCase));
                _expectedExternalizedTargetValues.Add(
                    style,
                    new Dictionary<string, string>(StringComparer.Ordinal));
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var grouped = new Dictionary<TranslationStyle, Dictionary<string, List<(SourceEntryDescriptor Descriptor, string Text)>>>();
            foreach (TranslatedEntry translated in translations.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stableId = CreateStableId(translated.RelativePath, translated.Key);
                if (!seen.Add(stableId))
                {
                    throw new InvalidDataException($"The translation batch contains duplicate stable id '{stableId}'.");
                }

                if (!_sourceEntries.TryGetValue(stableId, out SourceEntryDescriptor? descriptor))
                {
                    throw new InvalidDataException($"The translation batch references unknown stable id '{stableId}'.");
                }

                foreach (TranslationStyle style in _request.Styles)
                {
                    TranslationVariant? variant = translated.Variants.SingleOrDefault(item => item.Style == style);
                    if (variant is null)
                    {
                        throw new InvalidDataException(
                            $"Translation '{stableId}' does not contain the required {style} variant.");
                    }

                    if (!grouped.TryGetValue(style, out Dictionary<string, List<(SourceEntryDescriptor, string)>>? byPath))
                    {
                        byPath = new Dictionary<string, List<(SourceEntryDescriptor, string)>>(StringComparer.OrdinalIgnoreCase);
                        grouped.Add(style, byPath);
                    }

                    if (!byPath.TryGetValue(descriptor.TargetArchivePath, out List<(SourceEntryDescriptor, string)>? patches))
                    {
                        patches = new List<(SourceEntryDescriptor, string)>();
                        byPath.Add(descriptor.TargetArchivePath, patches);
                    }

                    patches.Add((descriptor, variant.Text));
                    if (descriptor.Kind == TranslatableResourceKind.ExternalizedLanguageJson)
                    {
                        string key = descriptor.Key
                            ?? throw new InvalidDataException(
                                $"Externalized translation '{stableId}' has no translation key.");
                        if (!_expectedExternalizedTargetValues[style].TryAdd(key, variant.Text))
                        {
                            throw new InvalidDataException(
                                $"Externalized translation key '{key}' is duplicated for {style}.");
                        }
                    }
                }
            }

            foreach ((TranslationStyle style, Dictionary<string, List<(SourceEntryDescriptor Descriptor, string Text)>> byPath) in grouped)
            {
                foreach ((string targetPath, List<(SourceEntryDescriptor Descriptor, string Text)> patches) in byPath)
                {
                    byte[] content = await BuildOverrideAsync(targetPath, patches, cancellationToken).ConfigureAwait(false);
                    _overrides[style][targetPath] = content;
                }
            }

            _translationsApplied = true;
            _journal.Write("apply_translations", "ok", $"translated_entries={translations.Entries.Count}");
        }
        catch (Exception exception)
        {
            _journal.Write("apply_translations", "failed", exception.Message);
            throw;
        }
    }

    public async Task<PackageVerification> StagePackageAsync(
        string outputPath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureExtracted();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_translationsApplied)
        {
            throw new InvalidOperationException("Translations must be applied before packages are staged.");
        }

        if (_request.SignedArchiveHandling == SignedArchiveHandling.Resign)
        {
            throw new NotSupportedException("Re-signing is not implemented; a signing request cannot be staged.");
        }

        string requestedOutput = ArchivePathSafety.Canonicalize(_request.OutputPath);
        string suppliedOutput = ArchivePathSafety.Canonicalize(outputPath);
        if (!string.Equals(requestedOutput, suppliedOutput, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The staging output must match the output path approved in the pipeline request.");
        }

        if (_stagedArtifacts.Count > 0)
        {
            throw new InvalidOperationException("Packages have already been staged for this workspace.");
        }

        var outputGuards = new Dictionary<string, DirectoryMutationGuard>(StringComparer.OrdinalIgnoreCase);
        var stagedArtifactGuards = new Dictionary<string, DirectoryMutationGuard>(StringComparer.OrdinalIgnoreCase);
        var verifiedDirectoryDigests = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            List<ArtifactTarget> targets = CreateArtifactTargetsForRequest(requestedOutput);
            foreach (ArtifactTarget artifactTarget in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TranslationStyle style = artifactTarget.Style;
                string target = artifactTarget.Path;
                string outputDirectory = Path.GetDirectoryName(target)
                    ?? throw new InvalidOperationException("The output path has no parent directory.");
                string canonicalDirectory = ArchivePathSafety.Canonicalize(outputDirectory);
                if (!outputGuards.ContainsKey(canonicalDirectory))
                {
                    ArchivePathSafety.RejectReparsePointsInExistingDirectoryAncestry(canonicalDirectory);
                    outputGuards.Add(
                        canonicalDirectory,
                        DirectoryMutationGuard.OpenOrCreateDirectoryForMutation(canonicalDirectory));
                }

                string stageName = $".{Path.GetFileName(target)}.{_jobId:N}.{style.ToString().ToLowerInvariant()}.staged";
                string stagedPath = Path.Combine(canonicalDirectory, stageName);
                if (File.Exists(stagedPath) || Directory.Exists(stagedPath))
                {
                    throw new IOException($"The staged output path already exists: '{stagedPath}'.");
                }

                _stagedArtifacts.Add(new StagedArtifact(
                    style,
                    stagedPath,
                    target,
                    artifactTarget.IsDirectory));
                if (artifactTarget.IsDirectory)
                {
                    await BuildDirectoryPackageAsync(style, stagedPath, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await BuildPackageAsync(style, stagedPath, cancellationToken).ConfigureAwait(false);
                }

                stagedArtifactGuards.Add(
                    stagedPath,
                    artifactTarget.IsDirectory
                        ? DirectoryMutationGuard.OpenDirectoryForTraversal(stagedPath)
                        : DirectoryMutationGuard.OpenFileForTraversal(stagedPath));
            }

            _verification = await VerifyStagedPackagesAsync(
                    stagedArtifactGuards,
                    verifiedDirectoryDigests,
                    cancellationToken)
                .ConfigureAwait(false);
            if (_verification.IsValidArchive && _verification.MetadataPreserved && _verification.Errors.Count == 0)
            {
                for (int index = 0; index < _stagedArtifacts.Count; index++)
                {
                    StagedArtifact artifact = _stagedArtifacts[index];
                    byte[] hash;
                    if (artifact.IsDirectory)
                    {
                        hash = verifiedDirectoryDigests[artifact.StagedPath];
                    }
                    else
                    {
                        hash = await stagedArtifactGuards[artifact.StagedPath]
                            .ComputeFileSha256Async(cancellationToken)
                            .ConfigureAwait(false);
                    }

                    _stagedArtifacts[index] = artifact with { VerifiedSha256 = hash };
                }
            }

            _journal.Write(
                "stage_and_verify",
                _verification.IsValidArchive && _verification.MetadataPreserved && _verification.Errors.Count == 0
                    ? "ok"
                    : "failed",
                _verification.Errors.Count == 0 ? $"artifacts={_stagedArtifacts.Count}" : string.Join("; ", _verification.Errors));
            return _verification;
        }
        catch (Exception exception)
        {
            _journal.Write("stage_and_verify", "failed", exception.Message);
            throw;
        }
        finally
        {
            foreach (DirectoryMutationGuard guard in stagedArtifactGuards.Values.Reverse())
            {
                guard.Dispose();
            }

            foreach (DirectoryMutationGuard guard in outputGuards.Values.Reverse())
            {
                guard.Dispose();
            }
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_verification is null || !_verification.IsValidArchive ||
            !_verification.MetadataPreserved || _verification.Errors.Count > 0)
        {
            throw new InvalidOperationException("Only successfully verified staged packages can be committed.");
        }

        if (_committed)
        {
            return;
        }

        var commitLeases = new List<CommitMutationLease>();
        var committedArtifacts = new List<CommitMutationLease>();
        var targetGuards = new Dictionary<string, DirectoryMutationGuard>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (StagedArtifact artifact in _stagedArtifacts)
            {
                if (artifact.VerifiedSha256 is null)
                {
                    throw new InvalidOperationException($"The staged artifact has no verified digest: '{artifact.StagedPath}'.");
                }

                string targetDirectory = ArchivePathSafety.Canonicalize(
                    Path.GetDirectoryName(artifact.TargetPath)
                    ?? throw new InvalidOperationException("The output path has no parent directory."));
                string stagedDirectory = ArchivePathSafety.Canonicalize(
                    Path.GetDirectoryName(artifact.StagedPath)
                    ?? throw new InvalidOperationException("The staged output path has no parent directory."));
                if (!string.Equals(targetDirectory, stagedDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("A staged artifact must share its approved target directory.");
                }

                ArchivePathSafety.RejectReparsePointsInExistingDirectoryAncestry(targetDirectory);
                if (!targetGuards.ContainsKey(targetDirectory))
                {
                    targetGuards.Add(
                        targetDirectory,
                        DirectoryMutationGuard.OpenDirectoryForMutationWithValidatedAncestry(targetDirectory));
                }

                if (File.Exists(artifact.TargetPath) || Directory.Exists(artifact.TargetPath))
                {
                    throw new IOException($"The output already exists and will not be overwritten: '{artifact.TargetPath}'.");
                }

                DirectoryMutationGuard artifactGuard = artifact.IsDirectory
                    ? DirectoryMutationGuard.OpenDirectoryForTraversal(artifact.StagedPath)
                    : DirectoryMutationGuard.OpenFileForMutation(artifact.StagedPath);
                var lease = new CommitMutationLease(artifact, artifactGuard);
                commitLeases.Add(lease);
                byte[] currentHash;
                if (artifact.IsDirectory)
                {
                    currentHash = await FolderSnapshotBuilder.ComputeTreeDigestAsync(
                            artifact.StagedPath,
                            _options,
                            cancellationToken,
                            artifactGuard)
                        .ConfigureAwait(false);
                }
                else
                {
                    currentHash = await artifactGuard.ComputeFileSha256Async(cancellationToken).ConfigureAwait(false);
                }

                if (!CryptographicOperations.FixedTimeEquals(currentHash, artifact.VerifiedSha256))
                {
                    throw new InvalidDataException(
                        $"The staged artifact changed after verification: '{artifact.StagedPath}'.");
                }
            }

            foreach (CommitMutationLease lease in commitLeases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StagedArtifact artifact = lease.Artifact;
                if (File.Exists(artifact.TargetPath) || Directory.Exists(artifact.TargetPath))
                {
                    throw new IOException($"The output already exists and will not be overwritten: '{artifact.TargetPath}'.");
                }

                lease.Guard.MoveLeafTo(artifact.TargetPath);
                committedArtifacts.Add(lease);
                if (artifact.IsDirectory)
                {
                    byte[] committedHash = await FolderSnapshotBuilder.ComputeTreeDigestAsync(
                            artifact.TargetPath,
                            _options,
                            cancellationToken,
                            lease.Guard)
                        .ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(committedHash, artifact.VerifiedSha256!))
                    {
                        throw new InvalidDataException(
                            $"The directory artifact changed during commit: '{artifact.TargetPath}'.");
                    }
                }
                else
                {
                    byte[] committedHash = await lease.Guard
                        .ComputeFileSha256Async(cancellationToken)
                        .ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(committedHash, artifact.VerifiedSha256!))
                    {
                        throw new InvalidDataException(
                            $"The file artifact changed during commit: '{artifact.TargetPath}'.");
                    }
                }
            }

            ReleaseSourceLock();
            SafeDeleteWorkspace();
            _journal.Write("commit", "ok", $"artifacts={committedArtifacts.Count}");
            _committed = true;
            return;
        }
        catch (Exception exception)
        {
            _committed = false;
            var withdrawalErrors = new List<string>();
            foreach (CommitMutationLease committed in committedArtifacts.AsEnumerable().Reverse())
            {
                _pendingPublishedArtifacts[committed.Artifact.TargetPath] = committed.Artifact.IsDirectory;
                try
                {
                    if (committed.Artifact.IsDirectory)
                    {
                        DirectoryMutationGuard.DeleteDirectoryTree(
                            committed.Artifact.TargetPath,
                            committed.Artifact.TargetPath,
                            committed.Guard);
                    }
                    else
                    {
                        committed.Guard.DeleteLeaf();
                    }

                    _pendingPublishedArtifacts.Remove(committed.Artifact.TargetPath);
                }
                catch (Exception withdrawalException) when (withdrawalException is IOException or UnauthorizedAccessException)
                {
                    withdrawalErrors.Add($"{committed.Artifact.TargetPath}: {withdrawalException.Message}");
                }
            }

            string detail = withdrawalErrors.Count == 0
                ? exception.Message
                : $"{exception.Message}; withdrawal failures: {string.Join(" | ", withdrawalErrors)}";
            _journal.Write("commit", "failed", detail);
            throw;
        }
        finally
        {
            foreach (CommitMutationLease lease in commitLeases.AsEnumerable().Reverse())
            {
                lease.Guard.Dispose();
            }

            foreach (DirectoryMutationGuard guard in targetGuards.Values.Reverse())
            {
                guard.Dispose();
            }
        }
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || _rolledBack || _committed)
        {
            return Task.CompletedTask;
        }

        var errors = new List<string>();
        ReleaseSourceLock();
        DeletePendingPublishedArtifacts(errors);
        DeleteStagedFiles(errors);
        try
        {
            SafeDeleteWorkspace();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add(exception.Message);
        }

        _journal.Write(
            "rollback",
            errors.Count == 0 ? "ok" : "partial",
            errors.Count == 0 ? null : string.Join(" | ", errors));
        if (errors.Count > 0)
        {
            throw new IOException($"Rollback did not fully complete: {string.Join(" | ", errors)}");
        }

        _rolledBack = true;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_committed && !_rolledBack)
            {
                await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                ReleaseSourceLock();
                if (_workspaceVerified && Directory.Exists(_workspacePath))
                {
                    SafeDeleteWorkspace();
                }
            }
        }
        finally
        {
            _disposed = true;
            try
            {
                _journal.Write("dispose", "ok");
            }
            finally
            {
                _journal.Dispose();
                _workspaceGuard?.Dispose();
                _workspaceGuard = null;
            }
        }
    }

    private void ValidateEntryInventory(ZipArchive archive)
    {
        if (archive.Entries.Count > _options.MaximumEntryCount)
        {
            throw new InvalidDataException("Archive exceeds the configured entry-count limit.");
        }

        if (_manifest!.Archive.Entries.Count != archive.Entries.Count)
        {
            throw new InvalidDataException("Native and managed ZIP inventories have different entry counts.");
        }
    }

    private static async Task ExtractFileAsync(
        ZipArchiveEntry entry,
        DirectoryMutationGuard destinationGuard,
        CancellationToken cancellationToken)
    {
        await using Stream source = entry.Open();
        long written;
        try
        {
            written = await destinationGuard.CopyFromStreamAsync(source, entry.Length, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"Archive entry expanded beyond its declared size: '{entry.FullName}'.",
                exception);
        }

        if (written != entry.Length)
        {
            throw new InvalidDataException($"Archive entry size did not match its declaration: '{entry.FullName}'.");
        }
    }

    private IEnumerable<NativeResourceEntry> SelectLanguageResources()
    {
        string targetLocale = TranslationLanguageCatalog
            .GetRequired(_request.TargetLanguage)
            .MinecraftLocale;
        IEnumerable<NativeResourceEntry> resources = _manifest!.Resources.Where(static resource =>
            resource.Kind is "language_json" or "language_lang" && resource.Namespace is not null && resource.Locale is not null);
        foreach (IGrouping<(string Namespace, string Kind), NativeResourceEntry> group in resources.GroupBy(
                     static resource => (resource.Namespace!, resource.Kind)))
        {
            NativeResourceEntry[] sourceCandidates = group
                .Where(resource => !string.Equals(
                    NormalizeLocale(resource.Locale!),
                    targetLocale,
                    StringComparison.Ordinal))
                .ToArray();
            if (sourceCandidates.Length == 0)
            {
                AddWarning(
                    $"Only target locale '{targetLocale}' exists for namespace '{group.Key.Namespace}' " +
                    $"and resource kind '{group.Key.Kind}'; the existing target resource was preserved.");
                continue;
            }

            NativeResourceEntry selected = sourceCandidates
                .OrderBy(resource => LocaleRank(resource.Locale!))
                .ThenBy(static resource => resource.Locale, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static resource => resource.Path, StringComparer.Ordinal)
                .First();
            string selectedLocale = NormalizeLocale(selected.Locale!);
            if (!string.Equals(selectedLocale, "en_us", StringComparison.Ordinal))
            {
                if (string.Equals(targetLocale, "en_us", StringComparison.Ordinal))
                {
                    AddWarning(
                        $"Target locale 'en_us' was excluded as a source for namespace '{selected.Namespace}'; " +
                        $"deterministic source '{selected.Path}' ({selectedLocale}) was selected.");
                }
                else
                {
                    AddWarning(
                        $"No en_us {selected.Kind} resource exists for namespace '{selected.Namespace}'; " +
                        $"deterministic fallback '{selected.Path}' was selected.");
                }
            }

            yield return selected;
        }
    }

    private IEnumerable<NativeResourceEntry> SelectDocumentResources() =>
        _manifest!.Resources
            .Where(static resource => resource.Kind is "pack_text" or "mcmeta")
            .OrderBy(static resource => resource.ArchiveIndex);

    private static int LocaleRank(string locale)
    {
        string normalized = NormalizeLocale(locale);
        if (normalized == "en_us")
        {
            return 0;
        }

        if (normalized == "en_gb")
        {
            return 1;
        }

        if (normalized.StartsWith("en_", StringComparison.Ordinal))
        {
            return 2;
        }

        return 3;
    }

    private string CreateTargetArchivePath(NativeResourceEntry resource)
    {
        if (resource.Kind is not ("language_json" or "language_lang"))
        {
            return resource.Path;
        }

        int slash = resource.Path.LastIndexOf('/');
        string extension = resource.Kind == "language_json" ? ".json" : ".lang";
        string targetLocale = TranslationLanguageCatalog
            .GetRequired(_request.TargetLanguage)
            .MinecraftLocale;
        return $"{resource.Path[..(slash + 1)]}{targetLocale}{extension}";
    }

    private string CreateExternalizedTargetPath() =>
        CreateExternalizedLanguagePath(
            TranslationLanguageCatalog.GetRequired(_request.TargetLanguage).MinecraftLocale);

    private string CreateExternalizedFallbackPath() =>
        CreateExternalizedLanguagePath("en_us");

    private string CreateExternalizedLanguagePath(string locale)
    {
        string path = $"assets/{_manifest!.ModMetadata.PrimaryModId}/lang/{locale}.json";
        return ArchivePathSafety.ValidateArchiveRelativePath(path);
    }

    private async Task PrepareUnsignedManifestOverrideAsync(CancellationToken cancellationToken)
    {
        if (_request.SignedArchiveHandling != SignedArchiveHandling.CreateUnsignedCopy)
        {
            return;
        }

        const string manifestPath = "META-INF/MANIFEST.MF";
        ArchiveEntrySnapshot? manifest = FindEntry(manifestPath);
        if (manifest is null || manifest.IsDirectory)
        {
            return;
        }

        var info = new FileInfo(manifest.ExtractedPath);
        if (!info.Exists || info.Length > MaximumTextResourceBytes || info.Length > int.MaxValue)
        {
            throw new InvalidDataException("Signed JAR manifest is missing or exceeds the static-analysis size limit.");
        }

        byte[] source = await File.ReadAllBytesAsync(manifest.ExtractedPath, cancellationToken).ConfigureAwait(false);
        if (_inspection?.IsSigned != true && !JarManifestDocument.MayContainSignatureClaims(source))
        {
            return;
        }

        JarManifestDocument document = JarManifestDocument.Parse(source);
        if (_inspection?.IsSigned != true && !document.ContainsSignatureClaims)
        {
            return;
        }

        _externalizationOverrides.Add(manifestPath, document.CreateUnsignedCopy());
        AddWarning(
            "The unsigned output copy removes signature blocks and manifest digest/signature claims; it does not claim the original signer or hashes.");
    }

    private void AddSourceEntry(
        List<TranslationEntry> result,
        string relativePath,
        string? key,
        string value,
        TranslatableResourceKind kind,
        string targetArchivePath)
    {
        var entry = new TranslationEntry(relativePath, key, value);
        if (!_sourceEntries.TryAdd(
                entry.StableId,
                new SourceEntryDescriptor(
                    entry.StableId,
                    entry.RelativePath,
                    entry.Key,
                    entry.SourceText,
                    kind,
                    targetArchivePath)))
        {
            throw new InvalidDataException($"Duplicate translatable stable id '{entry.StableId}'.");
        }

        result.Add(entry);
    }

    private async Task<byte[]> BuildOverrideAsync(
        string targetPath,
        List<(SourceEntryDescriptor Descriptor, string Text)> patches,
        CancellationToken cancellationToken)
    {
        SourceEntryDescriptor first = patches[0].Descriptor;
        string baseText;
        if (_externalizationOverrides.TryGetValue(targetPath, out byte[]? externalizedBase))
        {
            baseText = StrictUtf8.GetString(externalizedBase);
        }
        else if (FindEntry(targetPath) is not null)
        {
            baseText = await ReadTextResourceAsync(
                    GetExtractedFile(targetPath),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (first.Kind == TranslatableResourceKind.ExternalizedLanguageJson)
        {
            baseText = "{}";
        }
        else
        {
            baseText = await ReadTextResourceAsync(
                    GetExtractedFile(first.RelativePath),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((SourceEntryDescriptor descriptor, string text) in patches)
        {
            if (!AreCompatibleTranslationKinds(descriptor.Kind, first.Kind) ||
                !string.Equals(descriptor.TargetArchivePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Incompatible translation patches target '{targetPath}'.");
            }

            string key = descriptor.Key ?? string.Empty;
            if (!values.TryAdd(key, text))
            {
                throw new InvalidDataException($"Duplicate translation key '{key}' for '{targetPath}'.");
            }
        }

        return first.Kind switch
        {
            TranslatableResourceKind.LanguageJson or
                TranslatableResourceKind.ExternalizedLanguageJson => JsonResourceEditor.UpdateLanguage(baseText, values),
            TranslatableResourceKind.LanguageLang => LangResourceEditor.Update(baseText, values),
            TranslatableResourceKind.Mcmeta => JsonResourceEditor.UpdatePointers(baseText, values),
            TranslatableResourceKind.PackText when values.Count == 1 => StrictUtf8.GetBytes(values[string.Empty]),
            TranslatableResourceKind.PackText => throw new InvalidDataException("pack.txt must have exactly one translation."),
            _ => throw new InvalidOperationException("Unsupported translatable resource kind.")
        };
    }

    private static bool AreCompatibleTranslationKinds(
        TranslatableResourceKind left,
        TranslatableResourceKind right) =>
        left == right ||
        (left is TranslatableResourceKind.LanguageJson or TranslatableResourceKind.ExternalizedLanguageJson) &&
        (right is TranslatableResourceKind.LanguageJson or TranslatableResourceKind.ExternalizedLanguageJson);

    private async Task BuildPackageAsync(
        TranslationStyle style,
        string stagedPath,
        CancellationToken cancellationToken)
    {
        Dictionary<string, byte[]> overrides = _overrides[style];
        using var extractedRootGuard = DirectoryMutationGuard.OpenDirectoryForMutation(_extractionPath);
        await using (var output = new FileStream(
                         stagedPath,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.None,
                         bufferSize: 128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
            var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ArchiveEntrySnapshot original in _entries.OrderBy(static entry => entry.Index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldRemoveSignature(original.ArchivePath))
                {
                    continue;
                }

                bool hasOverride = overrides.TryGetValue(original.ArchivePath, out byte[]? overrideBytes);
                string outputArchivePath = hasOverride
                    ? overrides.Keys.First(path =>
                        string.Equals(path, original.ArchivePath, StringComparison.OrdinalIgnoreCase))
                    : original.ArchivePath;
                ArchiveEntrySnapshot outputSnapshot = original with { ArchivePath = outputArchivePath };
                await WriteArchiveEntryAsync(
                        archive,
                        outputSnapshot,
                        overrideBytes,
                        _extractionPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                writtenPaths.Add(outputArchivePath.TrimEnd('/'));
            }

            foreach ((string archivePath, byte[] content) in overrides.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                if (writtenPaths.Contains(archivePath.TrimEnd('/')))
                {
                    continue;
                }

                ArchiveEntrySnapshot template = FindTemplateForNewEntry(archivePath);
                var added = template with
                {
                    Index = int.MaxValue,
                    ArchivePath = archivePath,
                    IsDirectory = false,
                    ExtractedPath = string.Empty
                };
                await WriteArchiveEntryAsync(
                        archive,
                        added,
                        content,
                        _extractionPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (_archiveComment.Length > 0)
        {
            await ApplyArchiveCommentAsync(stagedPath, _archiveComment, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BuildDirectoryPackageAsync(
        TranslationStyle style,
        string stagedPath,
        CancellationToken cancellationToken)
    {
        Dictionary<string, byte[]> overrides = _overrides[style];
        using var extractedRootGuard = DirectoryMutationGuard.OpenDirectoryForMutation(_extractionPath);
        Directory.CreateDirectory(stagedPath);
        using var stagedRootGuard = DirectoryMutationGuard.OpenDirectoryForMutation(stagedPath);
        RejectReparsePoint(stagedPath);
        var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new List<(string Path, ArchiveEntrySnapshot Snapshot)>();

        foreach (ArchiveEntrySnapshot original in _entries.OrderBy(static entry => entry.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldRemoveSignature(original.ArchivePath))
            {
                continue;
            }

            bool hasOverride = overrides.TryGetValue(original.ArchivePath, out byte[]? overrideBytes);
            string outputArchivePath = hasOverride
                ? overrides.Keys.First(path =>
                    string.Equals(path, original.ArchivePath, StringComparison.OrdinalIgnoreCase))
                : original.ArchivePath;
            ArchiveEntrySnapshot outputSnapshot = original with { ArchivePath = outputArchivePath };
            string destination = ArchivePathSafety.CombineArchivePath(stagedPath, outputArchivePath);
            if (outputSnapshot.IsDirectory)
            {
                using (OpenOrCreateGuardedDirectoryPath(stagedPath, destination))
                {
                    // The returned leaf guard is intentionally held until creation has completed.
                }

                directories.Add((destination, outputSnapshot));
            }
            else
            {
                string? parent = Path.GetDirectoryName(destination);
                if (parent is null)
                {
                    throw new InvalidDataException(
                        $"Directory package entry has no safe parent: '{outputArchivePath}'.");
                }

                using DirectoryMutationGuard? parentGuard = OpenOrCreateGuardedDirectoryPath(stagedPath, parent);
                using DirectoryMutationGuard destinationGuard =
                    DirectoryMutationGuard.CreateFileForMutation(destination);
                if (overrideBytes is null)
                {
                    string sourceParent = Path.GetDirectoryName(outputSnapshot.ExtractedPath)
                        ?? throw new InvalidDataException(
                            $"Extracted package entry has no safe parent: '{outputArchivePath}'.");
                    using DirectoryMutationGuard? sourceParentGuard =
                        DirectoryMutationGuard.OpenExistingChildDirectoryForMutation(
                            _extractionPath,
                            sourceParent);
                    using var sourceGuard =
                        DirectoryMutationGuard.OpenFileForMutation(outputSnapshot.ExtractedPath);
                    await sourceGuard.CopyFileToAsync(destinationGuard, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await destinationGuard.WriteFileContentsAsync(overrideBytes, cancellationToken)
                        .ConfigureAwait(false);
                }

                destinationGuard.ApplyLeafMetadata(
                    outputSnapshot.LastWriteTime,
                    GetRecoverableAttributes(outputSnapshot.ExternalAttributes));
            }

            writtenPaths.Add(outputArchivePath.TrimEnd('/'));
        }

        foreach ((string archivePath, byte[] content) in overrides.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (writtenPaths.Contains(archivePath.TrimEnd('/')))
            {
                continue;
            }

            ArchiveEntrySnapshot template = FindTemplateForNewEntry(archivePath);
            string destination = ArchivePathSafety.CombineArchivePath(stagedPath, archivePath);
            string? parent = Path.GetDirectoryName(destination);
            if (parent is null)
            {
                throw new InvalidDataException(
                    $"Directory package entry has no safe parent: '{archivePath}'.");
            }

            using DirectoryMutationGuard? parentGuard = OpenOrCreateGuardedDirectoryPath(stagedPath, parent);
            using var destinationGuard = DirectoryMutationGuard.CreateFileForMutation(destination);
            await destinationGuard.WriteFileContentsAsync(content, cancellationToken).ConfigureAwait(false);
            destinationGuard.ApplyLeafMetadata(
                template.LastWriteTime,
                GetRecoverableAttributes(template.ExternalAttributes));
        }

        foreach ((string directory, ArchiveEntrySnapshot snapshot) in directories
                     .OrderByDescending(static item => item.Path.Length))
        {
            DirectoryMutationGuard? directoryGuard = OpenOrCreateGuardedDirectoryPath(stagedPath, directory);
            if (directoryGuard is null)
            {
                stagedRootGuard.ApplyLeafMetadata(
                    snapshot.LastWriteTime,
                    GetRecoverableAttributes(snapshot.ExternalAttributes));
                continue;
            }

            using (directoryGuard)
            {
                directoryGuard.ApplyLeafMetadata(
                    snapshot.LastWriteTime,
                    GetRecoverableAttributes(snapshot.ExternalAttributes));
            }
        }
    }

    private static DirectoryMutationGuard? OpenOrCreateGuardedDirectoryPath(
        string guardedRoot,
        string directory) =>
        DirectoryMutationGuard.OpenOrCreateChildDirectoryForMutation(guardedRoot, directory);

    private static async Task WriteArchiveEntryAsync(
        ZipArchive archive,
        ArchiveEntrySnapshot entry,
        byte[]? overrideBytes,
        string guardedSourceRoot,
        CancellationToken cancellationToken)
    {
        CompressionLevel level = entry.IsDirectory || entry.Compression == CompressionKind.Stored
            ? CompressionLevel.NoCompression
            : CompressionLevel.Optimal;
        ZipArchiveEntry outputEntry = archive.CreateEntry(entry.ArchivePath, level);
        outputEntry.LastWriteTime = ClampZipTimestamp(entry.LastWriteTime);
        outputEntry.ExternalAttributes = entry.ExternalAttributes;
        if (entry.IsDirectory)
        {
            return;
        }

        await using Stream target = outputEntry.Open();
        if (overrideBytes is not null)
        {
            await target.WriteAsync(overrideBytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        string sourceParent = Path.GetDirectoryName(entry.ExtractedPath)
            ?? throw new InvalidDataException(
                $"Extracted archive entry has no safe parent: '{entry.ArchivePath}'.");
        using DirectoryMutationGuard? sourceParentGuard =
            DirectoryMutationGuard.OpenExistingChildDirectoryForMutation(
                guardedSourceRoot,
                sourceParent);
        using var sourceGuard = DirectoryMutationGuard.OpenFileForMutation(entry.ExtractedPath);
        _ = await sourceGuard.CopyFileToStreamAsync(target, long.MaxValue, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PackageVerification> VerifyStagedPackagesAsync(
        Dictionary<string, DirectoryMutationGuard> stagedArtifactGuards,
        Dictionary<string, byte[]> verifiedDirectoryDigests,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var staticWarnings = new List<string>();
        var completedChecks = new HashSet<string>(StringComparer.Ordinal);
        bool validArchive = true;
        bool metadataPreserved = true;
        bool containsJavaBytecode = false;
        foreach (StagedArtifact artifact in _stagedArtifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveScanManifest rebuilt;
            FolderSnapshotBuilder.FolderSnapshotResult? snapshotResult = null;
            string scanPath = artifact.StagedPath;
            try
            {
                if (artifact.IsDirectory)
                {
                    scanPath = Path.Combine(
                        _workspacePath,
                        $"verify-{artifact.Style.ToString().ToLowerInvariant()}.zip");
                    ArchivePathSafety.EnsureChildPath(_workspacePath, scanPath);
                    snapshotResult = await FolderSnapshotBuilder.CreateSnapshotZipAsync(
                            artifact.StagedPath,
                            scanPath,
                            _options,
                            cancellationToken,
                            stagedArtifactGuards[artifact.StagedPath])
                        .ConfigureAwait(false);
                }

            }
            catch (Exception exception)
            {
                validArchive = false;
                errors.Add($"{artifact.Style}: staged ZIP scan failed: {exception.Message}");
                continue;
            }

            using DirectoryMutationGuard? snapshotGuard = artifact.IsDirectory
                ? DirectoryMutationGuard.OpenFileForTraversal(scanPath)
                : null;
            DirectoryMutationGuard verificationGuard =
                snapshotGuard ?? stagedArtifactGuards[artifact.StagedPath];
            try
            {
                if (snapshotResult is not null)
                {
                    byte[] currentSnapshotHash = await verificationGuard
                        .ComputeFileSha256Async(cancellationToken)
                        .ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(
                            snapshotResult.SnapshotSha256,
                            currentSnapshotHash))
                    {
                        throw new InvalidDataException(
                            "The directory verification snapshot changed before scanning.");
                    }
                }

                rebuilt = _scanner.ScanArchive(scanPath);
            }
            catch (Exception exception)
            {
                validArchive = false;
                errors.Add($"{artifact.Style}: staged ZIP scan failed: {exception.Message}");
                continue;
            }

            try
            {
                var modifiedPaths = _overrides[artifact.Style].Keys
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                ArtifactStaticValidation staticValidation = await ModArtifactStaticValidator
                    .ValidateAsync(scanPath, modifiedPaths, cancellationToken)
                    .ConfigureAwait(false);
                containsJavaBytecode |= staticValidation.ContainsJavaBytecode;
                completedChecks.UnionWith(staticValidation.CompletedChecks);
                foreach (string error in staticValidation.BlockingErrors)
                {
                    errors.Add($"{artifact.Style}: static validation failed: {error}");
                }

                foreach (string warning in staticValidation.Warnings)
                {
                    string scopedWarning = $"{artifact.Style}: {warning}";
                    staticWarnings.Add(scopedWarning);
                    AddWarning(scopedWarning);
                }

                if (staticValidation.BlockingErrors.Count > 0)
                {
                    validArchive = false;
                }

                _journal.Write(
                    "artifact_static_validation",
                    staticValidation.BlockingErrors.Count == 0 ? "ok" : "failed",
                    $"style={artifact.Style}; bytecode={staticValidation.ContainsJavaBytecode}; " +
                    $"source_build={staticValidation.ContainsSourceBuild}; " +
                    $"blocking={staticValidation.BlockingErrors.Count}; warnings={staticValidation.Warnings.Count}");
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                validArchive = false;
                errors.Add($"{artifact.Style}: static validation could not complete: {exception.Message}");
            }

            bool sourceUsesFallback = _manifest!.ModMetadata.UsedFilenameFallback;
            bool metadataMatches = sourceUsesFallback
                ? rebuilt.ModMetadata.UsedFilenameFallback &&
                    string.Equals(
                        rebuilt.ModMetadata.PrimaryLoader,
                        _manifest.ModMetadata.PrimaryLoader,
                        StringComparison.OrdinalIgnoreCase)
                : !rebuilt.ModMetadata.UsedFilenameFallback &&
                    string.Equals(
                        rebuilt.ModMetadata.PrimaryModId,
                        _manifest.ModMetadata.PrimaryModId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        rebuilt.ModMetadata.PrimaryLoader,
                        _manifest.ModMetadata.PrimaryLoader,
                        StringComparison.OrdinalIgnoreCase);
            if (!metadataMatches)
            {
                metadataPreserved = false;
                errors.Add($"{artifact.Style}: loader or modId metadata changed during repack.");
            }

            var rebuiltResources = rebuilt.Resources
                .Select(static resource => resource.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> expectedResources = _manifest.Resources
                .Select(static resource => resource.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (string expectedResource in expectedResources)
            {
                if (!rebuiltResources.Contains(expectedResource))
                {
                    validArchive = false;
                    errors.Add($"{artifact.Style}: expected resource '{expectedResource}' is missing.");
                }
            }

            var rebuiltEntries = rebuilt.Archive.Entries
                .Select(static entry => entry.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string expectedOverride in _overrides[artifact.Style].Keys)
            {
                if (!rebuiltEntries.Contains(expectedOverride))
                {
                    validArchive = false;
                    errors.Add($"{artifact.Style}: expected transformed entry '{expectedOverride}' is missing.");
                }
            }

            StagedArtifact verificationArtifact = artifact.IsDirectory
                ? artifact with { StagedPath = scanPath, IsDirectory = false }
                : artifact;
            try
            {
                await VerifyStagedOverrideContentsAsync(
                        artifact,
                        stagedArtifactGuards[artifact.StagedPath],
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                validArchive = false;
                errors.Add($"{artifact.Style}: staged override content check failed: {exception.Message}");
            }

            try
            {
                await VerifyStagedExternalizationAsync(
                        verificationArtifact,
                        verificationGuard,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                validArchive = false;
                errors.Add($"{artifact.Style}: verified safe-subset externalization check failed: {exception.Message}");
            }

            if (_request.SignedArchiveHandling == SignedArchiveHandling.CreateUnsignedCopy &&
                !string.Equals(rebuilt.Archive.Signatures.Status, "none", StringComparison.Ordinal))
            {
                validArchive = false;
                errors.Add($"{artifact.Style}: signature files remain in the requested unsigned copy.");
            }

            if (_request.SignedArchiveHandling == SignedArchiveHandling.CreateUnsignedCopy &&
                rebuilt.Archive.Entries.Any(static entry =>
                    string.Equals(entry.Path, "META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    byte[] manifestBytes = await ReadStagedEntryBytesAsync(
                            verificationArtifact,
                            verificationGuard,
                            "META-INF/MANIFEST.MF",
                            MaximumTextResourceBytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (JarManifestDocument.MayContainSignatureClaims(manifestBytes) &&
                        JarManifestDocument.Parse(manifestBytes).ContainsSignatureClaims)
                    {
                        validArchive = false;
                        errors.Add(
                            $"{artifact.Style}: the unsigned copy manifest still contains signature or digest claims.");
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    validArchive = false;
                    errors.Add($"{artifact.Style}: unsigned manifest verification failed: {exception.Message}");
                }
            }

            if (artifact.IsDirectory && snapshotResult is not null)
            {
                byte[] currentDigest = await FolderSnapshotBuilder.ComputeTreeDigestAsync(
                        artifact.StagedPath,
                        _options,
                        cancellationToken,
                        stagedArtifactGuards[artifact.StagedPath])
                    .ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(snapshotResult.TreeSha256, currentDigest))
                {
                    validArchive = false;
                    errors.Add($"{artifact.Style}: staged directory changed after its verification snapshot.");
                }
                else
                {
                    verifiedDirectoryDigests.Add(artifact.StagedPath, snapshotResult.TreeSha256);
                }
            }
        }

        IReadOnlyList<PackageArtifact> artifacts = _stagedArtifacts
            .Select(static artifact => new PackageArtifact(artifact.Style, artifact.TargetPath))
            .ToArray();
        ArtifactValidationMode validationMode = containsJavaBytecode
            ? ArtifactValidationMode.PrecompiledJarStaticAnalysis
            : ArtifactValidationMode.ArchiveStaticAnalysis;
        return new PackageVerification(
            validArchive,
            metadataPreserved,
            errors.AsReadOnly(),
            artifacts,
            validationMode,
            SourceCompilationPerformed: false,
            completedChecks.Order(StringComparer.Ordinal).ToArray(),
            staticWarnings.AsReadOnly());
    }

    private async Task VerifyStagedOverrideContentsAsync(
        StagedArtifact artifact,
        DirectoryMutationGuard artifactGuard,
        CancellationToken cancellationToken)
    {
        foreach ((string archivePath, byte[] expected) in _overrides[artifact.Style])
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] actual = await ReadStagedEntryBytesAsync(
                    artifact,
                    artifactGuard,
                    archivePath,
                    expected.Length,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw new InvalidDataException(
                    $"Transformed entry '{archivePath}' does not exactly match the verified override bytes.");
            }
        }
    }

    private async Task VerifyStagedExternalizationAsync(
        StagedArtifact artifact,
        DirectoryMutationGuard artifactGuard,
        CancellationToken cancellationToken)
    {
        foreach ((string classArchivePath, IReadOnlyList<ClassFileRewriteSelection> selections) in _appliedClassRewrites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] classBytes = await ReadStagedEntryBytesAsync(
                    artifact,
                    artifactGuard,
                    classArchivePath,
                    MaximumClassFileBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            MojangComponentLiteralClassFileRewriter.VerifyApplied(classBytes, selections);
        }

        foreach (IGrouping<string, ExternalizedSourceDescriptor> group in _externalizedSources.GroupBy(
                     static source => source.TargetArchivePath,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!_expectedExternalizedTargetValues.TryGetValue(
                    artifact.Style,
                    out Dictionary<string, string>? expectedTargetValues))
            {
                throw new InvalidDataException(
                    $"No expected externalized target values were recorded for {artifact.Style}.");
            }

            byte[] targetBytes = await ReadStagedEntryBytesAsync(
                    artifact,
                    artifactGuard,
                    group.Key,
                    MaximumTextResourceBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            Dictionary<string, string> targetEntries = JsonResourceEditor
                .ReadLanguageEntries(StrictUtf8.GetString(targetBytes))
                .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
            foreach (ExternalizedSourceDescriptor source in group)
            {
                if (!expectedTargetValues.TryGetValue(source.TranslationKey, out string? expectedTarget) ||
                    !targetEntries.TryGetValue(source.TranslationKey, out string? actualTarget) ||
                    !string.Equals(actualTarget, expectedTarget, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Target language resource '{group.Key}' does not contain the expected translation for " +
                        $"externalized key '{source.TranslationKey}'.");
                }
            }
        }

        string fallbackPath = CreateExternalizedFallbackPath();
        string targetPath = CreateExternalizedTargetPath();
        if (string.Equals(fallbackPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (IGrouping<string, ExternalizedSourceDescriptor> group in _externalizedSources.GroupBy(
                     _ => fallbackPath,
                     StringComparer.OrdinalIgnoreCase))
        {
            byte[] fallbackBytes = await ReadStagedEntryBytesAsync(
                    artifact,
                    artifactGuard,
                    group.Key,
                    MaximumTextResourceBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            Dictionary<string, string> fallbackEntries = JsonResourceEditor
                .ReadLanguageEntries(StrictUtf8.GetString(fallbackBytes))
                .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
            foreach (ExternalizedSourceDescriptor source in group)
            {
                if (!fallbackEntries.TryGetValue(source.TranslationKey, out string? fallback) ||
                    !string.Equals(fallback, source.OriginalText, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Fallback language resource '{group.Key}' does not preserve the original text for '{source.TranslationKey}'.");
                }
            }
        }
    }

    private static async Task<byte[]> ReadStagedEntryBytesAsync(
        StagedArtifact artifact,
        DirectoryMutationGuard artifactGuard,
        string archivePath,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (artifact.IsDirectory)
        {
            string path = ArchivePathSafety.CombineArchivePath(artifact.StagedPath, archivePath);
            artifactGuard.EnsurePinsDirectory(artifact.StagedPath);
            string parent = Path.GetDirectoryName(path)
                ?? throw new InvalidDataException($"Staged entry has no safe parent: '{archivePath}'.");
            using DirectoryMutationGuard? parentGuard =
                DirectoryMutationGuard.OpenExistingChildDirectoryForMutation(
                    artifact.StagedPath,
                    parent);
            using var fileGuard = DirectoryMutationGuard.OpenFileForMutation(path);
            using var content = new MemoryStream(capacity: Math.Min(maximumBytes, 64 * 1024));
            (long length, _) = await fileGuard
                .CopyFileToStreamAsync(content, maximumBytes, cancellationToken)
                .ConfigureAwait(false);
            if (length > int.MaxValue)
            {
                throw new InvalidDataException($"Staged entry '{archivePath}' exceeds the verification size limit.");
            }

            return content.ToArray();
        }

        await using var stream = new FileStream(
            artifact.StagedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: Encoding.UTF8);
        ZipArchiveEntry entry = archive.Entries.SingleOrDefault(item =>
                string.Equals(item.FullName, archivePath, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Staged archive is missing '{archivePath}'.");
        if (entry.Length > maximumBytes || entry.Length > int.MaxValue)
        {
            throw new InvalidDataException($"Staged entry '{archivePath}' exceeds the verification size limit.");
        }

        await using Stream entryStream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        await entryStream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private ArchiveEntrySnapshot FindTemplateForNewEntry(string targetPath)
    {
        SourceEntryDescriptor? descriptor = _sourceEntries.Values.FirstOrDefault(
            entry => string.Equals(entry.TargetArchivePath, targetPath, StringComparison.OrdinalIgnoreCase));
        ArchiveEntrySnapshot? template = descriptor is null
            ? null
            : descriptor.Kind == TranslatableResourceKind.ExternalizedLanguageJson
                ? FindEntry(CreateExternalizedFallbackPath())
                : FindEntry(descriptor.RelativePath);
        return template ?? new ArchiveEntrySnapshot(
            int.MaxValue,
            targetPath,
            IsDirectory: false,
            DateTimeOffset.Now,
            ExternalAttributes: 0,
            CompressionKind.Compressed,
            string.Empty);
    }

    private ArchiveEntrySnapshot? FindEntry(string archivePath) =>
        _entries.FirstOrDefault(entry =>
            string.Equals(entry.ArchivePath, archivePath, StringComparison.OrdinalIgnoreCase));

    private string GetExtractedFile(string archivePath)
    {
        ArchiveEntrySnapshot entry = FindEntry(archivePath)
            ?? throw new InvalidDataException($"Scanned resource '{archivePath}' is not in the extracted inventory.");
        if (entry.IsDirectory || !File.Exists(entry.ExtractedPath))
        {
            throw new InvalidDataException($"Scanned resource is not an extracted regular file: '{archivePath}'.");
        }

        ArchivePathSafety.EnsureChildPath(_extractionPath, entry.ExtractedPath);
        return entry.ExtractedPath;
    }

    private async Task<byte[]> ReadExtractedClassBytesAsync(
        ArchiveEntrySnapshot entry,
        CancellationToken cancellationToken)
    {
        ArchivePathSafety.EnsureChildPath(_extractionPath, entry.ExtractedPath);
        RejectReparsePoint(entry.ExtractedPath);
        var info = new FileInfo(entry.ExtractedPath);
        if (!info.Exists || info.Length > MaximumClassFileBytes || info.Length > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Class '{entry.ArchivePath}' is missing or exceeds the safe-subset size limit.");
        }

        return await File.ReadAllBytesAsync(entry.ExtractedPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadTextResourceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumTextResourceBytes)
        {
            throw new InvalidDataException($"Text resource exceeds {MaximumTextResourceBytes} bytes: '{path}'.");
        }

        string text = await File.ReadAllTextAsync(path, StrictUtf8, cancellationToken).ConfigureAwait(false);
        return text.TrimStart('\uFEFF');
    }

    private List<ArtifactTarget> CreateArtifactTargetsForRequest(string outputPath)
    {
        bool directoryOutput = _folderSource &&
            !string.Equals(Path.GetExtension(outputPath), ".zip", StringComparison.OrdinalIgnoreCase);
        string extension = directoryOutput ? string.Empty : Path.GetExtension(outputPath);
        string stem = extension.Length == 0 ? outputPath : outputPath[..^extension.Length];
        var result = new List<ArtifactTarget>();
        foreach (TranslationStyle style in _request.Styles.Order())
        {
            string target = result.Count == 0
                ? outputPath
                : directoryOutput
                    ? $"{outputPath}.{style.ToString().ToLowerInvariant()}"
                    : $"{stem}.{style.ToString().ToLowerInvariant()}{extension}";
            result.Add(new ArtifactTarget(style, target, directoryOutput));
        }

        return result;
    }

    private static HardcodedStringCandidate MapHardcodedCandidate(
        NativeClassStringReference reference,
        string modId)
    {
        string keyStem = string.Concat(reference.Value
            .ToLowerInvariant()
            .Select(static character => char.IsLetterOrDigit(character) ? character : '_'))
            .Trim('_');
        if (keyStem.Length == 0)
        {
            keyStem = "text";
        }

        if (keyStem.Length > 40)
        {
            keyStem = keyStem[..40].TrimEnd('_');
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference.Value)))[..8].ToLowerInvariant();
        return new HardcodedStringCandidate(
            reference.ArchiveIndex,
            reference.ArchivePath,
            reference.Class,
            reference.Method,
            reference.Descriptor,
            checked((int)reference.BytecodeOffset),
            reference.Opcode,
            reference.ConstantPoolIndex,
            reference.Value,
            $"{modId}.hardcoded.{keyStem}.{hash}",
            IsRecognizedSafePattern: false);
    }

    private static async Task<byte[]> ReadArchiveCommentAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        const int maximumTailBytes = ushort.MaxValue + 22;
        int tailLength = (int)Math.Min(stream.Length, maximumTailBytes);
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        await stream.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
        for (int index = tail.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) != 0x06054B50)
            {
                continue;
            }

            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, 2));
            if (index + 22 + commentLength == tail.Length)
            {
                return tail.AsSpan(index + 22, commentLength).ToArray();
            }
        }

        return Array.Empty<byte>();
    }

    private static async Task ApplyArchiveCommentAsync(
        string archivePath,
        byte[] comment,
        CancellationToken cancellationToken)
    {
        if (comment.Length > ushort.MaxValue)
        {
            throw new InvalidDataException("ZIP archive comment exceeds the format limit.");
        }

        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length < 22)
        {
            throw new InvalidDataException("Staged ZIP has no end-of-central-directory record.");
        }

        var eocd = new byte[22];
        stream.Position = stream.Length - eocd.Length;
        await stream.ReadExactlyAsync(eocd, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(eocd) != 0x06054B50 ||
            BinaryPrimitives.ReadUInt16LittleEndian(eocd.AsSpan(20, 2)) != 0)
        {
            throw new InvalidDataException("Staged ZIP end-of-central-directory record was not in the expected form.");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(20, 2), checked((ushort)comment.Length));
        stream.Position = stream.Length - eocd.Length;
        await stream.WriteAsync(eocd, cancellationToken).ConfigureAwait(false);
        stream.Position = stream.Length;
        await stream.WriteAsync(comment, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeLocale(string locale)
    {
        if (!TranslationLanguageCatalog.TryNormalizeMinecraftLocaleToken(locale, out string? normalized))
        {
            throw new InvalidDataException($"Unsafe or unsupported locale identifier '{locale}'.");
        }

        return normalized;
    }

    private static string CreateStableId(string relativePath, string? key) =>
        $"{relativePath.Replace('\\', '/').TrimStart('/')}\0{key ?? string.Empty}";

    private static string CreateCandidateIdentity(HardcodedStringCandidate candidate) =>
        $"{candidate.ArchiveIndex}\0{candidate.ArchivePath}\0{candidate.ClassName}" +
        $"\0{candidate.MethodName}\0{candidate.MethodDescriptor}\0{candidate.BytecodeOffset}" +
        $"\0{candidate.Opcode}\0{candidate.ConstantPoolIndex}";

    private static string CreatePackageIdentity(NativeModMetadata metadata) =>
        $"{metadata.PrimaryLoader ?? "unknown"}:{metadata.PrimaryModId}";

    private static MinecraftContentKind ClassifyContentKind(ArchiveScanManifest manifest)
    {
        bool hasLoaderMetadata = !string.IsNullOrWhiteSpace(manifest.ModMetadata.PrimaryLoader) ||
            manifest.ModMetadata.Sources.Count > 0;
        bool hasJavaClasses = manifest.Archive.Entries.Any(static entry =>
            string.Equals(entry.EntryType, "file", StringComparison.OrdinalIgnoreCase) &&
            entry.Path.EndsWith(".class", StringComparison.OrdinalIgnoreCase));
        bool usesJarExtension = manifest.Source.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
        if (hasLoaderMetadata || hasJavaClasses)
        {
            return MinecraftContentKind.Mod;
        }

        bool hasShaderPackRoot = manifest.Archive.Entries.Any(static entry =>
        {
            string path = entry.Path.Replace('\\', '/').TrimStart('/');
            if (!path.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(path, "shaders/shaders.properties", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("shaders/lang/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".lang", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".vsh", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".fsh", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".gsh", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".csh", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".glsl", StringComparison.OrdinalIgnoreCase);
        });
        if (hasShaderPackRoot)
        {
            return MinecraftContentKind.ShaderPack;
        }

        bool hasResourcePackStructure = manifest.Archive.Entries.Any(static entry =>
        {
            string path = entry.Path.Replace('\\', '/').TrimStart('/');
            return string.Equals(path, "pack.mcmeta", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "pack.txt", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase);
        });
        if (hasResourcePackStructure)
        {
            return MinecraftContentKind.ResourcePack;
        }

        return usesJarExtension
            ? MinecraftContentKind.Mod
            : MinecraftContentKind.Unknown;
    }

    private bool ShouldRemoveSignature(string archivePath) =>
        _request.SignedArchiveHandling == SignedArchiveHandling.CreateUnsignedCopy &&
        ArchivePathSafety.IsJarSignaturePath(archivePath);

    private bool VerifyWorkspaceRoot()
    {
        ArchivePathSafety.EnsureChildPath(_workspacesRoot, _workspacePath);
        if (!Directory.Exists(_workspacePath) ||
            (File.GetAttributes(_workspacePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The transaction workspace is missing or is a reparse point.");
        }

        return true;
    }

    private void SafeDeleteWorkspace()
    {
        if (!_workspaceVerified || !Directory.Exists(_workspacePath))
        {
            return;
        }

        ArchivePathSafety.EnsureChildPath(_workspacesRoot, _workspacePath);
        if ((File.GetAttributes(_workspacePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The transaction workspace became a reparse point and will not be recursively deleted.");
        }

        DirectoryMutationGuard? workspaceGuard = _workspaceGuard;
        _workspaceGuard = null;
        if (workspaceGuard is null)
        {
            DeleteDirectoryTreeWithoutFollowingReparsePoints(_workspacePath, _workspacePath);
            return;
        }

        try
        {
            DirectoryMutationGuard.DeleteDirectoryTree(
                _workspacePath,
                _workspacePath,
                workspaceGuard);
        }
        finally
        {
            workspaceGuard.Dispose();
        }
    }

    private static void DeleteDirectoryTreeWithoutFollowingReparsePoints(string root, string directory)
    {
        if (!string.Equals(
                ArchivePathSafety.Canonicalize(root),
                ArchivePathSafety.Canonicalize(directory),
                StringComparison.OrdinalIgnoreCase))
        {
            ArchivePathSafety.EnsureChildPath(root, directory);
        }

        DirectoryMutationGuard.DeleteDirectoryTree(directory);
    }

    private void DeleteStagedFiles(List<string> errors)
    {
        foreach (StagedArtifact artifact in _stagedArtifacts)
        {
            try
            {
                if (artifact.IsDirectory && Directory.Exists(artifact.StagedPath))
                {
                    DeleteDirectoryTreeWithoutFollowingReparsePoints(
                        artifact.StagedPath,
                        artifact.StagedPath);
                }
                else if (File.Exists(artifact.StagedPath))
                {
                    var stagedDirectory = Path.GetDirectoryName(artifact.StagedPath)
                        ?? throw new InvalidOperationException("The staged artifact has no parent directory.");
                    using var parentGuard = DirectoryMutationGuard.OpenAncestry(stagedDirectory);
                    using var fileGuard = DirectoryMutationGuard.OpenFileForMutation(artifact.StagedPath);
                    fileGuard.DeleteLeaf();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{artifact.StagedPath}: {exception.Message}");
            }
        }
    }

    private void DeletePendingPublishedArtifacts(List<string> errors)
    {
        foreach ((string targetPath, bool isDirectory) in _pendingPublishedArtifacts.ToArray())
        {
            try
            {
                if (isDirectory)
                {
                    if (File.Exists(targetPath))
                    {
                        throw new IOException(
                            $"The pending directory output changed into a file: '{targetPath}'.");
                    }

                    if (Directory.Exists(targetPath))
                    {
                        DeleteDirectoryTreeWithoutFollowingReparsePoints(targetPath, targetPath);
                    }
                }
                else
                {
                    if (Directory.Exists(targetPath))
                    {
                        throw new IOException(
                            $"The pending file output changed into a directory: '{targetPath}'.");
                    }

                    if (File.Exists(targetPath))
                    {
                        string parent = Path.GetDirectoryName(targetPath)
                            ?? throw new InvalidOperationException(
                                "The pending published artifact has no parent directory.");
                        using var parentGuard = DirectoryMutationGuard.OpenAncestry(parent);
                        using var fileGuard = DirectoryMutationGuard.OpenFileForMutation(targetPath);
                        fileGuard.DeleteLeaf();
                    }
                }

                _pendingPublishedArtifacts.Remove(targetPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{targetPath}: {exception.Message}");
            }
        }
    }

    private void ReleaseSourceLock()
    {
        _sourceLock?.Dispose();
        _sourceLock = null;
    }

    private void AddWarning(string warning)
    {
        if (!_warnings.Contains(warning, StringComparer.Ordinal))
        {
            _warnings.Add(warning);
            _journal.Write("warning", "recorded", warning);
        }
    }

    private void EnsureInspected()
    {
        if (_manifest is null)
        {
            throw new InvalidOperationException("The archive must be inspected before this operation.");
        }
    }

    private void EnsureExtracted()
    {
        EnsureInspected();
        if (!_extracted)
        {
            throw new InvalidOperationException("The archive must be extracted before this operation.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Workspace paths cannot be reparse points: '{path}'.");
        }
    }

    private static bool IsStored(string compressionMethod) =>
        compressionMethod.Contains("stored", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnixSymbolicLink(uint unixMode) => (unixMode & 0xF000) == 0xA000;

    private static FileAttributes GetRecoverableAttributes(int externalAttributes)
    {
        const FileAttributes restorable = FileAttributes.Archive |
            FileAttributes.Hidden |
            FileAttributes.NotContentIndexed |
            FileAttributes.ReadOnly |
            FileAttributes.System;
        return (FileAttributes)(externalAttributes & (int)restorable);
    }

    private static DateTimeOffset ClampZipTimestamp(DateTimeOffset timestamp)
    {
        DateTimeOffset minimum = new(1980, 1, 1, 0, 0, 0, timestamp.Offset);
        DateTimeOffset maximum = new(2107, 12, 31, 23, 59, 58, timestamp.Offset);
        return timestamp < minimum ? minimum : timestamp > maximum ? maximum : timestamp;
    }

    private sealed record ArtifactTarget(
        TranslationStyle Style,
        string Path,
        bool IsDirectory);

    private sealed record StagedArtifact(
        TranslationStyle Style,
        string StagedPath,
        string TargetPath,
        bool IsDirectory,
        byte[]? VerifiedSha256 = null);

    private sealed record CommitMutationLease(
        StagedArtifact Artifact,
        DirectoryMutationGuard Guard);

    private static async Task<byte[]> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }
}

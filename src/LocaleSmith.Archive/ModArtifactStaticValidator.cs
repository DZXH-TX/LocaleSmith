using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocaleSmith.Archive.ClassFile;

namespace LocaleSmith.Archive;

internal sealed record ArtifactStaticValidation(
    IReadOnlyList<string> BlockingErrors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> CompletedChecks,
    bool ContainsJavaBytecode,
    bool ContainsSourceBuild);

internal static partial class ModArtifactStaticValidator
{
    private const int MaximumTextBytes = 16 * 1024 * 1024;
    private const int MaximumClassBytes = 32 * 1024 * 1024;
    private const int MaximumReportedIssues = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async Task<ArtifactStaticValidation> ValidateAsync(
        string archivePath,
        HashSet<string> modifiedPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modifiedPaths);
        var blockingErrors = new List<string>();
        var warnings = new List<string>();
        var completedChecks = new List<string>();
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(
            stream,
            ZipArchiveMode.Read,
            leaveOpen: false,
            entryNameEncoding: Encoding.UTF8);

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var classNames = new HashSet<string>(StringComparer.Ordinal);
        var jsonDocuments = new Dictionary<string, JsonDocument>(StringComparer.OrdinalIgnoreCase);
        var manifestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var classEntries = new List<(ZipArchiveEntry Entry, string Path)>();
        var serviceDescriptors = new List<(ZipArchiveEntry Entry, string Path)>();
        try
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path;
                try
                {
                    path = ArchivePathSafety.ValidateArchiveRelativePath(entry.FullName);
                }
                catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
                {
                    AddIssue(blockingErrors, $"archive path '{entry.FullName}': {exception.Message}");
                    continue;
                }

                if (!entries.TryAdd(path.TrimEnd('/'), entry))
                {
                    AddIssue(blockingErrors, $"archive path collision: '{path}'.");
                    continue;
                }

                if (entry.Name.Length == 0)
                {
                    continue;
                }

                if (path.EndsWith(".class", StringComparison.OrdinalIgnoreCase))
                {
                    classEntries.Add((entry, path));
                    continue;
                }

                if (IsJsonPath(path))
                {
                    JsonDocument? document = await ValidateJsonAsync(
                            entry,
                            path,
                            modifiedPaths.Contains(path),
                            blockingErrors,
                            warnings,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (document is not null)
                    {
                        jsonDocuments.Add(path, document);
                    }

                    continue;
                }

                if (IsLanguageLangPath(path))
                {
                    await ValidateLangAsync(
                            entry,
                            path,
                            modifiedPaths.Contains(path),
                            blockingErrors,
                            warnings,
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(path, "META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase))
                {
                    await ValidateManifestAsync(
                            entry,
                            manifestHeaders,
                            modifiedPaths.Contains(path),
                            blockingErrors,
                            warnings,
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (path.StartsWith("META-INF/services/", StringComparison.OrdinalIgnoreCase))
                {
                    serviceDescriptors.Add((entry, path));
                    continue;
                }

                if (path.EndsWith(".accesswidener", StringComparison.OrdinalIgnoreCase))
                {
                    await ValidateAccessWidenerAsync(entry, path, warnings, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            completedChecks.Add("archive-reopen-and-safe-paths");
            completedChecks.Add("json-lang-and-manifest-syntax");
            bool multiRelease = manifestHeaders.TryGetValue("Multi-Release", out string? multiReleaseValue) &&
                string.Equals(multiReleaseValue.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            int validClassFileCount = 0;
            foreach ((ZipArchiveEntry entry, string path) in classEntries)
            {
                if (await ValidateClassAsync(
                        entry,
                        path,
                        multiRelease,
                        classNames,
                        blockingErrors,
                        warnings,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    validClassFileCount++;
                }
            }

            if (classEntries.Count > 0)
            {
                completedChecks.Add("java-class-structure-and-bytecode");
            }

            foreach ((ZipArchiveEntry entry, string path) in serviceDescriptors)
            {
                await ValidateServiceDescriptorAsync(
                        entry,
                        path,
                        classNames,
                        warnings,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            ValidateFabricReferences(jsonDocuments, entries, classNames, warnings);
            ValidateQuiltReferences(jsonDocuments, entries, classNames, warnings);
            ValidateMixinReferences(jsonDocuments, entries, classNames, warnings);
            ValidateManifestReferences(manifestHeaders, entries, warnings);
            await ValidateForgeResourceReferencesAsync(entries, warnings, cancellationToken).ConfigureAwait(false);
            completedChecks.Add("loader-service-and-resource-references");

            bool containsJavaSource = entries.Any(pair =>
                pair.Value.Name.Length > 0 && IsSourceCodePath(pair.Key));
            bool containsBuildEntry = entries.Any(pair =>
                pair.Value.Name.Length > 0 && IsSourceBuildEntry(pair.Key));
            bool containsSourceBuild = containsJavaSource || containsBuildEntry;
            if (containsSourceBuild)
            {
                AddIssue(
                    blockingErrors,
                    "source or Gradle/build entries were detected. The archive pipeline will not execute untrusted build scripts; " +
                    "compile this source in an explicitly trusted build workflow and translate the resulting artifact.");
            }

            return new ArtifactStaticValidation(
                blockingErrors.AsReadOnly(),
                warnings.AsReadOnly(),
                completedChecks.AsReadOnly(),
                validClassFileCount > 0,
                containsSourceBuild);
        }
        finally
        {
            foreach (JsonDocument document in jsonDocuments.Values)
            {
                document.Dispose();
            }
        }
    }

    private static async Task<bool> ValidateClassAsync(
        ZipArchiveEntry entry,
        string path,
        bool multiRelease,
        HashSet<string> classNames,
        ICollection<string> blockingErrors,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await ReadEntryAsync(entry, MaximumClassBytes, cancellationToken).ConfigureAwait(false);
            ParsedClassFile parsed = ParsedClassFile.Parse(bytes);
            ClassPathResolution resolution = ResolveClassPath(path, multiRelease);
            string pathClassName = resolution.ClassName;
            if (!string.Equals(parsed.ClassName, pathClassName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"declares class '{parsed.ClassName}', but its archive path identifies '{pathClassName}'.");
            }

            if (resolution.RuntimeVisible)
            {
                classNames.Add(parsed.ClassName);
            }
            else
            {
                AddIssue(
                    warnings,
                    $"class '{path}' is versioned but META-INF/MANIFEST.MF does not declare Multi-Release: true; " +
                    "it cannot satisfy runtime Loader or service references.");
            }

            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or DecoderFallbackException)
        {
            AddIssue(blockingErrors, $"class '{path}': {exception.Message}");
            return false;
        }
    }

    private static async Task<JsonDocument?> ValidateJsonAsync(
        ZipArchiveEntry entry,
        string path,
        bool modified,
        ICollection<string> blockingErrors,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await ReadEntryAsync(entry, MaximumTextBytes, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> jsonBytes = RemoveUtf8Bom(bytes);
            var reader = new Utf8JsonReader(jsonBytes.Span, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
            using JsonDocument duplicateCheck = JsonDocument.ParseValue(ref reader);
            if (reader.Read())
            {
                throw new InvalidDataException("trailing JSON values are not allowed.");
            }

            RecordDuplicateJsonProperties(
                duplicateCheck.RootElement,
                path,
                modified ? blockingErrors : warnings);
            JsonDocument document = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions { MaxDepth = 128 });
            if (IsLanguageJsonPath(path))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Minecraft language JSON root must be an object.");
                }

                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException(
                            $"Minecraft language key '{property.Name}' must have a string value.");
                    }
                }
            }

            return document;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or IOException or DecoderFallbackException)
        {
            ICollection<string> issues = modified ? blockingErrors : warnings;
            AddIssue(issues, $"JSON '{path}': {exception.Message}");
            return null;
        }
    }

    private static async Task ValidateLangAsync(
        ZipArchiveEntry entry,
        string path,
        bool modified,
        ICollection<string> blockingErrors,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            string text = StrictUtf8.GetString(
                    await ReadEntryAsync(entry, MaximumTextBytes, cancellationToken).ConfigureAwait(false))
                .TrimStart('\uFEFF');
            var keys = new HashSet<string>(StringComparer.Ordinal);
            int lineNumber = 0;
            foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                lineNumber++;
                string trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] is '#' or '!')
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0 || string.IsNullOrWhiteSpace(line[..separator]))
                {
                    throw new InvalidDataException($"line {lineNumber} has no non-empty key followed by '='.");
                }

                string key = line[..separator].Trim();
                if (!keys.Add(key))
                {
                    throw new InvalidDataException($"line {lineNumber} repeats key '{key}'.");
                }
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or DecoderFallbackException)
        {
            AddIssue(
                modified ? blockingErrors : warnings,
                $"language file '{path}': {exception.Message}");
        }
    }

    private static async Task ValidateManifestAsync(
        ZipArchiveEntry entry,
        Dictionary<string, string> headers,
        bool modified,
        ICollection<string> blockingErrors,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await ReadEntryAsync(entry, MaximumTextBytes, cancellationToken).ConfigureAwait(false);
            _ = JarManifestDocument.Parse(bytes);
            foreach ((string name, string value) in ReadMainManifestHeaders(bytes))
            {
                headers[name] = value;
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or DecoderFallbackException)
        {
            AddIssue(
                modified ? blockingErrors : warnings,
                $"manifest 'META-INF/MANIFEST.MF': {exception.Message}");
        }
    }

    private static async Task ValidateServiceDescriptorAsync(
        ZipArchiveEntry entry,
        string path,
        HashSet<string> classNames,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            string serviceName = path["META-INF/services/".Length..];
            ValidateBinaryClassName(serviceName, "service interface");
            string text = StrictUtf8.GetString(
                    await ReadEntryAsync(entry, MaximumTextBytes, cancellationToken).ConfigureAwait(false))
                .TrimStart('\uFEFF');
            var providers = new HashSet<string>(StringComparer.Ordinal);
            foreach (string rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                string provider = rawLine.Split('#', 2)[0].Trim();
                if (provider.Length == 0)
                {
                    continue;
                }

                ValidateBinaryClassName(provider, "service provider");
                if (!providers.Add(provider))
                {
                    throw new InvalidDataException($"service provider '{provider}' is duplicated.");
                }

                if (!classNames.Contains(provider.Replace('.', '/')))
                {
                    throw new InvalidDataException($"service provider class '{provider}' is not packaged in the artifact.");
                }
            }

            if (providers.Count == 0)
            {
                throw new InvalidDataException("service descriptor contains no provider classes.");
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or DecoderFallbackException)
        {
            AddIssue(warnings, $"service descriptor '{path}': {exception.Message}");
        }
    }

    private static async Task ValidateAccessWidenerAsync(
        ZipArchiveEntry entry,
        string path,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            string text = StrictUtf8.GetString(
                    await ReadEntryAsync(entry, MaximumTextBytes, cancellationToken).ConfigureAwait(false))
                .TrimStart('\uFEFF');
            string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            string? header = lines.Select(static line => line.Split('#', 2)[0].Trim())
                .FirstOrDefault(static line => line.Length > 0);
            if (header is null || !AccessWidenerHeaderRegex().IsMatch(header))
            {
                throw new InvalidDataException("access widener header is missing or malformed.");
            }

            foreach (string rawLine in lines.SkipWhile(line => !string.Equals(
                         line.Split('#', 2)[0].Trim(),
                         header,
                         StringComparison.Ordinal)).Skip(1))
            {
                string line = rawLine.Split('#', 2)[0].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                string[] parts = WhitespaceRegex().Split(line);
                string operation = parts[0].StartsWith("transitive-", StringComparison.Ordinal)
                    ? parts[0]["transitive-".Length..]
                    : parts[0];
                if (operation is not ("accessible" or "extendable" or "mutable") ||
                    parts.Length < 3 || parts[1] is not ("class" or "method" or "field"))
                {
                    throw new InvalidDataException($"access widener line is malformed: '{line}'.");
                }

                if (parts[1] == "class" && parts.Length != 3 ||
                    parts[1] is "method" or "field" && parts.Length != 5)
                {
                    throw new InvalidDataException($"access widener line has an invalid field count: '{line}'.");
                }

                if (parts[1] == "method" && !JavaDescriptorValidator.IsMethodDescriptor(parts[4]) ||
                    parts[1] == "field" && !JavaDescriptorValidator.IsFieldDescriptor(parts[4]))
                {
                    throw new InvalidDataException($"access widener descriptor is malformed: '{line}'.");
                }
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or DecoderFallbackException)
        {
            AddIssue(warnings, $"access widener '{path}': {exception.Message}");
        }
    }

    private static void ValidateFabricReferences(
        Dictionary<string, JsonDocument> documents,
        Dictionary<string, ZipArchiveEntry> entries,
        HashSet<string> classNames,
        ICollection<string> errors)
    {
        if (!documents.TryGetValue("fabric.mod.json", out JsonDocument? document) ||
            document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        JsonElement root = document.RootElement;
        ValidateEntrypoints(root, "entrypoints", "fabric.mod.json", classNames, errors);
        ValidateClassMap(root, "languageAdapters", "fabric.mod.json", classNames, errors);
        ValidatePathProperty(root, "accessWidener", "fabric.mod.json", entries, errors);
        ValidateIconProperty(root, "icon", "fabric.mod.json", entries, errors);
        ValidateMixinList(root, "mixins", "fabric.mod.json", entries, errors);
        if (root.TryGetProperty("jars", out JsonElement jars) && jars.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in jars.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("file", out JsonElement file) &&
                    file.ValueKind == JsonValueKind.String)
                {
                    RequireEntry(file.GetString(), "fabric.mod.json jars.file", entries, errors);
                }
            }
        }
    }

    private static void ValidateQuiltReferences(
        Dictionary<string, JsonDocument> documents,
        Dictionary<string, ZipArchiveEntry> entries,
        HashSet<string> classNames,
        ICollection<string> errors)
    {
        if (!documents.TryGetValue("quilt.mod.json", out JsonDocument? document) ||
            document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        JsonElement root = document.RootElement;
        if (root.TryGetProperty("quilt_loader", out JsonElement loader) && loader.ValueKind == JsonValueKind.Object)
        {
            ValidateEntrypoints(loader, "entrypoints", "quilt.mod.json", classNames, errors);
            ValidatePathProperty(loader, "access_widener", "quilt.mod.json", entries, errors);
            ValidatePathArray(loader, "jars", "quilt.mod.json", entries, errors);
        }

        ValidateMixinList(root, "mixin", "quilt.mod.json", entries, errors);
    }

    private static void ValidateMixinReferences(
        Dictionary<string, JsonDocument> documents,
        Dictionary<string, ZipArchiveEntry> entries,
        HashSet<string> classNames,
        ICollection<string> errors)
    {
        foreach ((string path, JsonDocument document) in documents.Where(static pair =>
                     pair.Key.EndsWith(".mixins.json", StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.EndsWith(".mixin.json", StringComparison.OrdinalIgnoreCase)))
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                AddIssue(errors, $"mixin config '{path}' root must be an object.");
                continue;
            }

            string package = root.TryGetProperty("package", out JsonElement packageElement) &&
                packageElement.ValueKind == JsonValueKind.String
                ? packageElement.GetString() ?? string.Empty
                : string.Empty;
            foreach (string propertyName in new[] { "mixins", "client", "server" })
            {
                if (!root.TryGetProperty(propertyName, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        AddIssue(errors, $"mixin config '{path}' {propertyName} contains a non-string class reference.");
                        continue;
                    }

                    string relative = item.GetString() ?? string.Empty;
                    string className = string.IsNullOrEmpty(package) ? relative : $"{package}.{relative}";
                    RequireClass(className, $"mixin config '{path}'", classNames, errors);
                }
            }

            if (root.TryGetProperty("plugin", out JsonElement plugin) && plugin.ValueKind == JsonValueKind.String)
            {
                RequireClass(plugin.GetString(), $"mixin config '{path}' plugin", classNames, errors);
            }

            ValidatePathProperty(root, "refmap", $"mixin config '{path}'", entries, errors);
        }
    }

    private static void ValidateManifestReferences(
        Dictionary<string, string> headers,
        Dictionary<string, ZipArchiveEntry> entries,
        ICollection<string> errors)
    {
        if (!headers.TryGetValue("MixinConfigs", out string? mixinConfigs))
        {
            return;
        }

        foreach (string config in mixinConfigs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            RequireEntry(config, "manifest MixinConfigs", entries, errors);
        }
    }

    private static async Task ValidateForgeResourceReferencesAsync(
        Dictionary<string, ZipArchiveEntry> entries,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        foreach (string path in new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml" })
        {
            if (!entries.TryGetValue(path, out ZipArchiveEntry? entry))
            {
                continue;
            }

            try
            {
                string text = StrictUtf8.GetString(
                        await ReadEntryAsync(entry, MaximumTextBytes, cancellationToken).ConfigureAwait(false))
                    .TrimStart('\uFEFF');
                if (text.Contains('\0', StringComparison.Ordinal))
                {
                    throw new InvalidDataException("loader TOML contains a null character.");
                }

                foreach (Match match in ForgeLogoRegex().Matches(text))
                {
                    RequireEntry(match.Groups[1].Value, $"{path} logoFile", entries, errors);
                }
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or DecoderFallbackException)
            {
                AddIssue(errors, $"loader metadata '{path}': {exception.Message}");
            }
        }
    }

    private static void ValidateEntrypoints(
        JsonElement owner,
        string propertyName,
        string context,
        HashSet<string> classNames,
        ICollection<string> errors)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement entrypoints) ||
            entrypoints.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty group in entrypoints.EnumerateObject())
        {
            foreach (JsonElement item in EnumerateOneOrArray(group.Value))
            {
                string? value = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("value", out JsonElement objectValue) &&
                        objectValue.ValueKind == JsonValueKind.String
                        ? objectValue.GetString()
                        : null;
                if (value is null)
                {
                    AddIssue(errors, $"{context} entrypoint group '{group.Name}' contains an invalid value.");
                    continue;
                }

                RequireClass(value.Split("::", 2, StringSplitOptions.None)[0], $"{context} entrypoint", classNames, errors);
            }
        }
    }

    private static void ValidateClassMap(
        JsonElement owner,
        string propertyName,
        string context,
        HashSet<string> classNames,
        ICollection<string> errors)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement map) || map.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in map.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                AddIssue(errors, $"{context} {propertyName}.{property.Name} must be a class-name string.");
                continue;
            }

            RequireClass(property.Value.GetString(), $"{context} {propertyName}.{property.Name}", classNames, errors);
        }
    }

    private static void ValidateMixinList(
        JsonElement owner,
        string propertyName,
        string context,
        Dictionary<string, ZipArchiveEntry> entries,
        ICollection<string> errors)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement mixins))
        {
            return;
        }

        foreach (JsonElement item in EnumerateOneOrArray(mixins))
        {
            string? config = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("config", out JsonElement configElement) &&
                    configElement.ValueKind == JsonValueKind.String
                    ? configElement.GetString()
                    : null;
            if (config is null)
            {
                AddIssue(errors, $"{context} {propertyName} contains an invalid config reference.");
            }
            else
            {
                RequireEntry(config, $"{context} {propertyName}", entries, errors);
            }
        }
    }

    private static void ValidatePathProperty(
        JsonElement owner,
        string propertyName,
        string context,
        Dictionary<string, ZipArchiveEntry> entries,
        ICollection<string> errors)
    {
        if (owner.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            RequireEntry(value.GetString(), $"{context} {propertyName}", entries, errors);
        }
    }

    private static void ValidatePathArray(
        JsonElement owner,
        string propertyName,
        string context,
        Dictionary<string, ZipArchiveEntry> entries,
        ICollection<string> errors)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value))
        {
            return;
        }

        foreach (JsonElement item in EnumerateOneOrArray(value))
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                RequireEntry(item.GetString(), $"{context} {propertyName}", entries, errors);
            }
        }
    }

    private static void ValidateIconProperty(
        JsonElement owner,
        string propertyName,
        string context,
        Dictionary<string, ZipArchiveEntry> entries,
        ICollection<string> errors)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement icon))
        {
            return;
        }

        if (icon.ValueKind == JsonValueKind.String)
        {
            RequireEntry(icon.GetString(), $"{context} {propertyName}", entries, errors);
        }
        else if (icon.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in icon.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    RequireEntry(property.Value.GetString(), $"{context} {propertyName}.{property.Name}", entries, errors);
                }
            }
        }
    }

    private static void RequireEntry(
        string? path,
        string context,
        Dictionary<string, ZipArchiveEntry> entries,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            AddIssue(errors, $"{context} contains an empty resource path.");
            return;
        }

        try
        {
            string normalized = ArchivePathSafety.ValidateArchiveRelativePath(path).TrimEnd('/');
            if (!entries.ContainsKey(normalized))
            {
                AddIssue(errors, $"{context} references missing archive resource '{normalized}'.");
            }
        }
        catch (InvalidDataException exception)
        {
            AddIssue(errors, $"{context} contains unsafe resource path '{path}': {exception.Message}");
        }
    }

    private static void RequireClass(
        string? binaryName,
        string context,
        HashSet<string> classNames,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(binaryName))
        {
            AddIssue(errors, $"{context} contains an empty class reference.");
            return;
        }

        try
        {
            ValidateBinaryClassName(binaryName, context);
            string internalName = binaryName.Replace('.', '/');
            if (!classNames.Contains(internalName))
            {
                AddIssue(errors, $"{context} references missing class '{binaryName}'.");
            }
        }
        catch (InvalidDataException exception)
        {
            AddIssue(errors, exception.Message);
        }
    }

    private static void ValidateBinaryClassName(string value, string context)
    {
        if (!BinaryClassNameRegex().IsMatch(value))
        {
            throw new InvalidDataException($"{context} contains invalid Java binary class name '{value}'.");
        }
    }

    private static void RecordDuplicateJsonProperties(
        JsonElement element,
        string path,
        ICollection<string> warnings)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    AddIssue(warnings, $"JSON '{path}' repeats property '{property.Name}'.");
                }

                RecordDuplicateJsonProperties(property.Value, path, warnings);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                RecordDuplicateJsonProperties(item, path, warnings);
            }
        }
    }

    private static IEnumerable<JsonElement> EnumerateOneOrArray(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                yield return item;
            }
        }
        else
        {
            yield return value;
        }
    }

    private static Dictionary<string, string> ReadMainManifestHeaders(ReadOnlySpan<byte> bytes)
    {
        string text = StrictUtf8.GetString(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentName = null;
        foreach (string line in text.Split('\n'))
        {
            if (line.Length == 0)
            {
                break;
            }

            if (line[0] == ' ' && currentName is not null)
            {
                result[currentName] += line[1..];
                continue;
            }

            int separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            currentName = line[..separator];
            result[currentName] = line[(separator + 2)..];
        }

        return result;
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes || entry.Length > int.MaxValue)
        {
            throw new InvalidDataException($"entry exceeds the {maximumBytes}-byte static-analysis limit.");
        }

        await using Stream stream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static ReadOnlyMemory<byte> RemoveUtf8Bom(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            ? bytes.AsMemory(Encoding.UTF8.Preamble.Length)
            : bytes;

    private static ClassPathResolution ResolveClassPath(string path, bool multiRelease)
    {
        const string versionPrefix = "META-INF/versions/";
        if (!path.StartsWith(versionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new ClassPathResolution(path[..^".class".Length], RuntimeVisible: true);
        }

        int versionSeparator = path.IndexOf('/', versionPrefix.Length);
        if (versionSeparator <= versionPrefix.Length)
        {
            throw new InvalidDataException("multi-release class path has no numeric version directory.");
        }

        string versionToken = path[versionPrefix.Length..versionSeparator];
        if (!int.TryParse(
                versionToken,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int version) ||
            version < 9)
        {
            throw new InvalidDataException(
                $"multi-release class path has invalid Java version directory '{versionToken}'.");
        }

        string classPath = path[(versionSeparator + 1)..];
        if (classPath.Length <= ".class".Length)
        {
            throw new InvalidDataException("multi-release class path has no class name after its version directory.");
        }

        return new ClassPathResolution(
            classPath[..^".class".Length],
            RuntimeVisible: multiRelease);
    }

    private static bool IsJsonPath(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mcmeta", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mcmod.info", StringComparison.OrdinalIgnoreCase);

    private static bool IsLanguageJsonPath(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
        (path.Contains("/lang/", StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith("shaders/lang/", StringComparison.OrdinalIgnoreCase));

    private static bool IsLanguageLangPath(string path) =>
        path.EndsWith(".lang", StringComparison.OrdinalIgnoreCase) &&
        (path.Contains("/lang/", StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith("shaders/lang/", StringComparison.OrdinalIgnoreCase));

    private static bool IsSourceCodePath(string path) =>
        path.EndsWith(".java", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".kt", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".groovy", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".scala", StringComparison.OrdinalIgnoreCase);

    private static bool IsSourceBuildEntry(string path)
    {
        string fileName = path[(path.LastIndexOf('/') + 1)..];
        return fileName.Equals("build.gradle", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("settings.gradle", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("settings.gradle.kts", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("gradlew", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("gradlew.bat", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/gradle/wrapper/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("gradle/wrapper/", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIssue(ICollection<string> issues, string issue)
    {
        if (issues.Count < MaximumReportedIssues)
        {
            issues.Add(issue);
        }
        else if (issues.Count == MaximumReportedIssues)
        {
            issues.Add("additional static-analysis issues were omitted after the reporting limit was reached.");
        }
    }

    private sealed record ClassPathResolution(string ClassName, bool RuntimeVisible);

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]*(?:\\.[A-Za-z_$][A-Za-z0-9_$]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex BinaryClassNameRegex();

    [GeneratedRegex("^accessWidener\\s+v[12]\\s+[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AccessWidenerHeaderRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("(?im)^\\s*logoFile\\s*=\\s*[\"']([^\"']+)[\"']\\s*(?:#.*)?$")]
    private static partial Regex ForgeLogoRegex();
}

using LocaleSmith.App.Services;
using LocaleSmith.Infrastructure.Security;
using LocaleSmith.Presentation.Models;

namespace LocaleSmith.App.Tests;

public sealed class LegacyAppDataMigratorTests
{
    [Fact]
    public async Task MigratesConfigurationCredentialsMemoryAndLogsWithoutDeletingLegacyState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("localesmith-app-data-migration-");
        using var legacySecrets = new InMemorySecretStore();
        using var currentSecrets = new InMemorySecretStore();
        try
        {
            var legacyRoot = Path.Combine(root.FullName, "JaxI18n");
            var currentRoot = Path.Combine(root.FullName, "LocaleSmith");
            var credentialReference = "providers/test-source";
            var documentsRoot = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.MyDocuments);
            Assert.False(string.IsNullOrWhiteSpace(documentsRoot));
            await legacySecrets.SetAsync(credentialReference, "legacy-api-key".AsMemory(), cancellationToken);

            Directory.CreateDirectory(legacyRoot);
            using (var legacyConfiguration = new EncryptedJsonConfigurationStore<AppConfiguration>(
                       Path.Combine(legacyRoot, LegacyAppDataMigrator.LegacyConfigurationFileName),
                       LegacyAppDataMigrator.LegacyConfigurationPurpose,
                       new CredentialManagerMasterKeyStore(legacySecrets),
                       associatedDataNamespace: LegacyAppDataMigrator.LegacyAssociatedDataNamespace))
            {
                await legacyConfiguration.SaveAsync(new AppConfiguration
                {
                    IsOnboardingComplete = true,
                    WorkspacePath = Path.Combine(documentsRoot, "JaxI18n"),
                    SandboxPath = Path.Combine(Path.GetTempPath(), "JaxI18n", "Sandbox"),
                    ModelSources =
                    [
                        new ModelSourceProfile
                        {
                            Id = "test-source",
                            DisplayName = "Test source",
                            CredentialReference = credentialReference
                        }
                    ]
                }, cancellationToken);
            }

            var legacyMemory = Path.Combine(legacyRoot, "translation-memory", "legacy.json");
            var legacyLog = Path.Combine(legacyRoot, "logs", "cli-audit.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyMemory)!);
            Directory.CreateDirectory(Path.GetDirectoryName(legacyLog)!);
            await File.WriteAllTextAsync(legacyMemory, "legacy-memory", cancellationToken);
            await File.WriteAllTextAsync(legacyLog, "legacy-log", cancellationToken);

            var migrator = new LegacyAppDataMigrator(
                legacyRoot,
                currentRoot,
                legacySecrets,
                currentSecrets);
            await migrator.MigrateAsync(cancellationToken);

            using var currentConfiguration = new EncryptedJsonConfigurationStore<AppConfiguration>(
                Path.Combine(currentRoot, LegacyAppDataMigrator.CurrentConfigurationFileName),
                LegacyAppDataMigrator.CurrentConfigurationPurpose,
                new CredentialManagerMasterKeyStore(currentSecrets));
            var migrated = await currentConfiguration.LoadAsync(cancellationToken);
            Assert.NotNull(migrated);
            Assert.True(migrated.IsOnboardingComplete);
            Assert.Equal(Path.Combine(documentsRoot, "LocaleSmith"), migrated.WorkspacePath);
            Assert.Equal(
                Path.Combine(Path.GetTempPath(), "LocaleSmith", "Sandbox"),
                migrated.SandboxPath);
            Assert.Equal(credentialReference, Assert.Single(migrated.ModelSources).CredentialReference);

            using var currentCredential = await currentSecrets.ResolveAsync(credentialReference, cancellationToken);
            using var legacyCredential = await legacySecrets.ResolveAsync(credentialReference, cancellationToken);
            Assert.Equal("legacy-api-key", currentCredential?.DangerousGetString());
            Assert.Equal("legacy-api-key", legacyCredential?.DangerousGetString());
            Assert.Equal(
                "legacy-memory",
                await File.ReadAllTextAsync(
                    Path.Combine(currentRoot, "translation-memory", "legacy.json"),
                    cancellationToken));
            Assert.Equal(
                "legacy-log",
                await File.ReadAllTextAsync(Path.Combine(currentRoot, "logs", "cli-audit.jsonl"), cancellationToken));
            Assert.True(File.Exists(legacyMemory));
            Assert.True(File.Exists(legacyLog));

            await File.WriteAllTextAsync(
                Path.Combine(currentRoot, "translation-memory", "legacy.json"),
                "current-memory",
                cancellationToken);
            await migrator.MigrateAsync(cancellationToken);
            Assert.Equal(
                "current-memory",
                await File.ReadAllTextAsync(
                    Path.Combine(currentRoot, "translation-memory", "legacy.json"),
                    cancellationToken));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExistingCurrentConfigurationIsNeverOverwritten()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("localesmith-current-config-preservation-");
        using var legacySecrets = new InMemorySecretStore();
        using var currentSecrets = new InMemorySecretStore();
        try
        {
            var legacyRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "JaxI18n")).FullName;
            var currentRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "LocaleSmith")).FullName;
            using (var legacyConfiguration = new EncryptedJsonConfigurationStore<AppConfiguration>(
                       Path.Combine(legacyRoot, LegacyAppDataMigrator.LegacyConfigurationFileName),
                       LegacyAppDataMigrator.LegacyConfigurationPurpose,
                       new CredentialManagerMasterKeyStore(legacySecrets),
                       associatedDataNamespace: LegacyAppDataMigrator.LegacyAssociatedDataNamespace))
            {
                await legacyConfiguration.SaveAsync(
                    new AppConfiguration { Language = "legacy-language" },
                    cancellationToken);
            }

            var currentPath = Path.Combine(
                currentRoot,
                LegacyAppDataMigrator.CurrentConfigurationFileName);
            using (var currentConfiguration = new EncryptedJsonConfigurationStore<AppConfiguration>(
                       currentPath,
                       LegacyAppDataMigrator.CurrentConfigurationPurpose,
                       new CredentialManagerMasterKeyStore(currentSecrets)))
            {
                await currentConfiguration.SaveAsync(
                    new AppConfiguration { Language = "current-language" },
                    cancellationToken);
            }

            var migrator = new LegacyAppDataMigrator(
                legacyRoot,
                currentRoot,
                legacySecrets,
                currentSecrets);
            await migrator.MigrateAsync(cancellationToken);

            using var verificationStore = new EncryptedJsonConfigurationStore<AppConfiguration>(
                currentPath,
                LegacyAppDataMigrator.CurrentConfigurationPurpose,
                new CredentialManagerMasterKeyStore(currentSecrets));
            var preserved = await verificationStore.LoadAsync(cancellationToken);
            Assert.Equal("current-language", preserved?.Language);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}

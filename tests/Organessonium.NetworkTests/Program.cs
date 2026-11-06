using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static readonly List<(string Name, Func<Task<TestOutcome>> Run)> Tests = new()
    {
        ("startupFavoritesTrashHidden", StartupFavoritesTrashHiddenAsync),
        ("permanentDeletionBatchHidden", PermanentDeletionBatchHiddenAsync),
        ("encryptedLocalPreviewsAndLogout", EncryptedLocalPreviewsAndLogoutAsync),
        ("cachedSerialStatisticsAtStartup", CachedSerialStatisticsAtStartupAsync),
        ("browserMemoryIsSeparate", BrowserMemoryIsSeparateAsync),
        ("setupAndEncryptedPreviewHidden", SetupAndEncryptedPreviewHiddenAsync),
        ("embeddedMediaRuntime", EmbeddedMediaRuntimeAsync),
        ("directDownloadOutOfOrder", DirectDownloadOutOfOrderAsync),
        ("directDownloadSparseRegions", DirectDownloadSparseRegionsAsync),
        ("directDownloadReusesPayloadBuffers", DirectDownloadReusesPayloadBuffersAsync),
        ("directDownloadCheckpointScheduling", DirectDownloadCheckpointSchedulingAsync),
        ("directDownloadIncrementalDiscovery", DirectDownloadIncrementalDiscoveryAsync),
        ("directDownloadLegacyLayouts", DirectDownloadLegacyLayoutsAsync),
        ("directDownloadResumeCorruption", DirectDownloadResumeCorruptionAsync),
        ("directDownloadInterruptedWrite", DirectDownloadInterruptedWriteAsync),
        ("directDownloadLegacyMigration", DirectDownloadLegacyMigrationAsync),
        ("directDownloadAuthenticationAndFinalization", DirectDownloadAuthenticationAndFinalizationAsync),
        ("directDownloadCancellationAndIdentity", DirectDownloadCancellationAndIdentityAsync),
        ("directDownloadLargeOffsets", DirectDownloadLargeOffsetsAsync),
        ("directDownloadMemoryAdmission", DirectDownloadMemoryAdmissionAsync),
        ("thumbnailViewportAndCrashRecoveryHidden", ThumbnailViewportAndCrashRecoveryHiddenAsync),
        ("thumbnailLargeFolderScrollHidden", ThumbnailLargeFolderScrollHiddenAsync),
        ("folderInformationTotals", FolderInformationTotalsAsync),
        ("trayKeepsActiveTransfersHidden", TrayKeepsActiveTransfersHiddenAsync),
        ("serialFreshnessAndMetadataCache", SerialFreshnessAndMetadataCacheAsync),
        ("videoBulkLookupAndImmediatePlayer", VideoBulkLookupAndImmediatePlayerAsync),
        ("resumedTransfersAndNetworkHidden", ResumedTransfersAndNetworkHiddenAsync),
        ("downloadOfflineRecoveryHidden", DownloadOfflineRecoveryHiddenAsync),
        ("downloadColdStartWithoutJournalHidden", DownloadColdStartWithoutJournalHiddenAsync),
        ("fileChecksumsTransferAndResume", FileChecksumsTransferAndResumeAsync),
        ("uploadChecksumOverlapsNetworkAndResumes", UploadChecksumOverlapsNetworkAndResumesAsync),
        ("uploadPreemptsBackgroundCleanup", UploadPreemptsBackgroundCleanupAsync),
        ("maintenanceYieldsToMutations", MaintenanceYieldsToMutationsAsync),
        ("mutationIdentityAndWriteBatches", MutationIdentityAndWriteBatchesAsync),
        ("coldUploadJournalReadsInParallel", ColdUploadJournalReadsInParallelAsync),
        ("fileChecksumsRejectCorruption", FileChecksumsRejectCorruptionAsync),
        ("loggingPersistsOnlyErrors", LoggingPersistsOnlyErrorsAsync),
        ("diagnosticLoggingAndBounds", DiagnosticLoggingAndBoundsAsync),
        ("diagnosticHiddenBrowserAndStall", DiagnosticHiddenBrowserAndStallAsync),
        ("diagnosticAbruptExit", DiagnosticAbruptExitAsync),
        ("fileVersionsLifecycle", FileVersionsLifecycleAsync),
        ("fileActionsHiddenBrowser", FileActionsHiddenBrowserAsync),
        ("appearanceHiddenBrowser", AppearanceHiddenBrowserAsync),
        ("settingsPageHiddenBrowser", SettingsPageHiddenBrowserAsync),
        ("sharedStorageCapacityAndPacking", SharedStorageCapacityAndPackingAsync),
        ("sharedStorageJournalRouting", SharedStorageJournalRoutingAsync),
        ("sharedStoragePublicationPipeline", SharedStoragePublicationPipelineAsync),
        ("uploadBatchCadence", UploadBatchCadenceAsync),
        ("uploadPoolBoundary", UploadPoolBoundaryAsync),
        ("resumedUploadDestinationsAndCancellation", ResumedUploadDestinationsAndCancellationAsync),
        ("cancelledUploadOutboxRecovery", CancelledUploadOutboxRecoveryAsync),
        ("imageUploadCompanions", ImageUploadCompanionsAsync),
        ("coldPreviewJournalSnapshot", ColdPreviewJournalSnapshotAsync),
        ("transferPauseAndResume", TransferPauseAndResumeAsync),
        ("serialRefreshAndSilentReconciliation", SerialRefreshAndSilentReconciliationAsync),
        ("janitorArchivesOnlyIdle", JanitorArchivesOnlyIdleAsync),
        ("serialsAfterBatchMutations", SerialsAfterBatchMutationsAsync),
        ("mediaUploadTypes", MediaUploadTypesAsync),
        ("sharedStorageDelayedProducers", SharedStorageDelayedProducersAsync),
        ("sharedSingleUploadStartsImmediately", SharedSingleUploadStartsImmediatelyAsync),
        ("webViewUploadAcceptanceSecondary", UploadAcceptanceSecondaryAsync),
        ("sharedStorageReferencesDownloadsAndThumbnails", SharedStorageReferencesDownloadsAndThumbnailsAsync),
        ("sharedStorageSelectiveCleanupAndRecovery", SharedStorageSelectiveCleanupAndRecoveryAsync),
        ("sharedStorageVersionsTrashAndLegacy", SharedStorageVersionsTrashAndLegacyAsync),
        ("sharedStorageResumePermissionsAndPublication", SharedStorageResumePermissionsAndPublicationAsync),
        ("sharedStorageConcurrentOwnershipAndDeletion", SharedStorageConcurrentOwnershipAndDeletionAsync),
        ("sharedStorageControllerBatchAndLargeRouting", SharedStorageControllerBatchAndLargeRoutingAsync),
        ("sharedStorageCardsHiddenBrowser", SharedStorageCardsHiddenBrowserAsync),
        ("sharedStorageMixedRolesAndReadProtection", SharedStorageMixedRolesAndReadProtectionAsync),
        ("sharedStorageCreationAckAndMetadataFences", SharedStorageCreationAckAndMetadataFencesAsync),
        ("sharedStorageIndividualCancellation", SharedStorageIndividualCancellationAsync),
        ("metadataV1RoundTripAndErrors", MetadataV1RoundTripAndErrorsAsync),
        ("metadataLegacyAndEncryptedWrites", MetadataLegacyAndEncryptedWritesAsync),
        ("metadataV1Interoperability", MetadataV1InteroperabilityAsync),
        ("metadataEncryptedSerialCache", MetadataEncryptedSerialCacheAsync),
        ("metadataAtomicLegacyUpdate", MetadataAtomicLegacyUpdateAsync),
        ("twoComputerSyncLifecycle", TwoComputerSyncLifecycleAsync),
        ("syncFailuresPreserveFiles", SyncFailuresPreserveFilesAsync),
        ("syncScannerPathsAndPersistence", SyncScannerPathsAndPersistenceAsync),
        ("syncDiscordPublicationAndRecovery", SyncDiscordPublicationAndRecoveryAsync),
        ("webViewSyncControls", WebViewSyncControlsAsync),
        ("encryptedPayloadRoundTripAndChunks", EncryptedPayloadRoundTripAndChunksAsync),
        ("videoVariableChunkMapping", VideoVariableChunkMappingAsync),
        ("videoVirtualResourceRanges", VideoVirtualResourceRangesAsync),
        ("videoResponseBuffersBounded", VideoResponseBuffersBoundedAsync),
        ("videoMemoryAdmissionAndClose", VideoMemoryAdmissionAndCloseAsync),
        ("videoSeekAndDeduplication", VideoSeekAndDeduplicationAsync),
        ("videoPrefetchAndRelease", VideoPrefetchAndReleaseAsync),
        ("videoCloseCancelsRequiredRead", VideoCloseCancelsRequiredReadAsync),
        ("videoPlaybackPersistence", VideoPlaybackPersistenceAsync),
        ("videoEncryptedChunksAndRenewal", VideoEncryptedChunksAndRenewalAsync),
        ("videoTamperedPayloadNotExposed", VideoTamperedPayloadNotExposedAsync),
        ("videoStreamingAndFullDownloadIndependent", VideoStreamingAndFullDownloadIndependentAsync),
        ("mp4FastStartOffsetsAndFallback", Mp4FastStartOffsetsAndFallbackAsync),
        ("largeVirtualMp4Ranges", LargeVirtualMp4RangesAsync),
        ("mediaPreviewWorkerRamOnly", MediaPreviewWorkerRamOnlyAsync),
        ("thumbnailFormatsRamOnly", ThumbnailFormatsRamOnlyAsync),
        ("ffmpegLongPreviewStaged", FfmpegLongPreviewStagedAsync),
        ("individualUploadCancellationAndVisibility", IndividualUploadCancellationAndVisibilityAsync),
        ("smallUploadBatchIndicesAndRefresh", SmallUploadBatchIndicesAndRefreshAsync),
        ("uploadCardsHiddenBrowser", UploadCardsHiddenBrowserAsync),
        ("thumbnailWaitsForMemoryAndCancels", ThumbnailWaitsForMemoryAndCancelsAsync),
        ("generatedMediaHiddenBrowser", GeneratedMediaHiddenBrowserAsync),
        ("webViewVirtualVideoPlayback", WebViewVirtualVideoPlaybackAsync),
        ("encryptedFileRangeRoundTrip", EncryptedFileRangeRoundTripAsync),
        ("encryptedFileRangeUsesCallerOwnedBuffers", EncryptedFileRangeUsesCallerOwnedBuffersAsync),
        ("encryptedPayloadMessageProtocol", EncryptedPayloadMessageProtocolAsync),
        ("encryptedPayloadTamperAndTruncation", EncryptedPayloadTamperAndTruncationAsync),
        ("encryptedPreviewAndVideoPayloads", EncryptedPreviewAndVideoPayloadsAsync),
        ("urlSafeEncryptionSaltCompatibility", UrlSafeEncryptionSaltCompatibilityAsync),
        ("customKeyExportImportAndMismatch", CustomKeyExportImportAndMismatchAsync),
        ("legacyEntryHasNoEncryptionMetadata", LegacyEntryHasNoEncryptionMetadataAsync),
        ("urlSafeEncryptedMetadataIsLoaded", UrlSafeEncryptedMetadataIsLoadedAsync),
        ("folderSerialRoundTrip", FolderSerialRoundTripAsync),
        ("webViewLibraryRefreshAndLayout", WebViewLibraryRefreshAndLayoutAsync),
        ("webViewTrashLifecycle", WebViewTrashLifecycleAsync),
        ("favoriteAndDeviceMetadataCompatibility", FavoriteAndDeviceMetadataCompatibilityAsync),
        ("folderIndexMappingProtocol", FolderIndexMappingProtocolAsync),
        ("fourChannelSettingsRequireFoldersForum", FourChannelSettingsRequireFoldersForumAsync),
        ("logoutDataCleanupKeepsRuntimeFolders", LogoutDataCleanupKeepsRuntimeFoldersAsync),
        ("folderIndexReturnsOnlyDirectChildren", FolderIndexReturnsOnlyDirectChildrenAsync),
        ("serialMessageEnvelopeProtocol", SerialMessageEnvelopeProtocolAsync),
        ("staleSerialMappingRepublishesCachedSerial", StaleSerialMappingRepublishesCachedSerialAsync),
        ("favoritesSerialKeepsMessageIdAndCache", EmptyStandaloneSerialIsDeletedAsync),
        ("serialResetPreservesRemoteAndClearsLocalCache", SerialResetDeletesRemoteAndLocalCacheAsync),
        ("activeThreadsUseGuildRoute", ActiveThreadsUseGuildRouteAsync),
        ("forumThreadsIncludeArchivedPages", ForumThreadsIncludeArchivedPagesAsync),
        ("parallelMessageIndexUsesConcurrentWindows", ParallelMessageIndexUsesConcurrentWindowsAsync),
        ("parallelAttachmentUploadsBypassRouteLock", ParallelAttachmentUploadsBypassRouteLockAsync),
        ("multiAttachmentMultipartLimits", MultiAttachmentMultipartLimitsAsync),
        ("memoryBudgetHiddenBrowser", MemoryBudgetHiddenBrowserAsync),
        ("ramOnlyLargeUploadAndMetrics", RamOnlyLargeUploadAndMetricsAsync),
        ("uploadPreparationOverlapsSending", UploadPreparationOverlapsSendingAsync),
        ("uploadQuotaSnapshotsKeepSending", UploadQuotaSnapshotsKeepSendingAsync),
        ("tenAttachmentUploadsOverlapAcknowledgements", TenAttachmentUploadsOverlapAcknowledgementsAsync),
        ("streamingUploadReplayAndSourceIntegrity", StreamingUploadReplayAndSourceIntegrityAsync),
        ("liveUploadWorkersAndSlowStorage", LiveUploadWorkersAndSlowStorageAsync),
        ("multiAttachmentEncryptedResumeDownload", MultiAttachmentEncryptedResumeDownloadAsync),
        ("multiAttachmentMetadataCompatibility", MultiAttachmentMetadataCompatibilityAsync),
        ("multiAttachmentRetriesAndFallback", MultiAttachmentRetriesAndFallbackAsync),
        ("multiAttachmentFailuresCancellationAndLegacyResume", MultiAttachmentFailuresCancellationAndLegacyResumeAsync),
        ("multiAttachmentVideoPreviewSeek", MultiAttachmentVideoPreviewSeekAsync),
        ("discordLimitsRejectOversizedPayloads", DiscordLimitsRejectOversizedPayloadsAsync),
        ("discordGlobalPacingAcrossRoutes", DiscordGlobalPacingAcrossRoutesAsync),
        ("discordNumericChannelIsolation", DiscordNumericChannelIsolationAsync),
        ("discordBucketsReserveParallelRequests", DiscordBucketsReserveParallelRequestsAsync),
        ("discordBucketRefillDoesNotPauseUploads", DiscordBucketRefillDoesNotPauseUploadsAsync),
        ("discordParallelRepliesPreserveAvailableQuota", DiscordParallelRepliesPreserveAvailableQuotaAsync),
        ("discordPendingRetryBlocksFreshQuota", DiscordPendingRetryBlocksFreshQuotaAsync),
        ("discordGlobalRetryBlocksOtherRoutes", DiscordGlobalRetryBlocksOtherRoutesAsync),
        ("memoryAttachmentRetryKeepsCiphertext", MemoryAttachmentRetryKeepsCiphertextAsync),
        ("uploadBufferReclaimerReleasesLargeArrays", UploadBufferReclaimerReleasesLargeArraysAsync),
        ("uploadMemoryPlannerKeepsLargeFilesBounded", UploadMemoryPlannerKeepsLargeFilesBoundedAsync),
        ("uploadMemoryPlannerFallsBackUnderPressure", UploadMemoryPlannerFallsBackUnderPressureAsync),
        ("uploadConcurrencyDistributesPartsPerFile", UploadConcurrencyDistributesPartsPerFileAsync),
        ("adaptiveUploadGateUsesAggregateThroughput", AdaptiveUploadGateUsesAggregateThroughputAsync),
        ("forcedUploadKeepsSixteenWorkersAndReleasesBuffers", ForcedUploadKeepsSixteenWorkersAndReleasesBuffersAsync),
        ("webViewForcedUploadSettings", WebViewForcedUploadSettingsAsync),
        ("httpProxyForwardsGet", HttpProxyForwardsGetAsync),
        ("httpProxyAuthAcceptAndReject", HttpProxyAuthAcceptAndRejectAsync),
        ("socks5ProxyForwardsGet", Socks5ProxyForwardsGetAsync),
        ("socks5WrongCredentialsRejected", Socks5WrongCredentialsRejectedAsync),
        ("socks4aProxyForwardsGet", Socks4aProxyForwardsGetAsync),
        ("directModeBypassesProxy", DirectModeBypassesProxyAsync),
        ("refusedProxyClassified", RefusedProxyClassifiedAsync),
        ("unreachableProxyClassified", UnreachableProxyClassifiedAsync),
        ("autoDetectChoosesHttp", AutoDetectChoosesHttpAsync),
        ("autoDetectChoosesSocks5", AutoDetectChoosesSocks5Async),
        ("discordCheckThroughProxySucceeds", DiscordCheckThroughProxySucceedsAsync),
        ("failedProbeReportedAsUnreachable", FailedProbeReportedAsUnreachableAsync),
        ("syncHeadlessTrayWindowLifecycle", SyncHeadlessTrayWindowLifecycleAsync)
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--memory-budget-child")
        {
            var memory = new byte[96 * 1024 * 1024]; Array.Fill(memory, (byte)7);
            Console.WriteLine("MEMORY_READY"); await Task.Delay(TimeSpan.FromMinutes(2)); GC.KeepAlive(memory); return 0;
        }
        if (args.FirstOrDefault() == VideoPreviewWorker.Argument) return await VideoPreviewWorker.RunAsync(args);
        if (args.Length == 2 && args[0] == "--diagnostic-probe") return await RunDiagnosticProbeAsync(args[1]);
        if (args.Length == 1 && args[0] == "--live-upload-cadence")
            return await RunLiveUploadCadenceAsync();
        var passed = 0;
        var failed = 0;
        var allowUi = args.Contains("--include-ui", StringComparer.OrdinalIgnoreCase);
        var names = args.Where(arg => !arg.Equals("--include-ui", StringComparison.OrdinalIgnoreCase)).ToArray();
        static bool OpensWindow(string name) => name.StartsWith("webView", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("syncHeadlessTrayWindowLifecycle", StringComparison.OrdinalIgnoreCase);
        if (!allowUi && names.Any(OpensWindow))
        {
            Console.WriteLine("Teste gráfico não executado. A opção --include-ui é necessária e pode abrir janelas.");
            return 1;
        }
        var selected = Tests.Where(test => (names.Length == 0 || names.Contains(test.Name, StringComparer.OrdinalIgnoreCase)) &&
            (allowUi || !OpensWindow(test.Name))).ToList();
        foreach (var (name, run) in selected)
        {
            try
            {
                var outcome = await run();
                if (outcome.Ok)
                {
                    Console.WriteLine("PASS  " + name);
                    passed++;
                }
                else
                {
                    Console.WriteLine("FAIL  " + name + " :: " + outcome.Message);
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR " + name + " :: " + ex.Message);
                failed++;
            }
            finally
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {selected.Count}  Passou: {passed}  Falhou: {failed}");
        return failed == 0 ? 0 : 1;
    }

    private sealed record TestOutcome(bool Ok, string Message)
    {
        public static TestOutcome Pass() => new(true, "");
        public static TestOutcome Fail(string message) => new(false, message);
    }

    private static Task<TestOutcome> FourChannelSettingsRequireFoldersForumAsync()
    {
        var configured = new AppSettings
        {
            BotToken = "token",
            NamesChannelId = "names-id",
            FoldersForumId = "folders-id",
            AppsForumId = "files-id",
            SerialChannelId = "serial-id"
        };
        var cloned = configured.Clone();
        var missingFolders = configured.Clone();
        missingFolders.FoldersForumId = "";
        var valid = configured.IsConfigured &&
                    !missingFolders.IsConfigured &&
                    cloned.NamesChannelId == "names-id" &&
                    cloned.FoldersForumId == "folders-id" &&
                    cloned.AppsForumId == "files-id" &&
                    cloned.SerialChannelId == "serial-id";
        return Task.FromResult(valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("a configuração não preservou os quatro canais separados"));
    }

    private static Task<TestOutcome> LogoutDataCleanupKeepsRuntimeFoldersAsync()
    {
        var root = Directory.CreateTempSubdirectory("organessonium_logout_test_");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "WebView", "User Data"));
            Directory.CreateDirectory(Path.Combine(root.FullName, "Logs"));
            Directory.CreateDirectory(Path.Combine(root.FullName, "Previews"));
            Directory.CreateDirectory(Path.Combine(root.FullName, "Serials"));
            Directory.CreateDirectory(Path.Combine(root.FullName, "Transfers"));
            var databasePath = Path.Combine(root.FullName, "organessonium.db");
            bool databaseCleared;
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE settings (key TEXT);
                    CREATE TABLE app_entries (name TEXT);
                    CREATE TABLE folder_index_map (folder_id TEXT);
                    INSERT INTO settings VALUES ('old-token');
                    INSERT INTO app_entries VALUES ('old-file');
                    INSERT INTO folder_index_map VALUES ('old-folder');
                    """;
                command.ExecuteNonQuery();
                SettingsStore.ClearDatabaseContentsForLogout(connection);
                command.CommandText = "SELECT (SELECT COUNT(*) FROM settings) + (SELECT COUNT(*) FROM app_entries) + (SELECT COUNT(*) FROM folder_index_map)";
                databaseCleared = Convert.ToInt64(command.ExecuteScalar()) == 0;
            }
            SqliteConnection.ClearAllPools();
            File.WriteAllText(Path.Combine(root.FullName, "WebView", "User Data", "profile.dat"), "webview");
            File.WriteAllText(Path.Combine(root.FullName, "Logs", "session.log"), "log");

            var failures = SettingsStore.ClearDataDirectoryContents(root);
            var children = root.EnumerateFileSystemInfos().Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var valid = databaseCleared && failures.Count == 0 &&
                        children.SetEquals(["WebView", "Logs"]) &&
                        File.Exists(Path.Combine(root.FullName, "WebView", "User Data", "profile.dat")) &&
                        File.Exists(Path.Combine(root.FullName, "Logs", "session.log"));
            return Task.FromResult(valid
                ? TestOutcome.Pass()
                : TestOutcome.Fail("a limpeza de logout não preservou apenas WebView e Logs"));
        }
        finally
        {
            try { root.Delete(true); } catch { }
        }
    }

    private static async Task<TestOutcome> EncryptedPayloadRoundTripAndChunksAsync()
    {
        var sourceDir = Directory.CreateTempSubdirectory("organessonium_crypto_test_");
        try
        {
            var inputBytes = RandomNumberGenerator.GetBytes(280_000);
            var input = new FileInfo(Path.Combine(sourceDir.FullName, "original.bin"));
            await File.WriteAllBytesAsync(input.FullName, inputBytes);
            var parts = await ArchiveSplitter.SplitAsync(input, maxPartBytes: 64 * 1024);
            var service = CreateEncryptionService(out _);
            var freshInstallService = CreateEncryptionService(out _);
            var encryption = service.CreateFileEncryption(EncryptionModes.Standard);
            using var staged = await service.StageFilesAsync(parts, "file-roundtrip", encryption, EncryptedPayloadRoles.Original);
            if (staged.Files.Count < 2 || staged.Files.Any(part => !part.Name.StartsWith("chunk-", StringComparison.Ordinal) || !part.Name.EndsWith(".bin", StringComparison.Ordinal)))
            {
                return TestOutcome.Fail("as partes criptografadas não receberam nomes genéricos");
            }

            using var output = new MemoryStream();
            byte[]? firstPayload = null;
            foreach (var part in staged.Files.Select((file, index) => (file, index)))
            {
                var encrypted = await File.ReadAllBytesAsync(part.file.FullName);
                if (part.index == 0)
                {
                    firstPayload = encrypted.ToArray();
                }

                var plaintext = freshInstallService.DecryptBytes(
                    encrypted,
                    new EncryptedPayloadContext("file-roundtrip", encryption, EncryptedPayloadRoles.Original, part.index, staged.Files.Count));
                await output.WriteAsync(plaintext);
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(encrypted);
            }

            var outputBytes = output.ToArray();
            var matches = CryptographicOperations.FixedTimeEquals(inputBytes, outputBytes);
            var hashesMatch = CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(inputBytes),
                SHA256.HashData(outputBytes));
            var envelopeMatches = firstPayload != null && firstPayload.AsSpan(0, 8).SequenceEqual("ORGAESGC"u8);
            var nonceDiffers = firstPayload != null && staged.Files.Count > 1 &&
                                !(await File.ReadAllBytesAsync(staged.Files[1].FullName)).AsSpan(42, 12).SequenceEqual(firstPayload.AsSpan(42, 12));
            CryptographicOperations.ZeroMemory(firstPayload ?? []);
            CryptographicOperations.ZeroMemory(outputBytes);
            ArchiveSplitter.CleanupParts(parts, input);
            return matches && hashesMatch && envelopeMatches && nonceDiffers
                ? TestOutcome.Pass()
                : TestOutcome.Fail("a reconstrução ou os nonces dos chunks não correspondeu ao esperado");
        }
        finally
        {
            try { sourceDir.Delete(true); } catch { }
        }
    }

    private static Task<TestOutcome> EncryptedPayloadTamperAndTruncationAsync()
    {
        var service = CreateEncryptionService(out _);
        var encryption = service.CreateFileEncryption(EncryptionModes.Standard);
        var context = new EncryptedPayloadContext("file-tamper", encryption, EncryptedPayloadRoles.Original, 0);
        var payload = service.EncryptBytes("conteúdo de teste"u8, context);
        var tampered = payload.ToArray();
        tampered[^1] ^= 0x01;
        var tamperDetected = Throws<EncryptedPayloadAuthenticationException>(() => service.DecryptBytes(tampered, context));
        var truncated = payload[..^1];
        var truncationDetected = Throws<EncryptedPayloadFormatException>(() => service.DecryptBytes(truncated, context));
        CryptographicOperations.ZeroMemory(payload);
        CryptographicOperations.ZeroMemory(tampered);
        CryptographicOperations.ZeroMemory(truncated);
        return Task.FromResult(tamperDetected && truncationDetected
            ? TestOutcome.Pass()
            : TestOutcome.Fail("alteração ou truncamento não foi rejeitado"));
    }

    private static async Task<TestOutcome> EncryptedFileRangeRoundTripAsync()
    {
        var sourceDir = Directory.CreateTempSubdirectory("organessonium_range_test_");
        try
        {
            var inputBytes = RandomNumberGenerator.GetBytes(2 * EncryptionService.MaximumPlaintextPayloadBytes + 321);
            var input = new FileInfo(Path.Combine(sourceDir.FullName, "original.bin"));
            await File.WriteAllBytesAsync(input.FullName, inputBytes);
            var service = CreateEncryptionService(out _);
            var encryption = service.CreateFileEncryption(EncryptionModes.Standard);
            var totalParts = ArchiveSplitter.GetPartCount(inputBytes.LongLength, EncryptionService.MaximumPlaintextPayloadBytes);
            using var output = new MemoryStream();

            for (var index = 0; index < totalParts; index++)
            {
                var offset = (long)index * EncryptionService.MaximumPlaintextPayloadBytes;
                var length = (int)Math.Min(EncryptionService.MaximumPlaintextPayloadBytes, inputBytes.LongLength - offset);
                var target = new FileInfo(Path.Combine(sourceDir.FullName, $"chunk-{index + 1}.bin"));
                var context = new EncryptedPayloadContext(
                    "file-range",
                    encryption,
                    EncryptedPayloadRoles.Original,
                    index,
                    totalParts);
                await service.EncryptFileRangeAsync(input, offset, length, target, context);
                var payload = await File.ReadAllBytesAsync(target.FullName);
                var restored = service.DecryptBytes(payload, context);
                await output.WriteAsync(restored);
                CryptographicOperations.ZeroMemory(payload);
                CryptographicOperations.ZeroMemory(restored);
                target.Delete();
            }

            var outputBytes = output.ToArray();
            var matches = CryptographicOperations.FixedTimeEquals(inputBytes, outputBytes);
            CryptographicOperations.ZeroMemory(inputBytes);
            CryptographicOperations.ZeroMemory(outputBytes);
            return matches
                ? TestOutcome.Pass()
                : TestOutcome.Fail("a leitura e criptografia por faixa não reconstruíram o arquivo");
        }
        finally
        {
            try { sourceDir.Delete(true); } catch { }
        }
    }

    private static async Task<TestOutcome> EncryptedFileRangeUsesCallerOwnedBuffersAsync()
    {
        var sourceDir = Directory.CreateTempSubdirectory("organessonium_memory_range_test_");
        try
        {
            var inputBytes = RandomNumberGenerator.GetBytes(196_608);
            var source = new FileInfo(Path.Combine(sourceDir.FullName, "source.bin"));
            await File.WriteAllBytesAsync(source.FullName, inputBytes);
            const int offset = 4_096;
            const int length = 131_072;
            var plaintext = GC.AllocateUninitializedArray<byte>(length);
            var ciphertext = GC.AllocateUninitializedArray<byte>(length + EncryptionService.EnvelopeOverheadBytes);
            var service = CreateEncryptionService(out _);
            var encryption = service.CreateFileEncryption(EncryptionModes.Standard);
            var context = new EncryptedPayloadContext(
                "memory-range",
                encryption,
                EncryptedPayloadRoles.Original,
                1,
                3);
            try
            {
                var written = await service.EncryptFileRangeIntoAsync(
                    source,
                    offset,
                    length,
                    plaintext,
                    ciphertext,
                    context);
                var restored = service.DecryptBytes(ciphertext.AsSpan(0, written), context);
                try
                {
                    var matches = CryptographicOperations.FixedTimeEquals(
                        inputBytes.AsSpan(offset, length),
                        restored);
                    var plaintextCleared = plaintext.All(value => value == 0);
                    return matches && plaintextCleared && written == length + EncryptionService.EnvelopeOverheadBytes
                        ? TestOutcome.Pass()
                        : TestOutcome.Fail("o chunk em buffer não manteve o envelope, o conteúdo ou a limpeza do plaintext");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(restored);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(inputBytes);
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }
        finally
        {
            try { sourceDir.Delete(true); } catch { }
        }
    }

    private static Task<TestOutcome> UploadMemoryPlannerKeepsLargeFilesBoundedAsync()
    {
        const long mib = 1024L * 1024;
        const long gib = 1024L * mib;
        var snapshot = new UploadMemorySnapshot(
            TotalPhysicalBytes: 32 * gib,
            AvailablePhysicalBytes: 18 * gib,
            ProcessWorkingSetBytes: 238 * mib);
        var medium = UploadMemoryPlanner.CreatePlan(500 * mib, 16, UploadBufferStrategy.Auto, snapshot);
        var veryLarge = UploadMemoryPlanner.CreatePlan(50 * gib, 16, UploadBufferStrategy.Auto, snapshot);
        var valid = medium.UseMemory &&
                    veryLarge.UseMemory &&
                    medium.MemorySafeWorkers == 8 &&
                    veryLarge.MemorySafeWorkers == medium.MemorySafeWorkers &&
                    medium.EstimatedBufferBudgetBytes <= UploadMemoryPlanner.ProcessTargetBytes - snapshot.ProcessWorkingSetBytes;
        return Task.FromResult(valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("o plano de RAM não manteve limite constante para arquivos grandes"));
    }

    private static Task<TestOutcome> UploadMemoryPlannerFallsBackUnderPressureAsync()
    {
        const long mib = 1024L * 1024;
        const long gib = 1024L * mib;
        var lowMemory = new UploadMemorySnapshot(
            TotalPhysicalBytes: 8 * gib,
            AvailablePhysicalBytes: 1300 * mib,
            ProcessWorkingSetBytes: 412 * mib);
        var fallback = UploadMemoryPlanner.CreatePlan(500 * mib, 16, UploadBufferStrategy.Auto, lowMemory);
        var small = UploadMemoryPlanner.CreatePlan(20 * mib, 16, UploadBufferStrategy.Auto, new UploadMemorySnapshot(32 * gib, 20 * gib, 240 * mib));
        var forcedDisk = UploadMemoryPlanner.CreatePlan(500 * mib, 16, UploadBufferStrategy.Disk, new UploadMemorySnapshot(32 * gib, 20 * gib, 240 * mib));
        var valid = !fallback.UseMemory &&
                    fallback.MemorySafeWorkers == 0 &&
                    !small.UseMemory &&
                    !forcedDisk.UseMemory;
        return Task.FromResult(valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("o plano de RAM não voltou ao fallback em disco sob baixa memória"));
    }

    private static Task<TestOutcome> UploadConcurrencyDistributesPartsPerFileAsync()
    {
        var valid = UploadConcurrencyPlanner.PartsPerFile(16, 8) == 2 &&
                    UploadConcurrencyPlanner.PartsPerFile(16, 3) == 5 &&
                    UploadConcurrencyPlanner.PartsPerFile(8, 8) == 1 &&
                    UploadConcurrencyPlanner.PartsPerFile(16, 1) == 16;
        return Task.FromResult(valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("a concorrência global não foi distribuída entre os arquivos ativos"));
    }

    private static async Task<TestOutcome> AdaptiveUploadGateUsesAggregateThroughputAsync()
    {
        using var healthyGate = new AdaptiveUploadGate(16);
        var healthyLeases = new List<AdaptiveUploadGate.Lease>();
        for (var worker = 0; worker < 16; worker++)
        {
            healthyLeases.Add(await healthyGate.EnterAsync(CancellationToken.None));
        }

        for (var sample = 0; sample < 4; sample++)
        {
            healthyGate.ReportCompleted(10 * 1024 * 1024, TimeSpan.FromSeconds(50));
        }

        healthyLeases.ForEach(lease => lease.Dispose());

        using var slowGate = new AdaptiveUploadGate(16);
        var slowLeases = new List<AdaptiveUploadGate.Lease>();
        for (var worker = 0; worker < 16; worker++)
        {
            slowLeases.Add(await slowGate.EnterAsync(CancellationToken.None));
        }
        for (var sample = 0; sample < 4; sample++)
        {
            slowGate.ReportCompleted(10 * 1024 * 1024, TimeSpan.FromSeconds(1024));
        }

        slowLeases.ForEach(lease => lease.Dispose());

        var valid = healthyGate.CurrentLimit == 16 && slowGate.CurrentLimit is >= 2 and < 16;
        return valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail(
                $"limites incorretos para vazão agregada: saudável={healthyGate.CurrentLimit}, lenta={slowGate.CurrentLimit}");
    }

    private static Task<TestOutcome> EncryptedPayloadMessageProtocolAsync()
    {
        var context = new EncryptedPayloadContext(
            "file-message-protocol",
            new EncryptionMetadata { Version = 1, Mode = EncryptionModes.Standard, Salt = "NyasiAVslEKTO__DCbEeKg" },
            EncryptedPayloadRoles.Original,
            1,
            3);
        var message = EncryptedPayloadProtocol.Message(context, 3);
        var parsed = EncryptedPayloadProtocol.TryParse(message, out var payload);
        var legacyRejected = !EncryptedPayloadProtocol.TryParse(
            "OES1|file-message-protocol|original|2/3",
            out _);
        var matches = parsed && legacyRejected &&
                      payload.FileId == context.FileId &&
                      payload.Role == context.Role &&
                      payload.ChunkIndex == context.ChunkIndex &&
                      payload.TotalParts == context.ChunkCount;
        return Task.FromResult(matches
            ? TestOutcome.Pass()
            : TestOutcome.Fail("o formato de mensagem criptografada não corresponde ao padrão do servidor"));
    }

    private static Task<TestOutcome> EncryptedPreviewAndVideoPayloadsAsync()
    {
        var service = CreateEncryptionService(out _);
        var encryption = service.CreateFileEncryption(EncryptionModes.Standard);
        var preview = RandomNumberGenerator.GetBytes(1_024);
        var video = RandomNumberGenerator.GetBytes(4_096);
        var previewPayload = service.EncryptBytes(preview, new EncryptedPayloadContext("file-preview", encryption, EncryptedPayloadRoles.Preview, 0));
        var videoPayload = service.EncryptBytes(video, new EncryptedPayloadContext("file-preview", encryption, EncryptedPayloadRoles.Video480p, 0));
        var previewRoundTrip = service.DecryptBytes(previewPayload, new EncryptedPayloadContext("file-preview", encryption, EncryptedPayloadRoles.Preview, 0));
        var videoRoundTrip = service.DecryptBytes(videoPayload, new EncryptedPayloadContext("file-preview", encryption, EncryptedPayloadRoles.Video480p, 0));
        var valid = CryptographicOperations.FixedTimeEquals(preview, previewRoundTrip) &&
                    CryptographicOperations.FixedTimeEquals(video, videoRoundTrip);
        CryptographicOperations.ZeroMemory(preview);
        CryptographicOperations.ZeroMemory(video);
        CryptographicOperations.ZeroMemory(previewPayload);
        CryptographicOperations.ZeroMemory(videoPayload);
        CryptographicOperations.ZeroMemory(previewRoundTrip);
        CryptographicOperations.ZeroMemory(videoRoundTrip);
        return Task.FromResult(valid ? TestOutcome.Pass() : TestOutcome.Fail("prévia ou vídeo 480p não voltou idêntico"));
    }

    private static Task<TestOutcome> UrlSafeEncryptionSaltCompatibilityAsync()
    {
        var service = CreateEncryptionService(out _);
        var encryption = service.CreateFileEncryption(EncryptionModes.Standard);
        var generatedSaltIsCanonical = encryption.Salt.Length > 0 &&
            encryption.Salt.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
        if (!generatedSaltIsCanonical)
        {
            return Task.FromResult(TestOutcome.Fail("o salt novo não foi gravado em Base64URL sem padding"));
        }

        encryption.Salt = "NyasiAVslEKTO__DCbEeKg";
        var original = RandomNumberGenerator.GetBytes(4096);
        var context = new EncryptedPayloadContext("file-url-safe-salt", encryption, EncryptedPayloadRoles.Original, 0);
        var payload = service.EncryptBytes(original, context);
        var restored = service.DecryptBytes(payload, context);
        var matches = CryptographicOperations.FixedTimeEquals(original, restored);
        CryptographicOperations.ZeroMemory(original);
        CryptographicOperations.ZeroMemory(payload);
        CryptographicOperations.ZeroMemory(restored);
        return Task.FromResult(matches
            ? TestOutcome.Pass()
            : TestOutcome.Fail("o salt base64url não foi aceito na criptografia"));
    }

    private static async Task<TestOutcome> CustomKeyExportImportAndMismatchAsync()
    {
        var serviceA = CreateEncryptionService(out _);
        serviceA.ConfigureCustomKey("senha de teste A");
        var encryption = serviceA.CreateFileEncryption(EncryptionModes.Custom);
        var context = new EncryptedPayloadContext("file-custom", encryption, EncryptedPayloadRoles.Original, 0);
        var original = RandomNumberGenerator.GetBytes(4096);
        var payload = serviceA.EncryptBytes(original, context);

        var serviceB = CreateEncryptionService(out _);
        serviceB.ConfigureCustomKey("senha de teste B");
        var rejected = Throws<EncryptedPayloadAuthenticationException>(() => serviceB.DecryptBytes(payload, context));

        var path = Path.Combine(Path.GetTempPath(), "organessonium-test-" + Guid.NewGuid().ToString("N") + ".organessonium-key");
        try
        {
            await serviceA.ExportCustomKeyAsync(path);
            var serviceImported = CreateEncryptionService(out _);
            await serviceImported.ImportCustomKeyAsync(path);
            var restored = serviceImported.DecryptBytes(payload, context);
            var matches = CryptographicOperations.FixedTimeEquals(original, restored);
            var replacementBlocked = Throws<InvalidOperationException>(() => serviceA.ConfigureCustomKey("senha diferente"));
            CryptographicOperations.ZeroMemory(restored);
            return rejected && matches && replacementBlocked
                ? TestOutcome.Pass()
                : TestOutcome.Fail("a chave personalizada não rejeitou a chave errada, falhou após a importação ou foi substituída sem aviso");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(original);
            CryptographicOperations.ZeroMemory(payload);
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task<TestOutcome> LegacyEntryHasNoEncryptionMetadataAsync()
    {
        var entry = AppEntry.File("legacy.txt", "thread", "2026-01-01T00:00:00Z", 1, "/");
        entry.ChunkMessageIds = ["legacy-chunk-message"];
        entry.ChunkCount = 1;
        var payload = await new MetadataService().CreateWritePayloadAsync(entry);
        try
        {
            return entry.Encryption == null && !payload.Content.Contains("\"encryption\"", StringComparison.Ordinal)
                ? TestOutcome.Pass()
                : TestOutcome.Fail("arquivo legado recebeu metadados de criptografia");
        }
        finally
        {
            try { payload.Attachment?.Delete(); } catch { }
        }
    }

    private static async Task<TestOutcome> UrlSafeEncryptedMetadataIsLoadedAsync()
    {
        var messages = new[]
        {
            new DiscordMessageDto
            {
                Id = "1542915922841436287",
                Content = """
                    {"fileId":"5mqlhvddv3errvi7c2sa4zp2qeo9g2t2","threadId":"1542915920857661451","name":"Documento (2).pdf","mimeType":"application/pdf","sizeBytes":17803,"uploadedAt":"2026-08-28T15:17:02Z","batchId":"k7opmdfl3v","parentFolderId":"root","chunks":["1542915920857661451"],"encryption":{"version":1,"mode":"standard","salt":"NyasiAVslEKTO__DCbEeKg"},"schemaVersion":2,"entryType":"file"}
                    """
            },
            new DiscordMessageDto
            {
                Id = "1543972701318684816",
                Content = """
                    {"fileId":"hi6iv49u6u0e0jzjwnr7pspn3ndm5k3c","threadId":"1543972697506316288","name":"IMG_20260403_140807.jpg","mimeType":"image/jpeg","sizeBytes":3161925,"uploadedAt":"2026-08-31T13:16:16Z","batchId":"yqzrf3n1xt","parentFolderId":"root","chunks":["1543972697506316288"],"preview":{"thumbnailMessageId":"1543972700282822786"},"encryption":{"version":1,"mode":"standard","salt":"cPEsp9RSk8AZKL7p-OPicw"},"schemaVersion":2,"entryType":"file"}
                    """
            }
        };
        var service = new MetadataService();
        foreach (var message in messages)
        {
            var envelope = await service.ReadEnvelopeAsync(message);
            if (envelope?.Entry is not FileMetadata file || file.Encryption == null)
            {
                return TestOutcome.Fail("metadata criptografada em base64url não foi carregada");
            }
        }

        var standardContent = messages[0].Content.Replace(
            "NyasiAVslEKTO__DCbEeKg",
            "AAECAwQFBgcICQoLDA0ODw==",
            StringComparison.Ordinal);
        var standardEnvelope = await service.ReadEnvelopeAsync(new DiscordMessageDto
        {
            Id = "standard-salt-must-be-rejected",
            Content = standardContent
        });
        if (standardEnvelope != null)
        {
            return TestOutcome.Fail("metadata em Base64 padrão foi aceita, mas o formato do servidor é Base64URL");
        }

        return TestOutcome.Pass();
    }

    private static Task<TestOutcome> FolderSerialRoundTripAsync()
    {
        var codec = new FolderSerialCodec();
        var generatedAt = DateTimeOffset.Parse("2026-09-07T15:00:00Z");
        var serial = new FolderSerial
        {
            FolderId = "root",
            ParentFolderId = "root",
            FolderName = "root",
            GeneratedAt = generatedAt,
            Aggregate = new FolderSerialAggregate
            {
                TotalItems = 1,
                FileCount = 1,
                FolderCount = 0,
                TotalBytes = 42,
                ImageCount = 1,
                VideoCount = 0,
                TypeCounts = new Dictionary<string, long> { ["jpg"] = 1 }
            },
            Children =
            [
                new FolderSerialEntry
                {
                    Id = "file-id",
                    NamesMessageId = "names-message-id",
                    ParentFolderId = "root",
                    Name = "foto.jpg",
                    ThreadId = "thread-id",
                    MimeType = "image/jpeg",
                    UploadedAt = generatedAt,
                    SizeBytes = 42,
                    PreviewMessageId = "preview-id",
                    Encryption = new EncryptionMetadata
                    {
                        Version = 1,
                        Mode = EncryptionModes.Standard,
                        Salt = "NyasiAVslEKTO__DCbEeKg"
                    },
                    DeviceType = "phone",
                    Favorite = true
                }
            ]
        };

        var restored = codec.DecodeFolder(codec.EncodeFolder(serial));
        var restoredV3 = codec.DecodeFolder(codec.EncodeFolder(serial, schemaVersion: 3));
        var child = restored.Children.SingleOrDefault();
        var valid = restored.FolderId == serial.FolderId &&
                    restored.GeneratedAt == generatedAt &&
                    restored.Aggregate.TotalBytes == 42 &&
                    child?.Id == "file-id" &&
                    child.SizeBytes == 42 &&
                    child.Encryption?.Salt == "NyasiAVslEKTO__DCbEeKg" &&
                    child.MimeType == "image/jpeg" &&
                    child.Favorite &&
                    child.DeviceType == "phone" &&
                    restoredV3.Children.Single().SizeBytes == 0 &&
                    FolderSerialCodec.PublishedVersion == 3;
        return Task.FromResult(valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("o serial OSER não voltou com a estrutura esperada"));
    }

    private static async Task<TestOutcome> FavoriteAndDeviceMetadataCompatibilityAsync()
    {
        var service = new MetadataService();
        var folder = AppEntry.Folder("Telefone", "/@synchronized");
        folder.ParentFolderId = "synchronized";
        folder.ThreadId = "device-thread";
        folder.DeviceType = "phone";
        folder.Favorite = true;
        var payload = await service.CreateWritePayloadAsync(folder);
        var envelope = await service.ReadEnvelopeAsync(new DiscordMessageDto
        {
            Id = "metadata-message",
            Content = payload.Content
        });
        var restored = envelope == null ? null : service.ToAppEntry(envelope);
        var decoded = service.DecodeJson(payload.Content);
        var valid = !payload.Content.Contains("\"deviceType\"", StringComparison.Ordinal) &&
                    decoded.Contains("\"deviceType\":\"phone\"", StringComparison.Ordinal) &&
                    decoded.Contains("\"fav\":true", StringComparison.Ordinal) &&
                    restored?.DeviceType == "phone" &&
                    restored.Favorite;
        return valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("fav/deviceType não preservaram o contrato de metadata do Flutter");
    }

    private static async Task<TestOutcome> FolderIndexMappingProtocolAsync()
    {
        var service = new MetadataService();
        var mapping = new FolderIndexMapping(
            "root",
            "root-thread",
            "names-message",
            "serial-message",
            new string('a', 64));
        var envelope = await service.ReadEnvelopeAsync(new DiscordMessageDto
        {
            Id = mapping.NamesMessageId,
            Content = service.SerializeFolderIndex(mapping)
        });
        var mappings = await service.ReadFolderIndexMappingsInParallelAsync(
            [new DiscordMessageDto { Id = mapping.NamesMessageId, Content = service.SerializeFolderIndex(mapping) }],
            maxConcurrency: 2);
        var valid = envelope?.Entry is FolderIndexMetadata index &&
                    index.FolderId == mapping.FolderId &&
                    index.ThreadId == mapping.ThreadId &&
                    mappings.Count == 1 &&
                    mappings[0] == mapping;
        return valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("o mapping folderId -> threadId não foi serializado e recuperado corretamente");
    }

    private static async Task<TestOutcome> FolderIndexReturnsOnlyDirectChildrenAsync()
    {
        var service = new MetadataService();
        var messages = new[]
        {
            new DiscordMessageDto
            {
                Id = "header",
                Content = service.SerializeFolderIndexHeader("root", "root", "root")
            },
            new DiscordMessageDto
            {
                Id = "file-root",
                Content = """{"schemaVersion":2,"entryType":"file","fileId":"root-file","threadId":"payload-thread","name":"root.txt","mimeType":"text/plain","sizeBytes":1,"uploadedAt":"2026-09-16T00:00:00Z","parentFolderId":"root","chunks":["chunk"]}"""
            },
            new DiscordMessageDto
            {
                Id = "folder-child",
                Content = """{"schemaVersion":2,"entryType":"folder","folderId":"photos","name":"Fotos","createdAt":"2026-09-16T00:00:00Z","parentFolderId":"root","folderColor":"#C4C7C5"}"""
            },
            new DiscordMessageDto
            {
                Id = "file-nested",
                Content = """{"schemaVersion":2,"entryType":"file","fileId":"nested-file","threadId":"payload-thread","name":"nested.txt","mimeType":"text/plain","sizeBytes":1,"uploadedAt":"2026-09-16T00:00:00Z","parentFolderId":"photos","chunks":["chunk"]}"""
            }
        };
        var envelopes = await service.ReadEnvelopesInParallelAsync(messages, maxConcurrency: 2);
        var entries = service.ToFolderEntries(
            envelopes,
            "root",
            "/",
            new Dictionary<string, FolderIndexMapping>
            {
                ["photos"] = new("photos", "photos-thread", "names-photos")
            });
        var valid = entries.Count == 2 &&
                    entries.Any(entry => entry.FileId == "root-file") &&
                    entries.Any(entry => entry.FolderId == "photos" && entry.ThreadId == "photos-thread") &&
                    entries.All(entry => entry.FileId != "nested-file");
        return valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("o índice da pasta incluiu um descendente ou não resolveu o thread da pasta filha");
    }

    private static Task<TestOutcome> SerialMessageEnvelopeProtocolAsync()
    {
        var content = SerialMessageEnvelope.Encode("folder-id", "Fotos", new string('a', 64));
        var parsed = SerialMessageEnvelope.TryParse(content, out var envelope);
        var rejected = !SerialMessageEnvelope.TryParse(
            "{\"schema\":1,\"entryType\":\"folderSerial\",\"folderId\":\"folder-id\",\"sha256\":\"bad\"}",
            out _);
        var valid = parsed && rejected && envelope.FolderId == "folder-id" && envelope.FolderName == "Fotos";
        return Task.FromResult(valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("o envelope do canal serial não correspondeu ao padrão existente"));
    }

    private static async Task<TestOutcome> StaleSerialMappingRepublishesCachedSerialAsync()
    {
        var cache = Directory.CreateTempSubdirectory("organessonium_serial_republish_test_");
        try
        {
            using var handler = new SerialRepublishHandler();
            using var discord = new DiscordApiClient("test-token", new HttpClient(handler));
            var service = new SerialIndexService(
                discord,
                new AppSettings { SerialChannelId = "serial-channel" },
                cache);
            var serial = new FolderSerial
            {
                FolderId = "folder-id",
                ParentFolderId = "root",
                FolderName = "Documentos",
                GeneratedAt = DateTimeOffset.Parse("2026-09-19T12:00:00Z"),
                Aggregate = new FolderSerialAggregate(),
                Children = []
            };

            var initial = await service.SaveFolderAsync(
                serial,
                new FolderIndexMapping("folder-id", "folder-thread", "names-message"));
            var repaired = await service.SaveFolderAsync(
                serial,
                initial with { SerialMessageId = "missing-message" });
            var valid = handler.Uploads == 2 &&
                        handler.MissingMessageChecks == 1 &&
                        repaired.SerialMessageId == "serial-message-2";
            return valid
                ? TestOutcome.Pass()
                : TestOutcome.Fail(
                    $"o serial em cache não foi republicado: uploads={handler.Uploads}, verificações={handler.MissingMessageChecks}, id={repaired.SerialMessageId}");
        }
        finally
        {
            try { cache.Delete(true); } catch { }
        }
    }

    private static async Task<TestOutcome> EmptyStandaloneSerialIsDeletedAsync()
    {
        var cache = Directory.CreateTempSubdirectory("organessonium_empty_serial_test_");
        try
        {
            using var handler = new EmptyStandaloneSerialHandler();
            using var discord = new DiscordApiClient("test-token", new HttpClient(handler));
            var service = new SerialIndexService(
                discord,
                new AppSettings { SerialChannelId = EmptyStandaloneSerialHandler.ChannelId, SearchWorkers = 1 },
                cache);
            await service.SaveStandaloneSerialAsync(new FolderSerial
            {
                FolderId = SerialIndexService.FavoritesSerialId,
                ParentFolderId = "root",
                FolderName = SerialIndexService.FavoritesSerialId,
                GeneratedAt = DateTimeOffset.Parse("2026-09-20T12:00:00Z"),
                Aggregate = new FolderSerialAggregate(),
                Children = [],
                Kind = FolderSerialKind.Favorites
            });

            var reopened = new SerialIndexService(
                discord,
                new AppSettings { SerialChannelId = EmptyStandaloneSerialHandler.ChannelId, SearchWorkers = 1 },
                cache);
            var cached = await reopened.LoadStandaloneSerialAsync(
                SerialIndexService.FavoritesSerialId, FolderSerialKind.Favorites);

            return handler.Deletes == 0 && handler.Uploads == 0 && handler.Replacements == 1 &&
                   handler.MessageReads == 1 && cached?.Kind == FolderSerialKind.Favorites
                ? TestOutcome.Pass()
                : TestOutcome.Fail($"serial de Favoritos não preservou ID/cache: deletes={handler.Deletes}, uploads={handler.Uploads}, replacements={handler.Replacements}, reads={handler.MessageReads}");
        }
        finally
        {
            try { cache.Delete(true); } catch { }
        }
    }

    private static async Task<TestOutcome> SerialResetDeletesRemoteAndLocalCacheAsync()
    {
        var cache = Directory.CreateTempSubdirectory("organessonium_serial_reset_test_");
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(cache.FullName, "serial_old.bin"), [1, 2, 3]);
            using var handler = new EmptyStandaloneSerialHandler();
            using var discord = new DiscordApiClient("test-token", new HttpClient(handler));
            var service = new SerialIndexService(
                discord,
                new AppSettings { SerialChannelId = EmptyStandaloneSerialHandler.ChannelId, SearchWorkers = 1 },
                cache);

            var removed = await service.ResetAllAsync();
            var valid = removed == 0 && handler.Deletes == 0 && !cache.EnumerateFiles("*.bin").Any();
            return valid
                ? TestOutcome.Pass()
                : TestOutcome.Fail($"reset incompleto: remotos={removed}, deletes={handler.Deletes}, cache={cache.EnumerateFiles("*.bin").Count()}");
        }
        finally
        {
            try { cache.Delete(true); } catch { }
        }
    }

    private static async Task<TestOutcome> ActiveThreadsUseGuildRouteAsync()
    {
        using var handler = new ActiveThreadsHandler();
        using var discord = new DiscordApiClient("test-token", new HttpClient(handler));
        var threads = await discord.GetActiveThreadsAsync("folders-forum");
        var valid = handler.UsedGuildRoute &&
                    threads.Count == 1 &&
                    threads[0].Id == "folder-thread" &&
                    threads[0].ParentId == "folders-forum";
        return valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("a listagem de threads ativas não consultou a rota do servidor ou incluiu outro fórum");
    }

    private static async Task<TestOutcome> ForumThreadsIncludeArchivedPagesAsync()
    {
        using var handler = new ForumThreadsHandler();
        using var discord = new DiscordApiClient("test-token", new HttpClient(handler));
        var threads = await discord.GetForumThreadsAsync("files-forum");
        var ids = threads.Select(thread => thread.Id).ToHashSet(StringComparer.Ordinal);
        var valid = ids.SetEquals(["active-file", "archived-file-1", "archived-file-2"]) &&
                    handler.ArchivedRequests == 2;
        return valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"threads incompletas: ids={string.Join(',', ids)}, páginas={handler.ArchivedRequests}");
    }

    private static async Task<TestOutcome> ParallelMessageIndexUsesConcurrentWindowsAsync()
    {
        const ulong channelId = 4_000_000_000_000_000_000;
        var messageIds = Enumerable.Range(1, 400)
            .Select(index => channelId + (ulong)(index * 100))
            .ToArray();
        using var handler = new ParallelMessageHandler(messageIds);
        using var discord = new DiscordApiClient("test-token", new HttpClient(handler));
        var announced = 0;
        var messages = await discord.GetMessagesInParallelAsync(
            channelId.ToString(),
            workers: 4,
            onBatch: batch =>
            {
                Interlocked.Add(ref announced, batch.Count);
                return Task.CompletedTask;
            });

        var received = messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        var expected = messageIds.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
        var valid = received.SetEquals(expected) &&
                    announced == expected.Count &&
                    handler.MaximumConcurrentRequests >= 2;
        return valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"índice incompleto ou sequencial: recebidos={received.Count}, callbacks={announced}, paralelo={handler.MaximumConcurrentRequests}");
    }

    private static async Task<TestOutcome> ParallelAttachmentUploadsBypassRouteLockAsync()
    {
        var directory = Directory.CreateTempSubdirectory("organessonium_parallel_upload_test_");
        try
        {
            var parts = Enumerable.Range(1, 3)
                .Select(index => new FileInfo(Path.Combine(directory.FullName, $"part-{index}.bin")))
                .ToList();
            foreach (var part in parts)
            {
                await File.WriteAllBytesAsync(part.FullName, RandomNumberGenerator.GetBytes(1024));
            }

            using var handler = new ParallelAttachmentUploadHandler();
            using var discord = new DiscordApiClient("test-token", new HttpClient(handler));
            using var uploadGate = new SemaphoreSlim(3, 3);
            var uploader = new ForumUploader(discord, "files-forum");
            var result = await uploader.UploadResumableAsync(
                "archive.bin",
                parts,
                "",
                "",
                new Dictionary<int, string>(),
                cancellationToken: default,
                maxParallelParts: 3,
                uploadGate: uploadGate);

            var valid = result.ChunkMessageIds.Count == 3 &&
                        result.ChunkMessageIds.All(id => !string.IsNullOrWhiteSpace(id)) &&
                        handler.MaximumConcurrentAttachments >= 2;
            return valid
                ? TestOutcome.Pass()
                : TestOutcome.Fail($"as partes do upload foram serializadas: paralelo={handler.MaximumConcurrentAttachments}");
        }
        finally
        {
            try { directory.Delete(true); } catch { }
        }
    }

    private static async Task<TestOutcome> MemoryAttachmentRetryKeepsCiphertextAsync()
    {
        var payload = RandomNumberGenerator.GetBytes(4096);
        using var pool = new UploadChunkBufferPool();
        var releaseCount = 0;
        using var lease = pool.Rent(1, payload.Length, () => Interlocked.Increment(ref releaseCount));
        payload.CopyTo(lease.Ciphertext.Span);
        using var part = new PreparedUploadPart("payload.bin", payload.Length, payload.Length, lease);
        using var handler = new RetryingMemoryAttachmentHandler();
        using var discord = new DiscordApiClient("test-token", new HttpClient(handler));
        try
        {
            var result = await discord.SendFileMessageAsync("thread", "payload", part, serializeRoute: false);
            part.Dispose();
            var valid = result.Id == "retry-message" &&
                        handler.Uploads.Count == 2 &&
                        handler.Uploads.All(upload => CryptographicOperations.FixedTimeEquals(upload, payload)) &&
                        releaseCount == 1;
            return valid
                ? TestOutcome.Pass()
                : TestOutcome.Fail("o retry HTTP não reutilizou corretamente o ciphertext em memória");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static Task<TestOutcome> UploadBufferReclaimerReleasesLargeArraysAsync()
    {
        var references = CreateReturnedUploadBufferReferences();
        UploadBufferMemoryReclaimer.Reclaim();
        var released = !references.Plaintext.IsAlive && !references.Ciphertext.IsAlive;
        return Task.FromResult(released
            ? TestOutcome.Pass()
            : TestOutcome.Fail("os arrays grandes continuaram retidos após descartar o pool de upload"));
    }

    private static (WeakReference Plaintext, WeakReference Ciphertext) CreateReturnedUploadBufferReferences()
    {
        using var pool = new UploadChunkBufferPool();
        using var lease = pool.Rent(10 * 1024 * 1024, 10 * 1024 * 1024 + 128, () => { });
        MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)lease.Plaintext, out var plaintext);
        MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)lease.Ciphertext, out var ciphertext);
        var references = (new WeakReference(plaintext.Array!), new WeakReference(ciphertext.Array!));
        lease.Dispose();
        pool.Dispose();
        return references;
    }

    private static EncryptionService CreateEncryptionService(out InMemoryEncryptionSecretStore store)
    {
        store = new InMemoryEncryptionSecretStore();
        return new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(store));
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private sealed class InMemoryEncryptionSecretStore : IEncryptionSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string ReadEncryptionSecret(string key)
        {
            return _values.TryGetValue(key, out var value) ? value : "";
        }

        public void WriteEncryptionSecret(string key, string value)
        {
            _values[key] = value;
        }
    }

    private sealed class ActiveThreadsHandler : HttpMessageHandler
    {
        public bool UsedGuildRoute { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/channels/folders-forum", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"id":"folders-forum","guild_id":"guild-id","type":15}"""));
            }

            if (path.EndsWith("/guilds/guild-id/threads/active", StringComparison.Ordinal))
            {
                UsedGuildRoute = true;
                return Task.FromResult(JsonResponse("""
                    {"threads":[
                        {"id":"folder-thread","parent_id":"folders-forum","type":11},
                        {"id":"other-thread","parent_id":"files-forum","type":11}
                    ]}
                    """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ForumThreadsHandler : HttpMessageHandler
    {
        public int ArchivedRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("URI ausente no teste de threads.");
            var path = uri.AbsolutePath;
            if (path.EndsWith("/channels/files-forum", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"id":"files-forum","guild_id":"guild-id","type":15}"""));
            }

            if (path.EndsWith("/guilds/guild-id/threads/active", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""
                    {"threads":[
                        {"id":"active-file","parent_id":"files-forum","type":11},
                        {"id":"active-other","parent_id":"other-forum","type":11}
                    ]}
                    """));
            }

            if (path.EndsWith("/channels/files-forum/threads/archived/public", StringComparison.Ordinal))
            {
                ArchivedRequests++;
                var secondPage = uri.Query.Contains("before=", StringComparison.Ordinal);
                var json = secondPage
                    ? """
                      {"threads":[
                          {"id":"archived-file-2","parent_id":"files-forum","type":11,
                           "thread_metadata":{"archive_timestamp":"2026-09-22T10:00:00.000000+00:00"}}
                      ],"has_more":false}
                      """
                    : """
                      {"threads":[
                          {"id":"archived-file-1","parent_id":"files-forum","type":11,
                           "thread_metadata":{"archive_timestamp":"2026-09-23T10:00:00.000000+00:00"}},
                          {"id":"archived-other","parent_id":"other-forum","type":11,
                           "thread_metadata":{"archive_timestamp":"2026-09-23T09:00:00.000000+00:00"}}
                      ],"has_more":true}
                      """;
                return Task.FromResult(JsonResponse(json));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SerialRepublishHandler : HttpMessageHandler
    {
        public int Uploads { get; private set; }
        public int MissingMessageChecks { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get &&
                path.EndsWith("/channels/serial-channel/messages/missing-message", StringComparison.Ordinal))
            {
                MissingMessageChecks++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (request.Method == HttpMethod.Post &&
                path.EndsWith("/channels/serial-channel/messages", StringComparison.Ordinal))
            {
                Uploads++;
                return Task.FromResult(JsonResponse(
                    $$"""{"id":"serial-message-{{Uploads}}","attachments":[]}"""));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class EmptyStandaloneSerialHandler : HttpMessageHandler
    {
        public const string ChannelId = "4000000000000000000";
        private const string MessageId = "4000000000000000100";
        private readonly string _envelope = SerialMessageEnvelope.Encode(
            SerialIndexService.FavoritesSerialId,
            SerialIndexService.FavoritesSerialId,
            new string('a', 64));

        public int Deletes { get; private set; }
        public int Uploads { get; private set; }
        public int Replacements { get; private set; }
        public int MessageReads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("URI ausente no teste de serial vazio.");
            var path = uri.AbsolutePath;
            if (request.Method == HttpMethod.Get &&
                path.EndsWith($"/channels/{ChannelId}/messages", StringComparison.Ordinal))
            {
                MessageReads++;
                var before = QueryValue(uri, "before");
                var include = string.IsNullOrWhiteSpace(before) ||
                              !ulong.TryParse(before, out var beforeId) ||
                              beforeId > ulong.Parse(MessageId);
                var json = include
                    ? $"[{{\"id\":\"{MessageId}\",\"content\":{System.Text.Json.JsonSerializer.Serialize(_envelope)},\"attachments\":[]}}]"
                    : "[]";
                return Task.FromResult(JsonResponse(json));
            }

            if (request.Method == HttpMethod.Delete &&
                path.EndsWith($"/channels/{ChannelId}/messages/{MessageId}", StringComparison.Ordinal))
            {
                Deletes++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (request.Method == HttpMethod.Patch &&
                path.EndsWith($"/channels/{ChannelId}/messages/{MessageId}", StringComparison.Ordinal))
            {
                Replacements++;
                return Task.FromResult(JsonResponse($"{{\"id\":\"{MessageId}\",\"content\":\"\",\"attachments\":[]}}"));
            }

            if (request.Method == HttpMethod.Post)
            {
                Uploads++;
                return Task.FromResult(JsonResponse("{\"id\":\"4000000000000000200\",\"content\":\"\",\"attachments\":[]}"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static string? QueryValue(Uri uri, string key)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }
            return null;
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ParallelMessageHandler : HttpMessageHandler
    {
        private readonly IReadOnlyList<ulong> _messageIds;
        private readonly object _sync = new();
        private int _activeRequests;
        private int _maximumConcurrentRequests;

        public ParallelMessageHandler(IReadOnlyList<ulong> messageIds)
        {
            _messageIds = messageIds.OrderByDescending(id => id).ToArray();
        }

        public int MaximumConcurrentRequests
        {
            get
            {
                lock (_sync)
                {
                    return _maximumConcurrentRequests;
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("URI ausente no teste de índice.");
            var limit = QueryValue(uri, "limit") is { } limitText && int.TryParse(limitText, out var parsedLimit)
                ? parsedLimit
                : 50;
            var before = QueryValue(uri, "before") is { } beforeText && ulong.TryParse(beforeText, out var parsedBefore)
                ? parsedBefore
                : ulong.MaxValue;
            lock (_sync)
            {
                _activeRequests++;
                _maximumConcurrentRequests = Math.Max(_maximumConcurrentRequests, _activeRequests);
            }

            try
            {
                await Task.Delay(30, cancellationToken);
                var selected = _messageIds
                    .Where(id => id < before)
                    .Take(limit)
                    .Select(id => $"{{\"id\":\"{id}\",\"content\":\"\",\"attachments\":[]}}")
                    .ToArray();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[" + string.Join(',', selected) + "]", Encoding.UTF8, "application/json")
                };
            }
            finally
            {
                lock (_sync)
                {
                    _activeRequests--;
                }
            }
        }

        private static string? QueryValue(Uri uri, string key)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }

            return null;
        }
    }

    private sealed class ParallelAttachmentUploadHandler : HttpMessageHandler
    {
        private readonly object _sync = new();
        private int _activeAttachments;
        private int _maximumConcurrentAttachments;
        private int _nextMessageId;

        public int MaximumConcurrentAttachments
        {
            get
            {
                lock (_sync)
                {
                    return _maximumConcurrentAttachments;
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _activeAttachments++;
                _maximumConcurrentAttachments = Math.Max(_maximumConcurrentAttachments, _activeAttachments);
            }

            try
            {
                await Task.Delay(50, cancellationToken);
                var messageId = Interlocked.Increment(ref _nextMessageId).ToString();
                var path = request.RequestUri?.AbsolutePath ?? "";
                var payload = path.EndsWith("/threads", StringComparison.Ordinal)
                    ? "{\"id\":\"file-thread\",\"message\":{\"id\":\"" + messageId + "\",\"content\":\"\",\"attachments\":[]}}"
                    : "{\"id\":\"" + messageId + "\",\"content\":\"\",\"attachments\":[]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            }
            finally
            {
                lock (_sync)
                {
                    _activeAttachments--;
                }
            }
        }
    }

    private sealed class RetryingMemoryAttachmentHandler : HttpMessageHandler
    {
        private readonly List<byte[]> _uploads = new();
        private int _attempt;

        public IReadOnlyList<byte[]> Uploads => _uploads;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var multipart = request.Content as MultipartFormDataContent
                ?? throw new InvalidOperationException("O attachment de teste não foi enviado como multipart.");
            var file = multipart.FirstOrDefault(content =>
                string.Equals(content.Headers.ContentDisposition?.Name?.Trim('"'), "files[0]", StringComparison.Ordinal));
            if (file == null)
            {
                throw new InvalidOperationException("O attachment de teste não contém files[0].");
            }

            _uploads.Add(await file.ReadAsByteArrayAsync(cancellationToken));
            if (Interlocked.Increment(ref _attempt) == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("retry", Encoding.UTF8, "text/plain")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"retry-message\",\"content\":\"\",\"attachments\":[]}", Encoding.UTF8, "application/json")
            };
        }
    }

    private static ProxySettings Manual(string host, int port, ProxyProtocol protocol, bool auth = false, string user = "", string pass = "")
    {
        return new ProxySettings
        {
            Mode = ProxyMode.Manual,
            Host = host,
            Port = port,
            Protocol = protocol,
            AuthenticationEnabled = auth,
            Username = user,
            Password = pass
        };
    }

    private static async Task<TestOutcome> HttpProxyForwardsGetAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new HttpProxyServer();
        var network = new NetworkConfig();
        network.Apply(Manual("127.0.0.1", proxy.Port, ProxyProtocol.Http));
        using var http = network.CreateClient();
        using var response = await http.GetAsync(target.ProbeUrl(""), HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();
        return response.StatusCode == HttpStatusCode.OK && body.Contains("OK", StringComparison.Ordinal)
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"resposta inesperada: {(int)response.StatusCode} {body}");
    }

    private static async Task<TestOutcome> HttpProxyAuthAcceptAndRejectAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new HttpProxyServer(requireAuth: true, user: "usuario", pass: "senha");

        var withAuth = new NetworkConfig();
        withAuth.Apply(Manual("127.0.0.1", proxy.Port, ProxyProtocol.Http, true, "usuario", "senha"));
        using (var http = withAuth.CreateClient())
        {
            using var response = await http.GetAsync(target.ProbeUrl(""), HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return TestOutcome.Fail("com credenciais corretas o proxy rejeitou o pedido");
            }
        }

        var withoutAuth = new NetworkConfig();
        withoutAuth.Apply(Manual("127.0.0.1", proxy.Port, ProxyProtocol.Http));
        using (var http = withoutAuth.CreateClient())
        {
            using var response = await http.GetAsync(target.ProbeUrl(""), HttpCompletionOption.ResponseHeadersRead);
            return response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired
                ? TestOutcome.Pass()
                : TestOutcome.Fail($"esperado 407, recebido {(int)response.StatusCode}");
        }
    }

    private static async Task<TestOutcome> Socks5ProxyForwardsGetAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new Socks5Server();
        var network = new NetworkConfig();
        network.Apply(Manual("127.0.0.1", proxy.Port, ProxyProtocol.Socks5));
        using var http = network.CreateClient();
        using var response = await http.GetAsync(target.ProbeUrl(""), HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();
        return response.StatusCode == HttpStatusCode.OK && body.Contains("OK", StringComparison.Ordinal)
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"resposta inesperada: {(int)response.StatusCode} {body}");
    }

    private static async Task<TestOutcome> Socks5WrongCredentialsRejectedAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new Socks5Server(requireAuth: true, user: "usuario", pass: "senha");
        var settings = Manual("127.0.0.1", proxy.Port, ProxyProtocol.Socks5, true, "usuario", "senhaErrada");
        var result = await ProxyTester.TestAsync(settings, false, default, target.ProbeUrl(""));
        return result.Success == false && result.Kind == ProxyTestErrorKind.ProxyAuthenticationRequired
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado ProxyAuthenticationRequired, recebido Success={result.Success} Kind={result.Kind} Msg={result.Message}");
    }

    private static async Task<TestOutcome> Socks4aProxyForwardsGetAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new Socks4aServer();
        var network = new NetworkConfig();
        network.Apply(Manual("127.0.0.1", proxy.Port, ProxyProtocol.Socks4a));
        using var http = network.CreateClient();
        using var response = await http.GetAsync(target.ProbeUrl(""), HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();
        return response.StatusCode == HttpStatusCode.OK && body.Contains("OK", StringComparison.Ordinal)
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"resposta inesperada: {(int)response.StatusCode} {body}");
    }

    private static async Task<TestOutcome> DirectModeBypassesProxyAsync()
    {
        using var target = new HttpTargetServer();
        var network = new NetworkConfig();
        network.Apply(new ProxySettings { Mode = ProxyMode.Disabled });
        using var http = network.CreateClient();
        using var response = await http.GetAsync(target.ProbeUrl(""), HttpCompletionOption.ResponseHeadersRead);
        return response.StatusCode == HttpStatusCode.OK
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"resposta inesperada: {(int)response.StatusCode}");
    }

    private static async Task<TestOutcome> RefusedProxyClassifiedAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new RefusalServer();
        var settings = Manual("127.0.0.1", proxy.Port, ProxyProtocol.Http);
        var result = await ProxyTester.TestAsync(settings, false, default, target.ProbeUrl(""));
        return result.Success == false && result.Kind == ProxyTestErrorKind.ProxyRefused
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado ProxyRefused, recebido Success={result.Success} Kind={result.Kind} Msg={result.Message}");
    }

    private static async Task<TestOutcome> UnreachableProxyClassifiedAsync()
    {
        using var target = new HttpTargetServer();
        var settings = Manual("proxy-host-nao-existe.invalid", 8080, ProxyProtocol.Http);
        var result = await ProxyTester.TestAsync(settings, false, default, target.ProbeUrl(""));
        return result.Success == false && result.Kind == ProxyTestErrorKind.ProxyUnreachable
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado ProxyUnreachable, recebido Success={result.Success} Kind={result.Kind} Msg={result.Message}");
    }

    private static async Task<TestOutcome> AutoDetectChoosesHttpAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new HttpProxyServer();
        var settings = Manual("127.0.0.1", proxy.Port, ProxyProtocol.Auto);
        var result = await ProxyTester.TestAsync(settings, false, default, target.ProbeUrl(""));
        return result.Success && result.DetectedProtocol == ProxyProtocol.Http
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado detecção HTTP, recebido Success={result.Success} Protocol={result.DetectedProtocol} Msg={result.Message}");
    }

    private static async Task<TestOutcome> AutoDetectChoosesSocks5Async()
    {
        using var target = new HttpTargetServer();
        using var proxy = new Socks5Server();
        var settings = Manual("127.0.0.1", proxy.Port, ProxyProtocol.Auto);
        var result = await ProxyTester.TestAsync(settings, false, default, target.ProbeUrl(""));
        return result.Success && result.DetectedProtocol == ProxyProtocol.Socks5
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado detecção SOCKS5, recebido Success={result.Success} Protocol={result.DetectedProtocol} Msg={result.Message}");
    }

    private static async Task<TestOutcome> DiscordCheckThroughProxySucceedsAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new HttpProxyServer();
        var settings = Manual("127.0.0.1", proxy.Port, ProxyProtocol.Http);
        var result = await ProxyTester.TestAsync(settings, true, default, target.ProbeUrl(""));
        return result.Success
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado sucesso, recebido Success={result.Success} Kind={result.Kind} Msg={result.Message}");
    }

    private static async Task<TestOutcome> FailedProbeReportedAsUnreachableAsync()
    {
        using var proxy = new HttpProxyServer();
        var settings = Manual("127.0.0.1", proxy.Port, ProxyProtocol.Http);
        var closedPortUrl = "http://127.0.0.1:" + ClosedPort() + "/";
        var result = await ProxyTester.TestAsync(settings, true, default, closedPortUrl);
        return result.Success == false && result.Kind == ProxyTestErrorKind.TargetUnreachable
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado TargetUnreachable, recebido Success={result.Success} Kind={result.Kind} Msg={result.Message}");
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class HttpTargetServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public HttpTargetServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }

        public string ProbeUrl(string path)
        {
            return $"http://127.0.0.1:{Port}/{path.TrimStart('/')}";
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private static async Task HandleAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                await ReadUntilHeaderEndAsync(stream);
                var body = "OK";
                var response = "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body;
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
            catch
            {
                // ignore
            }
            finally
            {
                client.Dispose();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private sealed class HttpProxyServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public HttpProxyServer(bool requireAuth = false, string? user = null, string? pass = null)
        {
            RequireAuth = requireAuth;
            User = user ?? "";
            Pass = pass ?? "";
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }
        public bool RequireAuth { get; }
        public string User { get; }
        public string Pass { get; }
        private int _requests;
        private int _authRejections;

        public int AuthRejections => Volatile.Read(ref _authRejections);
        public int Requests => Volatile.Read(ref _requests);

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var requestLine = await ReadLineAsync(stream);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                Interlocked.Increment(ref _requests);
                var headers = await ReadHeadersAsync(stream);
                if (RequireAuth && !HasValidAuth(headers))
                {
                    Interlocked.Increment(ref _authRejections);
                    await WriteStatusAsync(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }

                if (requestLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteStatusAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n");
                    var target = requestLine["CONNECT ".Length..].Trim();
                    var (host, port) = SplitHostPort(target);
                    TcpClient? upstream = null;
                    try
                    {
                        upstream = new TcpClient();
                        await upstream.ConnectAsync(host, port, _cts.Token);
                        await RelayAsync(stream, upstream.GetStream(), _cts.Token);
                    }
                    catch
                    {
                        if (upstream == null)
                        {
                            await Task.Delay(1500, _cts.Token);
                        }
                    }
                    finally
                    {
                        upstream?.Dispose();
                    }

                    return;
                }

                if (requestLine.StartsWith("GET ", StringComparison.OrdinalIgnoreCase))
                {
                    var absolute = requestLine["GET ".Length..].Split(' ')[0];
                    if (!Uri.TryCreate(absolute, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
                    {
                        await WriteStatusAsync(stream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                    }

                    using var upstream = new TcpClient();
                    try
                    {
                        await upstream.ConnectAsync(uri.Host, uri.Port, _cts.Token);
                    }
                    catch
                    {
                        await WriteStatusAsync(stream, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                    }

                    var upstreamStream = upstream.GetStream();
                    var forward = $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Host}\r\nConnection: close\r\n\r\n";
                    await upstreamStream.WriteAsync(Encoding.ASCII.GetBytes(forward), _cts.Token);
                    await RelayStreamAsync(upstreamStream, stream, _cts.Token);
                    return;
                }

                await WriteStatusAsync(stream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            }
            catch
            {
                // ignore
            }
            finally
            {
                client.Dispose();
            }
        }

        private bool HasValidAuth(IReadOnlyList<string> headers)
        {
            foreach (var header in headers)
            {
                if (!header.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = header[(header.IndexOf(':') + 1)..].Trim();
                if (!value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value["Basic ".Length..].Trim()));
                    return decoded == User + ":" + Pass;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static (string Host, int Port) SplitHostPort(string target)
        {
            var parts = target.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[^1], out var port) ? port : 80);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private sealed class Socks5Server : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public Socks5Server(bool requireAuth = false, string? user = null, string? pass = null)
        {
            RequireAuth = requireAuth;
            User = user ?? "";
            Pass = pass ?? "";
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }
        public bool RequireAuth { get; }
        public string User { get; }
        public string Pass { get; }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var greeting = await ReadExactlyAsync(stream, 2);
                var methods = await ReadExactlyAsync(stream, greeting[1]);

                var supportsAuth = methods.Contains((byte)0x02);
                var supportsNone = methods.Contains((byte)0x00);

                if (RequireAuth)
                {
                    if (!supportsAuth)
                    {
                        stream.Write(new byte[] { 0x05, 0xFF });
                        return;
                    }

                    stream.Write(new byte[] { 0x05, 0x02 });
                    var authVersion = await ReadExactlyAsync(stream, 1);
                    var userNameLength = (await ReadExactlyAsync(stream, 1))[0];
                    var userName = Encoding.UTF8.GetString(await ReadExactlyAsync(stream, userNameLength));
                    var passwordLength = (await ReadExactlyAsync(stream, 1))[0];
                    var password = Encoding.UTF8.GetString(await ReadExactlyAsync(stream, passwordLength));
                    if (userName != User || password != Pass)
                    {
                        stream.Write(new byte[] { 0x01, 0xFF });
                        return;
                    }

                    stream.Write(new byte[] { 0x01, 0x00 });
                }
                else
                {
                    if (!supportsNone)
                    {
                        stream.Write(new byte[] { 0x05, 0xFF });
                        return;
                    }

                    stream.Write(new byte[] { 0x05, 0x00 });
                }

                var request = await ReadExactlyAsync(stream, 4);
                var command = request[1];
                var atyp = request[3];
                string host;
                if (atyp == 0x01)
                {
                    var ip = await ReadExactlyAsync(stream, 4);
                    host = string.Join('.', ip);
                }
                else if (atyp == 0x03)
                {
                    var length = (await ReadExactlyAsync(stream, 1))[0];
                    host = Encoding.UTF8.GetString(await ReadExactlyAsync(stream, length));
                }
                else
                {
                    stream.Write(new byte[] { 0x05, 0x08, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                var portBytes = await ReadExactlyAsync(stream, 2);
                var port = (portBytes[0] << 8) | portBytes[1];

                if (command != 0x01)
                {
                    stream.Write(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                using var upstream = new TcpClient();
                try
                {
                    await upstream.ConnectAsync(host, port, _cts.Token);
                }
                catch
                {
                    stream.Write(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                stream.Write(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                await RelayAsync(stream, upstream.GetStream(), _cts.Token);
            }
            catch
            {
                // ignore
            }
            finally
            {
                client.Dispose();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private sealed class Socks4aServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public Socks4aServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var header = await ReadExactlyAsync(stream, 8);
                var command = header[1];
                var port = (header[2] << 8) | header[3];

                string host;
                if (header[4] == 0 && header[5] == 0 && header[6] == 0 && header[7] != 0)
                {
                    await ReadLineUntilNullAsync(stream);
                    host = await ReadLineUntilNullAsync(stream);
                }
                else
                {
                    host = string.Join('.', header.Skip(4).Take(4));
                    await ReadLineUntilNullAsync(stream);
                }

                if (string.IsNullOrWhiteSpace(host))
                {
                    stream.Write(new byte[] { 0, 0x5B, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                if (command != 0x01)
                {
                    stream.Write(new byte[] { 0, 0x5B, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                using var upstream = new TcpClient();
                try
                {
                    await upstream.ConnectAsync(host, port, _cts.Token);
                }
                catch
                {
                    stream.Write(new byte[] { 0, 0x5B, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                stream.Write(new byte[] { 0, 0x5A, 0, 0, 0, 0, 0, 0 });
                await RelayAsync(stream, upstream.GetStream(), _cts.Token);
            }
            catch
            {
                // ignore
            }
            finally
            {
                client.Dispose();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private sealed class RefusalServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public RefusalServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private static Task HandleAsync(TcpClient client)
        {
            try
            {
                client.LingerState = new LingerOption(true, 0);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.Client.LingerState = new LingerOption(true, 0);
            }
            catch
            {
                // ignore
            }

            client.Dispose();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private static async Task RelayAsync(NetworkStream left, NetworkStream right, CancellationToken cancellationToken)
    {
        var leftToRight = RelayStreamAsync(left, right, cancellationToken);
        var rightToLeft = RelayStreamAsync(right, left, cancellationToken);
        await Task.WhenAny(leftToRight, rightToLeft);
    }

    private static async Task RelayStreamAsync(NetworkStream from, NetworkStream to, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            try { to.Dispose(); } catch { }
        }
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return builder.ToString();
            }

            if (buffer[0] == (byte)'\n')
            {
                return builder.ToString();
            }

            if (buffer[0] != (byte)'\r')
            {
                builder.Append((char)buffer[0]);
            }
        }
    }

    private static async Task<string> ReadLineUntilNullAsync(NetworkStream stream)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0 || buffer[0] == 0)
            {
                return builder.ToString();
            }

            builder.Append((char)buffer[0]);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadHeadersAsync(NetworkStream stream)
    {
        var headers = new List<string>();
        while (true)
        {
            var line = (await ReadLineAsync(stream)).TrimEnd();
            if (line.Length == 0)
            {
                return headers;
            }

            headers.Add(line);
        }
    }

    private static async Task ReadUntilHeaderEndAsync(NetworkStream stream)
    {
        var buffer = new byte[1];
        var tails = 0;
        while (tails < 4)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return;
            }

            if (buffer[0] == (byte)'\r' || buffer[0] == (byte)'\n')
            {
                tails++;
            }
            else
            {
                tails = 0;
            }
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task WriteStatusAsync(NetworkStream stream, string status)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(status));
    }
}

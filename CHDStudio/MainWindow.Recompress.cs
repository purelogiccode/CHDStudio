using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CHDSharp;
using CHDStudio.Models;
using CHDStudio.Services;
using CHDStudio.Utilities;

namespace CHDStudio;

/// <summary>
///     Recompress tab: changes the codec set of existing CHD files and consolidates differential
///     child images into standalone CHDs.
/// </summary>
[SuppressMessage("ReSharper", "AsyncVoidEventHandlerMethod")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "UnusedParameter.Local")]
internal partial class MainWindow
{
    /// <summary>Reloads the recompression file list for the current input folder.</summary>
    private async Task LoadFilesForRecompressionAsync()
    {
        var inputFolder = RecompressInputFolderTextBox.Text;
        if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder))
            return;

        CancellationToken token;
        lock (_ctsLock)
        {
            token = _cts.Token;
        }

        await AwaitFileScanAsync(Task.Run(
            async () =>
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
                };

                var files = Directory
                    .GetFiles(inputFolder, "*.chd", options)
                    .Select(f => new FileItem
                    {
                        FileName = Path.GetRelativePath(inputFolder, f),
                        FullPath = f,
                        FileSize = new FileInfo(f).Length,
                        IsSelected = true
                    })
                    .ToList();

                Dispatcher.UIThread.Invoke(() =>
                {
                    _recompressFiles.Clear();
                    TotalFilesValue.Text = "0";
                });

                const int chunkSize = 100;
                try
                {
                    for (var i = 0; i < files.Count; i += chunkSize)
                    {
                        var chunk = files.Skip(i).Take(chunkSize).ToList();
                        await Dispatcher.UIThread.InvokeAsync(
                            () =>
                            {
                                foreach (var item in chunk)
                                    _recompressFiles.Add(item);
                                TotalFilesValue.Text = _recompressFiles.Count.ToString(
                                    CultureInfo.InvariantCulture
                                );
                            },
                            DispatcherPriority.Background,
                            token
                        );
                    }
                }
                catch (OperationCanceledException)
                {
                    // Partial results are fine while the folder is changing.
                }
            },
            token
        ));
    }

    /// <summary>Writes the Recompress tab guidance to the activity log.</summary>
    private void DisplayRecompressInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Recompress Mode)");
        LogMessage(
            "Recompress existing CHDs, change codecs, or consolidate a child CHD with its parent."
        );
        LogMessage("--- Ready to recompress ---");
    }

    /// <summary>Opens the recompression input folder picker.</summary>
    private async void BrowseRecompressInputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        var folder = await SelectFolderAsync("Select the folder containing CHD files to recompress");
        if (string.IsNullOrEmpty(folder)) return;

        RecompressInputFolderTextBox.Text = folder;
        if (string.IsNullOrWhiteSpace(RecompressOutputFolderTextBox.Text))
            RecompressOutputFolderTextBox.Text = folder;

        try
        {
            await LoadFilesForRecompressionAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogError("Failed to load CHD files for recompression", ex);
        }
    }

    /// <summary>Opens the recompression output folder picker.</summary>
    private async void BrowseRecompressOutputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        var folder = await SelectFolderAsync("Select the recompression output folder");
        if (!string.IsNullOrEmpty(folder))
            RecompressOutputFolderTextBox.Text = folder;
    }

    /// <summary>Selects the parent CHD for a child source.</summary>
    private async void BrowseRecompressParentButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        var path = await SelectChdFileAsync("Select the parent CHD of the child source");
        if (!string.IsNullOrEmpty(path))
            RecompressParentChdTextBox.Text = path;
    }

    /// <summary>Selects every CHD in the recompression list.</summary>
    private void SelectAllRecompress_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var file in _recompressFiles) file.IsSelected = true;
    }

    /// <summary>Deselects every CHD in the recompression list.</summary>
    private void DeselectAllRecompress_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var file in _recompressFiles) file.IsSelected = false;
    }

    /// <summary>Validates the recompression inputs and runs the batch.</summary>
    private async void StartRecompressButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync((Action)ClearUiLog);
            DisplayRecompressInstructionsInLog();

            var inputFolder = PathUtils.ValidateAndNormalizePath(
                RecompressInputFolderTextBox.Text,
                "CHD Files Folder",
                ShowError,
                LogMessage
            );
            var outputFolder = PathUtils.ValidateAndNormalizePath(
                RecompressOutputFolderTextBox.Text,
                "Output Folder",
                ShowError,
                LogMessage
            );
            if (inputFolder is null || outputFolder is null) return;

            var selectedFiles = _recompressFiles
                .Where(static f => f.IsSelected)
                .Select(static f => f.FullPath)
                .ToArray();
            if (selectedFiles.Length == 0)
            {
                ShowError("No files selected for recompression.");
                return;
            }

            // The list is filled asynchronously when the input folder changes. If Start is
            // clicked before that scan finishes, the old folder's entries must not be processed
            // against the new input folder (which could write outside the output root).
            if (
                selectedFiles.Any(file =>
                    !PathUtils.IsSameOrInsideDirectory(
                        inputFolder,
                        Path.GetDirectoryName(file)
                    )
                )
            )
            {
                ShowError(
                    "The file list is still updating for the selected folder. Wait for the scan to finish and try again."
                );
                return;
            }

            var preset =
                (RecompressCompressionComboBox.SelectedItem as ChdCompressionPresetInfo)?.Preset
                ?? ChdCompressionPreset.Default;
            var parentPath = RecompressParentChdTextBox.Text;
            var autoDetectParent = AutoDetectRecompressParentCheckBox.IsChecked ?? true;
            var verify = RecompressVerifyCheckBox.IsChecked ?? true;
            var deleteOriginal = RecompressDeleteOriginalCheckBox.IsChecked ?? false;
            var upgradeMetadata = RecompressUpgradeMetadataCheckBox.IsChecked ?? true;

            RenewCancellationTokenSource();

            ResetOperationStats();
            SetControlsState(false);
            _activeOperation = "Recompression";
            await Task.Yield();
            StartOperationTimer();
            ResetSpeedCounters();

            LogMessage("--- Starting batch recompression process... ---");
            _wasCancelled = false;

            try
            {
                CancellationToken token;
                lock (_ctsLock)
                {
                    token = _cts.Token;
                }

                await PerformBatchRecompressionAsync(
                    inputFolder,
                    outputFolder,
                    selectedFiles,
                    preset,
                    parentPath,
                    autoDetectParent,
                    verify,
                    deleteOriginal,
                    upgradeMetadata,
                    token
                );
            }
            catch (OperationCanceledException)
            {
                LogMessage("Recompression canceled.");
                _wasCancelled = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogError(ex.Message, ex);
            }
            finally
            {
                await FinishOperationAsync("Recompression");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogError("StartRecompressButton_Click error", ex);
        }
    }

    /// <summary>Runs the batch over the selected CHD files.</summary>
    private async Task PerformBatchRecompressionAsync(
        string inputFolder,
        string outputFolder,
        string[] selectedFiles,
        ChdCompressionPreset preset,
        string? parentPath,
        bool autoDetectParent,
        bool verify,
        bool deleteOriginal,
        bool upgradeMetadata,
        CancellationToken token
    )
    {
        _totalFilesProcessed = selectedFiles.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} CHD files to recompress.");
        if (_totalFilesProcessed == 0) return;

        await CheckDiskSpaceAsync(outputFolder, selectedFiles, true, token);

        await Dispatcher.UIThread.InvokeAsync(() => ProgressBar.Maximum = _totalFilesProcessed);
        var processed = 0;
        ResetSpeedCounters();

        LogMessage("Checking CHD headers for parent/child images...");
        var plan = await Task.Run(
            () => ParentChdResolver.PrepareBatch(selectedFiles, parentPath, autoDetectParent, token),
            token
        );
        foreach (var file in plan.OrderedFiles)
        {
            token.ThrowIfCancellationRequested();

            UpdateProgressDisplay(
                processed,
                _totalFilesProcessed,
                Path.GetFileName(file),
                "Recompressing"
            );

            var success = await RecompressSingleFileAsync(
                inputFolder,
                outputFolder,
                file,
                preset,
                plan.ResolutionFor(file),
                verify,
                deleteOriginal,
                upgradeMetadata,
                token
            );

            if (success)
                Interlocked.Increment(ref _processedOkCount);
            else
                Interlocked.Increment(ref _failedCount);

            processed++;
            UpdateProgressDisplay(
                processed,
                _totalFilesProcessed,
                Path.GetFileName(file),
                "Finishing"
            );
            UpdateStatsDisplay();
            UpdateProcessingTimeDisplay();
            UpdateWriteSpeedFromPerformanceCounter();
        }
    }

    /// <summary>Recompresses one CHD and moves the verified output into place.</summary>
    /// <param name="inputFolder">Root of the recompression input folder.</param>
    /// <param name="outputFolder">Root of the recompression output folder.</param>
    /// <param name="sourcePath">Full path of the CHD to recompress.</param>
    /// <param name="preset">Compression preset to apply.</param>
    /// <param name="resolution">Precomputed parent resolution for the source file.</param>
    /// <param name="verify">True to verify each recompressed CHD.</param>
    /// <param name="deleteOriginal">True to delete the source after a successful recompression.</param>
    /// <param name="upgradeMetadata">True to upgrade the CHD metadata while recompressing.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>True when the file was recompressed successfully.</returns>
    private async Task<bool> RecompressSingleFileAsync(
        string inputFolder,
        string outputFolder,
        string sourcePath,
        ChdCompressionPreset preset,
        ParentChdResolver.Resolution resolution,
        bool verify,
        bool deleteOriginal,
        bool upgradeMetadata,
        CancellationToken token
    )
    {
        if (!string.IsNullOrWhiteSpace(resolution.Warning))
            LogWarning($" {resolution.Warning}");

        if (resolution.Error is not null)
        {
            LogError($" Failed to recompress '{Path.GetFileName(sourcePath)}': {resolution.Error}");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(resolution.ParentPath))
            LogMessage($" Parent: {Path.GetFileName(resolution.ParentPath)}");

        var relativeDirectory = PathUtils.GetSafeRelativePath(
            inputFolder,
            Path.GetDirectoryName(sourcePath) ?? inputFolder
        );
        var outputDirectory = string.Equals(relativeDirectory, ".", StringComparison.Ordinal)
            ? outputFolder
            : Path.Join(outputFolder, relativeDirectory);
        if (!PathUtils.TryCreateDirectory(outputDirectory, out var outputDirectoryError))
        {
            LogError(
                $" Output folder is not available: {outputDirectory} ({outputDirectoryError})."
            );
            return false;
        }

        var destination = Path.Join(outputDirectory, Path.GetFileName(sourcePath));
        if (
            string.Equals(
                Path.GetFullPath(destination),
                Path.GetFullPath(sourcePath),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            destination = Path.Join(
                outputDirectory,
                Path.GetFileNameWithoutExtension(sourcePath) + ".recompressed.chd"
            );
        }

        var staging = Path.Join(
            outputDirectory,
            Path.GetFileNameWithoutExtension(destination)
                + "."
                + Guid.NewGuid().ToString("N")[..8]
                + StagingExtension
        );

        try
        {
            var progress = new ChdSharpProgressLogger(LogMessage, "Recompressing");
            var cores = Environment.ProcessorCount;

            using var speedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var speedToken = speedCts.Token;
            var speedTask = Task.Run(
                async () =>
                {
                    try
                    {
                        while (!speedToken.IsCancellationRequested)
                        {
                            UpdateWriteSpeedFromPerformanceCounter();
                            await Task.Delay(
                                AppConfig.WriteSpeedUpdateIntervalMs,
                                speedToken
                            );
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when the file finishes.
                    }
                },
                CancellationToken.None
            );

            try
            {
                await Task.Run(
                    () =>
                        ChdRecompressService.Recompress(
                            sourcePath,
                            staging,
                            preset,
                            resolution.ParentPath,
                            upgradeMetadata,
                            cores,
                            token,
                            progress.ReportHunk
                        ),
                    token
                );
            }
            finally
            {
                speedCts.Cancel();
                await Task.WhenAny(speedTask, Task.Delay(500, CancellationToken.None));
            }

            if (!File.Exists(staging) || new FileInfo(staging).Length == 0)
            {
                LogError(
                    $" Recompression of '{Path.GetFileName(sourcePath)}' produced no output file."
                );
                TryBestEffortDelete(staging);
                return false;
            }

            if (!verify && deleteOriginal)
            {
                LogMessage(
                    " Deep verification is required before deleting the original; verifying the recompressed CHD."
                );
            }

            if (verify || deleteOriginal)
            {
                var check = await Task.Run(
                    () =>
                    {
                        using var stream = File.OpenRead(staging);
                        return Chd.CheckFile(
                            stream,
                            Path.GetFileName(staging),
                            true,
                            cancellationToken: token
                        );
                    },
                    token
                );

                if (!check.IsSuccess)
                {
                    LogError(
                        $" Recompressed CHD for '{Path.GetFileName(sourcePath)}' failed verification ({check.Error.GetMessage()}); keeping the original."
                    );
                    TryBestEffortDelete(staging);
                    return false;
                }
            }

            if (File.Exists(destination))
            {
                var deleted = await RetryingFileOperations
                    .TryDeleteAsync(destination, token)
                    .ConfigureAwait(false);
                if (!deleted)
                    throw new IOException($"Could not replace '{destination}'.");
            }

            var moved = await RetryingFileOperations
                .TryMoveAsync(staging, destination, token)
                .ConfigureAwait(false);
            if (!moved)
                throw new IOException($"Could not move the recompressed CHD to '{destination}'.");

            LogMessage($" Recompressed: {Path.GetFileName(destination)}");

            if (deleteOriginal && !string.Equals(destination, sourcePath, StringComparison.OrdinalIgnoreCase))
                await TryDeleteFileAsync(sourcePath, "original CHD", token);

            return true;
        }
        catch (OperationCanceledException)
        {
            TryBestEffortDelete(staging);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TryBestEffortDelete(staging);
            LogError($" Failed to recompress '{Path.GetFileName(sourcePath)}': {ex.Message}");
            return false;
        }
    }
}

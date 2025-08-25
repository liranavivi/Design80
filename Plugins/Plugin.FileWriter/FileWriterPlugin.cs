using Microsoft.Extensions.Logging;
using Plugin.FileWriter.Models;
using Plugin.FileWriter.Services;
using Plugin.Shared.Interfaces;
using Plugin.Shared.Utilities;
using Processor.Base.Models;
using Processor.Base.Utilities;
using Shared.Correlation;
using Shared.Models;
using System.Text.Json;

namespace Plugin.FileWriter;

/// <summary>
/// FileWriter plugin implementation that writes extracted files from compressed archives to disk
/// Processes cache data from FileReaderProcessor containing compressed file metadata and extracted files
/// </summary>
public class FileWriterPlugin : IPlugin
{
    private readonly ILogger<FileWriterPlugin> _logger;
    private readonly IFileWriterPluginMetricsService _metricsService;



    /// <summary>
    /// Constructor with dependency injection - simplified to align with processor
    /// </summary>
    public FileWriterPlugin(ILogger<FileWriterPlugin> logger)
    {
        _logger = logger;

        // Create metrics service with a logger
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var metricsLogger = loggerFactory.CreateLogger<FileWriterPluginMetricsService>();
        _metricsService = new FileWriterPluginMetricsService(metricsLogger);
    }

    /// <summary>
    /// Plugin implementation of ProcessActivityDataAsync
    /// Processes cache data from FileReaderProcessor and writes extracted files to disk
    /// </summary>
    public async Task<IEnumerable<ProcessedActivityData>> ProcessActivityDataAsync(
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid executionId,
        Guid publishId,
        List<AssignmentModel> entities,
        string inputData, // Contains cacheData from FileReaderProcessor
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var processingStart = DateTime.UtcNow;

        _logger.LogInformationWithCorrelation(
            "Starting FileWriter plugin processing - ProcessorId: {ProcessorId}, StepId: {StepId}, ExecutionId: {ExecutionId}",
            processorId, stepId, executionId);

        try
        {
            // 1. Validate entities collection - must have at least one AddressAssignmentModel
            var addressAssignment = entities.OfType<AddressAssignmentModel>().FirstOrDefault();
            if (addressAssignment == null)
            {
                throw new InvalidOperationException("AddressAssignmentModel not found in entities. FileWriterPlugin expects at least one AddressAssignmentModel.");
            }

            _logger.LogInformationWithCorrelation(
                "Processing {EntityCount} entities with AddressAssignmentModel: {AddressName} (EntityId: {EntityId})",
                entities.Count, addressAssignment.Name, addressAssignment.EntityId);

            // 1. Extract configuration from AddressAssignmentModel (fresh every time - stateless)
            var config = await ExtractConfigurationFromAddressAssignmentAsync(addressAssignment, _logger);

            // 2. Validate configuration
            await ValidateConfigurationAsync(config, _logger);

            // 3. Parse input data (compressed file cache data from FileReaderProcessor)
            var cacheData = await ParseInputCacheDataAsync(inputData, _logger);

            // 4. Process and write all extracted files from the compressed archive
            var result = await ProcessFileCacheDataWithExceptionHandlingAsync(
                cacheData, config, processorId, orchestratedFlowEntityId, stepId,
                executionId, correlationId, _logger, cancellationToken);

            return [result];
        }
        catch (Exception ex)
        {
            var processingDuration = DateTime.UtcNow - processingStart;
            _logger.LogErrorWithCorrelation(ex,
                "FileWriter plugin processing failed - ProcessorId: {ProcessorId}, StepId: {StepId}, Duration: {Duration}ms",
                processorId, stepId, processingDuration.TotalMilliseconds);

            // Return error result
            return new[]
            {
                new ProcessedActivityData
                {
                    Result = $"Error in FileWriter plugin processing: {ex.Message}",
                    Status = ActivityExecutionStatus.Failed,
                    Data = new { },
                    ProcessorName = "FileWriterProcessor", // Keep same name for compatibility
                    Version = "1.0",
                    ExecutionId = executionId
                }
            };
        }
    }

    /// <summary>
    /// Extract configuration from AddressAssignmentModel
    /// </summary>
    private Task<FileWriterConfiguration> ExtractConfigurationFromAddressAssignmentAsync(
        AddressAssignmentModel addressAssignment, ILogger logger)
    {
        logger.LogDebugWithCorrelation("Extracting configuration from AddressAssignmentModel. EntityId: {EntityId}, Name: {Name}",
            addressAssignment.EntityId, addressAssignment.Name);

        // Parse JSON payload
        JsonElement root;
        try
        {
            var document = JsonDocument.Parse(addressAssignment.Payload);
            root = document.RootElement;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in AddressAssignmentModel payload: {ex.Message}", ex);
        }

        // Extract configuration using shared utilities (the old way)
        var config = new FileWriterConfiguration
        {
            // From ConnectionString (the old way)
            FolderPath = addressAssignment.ConnectionString,

            // From Payload JSON using shared utilities
            ProcessingMode = JsonConfigurationExtractor.GetEnumValue<FileProcessingMode>(root, "processingMode", FileProcessingMode.LeaveUnchanged),
            ProcessedExtension = JsonConfigurationExtractor.GetStringValue(root, "processedExtension", ".written"),
            BackupFolder = JsonConfigurationExtractor.GetStringValue(root, "backupFolder", string.Empty),
            MinExtractedContentSize = JsonConfigurationExtractor.GetLongValue(root, "minExtractedContentSize", 0),
            MaxExtractedContentSize = JsonConfigurationExtractor.GetLongValue(root, "maxExtractedContentSize", long.MaxValue)
        };

        logger.LogInformationWithCorrelation(
            "Extracted FileWriter configuration - FolderPath: {FolderPath}, ProcessingMode: {ProcessingMode}, ProcessedExtension: {ProcessedExtension}",
            config.FolderPath, config.ProcessingMode, config.ProcessedExtension);

        return Task.FromResult(config);
    }

    /// <summary>
    /// Validate the extracted configuration
    /// </summary>
    private Task ValidateConfigurationAsync(FileWriterConfiguration config, ILogger logger)
    {
        logger.LogDebugWithCorrelation("Validating configuration");

        if (string.IsNullOrWhiteSpace(config.FolderPath))
        {
            throw new InvalidOperationException("FolderPath cannot be empty");
        }

        // Validate backup folder if needed for backup modes (aligned with other processors)
        var backupModes = new[]
        {
            FileProcessingMode.MoveToBackup,
            FileProcessingMode.CopyToBackup,
            FileProcessingMode.BackupAndMarkProcessed,
            FileProcessingMode.BackupAndDelete
        };

        if (backupModes.Contains(config.ProcessingMode) && string.IsNullOrWhiteSpace(config.BackupFolder))
        {
            throw new InvalidOperationException($"BackupFolder is required for processing mode: {config.ProcessingMode}");
        }

        logger.LogDebugWithCorrelation("Configuration validation completed successfully");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Parse input data containing compressed file cache data from FileReaderProcessor
    /// </summary>
    private async Task<object> ParseInputCacheDataAsync(string inputData, ILogger logger)
    {
        logger.LogDebugWithCorrelation("Parsing input compressed file cache data");

        if (string.IsNullOrWhiteSpace(inputData))
        {
            throw new InvalidOperationException("Input data is empty - FileWriter expects cache data from FileReaderProcessor");
        }

        try
        {
            // Parse as single JSON object (FileWriter processes one compressed file cache data at a time)
            var jsonElement = JsonSerializer.Deserialize<JsonElement>(inputData);

            logger.LogInformationWithCorrelation("Successfully parsed compressed file cache data item");
            return await Task.FromResult(jsonElement);
        }
        catch (JsonException ex)
        {
            logger.LogErrorWithCorrelation(ex, "Failed to parse input cache data as JSON");
            throw new InvalidOperationException("Invalid JSON format in input data", ex);
        }
    }

    /// <summary>
    /// Process compressed file cache data with exception handling
    /// </summary>
    private async Task<ProcessedActivityData> ProcessFileCacheDataWithExceptionHandlingAsync(
        object cacheDataItem,
        FileWriterConfiguration config,
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid executionId,
        Guid correlationId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var processingStart = DateTime.UtcNow;
        var metricsService = _metricsService;

        try
        {
            metricsService.IncrementActiveWriting();

            var result = await ProcessFileCacheDataAsync(
                cacheDataItem, config, processorId, orchestratedFlowEntityId, stepId,
                executionId, correlationId, logger, cancellationToken);

            var processingDuration = DateTime.UtcNow - processingStart;
            logger.LogDebugWithCorrelation(
                "Successfully processed compressed file cache data item in {Duration}ms",
                processingDuration.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            var processingDuration = DateTime.UtcNow - processingStart;
            logger.LogErrorWithCorrelation(ex,
                "Failed to process compressed file cache data item in {Duration}ms",
                processingDuration.TotalMilliseconds);

            metricsService.RecordWritingError(ex.GetType().Name, "unknown");

            return new ProcessedActivityData
            {
                Result = $"Error processing compressed file cache data: {ex.Message}",
                Status = ActivityExecutionStatus.Failed,
                Data = new { },
                ProcessorName = "FileWriterProcessor",
                Version = "1.0",
                ExecutionId = executionId
            };
        }
        finally
        {
            metricsService.DecrementActiveWriting();
        }
    }

    /// <summary>
    /// Process compressed file cache data and write extracted files to disk
    /// </summary>
    private async Task<ProcessedActivityData> ProcessFileCacheDataAsync(
        object cacheDataItem,
        FileWriterConfiguration config,
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid executionId,
        Guid correlationId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var processingStart = DateTime.UtcNow;
        var metricsService = _metricsService;

        logger.LogInformationWithCorrelation("Processing compressed file cache data for writing");

        try
        {
            // Parse the cache data structure
            var cacheDataJson = JsonSerializer.Serialize(cacheDataItem);
            var cacheDataDoc = JsonDocument.Parse(cacheDataJson);
            var root = cacheDataDoc.RootElement;

            // Extract file cache data object
            if (!root.TryGetProperty("fileCacheDataObject", out JsonElement fileCacheDataObject))
            {
                throw new InvalidOperationException("Missing fileCacheDataObject in cache data");
            }

            // Extract extracted files array (can be empty for regular files or empty archives)
            var extractedFiles = new List<ProcessedFileInfo>();
            if (fileCacheDataObject.TryGetProperty("extractedFiles", out JsonElement extractedFilesElement))
            {
                foreach (var fileElement in extractedFilesElement.EnumerateArray())
                {
                    var fileInfo = ParseExtractedFileInfo(fileElement, logger);
                    if (fileInfo != null && IsFileSizeValid(fileInfo, config, logger))
                    {
                        extractedFiles.Add(fileInfo);
                    }
                }
            }

            logger.LogInformationWithCorrelation(
                "Found {ExtractedFileCount} valid extracted files to write (empty list is valid for regular files or empty archives)",
                extractedFiles.Count);

            // Write the original file first
            var writtenFiles = new List<string>();
            var totalBytesWritten = 0L;

            // Parse and write the original file from fileCacheDataObject
            var originalFileInfo = ParseOriginalFileInfo(fileCacheDataObject, logger);
            if (originalFileInfo != null && IsFileSizeValid(originalFileInfo, config, logger))
            {
                var originalOutputPath = await WriteExtractedFileAsync(
                    originalFileInfo, config, logger, cancellationToken);

                writtenFiles.Add(originalOutputPath);
                totalBytesWritten += originalFileInfo.FileContent?.Length ?? 0;

                logger.LogInformationWithCorrelation(
                    "Successfully wrote original file: {FileName} ({FileSize} bytes)",
                    originalFileInfo.FileName, originalFileInfo.FileContent?.Length ?? 0);
            }

            // Write all extracted files (if any)
            foreach (var fileInfo in extractedFiles)
            {
                var outputFilePath = await WriteExtractedFileAsync(
                    fileInfo, config, logger, cancellationToken);

                writtenFiles.Add(outputFilePath);
                totalBytesWritten += fileInfo.FileContent?.Length ?? 0;
            }

            var processingDuration = DateTime.UtcNow - processingStart;
            var totalFilesProcessed = writtenFiles.Count; // Includes original file + extracted files
            var archiveType = extractedFiles.Count > 0 ? "archive_with_files" : "regular_file_or_empty_archive";
            metricsService.RecordArchiveProcessing(
                processingDuration.TotalMilliseconds,
                totalFilesProcessed,
                archiveType);

            metricsService.RecordThroughput(totalBytesWritten, processingDuration.TotalMilliseconds);

            // Log appropriate message based on what was written
            if (extractedFiles.Count > 0)
            {
                logger.LogInformationWithCorrelation(
                    "Successfully wrote original file + {ExtractedFileCount} extracted files = {TotalFileCount} total files, Total size: {TotalSize} bytes, Duration: {Duration}ms",
                    extractedFiles.Count, totalFilesProcessed, totalBytesWritten, processingDuration.TotalMilliseconds);
            }
            else
            {
                logger.LogInformationWithCorrelation(
                    "Successfully wrote original file (no extracted files), Total files: {TotalFileCount}, Total size: {TotalSize} bytes, Duration: {Duration}ms",
                    totalFilesProcessed, totalBytesWritten, processingDuration.TotalMilliseconds);
            }

            return new ProcessedActivityData
            {
                Status = ActivityExecutionStatus.Completed,
                Data = new { },
                Result = extractedFiles.Count > 0
                    ? $"Successfully wrote original file + {extractedFiles.Count} extracted files = {writtenFiles.Count} total files ({totalBytesWritten} bytes)"
                    : $"Successfully wrote original file ({totalBytesWritten} bytes)",
                ProcessorName = "FileWriterProcessor",
                Version = "1.0",
                ExecutionId = executionId
            };
        }
        catch (Exception ex)
        {
            var processingDuration = DateTime.UtcNow - processingStart;
            logger.LogErrorWithCorrelation(ex,
                "Failed to process compressed file cache data in {Duration}ms",
                processingDuration.TotalMilliseconds);

            metricsService.RecordArchiveProcessingError(ex.GetType().Name, "compressed_archive");

            return new ProcessedActivityData
            {
                Result = $"Error processing compressed file cache data: {ex.Message}",
                Status = ActivityExecutionStatus.Failed,
                Data = new { },
                ProcessorName = "FileWriterProcessor",
                Version = "1.0",
                ExecutionId = executionId
            };
        }
    }

    /// <summary>
    /// Parse original file info from fileCacheDataObject
    /// </summary>
    private ProcessedFileInfo? ParseOriginalFileInfo(JsonElement fileCacheDataObject, ILogger logger)
    {
        try
        {
            // Extract fileMetadata
            if (!fileCacheDataObject.TryGetProperty("fileMetadata", out JsonElement fileMetadata))
            {
                logger.LogWarningWithCorrelation("Missing fileMetadata in fileCacheDataObject");
                return null;
            }

            // Extract fileContent
            if (!fileCacheDataObject.TryGetProperty("fileContent", out JsonElement fileContent))
            {
                logger.LogWarningWithCorrelation("Missing fileContent in fileCacheDataObject");
                return null;
            }

            // Parse file metadata
            var fileName = fileMetadata.TryGetProperty("fileName", out var fileNameElement) ? fileNameElement.GetString() ?? "" : "";
            var filePath = fileMetadata.TryGetProperty("filePath", out var filePathElement) ? filePathElement.GetString() ?? "" : "";
            var fileSize = fileMetadata.TryGetProperty("fileSize", out var fileSizeElement) ? fileSizeElement.GetInt64() : 0;
            var createdDate = fileMetadata.TryGetProperty("createdDate", out var createdElement) ?
                (DateTime.TryParse(createdElement.GetString(), out var created) ? created : DateTime.UtcNow) : DateTime.UtcNow;
            var modifiedDate = fileMetadata.TryGetProperty("modifiedDate", out var modifiedElement) ?
                (DateTime.TryParse(modifiedElement.GetString(), out var modified) ? modified : DateTime.UtcNow) : DateTime.UtcNow;
            var fileExtension = fileMetadata.TryGetProperty("fileExtension", out var extensionElement) ? extensionElement.GetString() ?? "" : "";

            // Parse file content
            var binaryData = fileContent.TryGetProperty("binaryData", out var binaryElement) ? binaryElement.GetString() ?? "" : "";
            var encoding = fileContent.TryGetProperty("encoding", out var encodingElement) ? encodingElement.GetString() ?? "base64" : "base64";

            // Decode content
            byte[] contentBytes;
            if (encoding == "base64" && !string.IsNullOrEmpty(binaryData))
            {
                contentBytes = Convert.FromBase64String(binaryData);
            }
            else
            {
                logger.LogWarningWithCorrelation("Unsupported encoding or empty content: {Encoding}", encoding);
                return null;
            }

            return new ProcessedFileInfo
            {
                FileName = fileName,
                FilePath = filePath,
                FileContent = contentBytes,
                CreatedDate = createdDate,
                ModifiedDate = modifiedDate,
                FileExtension = fileExtension
            };
        }
        catch (Exception ex)
        {
            logger.LogWarningWithCorrelation(ex, "Failed to parse original file info");
            return null;
        }
    }

    /// <summary>
    /// Parse extracted file info from JSON element
    /// </summary>
    private ProcessedFileInfo? ParseExtractedFileInfo(JsonElement fileElement, ILogger logger)
    {
        try
        {
            var fileName = fileElement.GetProperty("fileName").GetString() ?? "";
            var filePath = fileElement.GetProperty("filePath").GetString() ?? "";
            var fileSize = fileElement.GetProperty("fileSize").GetInt64();
            var fileExtension = fileElement.GetProperty("fileExtension").GetString() ?? "";
            var fileType = fileElement.GetProperty("fileType").GetString() ?? "";
            var contentEncoding = fileElement.GetProperty("contentEncoding").GetString() ?? "";
            var detectedMimeType = fileElement.GetProperty("detectedMimeType").GetString() ?? "";
            var contentHash = fileElement.GetProperty("contentHash").GetString() ?? "";

            // Parse dates
            var createdDate = DateTime.TryParse(fileElement.GetProperty("createdDate").GetString(), out var created) ? created : DateTime.UtcNow;
            var modifiedDate = DateTime.TryParse(fileElement.GetProperty("modifiedDate").GetString(), out var modified) ? modified : DateTime.UtcNow;

            // Decode file content from Base64
            var fileContentBase64 = fileElement.GetProperty("fileContent").GetString() ?? "";
            var fileContent = Convert.FromBase64String(fileContentBase64);

            return new ProcessedFileInfo
            {
                FileName = fileName,
                FilePath = filePath,
                FileSize = fileSize,
                CreatedDate = createdDate,
                ModifiedDate = modifiedDate,
                FileExtension = fileExtension,
                FileContent = fileContent,
                ContentEncoding = contentEncoding,
                DetectedMimeType = detectedMimeType,
                FileType = fileType,
                ContentHash = contentHash
            };
        }
        catch (Exception ex)
        {
            logger.LogWarningWithCorrelation(ex, "Failed to parse extracted file info");
            return null;
        }
    }

    /// <summary>
    /// Check if file size is within configured limits
    /// </summary>
    private bool IsFileSizeValid(ProcessedFileInfo fileInfo, FileWriterConfiguration config, ILogger logger)
    {
        var fileSize = fileInfo.FileContent?.Length ?? 0;

        if (fileSize < config.MinExtractedContentSize)
        {
            logger.LogDebugWithCorrelation(
                "Skipping file due to size below minimum - File: {FileName}, Size: {Size}, Min: {Min}",
                fileInfo.FileName, fileSize, config.MinExtractedContentSize);
            return false;
        }

        if (fileSize > config.MaxExtractedContentSize)
        {
            logger.LogDebugWithCorrelation(
                "Skipping file due to size above maximum - File: {FileName}, Size: {Size}, Max: {Max}",
                fileInfo.FileName, fileSize, config.MaxExtractedContentSize);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Write extracted file to disk with full processing pipeline
    /// </summary>
    private async Task<string> WriteExtractedFileAsync(
        ProcessedFileInfo fileInfo,
        FileWriterConfiguration config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var processingStart = DateTime.UtcNow;
        var metricsService = _metricsService;

        logger.LogDebugWithCorrelation(
            "Writing extracted file - Name: {FileName}, Size: {Size} bytes",
            fileInfo.FileName, fileInfo.FileContent?.Length ?? 0);

        try
        {
            // Generate output file path preserving original filename with collision handling
            var outputFilePath = await GenerateOutputFilePathAsync(fileInfo, config, logger);
            
            // Ensure output directory exists
            var outputDirectory = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
                logger.LogDebugWithCorrelation("Created output directory: {Directory}", outputDirectory);
            }

            // Write the file (same pattern as FileWriterProcessor)
            var writeStart = DateTime.UtcNow;
            await WriteFileAsync(outputFilePath, fileInfo, logger, cancellationToken);
            var writeDuration = DateTime.UtcNow - writeStart;
            metricsService.RecordExtractedFileWrite(writeDuration.TotalMilliseconds, fileInfo.FileContent?.Length ?? 0, fileInfo.FileExtension, fileInfo.FileType);

            // Validate written file
            await ValidateWrittenFileAsync(outputFilePath, fileInfo, logger);

            // Post-process the file using shared utilities
            var postProcessStart = DateTime.UtcNow;
            await FilePostProcessing.PostProcessFileAsync(outputFilePath, config, logger);
            var postProcessDuration = DateTime.UtcNow - postProcessStart;
            metricsService.RecordFilePostProcessing(postProcessDuration.TotalMilliseconds, config.ProcessingMode.ToString(), fileInfo.FileExtension);

            var processingDuration = DateTime.UtcNow - processingStart;
            logger.LogInformationWithCorrelation(
                "Successfully wrote extracted file - Path: {FilePath}, Size: {Size} bytes, Duration: {Duration}ms",
                outputFilePath, fileInfo.FileContent?.Length ?? 0, processingDuration.TotalMilliseconds);

            return outputFilePath;
        }
        catch (Exception ex)
        {
            var processingDuration = DateTime.UtcNow - processingStart;
            logger.LogErrorWithCorrelation(ex,
                "Failed to write extracted file - Name: {FileName}, Duration: {Duration}ms",
                fileInfo.FileName, processingDuration.TotalMilliseconds);

            metricsService.RecordWritingError(ex.GetType().Name, fileInfo.FileExtension);
            throw;
        }
    }

    /// <summary>
    /// Generate output file path preserving original filename with collision handling using shared utilities
    /// </summary>
    private Task<string> GenerateOutputFilePathAsync(
        ProcessedFileInfo fileInfo,
        FileWriterConfiguration config,
        ILogger logger)
    {
        // Use standardized collision handling from shared utilities
        var outputFilePath = FilePostProcessing.GenerateOutputPath(
            fileInfo.FileName,
            config.FolderPath);

        logger.LogDebugWithCorrelation(
            "Generated output file path - Original: {Original}, Output: {Output}",
            fileInfo.FileName, outputFilePath);

        return Task.FromResult(outputFilePath);
    }

    /// <summary>
    /// Write file content to disk (same pattern as FileWriterProcessor)
    /// </summary>
    private static async Task WriteFileAsync(string filePath, ProcessedFileInfo fileInfo, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            if (fileInfo.FileContent == null)
            {
                throw new InvalidOperationException("File content is null");
            }

            await File.WriteAllBytesAsync(filePath, fileInfo.FileContent, cancellationToken);

            // Preserve original metadata (same pattern as FileWriterProcessor)
            var fileInfo_system = new FileInfo(filePath);
            fileInfo_system.CreationTime = fileInfo.CreatedDate;
            fileInfo_system.LastWriteTime = fileInfo.ModifiedDate;

            logger.LogDebugWithCorrelation(
                "Successfully wrote file - Path: {FilePath}, Size: {Size} bytes",
                filePath, fileInfo.FileContent.Length);
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex,
                "Failed to write file - Path: {FilePath}",
                filePath);
            throw;
        }
    }

    /// <summary>
    /// Validate written file (same pattern as FileWriterProcessor)
    /// </summary>
    private static Task ValidateWrittenFileAsync(string filePath, ProcessedFileInfo originalFileInfo, ILogger logger)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                throw new InvalidOperationException($"Written file does not exist: {filePath}");
            }

            var writtenFileInfo = new FileInfo(filePath);
            var expectedSize = originalFileInfo.FileContent?.Length ?? 0;

            if (writtenFileInfo.Length != expectedSize)
            {
                throw new InvalidOperationException(
                    $"File size mismatch - Expected: {expectedSize}, Actual: {writtenFileInfo.Length}");
            }

            logger.LogDebugWithCorrelation(
                "File validation successful - Path: {FilePath}, Size: {Size} bytes",
                filePath, writtenFileInfo.Length);
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex,
                "File validation failed - Path: {FilePath}",
                filePath);
            throw;
        }

        return Task.CompletedTask;
    }
}

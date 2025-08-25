using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plugin.FileReader.Interfaces;
using Plugin.FileReader.Models;
using Plugin.FileReader.Services;
using Plugin.FileReader.Utilities;
using Plugin.Shared.Interfaces;
using Plugin.Shared.Utilities;
using Processor.Base.Interfaces;
using Processor.Base.Models;
using Processor.Base.Utilities;
using Shared.Correlation;
using Shared.MassTransit.Commands;
using Shared.Models;
using Shared.Services.Interfaces;
using SharpCompress.Archives;
using SharpCompress.Common;
using System.Text.Json;

namespace Plugin.FileReader;

/// <summary>
/// FileReader plugin implementation that handles compressed archives (ZIP, RAR, 7-Zip, GZIP, TAR)
/// Implements IPlugin interface for dynamic loading by PluginLoaderProcessor
/// </summary>
public class FileReaderPlugin : IPlugin
{
    private readonly ILogger<FileReaderPlugin> _logger;
    private readonly IBus _bus;
    private readonly IProcessorService _processorService;
    private readonly IFileReaderPluginMetricsService _metricsService;
    private readonly IFileRegistrationService _fileRegistrationService;



    /// <summary>
    /// Constructor with dependency injection using plugin-scoped service collection
    /// </summary>
    public FileReaderPlugin(
        ILogger<FileReaderPlugin> logger,
        IBus bus,
        IProcessorService processorService,
        ICacheService cacheService,
        ILoggerFactory loggerFactory)
    {
        // Store host-provided services
        _logger = logger;
        _bus = bus;
        _processorService = processorService;

        // Create plugin's own service collection for plugin-specific dependencies
        var services = new ServiceCollection();

        // Add logging infrastructure to enable ILogger<T> resolution
        services.AddLogging();
        services.AddSingleton(loggerFactory);

        // Register plugin-specific services
        services.AddSingleton<IFileReaderPluginMetricsService, FileReaderPluginMetricsService>();
        services.AddSingleton(cacheService);

        // Configure plugin-specific options
        services.Configure<FileRegistrationCacheConfiguration>(config => {
            // Use default configuration - can be customized as needed
        });

        // Register FileRegistrationService
        services.AddSingleton<IFileRegistrationService, FileRegistrationService>();

        // Build plugin's service provider
        var pluginServiceProvider = services.BuildServiceProvider();

        // Resolve plugin-specific services
        _metricsService = pluginServiceProvider.GetRequiredService<IFileReaderPluginMetricsService>();
        _fileRegistrationService = pluginServiceProvider.GetRequiredService<IFileRegistrationService>();
    }

    /// <summary>
    /// Plugin implementation of ProcessActivityDataAsync
    /// Handles both discovery phase (executionId empty) and processing phase (executionId populated)
    /// </summary>
    public async Task<IEnumerable<ProcessedActivityData>> ProcessActivityDataAsync(
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid executionId,
        Guid publishId,
        List<AssignmentModel> entities,
        string inputData, // Discovery: original config | Processing: cached file path
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var processingStart = DateTime.UtcNow;

        _logger.LogInformationWithCorrelation(
            "Starting FileReader plugin processing - ProcessorId: {ProcessorId}, StepId: {StepId}, ExecutionId: {ExecutionId}",
            processorId, stepId, executionId);

        try
        {
            // 1. Validate entities collection - must have at least one AddressAssignmentModel
            var addressAssignment = entities.OfType<AddressAssignmentModel>().FirstOrDefault();
            if (addressAssignment == null)
            {
                throw new InvalidOperationException("AddressAssignmentModel not found in entities. FileReaderPlugin expects at least one AddressAssignmentModel.");
            }

            _logger.LogInformationWithCorrelation(
                "Processing {EntityCount} entities with AddressAssignmentModel: {AddressName} (EntityId: {EntityId})",
                entities.Count, addressAssignment.Name, addressAssignment.EntityId);

            // 1. Extract configuration from AddressAssignmentModel (fresh every time - stateless)
            var config = await ExtractConfigurationFromAddressAssignmentAsync(addressAssignment, _logger);

            // 2. Validate configuration
            await ValidateConfigurationAsync(config, _logger);

            // 3. Route based on execution phase
            if (executionId == Guid.Empty)
            {
                // Discovery phase: discover files and cache them
                _logger.LogInformationWithCorrelation("Starting file discovery phase");
                return await ProcessFileDiscoveryAsync(
                    config, entities, processorId, orchestratedFlowEntityId, stepId,
                    executionId, correlationId, _logger, cancellationToken);
            }
            else
            {
                // Processing phase: process individual file from cache
                _logger.LogInformationWithCorrelation("Starting individual file processing phase for executionId: {ExecutionId}", executionId);
                var result = await ProcessIndividualFileAsync(
                    config, inputData, processorId, orchestratedFlowEntityId, stepId,
                    executionId, correlationId, _logger, cancellationToken);
                return new[] { result };
            }
        }
        catch (Exception ex)
        {
            var processingDuration = DateTime.UtcNow - processingStart;
            _logger.LogErrorWithCorrelation(ex,
                "FileReader plugin processing failed - ProcessorId: {ProcessorId}, StepId: {StepId}, Duration: {Duration}ms",
                processorId, stepId, processingDuration.TotalMilliseconds);

            // Return error result
            return new[]
            {
                new ProcessedActivityData
                {
                    Result = $"Error in FileReader plugin processing: {ex.Message}",
                    Status = ActivityExecutionStatus.Failed,
                    Data = new { },
                    ProcessorName = "FileReaderProcessor", // Keep same name for compatibility
                    Version = "1.0",
                    ExecutionId = executionId
                }
            };
        }
    }

    /// <summary>
    /// Process file discovery phase - discover files and cache them for individual processing
    /// </summary>
    private async Task<IEnumerable<ProcessedActivityData>> ProcessFileDiscoveryAsync(
        FileReaderConfiguration config,
        List<AssignmentModel> entities,
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid executionId,
        Guid correlationId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformationWithCorrelation(
            "Using cache-based approach for file discovery and processing");

        try
        {
            // Use local discovery and caching logic
            var registeredFiles = await DiscoverAndRegisterFilesAsync(
                config,
                _fileRegistrationService,
                _processorService,
                processorId,
                executionId,
                correlationId,
                orchestratedFlowEntityId,
                stepId,
                entities,
                logger);

            logger.LogInformationWithCorrelation(
                "File discovery phase completed - Registered {RegisteredFiles} files for individual processing",
                registeredFiles);

            // Return empty result - actual processing happens in individual file processing phase
            return new List<ProcessedActivityData>();
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex,
                "Failed to complete file discovery phase");

            // Return error result for discovery phase
            return new[]
            {
                new ProcessedActivityData
                {
                    Result = $"Error in file discovery phase: {ex.Message}",
                    Status = ActivityExecutionStatus.Failed,
                    Data = new { },
                    ProcessorName = "FileReaderProcessor",
                    Version = "1.0",
                    ExecutionId = executionId
                }
            };
        }
    }

    private Task<FileReaderConfiguration> ExtractConfigurationFromAddressAssignmentAsync(
        AddressAssignmentModel addressAssignment,
        ILogger logger)
    {
        logger.LogDebugWithCorrelation("Extracting configuration from AddressAssignmentModel. EntityId: {EntityId}, Name: {Name}",
            addressAssignment.EntityId, addressAssignment.Name);

        if (string.IsNullOrEmpty(addressAssignment.Payload))
        {
            throw new InvalidOperationException("AddressAssignmentModel.Payload (configuration JSON) cannot be empty");
        }

        // Parse JSON using consistent JsonElement pattern
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(addressAssignment.Payload ?? "{}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in AddressAssignmentModel.Payload", ex);
        }

        // Extract configuration using shared utilities (the old way)
        var config = new FileReaderConfiguration
        {
            // From ConnectionString (the old way)
            FolderPath = addressAssignment.ConnectionString,
            SearchPattern = JsonConfigurationExtractor.GetStringValue(root, "searchPattern", "*.{zip,rar,7z,gz,tar}"),
            MaxFilesToProcess = JsonConfigurationExtractor.GetIntValue(root, "maxFilesToProcess", 50),
            ProcessingMode = JsonConfigurationExtractor.GetEnumValue<FileProcessingMode>(root, "processingMode", FileProcessingMode.LeaveUnchanged),
            ProcessedExtension = JsonConfigurationExtractor.GetStringValue(root, "processedExtension", ".processed"),
            BackupFolder = JsonConfigurationExtractor.GetStringValue(root, "backupFolder", ""),

            // File size filters
            MinFileSize = JsonConfigurationExtractor.GetLongValue(root, "minFileSize", 0),
            MaxFileSize = JsonConfigurationExtractor.GetLongValue(root, "maxFileSize", 100 * 1024 * 1024), // 100MB
            MinExtractedSize = JsonConfigurationExtractor.GetLongValue(root, "minExtractedSize", 0),
            MaxExtractedSize = JsonConfigurationExtractor.GetLongValue(root, "maxExtractedSize", 50 * 1024 * 1024), // 50MB

            // Archive analysis
            MinEntriesToList = JsonConfigurationExtractor.GetIntValue(root, "minEntriesToList", 1),
            MaxEntriesToList = JsonConfigurationExtractor.GetIntValue(root, "maxEntriesToList", 100)
        };

        logger.LogInformationWithCorrelation(
            "Extracted FileReader configuration from AddressAssignmentModel - FolderPath: {FolderPath}, SearchPattern: {SearchPattern}, MaxFiles: {MaxFiles}",
            config.FolderPath, config.SearchPattern, config.MaxFilesToProcess);

        return Task.FromResult(config);
    }

    private Task ValidateConfigurationAsync(FileReaderConfiguration config, ILogger logger)
    {
        logger.LogInformationWithCorrelation("Validating FileReader configuration");

        if (string.IsNullOrWhiteSpace(config.FolderPath))
        {
            throw new InvalidOperationException("FolderPath cannot be empty");
        }

        if (!Directory.Exists(config.FolderPath))
        {
            throw new DirectoryNotFoundException($"Folder does not exist: {config.FolderPath}");
        }

        if (config.MaxFilesToProcess <= 0)
        {
            throw new InvalidOperationException($"MaxFilesToProcess must be greater than 0, but was {config.MaxFilesToProcess}");
        }

        if (config.MaxFileSize <= 0)
        {
            throw new InvalidOperationException($"MaxFileSize must be greater than 0, but was {config.MaxFileSize}");
        }

        if (config.MaxExtractedSize <= 0)
        {
            throw new InvalidOperationException($"MaxExtractedSize must be greater than 0, but was {config.MaxExtractedSize}");
        }

        if (config.MinExtractedSize < 0)
        {
            throw new InvalidOperationException($"MinExtractedSize must be greater than or equal to 0, but was {config.MinExtractedSize}");
        }

        if (config.MinExtractedSize > config.MaxExtractedSize)
        {
            throw new InvalidOperationException($"MinExtractedSize ({config.MinExtractedSize}) cannot be greater than MaxExtractedSize ({config.MaxExtractedSize})");
        }

        if (config.MinEntriesToList < 0)
        {
            throw new InvalidOperationException($"MinEntriesToList must be greater than 0, but was {config.MinEntriesToList}");
        }

        if (config.MaxEntriesToList <= 0)
        {
            throw new InvalidOperationException($"MaxEntriesToList must be greater than 0, but was {config.MaxEntriesToList}");
        }

        if (config.MinEntriesToList > config.MaxEntriesToList)
        {
            throw new InvalidOperationException($"MinEntriesToList ({config.MinEntriesToList}) cannot be greater than MaxEntriesToList ({config.MaxEntriesToList})");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Discovers and registers files for processing via cache and ExecuteActivityCommand
    /// Each file gets a unique executionId generated during registration
    /// </summary>
    private async Task<int> DiscoverAndRegisterFilesAsync(
        FileReaderConfiguration config,
        IFileRegistrationService fileRegistrationService,
        IProcessorService processorService,
        Guid processorId,
        Guid executionId,
        Guid correlationId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        List<AssignmentModel> entities,
        ILogger logger)
    {
        // Single file enumeration
        var allFiles = FilePatternExpander.EnumerateFiles(config.FolderPath, config.SearchPattern).ToList();

        // Apply MaxFilesToProcess limit if specified
        if (config.MaxFilesToProcess > 0 && allFiles.Count > config.MaxFilesToProcess)
        {
            allFiles = allFiles.Take(config.MaxFilesToProcess).ToList();
            logger.LogInformationWithCorrelation(
                "Limited file discovery to {MaxFiles} files (found {TotalFiles} total)",
                config.MaxFilesToProcess, allFiles.Count);
        }

        logger.LogInformationWithCorrelation(
            "Discovered {FileCount} files for processing", allFiles.Count);

        // Registration, caching, and command publishing loop
        var registeredFiles = 0;
        foreach (var filePath in allFiles)
        {
            // Generate unique executionId for each file processing
            var fileExecutionId = Guid.NewGuid();

            // Atomically try to register the file - returns true if successfully added, false if already registered
            var wasAdded = await fileRegistrationService.TryToAddAsync(filePath, processorId, fileExecutionId, correlationId);
            if (wasAdded)
            {
                // Generate unique publishId for each file
                var filePublishId = Guid.NewGuid();

                // Store file path in cache for individual processing
                await processorService.SaveCachedDataAsync(orchestratedFlowEntityId, correlationId, fileExecutionId, stepId, filePublishId, filePath);

                // Publish ExecuteActivityCommand for individual file processing
                await _bus.Publish(new ExecuteActivityCommand
                {
                    ProcessorId = processorId,
                    OrchestratedFlowEntityId = orchestratedFlowEntityId,
                    StepId = stepId,
                    ExecutionId = fileExecutionId, // Use unique executionId for each file
                    PublishId = filePublishId, // Generate unique PublishId for each file
                    CorrelationId = correlationId,
                    Entities = entities, // Include entities for configuration extraction in processing phase
                    CreatedAt = DateTime.UtcNow
                });

                registeredFiles++;

                logger.LogDebugWithCorrelation(
                    "Cached file path and published command for: {FilePath} with ExecutionId: {ExecutionId}",
                    filePath, fileExecutionId);
            }
        }

        logger.LogInformationWithCorrelation(
            "Registered {RegisteredFiles} new files out of {TotalFiles} discovered",
            registeredFiles, allFiles.Count);

        return registeredFiles;
    }

    /// <summary>
    /// Process individual file from cache with pre-processing and file processing logic
    /// </summary>
    private async Task<ProcessedActivityData> ProcessIndividualFileAsync(
        FileReaderConfiguration config,
        string filePath,
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid executionId,
        Guid correlationId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformationWithCorrelation(
            "Processing individual file: {FilePath} with ExecutionId: {ExecutionId}",
            filePath, executionId);

        try
        {
            // FIRST: Call pre-processing (direct static call)
            await FilePostProcessing.PostProcessFileAsync<FileReaderConfiguration>(filePath, config, logger);

            logger.LogDebugWithCorrelation("Pre-processing completed for file: {FilePath}", filePath);

            var processingStart = DateTime.UtcNow;

            // 1. File reading and validation
            if (!File.Exists(filePath))
            {
                logger.LogWarningWithCorrelation("File not found: {FilePath}", filePath);
                return new ProcessedActivityData
                {
                    Result = $"Error in individual file processing: File not found: {filePath}",
                    Status = ActivityExecutionStatus.Failed,
                    Data = new { },
                    ProcessorName = "FileReaderProcessor",
                    Version = "1.0",
                    ExecutionId = executionId
                };
            }

            var fileInfo = new FileInfo(filePath);
            var fileContent = await File.ReadAllBytesAsync(filePath, cancellationToken);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            logger.LogDebugWithCorrelation("Read file: {FilePath}, Size: {Size} bytes", filePath, fileContent.Length);

            // 2. MIME type detection using local method
            var detectedMimeType = DetectMimeType(fileContent, extension);

            // 3. Determine file type and process accordingly
            var fileType = DetermineFileType(extension);
            var extractedFiles = new List<ProcessedFileInfo>();

            // 4. Archive extraction if applicable
            if (IsArchiveFile(extension))
            {
                extractedFiles = await ExtractArchiveAsync(filePath, fileContent, extension, logger, cancellationToken);
                logger.LogInformationWithCorrelation("Extracted {Count} files from archive: {FilePath}", extractedFiles.Count, filePath);
            }

            // 5. Create file cache data object
            var fileCacheData = CreateFileCacheDataObject(filePath, fileContent, detectedMimeType, fileType, extractedFiles, fileInfo);

            var processingDuration = DateTime.UtcNow - processingStart;

            // 6. Record metrics (commented out for now)
            _metricsService.RecordFileProcessed(
                fileType,
                processingDuration.TotalMilliseconds,
                fileContent.Length,
                extractedFiles.Sum(f => f.FileContent?.Length ?? 0),
                extractedFiles.Count > 0 ? (double)fileContent.Length / extractedFiles.Sum(f => f.FileContent?.Length ?? 1) : 1.0,
                extractedFiles.Count,
                0 // Security warnings - TODO: implement security analysis
            );

            logger.LogInformationWithCorrelation("Successfully processed file: {FilePath} in {Duration}ms", filePath, processingDuration.TotalMilliseconds);

            return new ProcessedActivityData
            {
                Status = ActivityExecutionStatus.Completed,
                Data = fileCacheData,
                Result = $"Successfully processed {fileType} file: {Path.GetFileName(filePath)} ({extractedFiles.Count} extracted files)",
                ProcessorName = "FileReaderProcessor",
                Version = "1.0",
                ExecutionId = executionId
            };
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex,
                "Failed to process individual file: {FilePath} with ExecutionId: {ExecutionId}",
                filePath, executionId);

            return new ProcessedActivityData
            {
                Result = $"Error in individual file processing: {ex.Message}",
                Status = ActivityExecutionStatus.Failed,
                Data = new { },
                ProcessorName = "FileReaderProcessor",
                Version = "1.0",
                ExecutionId = executionId
            };
        }
    }

    /// <summary>
    /// Detect MIME type from file content and extension
    /// </summary>
    private string DetectMimeType(byte[] fileContent, string fileExtension)
    {
        // Basic MIME type detection based on file signatures
        if (fileContent.Length >= 4)
        {
            var signature = fileContent.Take(4).ToArray();

            // PDF
            if (signature.Take(4).SequenceEqual(new byte[] { 0x25, 0x50, 0x44, 0x46 }))
                return "application/pdf";

            // PNG
            if (signature.Take(4).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }))
                return "image/png";

            // JPEG
            if (signature.Take(2).SequenceEqual(new byte[] { 0xFF, 0xD8 }))
                return "image/jpeg";

            // ZIP
            if (signature.Take(2).SequenceEqual(new byte[] { 0x50, 0x4B }))
                return "application/zip";

            // RAR (Rar! signature)
            if (signature.Take(4).SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21 }))
                return "application/x-rar-compressed";

            // 7-Zip
            if (fileContent.Length >= 6 && fileContent.Take(6).SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }))
                return "application/x-7z-compressed";

            // GIF
            if (signature.Take(3).SequenceEqual(new byte[] { 0x47, 0x49, 0x46 }))
                return "image/gif";

            // BMP
            if (signature.Take(2).SequenceEqual(new byte[] { 0x42, 0x4D }))
                return "image/bmp";
        }

        // Fallback to extension-based detection
        return fileExtension.ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".zip" => "application/zip",
            ".rar" => "application/x-rar-compressed",
            ".7z" => "application/x-7z-compressed",
            ".tar" => "application/x-tar",
            ".gz" => "application/gzip",
            ".html" => "text/html",
            ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".doc" => "application/msword",
            ".xls" => "application/vnd.ms-excel",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".rtf" => "application/rtf",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// Determine file type from extension
    /// </summary>
    private static string DetermineFileType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".zip" => "ZIP Archive",
            ".rar" => "RAR Archive",
            ".7z" => "7-Zip Archive",
            ".tar" => "TAR Archive",
            ".gz" => "GZIP Archive",
            ".pdf" => "PDF Document",
            ".txt" => "Text File",
            ".json" => "JSON File",
            ".xml" => "XML File",
            ".csv" => "CSV File",
            ".docx" => "Word Document",
            ".xlsx" => "Excel Spreadsheet",
            ".pptx" => "PowerPoint Presentation",
            _ => "Unknown File"
        };
    }

    /// <summary>
    /// Check if file is an archive that can be extracted
    /// </summary>
    private static bool IsArchiveFile(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => true,
            _ => false
        };
    }

    /// <summary>
    /// Extract files from archive
    /// </summary>
    private async Task<List<ProcessedFileInfo>> ExtractArchiveAsync(string filePath, byte[] fileContent, string extension, ILogger logger, CancellationToken cancellationToken)
    {
        var extractedFiles = new List<ProcessedFileInfo>();

        try
        {
            switch (extension.ToLowerInvariant())
            {
                case ".zip":
                    extractedFiles = await ExtractZipArchiveAsync(fileContent, logger, cancellationToken);
                    break;
                case ".gz":
                    extractedFiles = await ExtractGzipArchiveAsync(fileContent, Path.GetFileNameWithoutExtension(filePath), logger, cancellationToken);
                    break;
                case ".rar":
                    extractedFiles = await ExtractRarArchiveAsync(fileContent, logger, cancellationToken);
                    break;
                case ".7z":
                    extractedFiles = await ExtractSevenZipArchiveAsync(fileContent, logger, cancellationToken);
                    break;
                case ".tar":
                    extractedFiles = await ExtractTarArchiveAsync(fileContent, logger, cancellationToken);
                    break;
                default:
                    logger.LogWarningWithCorrelation("Archive type {Extension} not yet implemented for extraction", extension);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex, "Failed to extract archive: {FilePath}", filePath);
            // Return empty list on extraction failure
        }

        return extractedFiles;
    }

    /// <summary>
    /// Extract ZIP archive
    /// </summary>
    private async Task<List<ProcessedFileInfo>> ExtractZipArchiveAsync(byte[] zipContent, ILogger logger, CancellationToken cancellationToken)
    {
        var extractedFiles = new List<ProcessedFileInfo>();

        using var memoryStream = new MemoryStream(zipContent);
        using var archive = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (entry.Length == 0 || entry.FullName.EndsWith('/')) continue; // Skip directories

            try
            {
                using var entryStream = entry.Open();
                using var contentStream = new MemoryStream();
                await entryStream.CopyToAsync(contentStream, cancellationToken);

                var content = contentStream.ToArray();
                var extension = Path.GetExtension(entry.Name);
                var contentHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(content));

                extractedFiles.Add(new ProcessedFileInfo
                {
                    FileName = entry.Name,
                    FilePath = entry.FullName,
                    FileSize = content.Length,
                    CreatedDate = entry.LastWriteTime.DateTime,
                    ModifiedDate = entry.LastWriteTime.DateTime,
                    FileExtension = extension,
                    FileContent = content,
                    ContentEncoding = "binary",
                    DetectedMimeType = DetectMimeType(content, extension),
                    FileType = DetermineFileType(extension),
                    ContentHash = contentHash
                });
            }
            catch (Exception ex)
            {
                logger.LogWarningWithCorrelation(ex, "Failed to extract ZIP entry: {EntryName}", entry.FullName);
            }
        }

        return extractedFiles;
    }

    /// <summary>
    /// Extract GZIP archive
    /// </summary>
    private async Task<List<ProcessedFileInfo>> ExtractGzipArchiveAsync(byte[] gzipContent, string originalFileName, ILogger logger, CancellationToken cancellationToken)
    {
        var extractedFiles = new List<ProcessedFileInfo>();

        try
        {
            using var compressedStream = new MemoryStream(gzipContent);
            using var gzipStream = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionMode.Decompress);
            using var decompressedStream = new MemoryStream();

            await gzipStream.CopyToAsync(decompressedStream, cancellationToken);
            var content = decompressedStream.ToArray();
            var extension = Path.GetExtension(originalFileName);
            var contentHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(content));

            extractedFiles.Add(new ProcessedFileInfo
            {
                FileName = originalFileName,
                FilePath = originalFileName,
                FileSize = content.Length,
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow,
                FileExtension = extension,
                FileContent = content,
                ContentEncoding = "binary",
                DetectedMimeType = DetectMimeType(content, extension),
                FileType = DetermineFileType(extension),
                ContentHash = contentHash
            });
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex, "Failed to extract GZIP content");
        }

        return extractedFiles;
    }

    /// <summary>
    /// Extract RAR archive using SharpCompress
    /// </summary>
    private async Task<List<ProcessedFileInfo>> ExtractRarArchiveAsync(byte[] rarContent, ILogger logger, CancellationToken cancellationToken)
    {
        var extractedFiles = new List<ProcessedFileInfo>();

        try
        {
            using var memoryStream = new MemoryStream(rarContent);
            using var archive = ArchiveFactory.Open(memoryStream);

            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || entry.Size == 0) continue; // Skip directories and empty files

                try
                {
                    using var entryStream = entry.OpenEntryStream();
                    using var contentStream = new MemoryStream();
                    await entryStream.CopyToAsync(contentStream, cancellationToken);

                    var content = contentStream.ToArray();
                    var extension = Path.GetExtension(entry.Key);
                    var contentHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(content));

                    extractedFiles.Add(new ProcessedFileInfo
                    {
                        FileName = Path.GetFileName(entry.Key) ?? string.Empty,
                        FilePath = entry.Key ?? string.Empty,
                        FileSize = content.Length,
                        CreatedDate = entry.CreatedTime ?? DateTime.UtcNow,
                        ModifiedDate = entry.LastModifiedTime ?? DateTime.UtcNow,
                        FileExtension = extension ?? string.Empty,
                        FileContent = content,
                        ContentEncoding = "binary",
                        DetectedMimeType = DetectMimeType(content, extension ?? string.Empty),
                        FileType = DetermineFileType(extension ?? string.Empty),
                        ContentHash = contentHash
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarningWithCorrelation(ex, "Failed to extract entry {EntryName} from RAR archive", entry.Key);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex, "Failed to extract RAR archive");
        }

        return extractedFiles;
    }

    /// <summary>
    /// Extract 7-Zip archive using SharpCompress
    /// </summary>
    private async Task<List<ProcessedFileInfo>> ExtractSevenZipArchiveAsync(byte[] sevenZipContent, ILogger logger, CancellationToken cancellationToken)
    {
        var extractedFiles = new List<ProcessedFileInfo>();

        try
        {
            using var memoryStream = new MemoryStream(sevenZipContent);
            using var archive = ArchiveFactory.Open(memoryStream);

            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || entry.Size == 0) continue; // Skip directories and empty files

                try
                {
                    using var entryStream = entry.OpenEntryStream();
                    using var contentStream = new MemoryStream();
                    await entryStream.CopyToAsync(contentStream, cancellationToken);

                    var content = contentStream.ToArray();
                    var extension = Path.GetExtension(entry.Key);
                    var contentHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(content));

                    extractedFiles.Add(new ProcessedFileInfo
                    {
                        FileName = Path.GetFileName(entry.Key) ?? string.Empty,
                        FilePath = entry.Key ?? string.Empty,
                        FileSize = content.Length,
                        CreatedDate = entry.CreatedTime ?? DateTime.UtcNow,
                        ModifiedDate = entry.LastModifiedTime ?? DateTime.UtcNow,
                        FileExtension = extension ?? string.Empty,
                        FileContent = content,
                        ContentEncoding = "binary",
                        DetectedMimeType = DetectMimeType(content, extension ?? string.Empty),
                        FileType = DetermineFileType(extension ?? string.Empty),
                        ContentHash = contentHash
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarningWithCorrelation(ex, "Failed to extract entry {EntryName} from 7-Zip archive", entry.Key);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex, "Failed to extract 7-Zip archive");
        }

        return extractedFiles;
    }

    /// <summary>
    /// Extract TAR archive using SharpCompress
    /// </summary>
    private async Task<List<ProcessedFileInfo>> ExtractTarArchiveAsync(byte[] tarContent, ILogger logger, CancellationToken cancellationToken)
    {
        var extractedFiles = new List<ProcessedFileInfo>();

        try
        {
            using var memoryStream = new MemoryStream(tarContent);
            using var archive = ArchiveFactory.Open(memoryStream);

            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || entry.Size == 0) continue; // Skip directories and empty files

                try
                {
                    using var entryStream = entry.OpenEntryStream();
                    using var contentStream = new MemoryStream();
                    await entryStream.CopyToAsync(contentStream, cancellationToken);

                    var content = contentStream.ToArray();
                    var extension = Path.GetExtension(entry.Key);
                    var contentHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(content));

                    extractedFiles.Add(new ProcessedFileInfo
                    {
                        FileName = Path.GetFileName(entry.Key) ?? string.Empty,
                        FilePath = entry.Key ?? string.Empty,
                        FileSize = content.Length,
                        CreatedDate = entry.CreatedTime ?? DateTime.UtcNow,
                        ModifiedDate = entry.LastModifiedTime ?? DateTime.UtcNow,
                        FileExtension = extension ?? string.Empty,
                        FileContent = content,
                        ContentEncoding = "binary",
                        DetectedMimeType = DetectMimeType(content, extension ?? string.Empty),
                        FileType = DetermineFileType(extension ?? string.Empty),
                        ContentHash = contentHash
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarningWithCorrelation(ex, "Failed to extract entry {EntryName} from TAR archive", entry.Key);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogErrorWithCorrelation(ex, "Failed to extract TAR archive");
        }

        return extractedFiles;
    }

    /// <summary>
    /// Create file cache data object for serialization
    /// </summary>
    private object CreateFileCacheDataObject(string filePath, byte[] fileContent, string detectedMimeType, string fileType, List<ProcessedFileInfo> extractedFiles, FileInfo fileInfo)
    {
        var contentHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(fileContent));

        return new
        {
            fileCacheDataObject = new
            {
                fileMetadata = new
                {
                    fileName = Path.GetFileName(filePath),
                    filePath = filePath,
                    fileSize = fileContent.Length,
                    createdDate = fileInfo.CreationTime,
                    modifiedDate = fileInfo.LastWriteTime,
                    fileExtension = Path.GetExtension(filePath),
                    detectedMimeType = detectedMimeType,
                    fileType = fileType,
                    contentHash = contentHash
                },
                fileContent = new
                {
                    binaryData = Convert.ToBase64String(fileContent),
                    encoding = "base64"
                }
            },
            ExtractedFileCacheDataObject = extractedFiles.Select(f => new
            {
                fileMetadata = new
                {
                    fileName = f.FileName,
                    filePath = f.FilePath,
                    fileSize = f.FileSize,
                    createdDate = f.CreatedDate,
                    modifiedDate = f.ModifiedDate,
                    fileExtension = f.FileExtension,
                    detectedMimeType = f.DetectedMimeType,
                    fileType = f.FileType,
                    contentHash = f.ContentHash
                },
                fileContent = new
                {
                    binaryData = Convert.ToBase64String(f.FileContent),
                    encoding = "base64"
                }
            }).ToArray()
        };
    }
}

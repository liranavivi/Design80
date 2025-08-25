namespace Processor.PluginLoader.Services;

/// <summary>
/// Metrics service interface for PluginLoader processor operations
/// </summary>
public interface IPluginLoaderProcessorMetricsService
{
    // ========================================
    // ACTIVE OPERATIONS TRACKING
    // ========================================
    void IncrementActiveWriting();
    void DecrementActiveWriting();

    // ========================================
    // FILE WRITING METRICS
    // ========================================
    void RecordFileWrite(double durationMs, long fileSizeBytes, string fileExtension);
    void RecordExtractedFileWrite(double durationMs, long fileSizeBytes, string fileType, string archiveType);
    void RecordArchiveProcessing(double durationMs, int extractedFileCount, string archiveType);

    // ========================================
    // ERROR TRACKING
    // ========================================
    void RecordWritingError(string errorType, string fileExtension);
    void RecordArchiveProcessingError(string errorType, string archiveType);

    // ========================================
    // POST-PROCESSING METRICS
    // ========================================
    void RecordFilePostProcessing(double durationMs, string processingMode, string fileExtension);

    // ========================================
    // PERFORMANCE METRICS
    // ========================================
    void RecordThroughput(long bytesWritten, double durationMs);
    void RecordConcurrentOperations(int activeOperations);
}

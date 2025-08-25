namespace Plugin.FileWriter.Services;

/// <summary>
/// Interface for FileWriter plugin metrics service
/// </summary>
public interface IFileWriterPluginMetricsService
{
    /// <summary>
    /// Increment active writing operations counter
    /// </summary>
    void IncrementActiveWriting();

    /// <summary>
    /// Decrement active writing operations counter
    /// </summary>
    void DecrementActiveWriting();

    /// <summary>
    /// Record archive processing metrics
    /// </summary>
    /// <param name="processingTimeMs">Processing time in milliseconds</param>
    /// <param name="fileCount">Number of files processed</param>
    /// <param name="archiveType">Type of archive processed</param>
    void RecordArchiveProcessing(double processingTimeMs, int fileCount, string archiveType);

    /// <summary>
    /// Record archive processing error
    /// </summary>
    /// <param name="errorType">Type of error</param>
    /// <param name="archiveType">Type of archive</param>
    void RecordArchiveProcessingError(string errorType, string archiveType);

    /// <summary>
    /// Record extracted file write metrics
    /// </summary>
    /// <param name="writeTimeMs">Write time in milliseconds</param>
    /// <param name="fileSize">Size of file written</param>
    /// <param name="fileExtension">File extension</param>
    /// <param name="fileType">File type</param>
    void RecordExtractedFileWrite(double writeTimeMs, long fileSize, string fileExtension, string fileType);

    /// <summary>
    /// Record file post-processing metrics
    /// </summary>
    /// <param name="postProcessTimeMs">Post-processing time in milliseconds</param>
    /// <param name="processingMode">Processing mode used</param>
    /// <param name="fileExtension">File extension</param>
    void RecordFilePostProcessing(double postProcessTimeMs, string processingMode, string fileExtension);

    /// <summary>
    /// Record throughput metrics
    /// </summary>
    /// <param name="bytesProcessed">Number of bytes processed</param>
    /// <param name="processingTimeMs">Processing time in milliseconds</param>
    void RecordThroughput(long bytesProcessed, double processingTimeMs);

    /// <summary>
    /// Record writing error
    /// </summary>
    /// <param name="errorType">Type of error</param>
    /// <param name="fileExtension">File extension</param>
    void RecordWritingError(string errorType, string fileExtension);
}

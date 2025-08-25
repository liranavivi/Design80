using System.Diagnostics.Metrics;

namespace Plugin.FileReader.Interfaces;

/// <summary>
/// Service for collecting and exposing metrics specific to compressed file processing operations.
/// Provides OpenTelemetry-compatible metrics for monitoring plugin performance and health.
/// NOTE: All metrics functionality is currently commented out for initial implementation.
/// </summary>
public interface IFileReaderPluginMetricsService
{
    // TODO: Uncomment and implement metrics when needed
    
    /*
    /// <summary>
    /// Counter for total number of compressed files processed
    /// </summary>
    Counter<long> FilesProcessed { get; }

    /// <summary>
    /// Counter for total number of archive entries analyzed
    /// </summary>
    Counter<long> ArchiveEntriesAnalyzed { get; }

    /// <summary>
    /// Counter for security warnings detected in archives
    /// </summary>
    Counter<long> SecurityWarningsDetected { get; }

    /// <summary>
    /// Histogram for compressed file processing duration in milliseconds
    /// </summary>
    Histogram<double> FileProcessingDuration { get; }

    /// <summary>
    /// Histogram for archive analysis duration in milliseconds
    /// </summary>
    Histogram<double> ArchiveAnalysisDuration { get; }

    /// <summary>
    /// Histogram for compression ratio measurements
    /// </summary>
    Histogram<double> CompressionRatio { get; }

    /// <summary>
    /// Histogram for archive size measurements in bytes
    /// </summary>
    Histogram<long> ArchiveSize { get; }

    /// <summary>
    /// Histogram for uncompressed size measurements in bytes
    /// </summary>
    Histogram<long> UncompressedSize { get; }

    /// <summary>
    /// Counter for different archive types processed
    /// </summary>
    Counter<long> ArchiveTypeProcessed { get; }

    /// <summary>
    /// Counter for processing errors by type
    /// </summary>
    Counter<long> ProcessingErrors { get; }

    /// <summary>
    /// Gauge for current number of files being processed
    /// </summary>
    UpDownCounter<int> ActiveProcessingCount { get; }
    */

    /// <summary>
    /// Records a compressed file processing operation
    /// </summary>
    /// <param name="archiveType">Type of archive (ZIP, RAR, etc.)</param>
    /// <param name="processingDurationMs">Processing duration in milliseconds</param>
    /// <param name="archiveSize">Size of the archive in bytes</param>
    /// <param name="uncompressedSize">Uncompressed size in bytes</param>
    /// <param name="compressionRatio">Compression ratio (0.0 to 1.0)</param>
    /// <param name="entryCount">Number of entries in the archive</param>
    /// <param name="securityWarningCount">Number of security warnings detected</param>
    void RecordFileProcessed(
        string archiveType,
        double processingDurationMs,
        long archiveSize,
        long uncompressedSize,
        double compressionRatio,
        int entryCount,
        int securityWarningCount);

    // TODO: Add other recording methods when metrics are implemented
}

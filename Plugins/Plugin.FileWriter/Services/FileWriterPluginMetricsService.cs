using Microsoft.Extensions.Logging;
using System.Diagnostics.Metrics;

namespace Plugin.FileWriter.Services;

/// <summary>
/// Metrics service implementation for FileWriter plugin operations
/// </summary>
public class FileWriterPluginMetricsService : IFileWriterPluginMetricsService, IDisposable
{
    private readonly ILogger<FileWriterPluginMetricsService> _logger;
    private readonly Meter _meter;

    // Counters
    private readonly Counter<long> _filesWrittenCounter;
    private readonly Counter<long> _bytesWrittenCounter;
    private readonly Counter<long> _errorsCounter;
    private readonly Counter<long> _archivesProcessedCounter;

    // Histograms
    private readonly Histogram<double> _fileWriteDurationHistogram;
    private readonly Histogram<double> _archiveProcessingDurationHistogram;
    private readonly Histogram<long> _fileSizeHistogram;

    // Gauges
    private readonly UpDownCounter<int> _activeWritingGauge;

    public FileWriterPluginMetricsService(ILogger<FileWriterPluginMetricsService> logger)
    {
        _logger = logger;
        _meter = new Meter("Plugin.FileWriter", "1.0.0");

        // Initialize counters
        _filesWrittenCounter = _meter.CreateCounter<long>("plugin_file_writer_files_written_total", "files", "Total number of files written");
        _bytesWrittenCounter = _meter.CreateCounter<long>("plugin_file_writer_bytes_written_total", "bytes", "Total bytes written");
        _errorsCounter = _meter.CreateCounter<long>("plugin_file_writer_errors_total", "errors", "Total number of writing errors");
        _archivesProcessedCounter = _meter.CreateCounter<long>("plugin_file_writer_archives_processed_total", "archives", "Total number of archives processed");

        // Initialize histograms
        _fileWriteDurationHistogram = _meter.CreateHistogram<double>("plugin_file_writer_file_write_duration_ms", "ms", "Duration of individual file write operations");
        _archiveProcessingDurationHistogram = _meter.CreateHistogram<double>("plugin_file_writer_archive_processing_duration_ms", "ms", "Duration of archive processing operations");
        _fileSizeHistogram = _meter.CreateHistogram<long>("plugin_file_writer_file_size_bytes", "bytes", "Size distribution of written files");

        // Initialize gauges
        _activeWritingGauge = _meter.CreateUpDownCounter<int>("plugin_file_writer_active_writing_operations", "operations", "Number of active file writing operations");
    }

    public void IncrementActiveWriting()
    {
        _activeWritingGauge.Add(1);
        _logger.LogDebug("Incremented active writing operations");
    }

    public void DecrementActiveWriting()
    {
        _activeWritingGauge.Add(-1);
        _logger.LogDebug("Decremented active writing operations");
    }

    public void RecordFileWrite(double durationMs, long fileSizeBytes, string fileExtension)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("file_extension", fileExtension),
            new("operation", "file_write")
        };

        _filesWrittenCounter.Add(1, tags);
        _bytesWrittenCounter.Add(fileSizeBytes, tags);
        _fileWriteDurationHistogram.Record(durationMs, tags);
        _fileSizeHistogram.Record(fileSizeBytes, tags);

        _logger.LogDebug(
            "Recorded file write metrics - Duration: {Duration}ms, Size: {Size} bytes, Extension: {Extension}",
            durationMs, fileSizeBytes, fileExtension);
    }

    public void RecordExtractedFileWrite(double writeTimeMs, long fileSize, string fileExtension, string fileType)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("file_extension", fileExtension),
            new("file_type", fileType),
            new("operation", "extracted_file_write")
        };

        _filesWrittenCounter.Add(1, tags);
        _bytesWrittenCounter.Add(fileSize, tags);
        _fileWriteDurationHistogram.Record(writeTimeMs, tags);
        _fileSizeHistogram.Record(fileSize, tags);

        _logger.LogDebug(
            "Recorded extracted file write metrics - Duration: {Duration}ms, Size: {Size} bytes, Extension: {Extension}, Type: {Type}",
            writeTimeMs, fileSize, fileExtension, fileType);
    }

    public void RecordArchiveProcessing(double durationMs, int extractedFileCount, string archiveType)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("archive_type", archiveType),
            new("extracted_file_count", extractedFileCount),
            new("operation", "archive_processing")
        };

        _archivesProcessedCounter.Add(1, tags);
        _archiveProcessingDurationHistogram.Record(durationMs, tags);

        _logger.LogDebug(
            "Recorded archive processing metrics - Duration: {Duration}ms, Files: {FileCount}, Type: {Type}",
            durationMs, extractedFileCount, archiveType);
    }

    public void RecordWritingError(string errorType, string fileExtension)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("error_type", errorType),
            new("file_extension", fileExtension),
            new("operation", "writing_error")
        };

        _errorsCounter.Add(1, tags);

        _logger.LogDebug(
            "Recorded writing error - Type: {ErrorType}, Extension: {Extension}",
            errorType, fileExtension);
    }

    public void RecordArchiveProcessingError(string errorType, string archiveType)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("error_type", errorType),
            new("archive_type", archiveType),
            new("operation", "archive_processing_error")
        };

        _errorsCounter.Add(1, tags);

        _logger.LogDebug(
            "Recorded archive processing error - Type: {ErrorType}, Archive: {Archive}",
            errorType, archiveType);
    }

    public void RecordFilePostProcessing(double durationMs, string processingMode, string fileExtension)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("processing_mode", processingMode),
            new("file_extension", fileExtension),
            new("operation", "post_processing")
        };

        _logger.LogDebug(
            "Recorded file post-processing - Duration: {Duration}ms, Mode: {Mode}, Extension: {Extension}",
            durationMs, processingMode, fileExtension);
    }

    public void RecordThroughput(long bytesWritten, double durationMs)
    {
        var throughputMbps = (bytesWritten / (1024.0 * 1024.0)) / (durationMs / 1000.0);

        _logger.LogDebug(
            "Recorded throughput - {Bytes} bytes in {Duration}ms ({Throughput:F2} MB/s)",
            bytesWritten, durationMs, throughputMbps);
    }

    public void RecordConcurrentOperations(int activeOperations)
    {
        _logger.LogDebug(
            "Recorded concurrent operations - Active: {ActiveOperations}",
            activeOperations);
    }

    public void Dispose()
    {
        _meter?.Dispose();
    }
}

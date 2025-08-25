using Microsoft.Extensions.Logging;
using Shared.Correlation;
using System.Diagnostics.Metrics;

namespace Processor.PluginLoader.Services;

/// <summary>
/// Metrics service implementation for PluginLoader processor operations
/// </summary>
public class PluginLoaderProcessorMetricsService : IPluginLoaderProcessorMetricsService
{
    private readonly ILogger<PluginLoaderProcessorMetricsService> _logger;
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

    // Gauges (using UpDownCounter)
    private readonly UpDownCounter<int> _activeWritingGauge;

    public PluginLoaderProcessorMetricsService(ILogger<PluginLoaderProcessorMetricsService> logger)
    {
        _logger = logger;
        _meter = new Meter("PluginLoader.Processor", "1.0.0");

        // Initialize counters
        _filesWrittenCounter = _meter.CreateCounter<long>("compressed_file_writer_files_written_total", "files", "Total number of files written from compressed archives");
        _bytesWrittenCounter = _meter.CreateCounter<long>("compressed_file_writer_bytes_written_total", "bytes", "Total bytes written from compressed archives");
        _errorsCounter = _meter.CreateCounter<long>("compressed_file_writer_errors_total", "errors", "Total number of writing errors");
        _archivesProcessedCounter = _meter.CreateCounter<long>("compressed_file_writer_archives_processed_total", "archives", "Total number of compressed archives processed");

        // Initialize histograms
        _fileWriteDurationHistogram = _meter.CreateHistogram<double>("compressed_file_writer_file_write_duration_ms", "ms", "Duration of individual file write operations");
        _archiveProcessingDurationHistogram = _meter.CreateHistogram<double>("compressed_file_writer_archive_processing_duration_ms", "ms", "Duration of archive processing operations");
        _fileSizeHistogram = _meter.CreateHistogram<long>("compressed_file_writer_file_size_bytes", "bytes", "Size distribution of written files");

        // Initialize gauges
        _activeWritingGauge = _meter.CreateUpDownCounter<int>("compressed_file_writer_active_writing_operations", "operations", "Number of active file writing operations");
    }

    public void IncrementActiveWriting()
    {
        _activeWritingGauge.Add(1);
        _logger.LogDebugWithCorrelation("Incremented active writing operations");
    }

    public void DecrementActiveWriting()
    {
        _activeWritingGauge.Add(-1);
        _logger.LogDebugWithCorrelation("Decremented active writing operations");
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

        _logger.LogDebugWithCorrelation(
            "Recorded file write metrics - Duration: {Duration}ms, Size: {Size} bytes, Extension: {Extension}",
            durationMs, fileSizeBytes, fileExtension);
    }

    public void RecordExtractedFileWrite(double durationMs, long fileSizeBytes, string fileType, string archiveType)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("file_type", fileType),
            new("archive_type", archiveType),
            new("operation", "extracted_file_write")
        };

        _filesWrittenCounter.Add(1, tags);
        _bytesWrittenCounter.Add(fileSizeBytes, tags);
        _fileWriteDurationHistogram.Record(durationMs, tags);
        _fileSizeHistogram.Record(fileSizeBytes, tags);

        _logger.LogDebugWithCorrelation(
            "Recorded extracted file write metrics - Duration: {Duration}ms, Size: {Size} bytes, Type: {Type}, Archive: {Archive}",
            durationMs, fileSizeBytes, fileType, archiveType);
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

        _logger.LogDebugWithCorrelation(
            "Recorded archive processing metrics - Duration: {Duration}ms, Files: {FileCount}, Type: {Type}",
            durationMs, extractedFileCount, archiveType);
    }

    public void RecordWritingError(string errorType, string fileExtension)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("error_type", errorType),
            new("file_extension", fileExtension),
            new("operation", "file_write_error")
        };

        _errorsCounter.Add(1, tags);

        _logger.LogWarningWithCorrelation(
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

        _logger.LogWarningWithCorrelation(
            "Recorded archive processing error - Type: {ErrorType}, Archive: {ArchiveType}",
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

        _fileWriteDurationHistogram.Record(durationMs, tags);

        _logger.LogDebugWithCorrelation(
            "Recorded post-processing metrics - Duration: {Duration}ms, Mode: {Mode}, Extension: {Extension}",
            durationMs, processingMode, fileExtension);
    }

    public void RecordThroughput(long bytesWritten, double durationMs)
    {
        var throughputMbps = (bytesWritten / (1024.0 * 1024.0)) / (durationMs / 1000.0);
        
        _logger.LogDebugWithCorrelation(
            "Recorded throughput - {Bytes} bytes in {Duration}ms ({Throughput:F2} MB/s)",
            bytesWritten, durationMs, throughputMbps);
    }

    public void RecordConcurrentOperations(int activeOperations)
    {
        _logger.LogDebugWithCorrelation(
            "Recorded concurrent operations - Active: {ActiveOperations}",
            activeOperations);
    }

    public void Dispose()
    {
        _meter?.Dispose();
    }
}

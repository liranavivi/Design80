using Plugin.Shared.Utilities;

namespace Plugin.FileReader.Models;

/// <summary>
/// Configuration extracted from PluginAssignmentModel for compressed file reading operations
/// </summary>
public class FileReaderConfiguration : IFileProcessingConfiguration
{
    // ========================================
    // 1. DISCOVERY CONFIGURATION
    // ========================================
    public string FolderPath { get; set; } = string.Empty;
    public string SearchPattern { get; set; } = "*.{txt,zip,rar,7z,gz,tar}";

    // ========================================
    // 2. FILTERING CONFIGURATION
    // ========================================
    public long MinFileSize { get; set; } = 0; // Filter for compressed file itself
    public long MaxFileSize { get; set; } = 100 * 1024 * 1024; // 100MB for compressed files
    public long MinExtractedSize { get; set; } = 0; // Filter for each extracted file
    public long MaxExtractedSize { get; set; } = 50 * 1024 * 1024; // 50MB for each extracted file

    // ========================================
    // 3. PROCESSING CONFIGURATION
    // ========================================
    public FileProcessingMode ProcessingMode { get; set; } = FileProcessingMode.LeaveUnchanged;
    public string ProcessedExtension { get; set; } = ".processed";
    public string BackupFolder { get; set; } = string.Empty;

    // ========================================
    // 4. PERFORMANCE CONFIGURATION
    // ========================================
    public int MaxFilesToProcess { get; set; } = 50; // Lower than FileReader due to complexity

    // ========================================
    // 5. ARCHIVE ANALYSIS CONFIGURATION
    // ========================================
    public int MinEntriesToList { get; set; } = 0; // Minimum extracted files to process
    public int MaxEntriesToList { get; set; } = 100; // Maximum extracted files to process


}

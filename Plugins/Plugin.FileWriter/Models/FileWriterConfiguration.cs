using Plugin.Shared.Utilities;

namespace Plugin.FileWriter.Models;

/// <summary>
/// Configuration extracted from PluginAssignmentModel for compressed file writing operations
/// </summary>
public class FileWriterConfiguration : IFileProcessingConfiguration
{
    // ========================================
    // 1. OUTPUT CONFIGURATION
    // ========================================
    public string FolderPath { get; set; } = string.Empty;

    // ========================================
    // 2. EXTRACTED FILE SIZE CONFIGURATION
    // ========================================
    public long MinExtractedContentSize { get; set; } = 0; // Filter for each extracted file content
    public long MaxExtractedContentSize { get; set; } = long.MaxValue; // Filter for each extracted file content

    // ========================================
    // 3. CONTENT PROCESSING CONFIGURATION
    // ========================================
    // Note: Cache always stores content as Base64, so decoding is always required

    // ========================================
    // 4. FILE PROCESSING CONFIGURATION
    // ========================================
    public FileProcessingMode ProcessingMode { get; set; } = FileProcessingMode.LeaveUnchanged;
    public string ProcessedExtension { get; set; } = ".written";
    public string BackupFolder { get; set; } = string.Empty;
}

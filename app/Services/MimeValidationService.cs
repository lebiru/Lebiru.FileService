using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lebiru.FileService.Services
{
  /// <summary>
  /// Service for validating file types by MIME and content
  /// </summary>
  public class MimeValidationService
  {
    // List of allowed MIME types
    private readonly List<string> _allowedMimeTypes = new List<string>
        {
            // Documents
            "application/pdf",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", // .docx
            "application/vnd.ms-excel",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", // .xlsx
            "application/vnd.ms-powerpoint",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation", // .pptx
            "text/plain",
            "text/csv",
            "application/rtf",
            "application/zip",
            "application/x-rar-compressed",
            "application/x-7z-compressed",
            
            // Images
            "image/jpeg",
            "image/png",
            "image/gif",
            "image/bmp",
            "image/webp",
            "image/svg+xml",
            "image/tiff",
            
            // Audio
            "audio/mpeg",
            "audio/wav",
            "audio/ogg",
            "audio/webm",
            
            // Video
            "video/mp4",
            "video/webm",
            "video/ogg",
            "video/quicktime",
            
            // Other common formats
            "application/json",
            "text/html",
            "text/css",
            "application/javascript",
            "application/xml",
            "text/xml"
        };

    // List of known risky MIME types
    private readonly List<string> _riskyMimeTypes = new List<string>
        {
            "application/x-msdownload", // .exe
            "application/x-ms-installer", // .msi
            "application/x-sh", // .sh
            "application/x-csh", // .csh
            "application/x-bat", // .bat
            "application/x-cmd", // .cmd
            "application/java-archive", // .jar
            "application/x-javascript", // .js as executable
            "application/vnd.microsoft.portable-executable", // .exe
            "application/x-dosexec", // DOS executables
            "application/vnd.apple.installer+xml", // .mpkg
            "application/vnd.ms-cab-compressed", // .cab
            "application/x-httpd-php", // .php
            "text/x-php", // Another PHP variant
            "application/x-perl", // .pl
            "application/x-python", // .py as executable
            "application/x-ruby" // .rb as executable
        };

    // List of risky file extensions
    private readonly List<string> _riskyExtensions = new List<string>
        {
            ".exe", ".msi", ".bat", ".cmd", ".sh", ".ps1",
            ".php", ".jar", ".dll", ".com", ".vbs", ".js",
            ".py", ".pl", ".rb"
        };

    /// <summary>
    /// Validates if a file's MIME type is allowed
    /// </summary>
    /// <param name="fileName">The name of the file</param>
    /// <param name="contentType">The MIME type of the file</param>
    /// <param name="fileStream">Stream to the file contents (optional, for content validation)</param>
    /// <returns>True if the file is valid, false otherwise</returns>
    public bool IsValidFile(string fileName, string contentType, Stream? fileStream = null)
    {
      // Check file extension first
      string? extension = Path.GetExtension(fileName)?.ToLowerInvariant();
      if (!string.IsNullOrEmpty(extension) && _riskyExtensions.Contains(extension))
      {
        return false;
      }

      // Check if content type is in the risky list
      if (_riskyMimeTypes.Contains(contentType))
      {
        return false;
      }

      // Check if content type is explicitly allowed
      return _allowedMimeTypes.Contains(contentType);
    }

    /// <summary>
    /// Gets a more detailed validation result
    /// </summary>
    /// <param name="fileName">The name of the file</param>
    /// <param name="contentType">The MIME type of the file</param>
    /// <returns>A tuple with (isValid, message)</returns>
    public (bool IsValid, string Message) ValidateFileDetailed(string fileName, string contentType)
    {
      string? extension = Path.GetExtension(fileName)?.ToLowerInvariant();

      // Check file extension
      if (!string.IsNullOrEmpty(extension) && _riskyExtensions.Contains(extension))
      {
        return (false, $"File with extension '{extension}' is not allowed for security reasons");
      }

      // Check if content type is in the risky list
      if (_riskyMimeTypes.Contains(contentType))
      {
        return (false, $"File type '{contentType}' is not allowed for security reasons");
      }

      // Check if content type is explicitly allowed
      if (_allowedMimeTypes.Contains(contentType))
      {
        return (true, "File type is allowed");
      }

      return (false, $"Unknown file type '{contentType}' is not allowed");
    }
  }
}
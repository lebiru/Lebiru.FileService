using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lebiru.FileService.Services
{
  /// <summary>
  /// Interface for validating file types by MIME and content
  /// </summary>
  public interface IMimeValidationService
  {
    /// <summary>
    /// Determines if a file is valid based on its name and content type
    /// </summary>
    /// <param name="fileName">The name of the file</param>
    /// <param name="contentType">The MIME type of the file</param>
    /// <returns>Whether the file is valid</returns>
    bool ValidateFile(string fileName, string contentType);

    /// <summary>
    /// Validates a file and returns detailed information about the validation result
    /// </summary>
    /// <param name="fileName">The name of the file</param>
    /// <param name="contentType">The MIME type of the file</param>
    /// <returns>A tuple with (isValid, message)</returns>
    (bool IsValid, string Message) ValidateFileDetailed(string fileName, string contentType);
  }
}
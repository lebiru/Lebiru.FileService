using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hangfire;
using Hangfire.Console;
using Hangfire.Console.Progress;
using Hangfire.Server;
using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Authorization;
using FileInfo = Lebiru.FileService.Models.FileInfo;

namespace Lebiru.FileService.Controllers
{
  /// <summary>
  /// Controller for managing file transformations
  /// </summary>
  [Route("Transform")]
  [Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Contributor}")]
  public class TransformController : Controller
  {
    private readonly IHttpClientFactory? _httpClientFactory;
    private const string UploadsFolder = "uploads";
    private const string DataFolder = "app-data";
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly string _transformsPath;
    private readonly string _activitiesPath;
    private readonly FileServiceConfig _config;
    private readonly ILogger<TransformController> _logger;
    private readonly string _serviceUrl;
    private readonly string _appRootPath;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private const int MaxRegexPatternLength = 1000;

    /// <summary>
    /// Constructor for TransformController
    /// </summary>
    public TransformController(
        IBackgroundJobClient backgroundJobClient,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<TransformController> logger,
        IHttpClientFactory? httpClientFactory = null)
    {
      _backgroundJobClient = backgroundJobClient;
      _logger = logger;
      _httpClientFactory = httpClientFactory;
      _appRootPath = environment.ContentRootPath;

      // Get base service URL from config or use default
      _serviceUrl = configuration["ServiceUrl"] ?? "http://localhost";
      _logger.LogInformation("Service URL: {ServiceUrl}", _serviceUrl);

      string basePath = Path.Combine(environment.ContentRootPath, DataFolder);
      _logger.LogInformation("Content root path: {ContentRoot}", environment.ContentRootPath);
      _logger.LogInformation("Fixed app data path: {DataPath}", basePath);
      Directory.CreateDirectory(basePath);

      // Set paths for transform data files
      _transformsPath = Path.Combine(basePath, "transforms.json");
      _activitiesPath = Path.Combine(basePath, "transformActivities.json");
      _logger.LogInformation("Transforms path: {TransformsPath}", _transformsPath);
      _logger.LogInformation("Activities path: {ActivitiesPath}", _activitiesPath);

      // Create empty files if they don't exist
      if (!System.IO.File.Exists(_transformsPath))
      {
        _logger.LogInformation("Creating empty transforms file at {Path}", _transformsPath);
        System.IO.File.WriteAllText(_transformsPath, "[]");
      }
      else
      {
        _logger.LogInformation("Transforms file already exists at {Path} with size {Size} bytes",
          _transformsPath, new System.IO.FileInfo(_transformsPath).Length);
      }

      if (!System.IO.File.Exists(_activitiesPath))
      {
        _logger.LogInformation("Creating empty activities file at {Path}", _activitiesPath);
        System.IO.File.WriteAllText(_activitiesPath, "[]");
      }
      else
      {
        _logger.LogInformation("Activities file already exists at {Path} with size {Size} bytes",
          _activitiesPath, new System.IO.FileInfo(_activitiesPath).Length);
      }

      // Get configuration
      _config = configuration.GetSection("FileServiceConfig").Get<FileServiceConfig>() ?? new FileServiceConfig();
    }

    /// <summary>
    /// Display the transform sources and recent activities
    /// </summary>
    [HttpGet]
    public IActionResult Index()
    {
      // If we've just completed a successful save, clear any stale error messages
      if (HttpContext != null && TempData["SuccessMessage"] != null)
      {
        TempData.Remove("ErrorMessage");
      }

      // Verify transform file integrity
      VerifyTransformFileIntegrity();

      var sources = GetTransformSources();
      _logger.LogInformation("Index: Retrieved {Count} transform sources", sources.Count);

      if (sources.Count > 0)
      {
        _logger.LogInformation("First source - ID: {Id}, Title: {Title}", sources[0].Id, sources[0].Title);
      }
      else
      {
        _logger.LogWarning("No transform sources found");
      }

      var model = new TransformViewModel
      {
        TransformSources = sources,
        LatestActivities = GetLatestTransformActivities()
      };

      return View("Transform", model);
    }

    /// <summary>
    /// Verify transform file integrity
    /// </summary>
    private void VerifyTransformFileIntegrity()
    {
      try
      {
        _logger.LogInformation("Verifying transform file integrity at {Path}", _transformsPath);

        // Check if file exists
        if (!System.IO.File.Exists(_transformsPath))
        {
          _logger.LogWarning("Transform file does not exist, creating it");
          System.IO.File.WriteAllText(_transformsPath, "[]");
          return;
        }

        // Check if file is readable
        var content = System.IO.File.ReadAllText(_transformsPath);

        if (string.IsNullOrWhiteSpace(content))
        {
          _logger.LogWarning("Transform file is empty, reinitializing");
          System.IO.File.WriteAllText(_transformsPath, "[]");
          return;
        }

        // Try to deserialize the content
        try
        {
          var sources = JsonSerializer.Deserialize<List<TransformModel>>(content);
          _logger.LogInformation("Transform file integrity check passed. Found {Count} transforms", sources?.Count ?? 0);
        }
        catch (JsonException ex)
        {
          _logger.LogError(ex, "Transform file contains invalid JSON, reinitializing");
          System.IO.File.WriteAllText(_transformsPath, "[]");
        }
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error during transform file integrity check");
      }
    }

    /// <summary>
    /// Show the form to add a new transform source
    /// </summary>
    [HttpGet("AddTransform")]
    public IActionResult AddTransform()
    {
      return View(new TransformModel());
    }

    /// <summary>
    /// Save a new or updated transform source
    /// </summary>
    [HttpPost("SaveTransform")]
    [ValidateAntiForgeryToken]
    public IActionResult SaveTransform()
    {
      // Clear any previous error messages
      TempData.Remove("ErrorMessage");

      // Get existing sources first
      var sources = GetTransformSources();

      // Check if this is an edit (ID from form is in sources.json)
      string? formId = null;
      bool isEditing = false;

      if (Request.Form.ContainsKey("Id"))
      {
        formId = Request.Form["Id"].ToString();
        _logger.LogInformation("Found form ID: '{Id}'", formId);

        // Check if this ID exists in the transform sources
        isEditing = sources.Any(s => s.Id == formId);

        _logger.LogInformation("ID exists in sources.json: {Exists}", isEditing);
      }
      else
      {
        _logger.LogInformation("No ID found in form data, treating as new transform");
      }

      _logger.LogInformation("Final isEditing status = {IsEditing}", isEditing);
      TransformModel? existingSource = null;

      if (isEditing && formId != null)
      {
        _logger.LogInformation("This is an edit operation for existing transform");

        // Find the existing source (we already checked it exists)
        existingSource = sources.FirstOrDefault(s => s.Id == formId);
        _logger.LogInformation("Found existing transform - ID: '{Id}', Title: '{Title}'",
          existingSource?.Id, existingSource?.Title);
      }
      else
      {
        _logger.LogInformation("This is a new transform creation");
      }

      // Create a new model and manually bind form values
      var model = new TransformModel
      {
        Title = Request.Form["Title"].ToString(),
        FilePattern = Request.Form["FilePattern"].ToString(),
        RegexPattern = Request.Form["RegexPattern"].ToString(),
        IsActive = Request.Form["IsActive"] == "on" || Request.Form["IsActive"] == "true",
        TransformIntervalMinutes = int.TryParse(Request.Form["TransformIntervalMinutes"], out int interval) ? interval : 60,
        ModifyExistingFile = Request.Form["ModifyExistingFile"] == "on" || Request.Form["ModifyExistingFile"] == "true"
      };

      // Set or preserve ID and creation date
      if (isEditing && existingSource != null)
      {
        _logger.LogInformation("Updating existing transform with ID: {Id}", existingSource.Id);
        model.Id = existingSource.Id;
        model.CreatedAt = existingSource.CreatedAt;
        model.LastExecutedTime = existingSource.LastExecutedTime;
        model.LastProcessedFileCount = existingSource.LastProcessedFileCount;

        // Update the existing source
        int index = sources.FindIndex(s => s.Id == model.Id);
        if (index != -1)
        {
          sources[index] = model;
          _logger.LogInformation("Updated existing transform at index {Index}", index);
        }
        else
        {
          _logger.LogWarning("Failed to find transform index in sources collection");
        }
      }
      else
      {
        // Generate a new ID for a new source
        model.Id = Guid.NewGuid().ToString();
        model.CreatedAt = DateTime.UtcNow;

        _logger.LogInformation("Created new transform with generated ID: {Id}", model.Id);

        // Add to the list
        sources.Add(model);
        _logger.LogInformation("Added new transform to sources collection. Total count: {Count}", sources.Count);
      }

      // Save the updated list
      SaveTransformSources(sources);

      // Schedule the transform job if active
      if (model.IsActive)
      {
        ScheduleTransformJob(model);
      }

      // Set success message and redirect
      TempData["SuccessMessage"] = isEditing
          ? "Transform source updated successfully."
          : "Transform source added successfully.";

      return RedirectToAction("Index");
    }

    /// <summary>
    /// Show the form to edit an existing transform source
    /// </summary>
    [HttpGet("EditTransform/{id}")]
    public IActionResult EditTransform(string id)
    {
      _logger.LogInformation("EditTransform called with ID: {Id}", id);

      // Verify transform file integrity
      VerifyTransformFileIntegrity();

      var sources = GetTransformSources();
      _logger.LogInformation("Retrieved {Count} transform sources", sources.Count);

      // Log all sources
      foreach (var src in sources)
      {
        _logger.LogInformation("Source in list - ID: {Id}, Title: {Title}", src.Id, src.Title);
      }

      // Try exact match first
      var source = sources.FirstOrDefault(s => s.Id == id);

      // If not found, try case-insensitive match
      if (source == null)
      {
        _logger.LogInformation("No exact match found, trying case-insensitive search");
        source = sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
      }

      // If still not found, try contains match
      if (source == null && !string.IsNullOrEmpty(id) && sources.Count > 0)
      {
        _logger.LogInformation("No case-insensitive match found, trying partial match");
        source = sources.FirstOrDefault(s => s.Id.Contains(id) || id.Contains(s.Id));

        // If we found something by partial match, log it
        if (source != null)
        {
          _logger.LogWarning("Found source by partial ID match - Expected: {ExpectedId}, Found: {ActualId}", id, source.Id);
        }
      }

      if (source == null)
      {
        _logger.LogWarning("Transform source not found with ID: {Id}", id);
        TempData["ErrorMessage"] = "Transform source not found.";
        return RedirectToAction("Index");
      }

      _logger.LogInformation("Found transform source - ID: {Id}, Title: {Title}", source.Id, source.Title);
      return View("AddTransform", source);
    }

    /// <summary>
    /// Delete a transform source
    /// </summary>
    [HttpPost("DeleteTransform")]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteTransform([FromForm] string transformId)
    {
      var sources = GetTransformSources();
      var sourceToDelete = sources.FirstOrDefault(s => s.Id == transformId);

      if (sourceToDelete == null)
      {
        TempData["ErrorMessage"] = "Transform source not found.";
        return RedirectToAction("Index");
      }

      // Remove the source
      sources.Remove(sourceToDelete);
      SaveTransformSources(sources);

      TempData["SuccessMessage"] = $"Transform source '{sourceToDelete.Title}' has been deleted.";
      return RedirectToAction("Index");
    }

    /// <summary>
    /// Execute a transform operation for a specific source
    /// </summary>
    [HttpPost("ExecuteTransform")]
    [ValidateAntiForgeryToken]
    public IActionResult ExecuteTransform([FromForm] string transformId)
    {
      // Log that we've received a request
      _logger.LogInformation("Received request to execute transform {TransformId}", transformId);

      var sources = GetTransformSources();
      var source = sources.FirstOrDefault(s => s.Id == transformId);

      if (source == null)
      {
        TempData["ErrorMessage"] = "Transform source not found.";
        return RedirectToAction("Index");
      }

      // Add activity record for starting the transform
      AddTransformActivity(new TransformActivityModel
      {
        TransformId = source.Id,
        TransformTitle = source.Title,
        Status = TransformStatus.InProgress,
        Message = $"Starting transformation '{source.Title}'...",
        Timestamp = DateTime.UtcNow
      });

      // Queue the background job to process the transform
      _backgroundJobClient.Enqueue<TransformController>(x => x.ProcessTransform(transformId, null));

      TempData["SuccessMessage"] = $"Transform '{source.Title}' has been queued for execution.";
      return RedirectToAction("Index");
    }

    /// <summary>
    /// Test a transform configuration
    /// </summary>
    [HttpPost("TestTransform")]
    public async Task<IActionResult> TestTransform(string? transformId = null)
    {
      _logger.LogInformation("TestTransform called with transformId: {TransformId}", transformId);
      TransformModel transform = new TransformModel();

      // If transformId is a GUID, look for an existing transform
      if (!string.IsNullOrEmpty(transformId) && Guid.TryParse(transformId, out _))
      {
        var transforms = GetTransformSources();
        var foundTransform = transforms.FirstOrDefault(t => t.Id == transformId);
        if (foundTransform != null)
        {
          transform = foundTransform;
        }
        else
        {
          return Json(new { success = false, message = "Transform source not found." });
        }
      }
      else
      {
        // Otherwise, bind from form data for a new transform being tested
        try
        {
          _logger.LogInformation("Binding transform from form data");

          // Create a model from form data
          transform = new TransformModel
          {
            Title = Request.Form["Title"].ToString() ?? string.Empty,
            FilePattern = Request.Form["FilePattern"].ToString() ?? string.Empty,
            RegexPattern = Request.Form["RegexPattern"].ToString() ?? string.Empty
          };
        }
        catch (Exception ex)
        {
          _logger.LogError(ex, "Error binding form data for transform test");
          return Json(new { success = false, message = "Invalid form data." });
        }
      }

      try
      {
        // Log the transform being tested
        _logger.LogInformation("Testing transform: {Title}, Pattern: {Pattern}", transform.Title, transform.RegexPattern);

        // Find files matching the pattern
        var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder);
        var filePattern = transform.FilePattern;

        // If pattern is empty, use *.*
        if (string.IsNullOrWhiteSpace(filePattern))
        {
          filePattern = "*.*";
        }

        // Handle wildcards in pattern
        var files = Directory.EnumerateFiles(uploadsPath, filePattern)
            .Take(5) // Limit to 5 files for testing
            .ToList();

        if (!files.Any())
        {
          return Json(new
          {
            success = true,
            message = $"Pattern matches 0 files. No files found matching '{transform.FilePattern}'."
          });
        }

        // Test the regex on the first file
        var testFile = files.First();
        var fileName = Path.GetFileName(testFile);
        var fileContent = await System.IO.File.ReadAllTextAsync(testFile);

        // Apply the regex pattern
        ValidateRegexPattern(transform.RegexPattern);
        var regex = new Regex(transform.RegexPattern, RegexOptions.CultureInvariant, RegexTimeout);
        var match = regex.Match(fileContent);

        if (match.Success)
        {
          // Get all capturing groups
          var captures = match.Groups.Count > 1
              ? string.Join(", ", match.Groups.Cast<Group>().Skip(1).Select(g => g.Value))
              : match.Value;

          return Json(new
          {
            success = true,
            message = $"Pattern matched {files.Count} file(s). First match on file '{fileName}': {captures}"
          });
        }
        else
        {
          return Json(new
          {
            success = true,
            message = $"Pattern matches {files.Count} file(s), but no regex matches found in file '{fileName}'."
          });
        }
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error testing transform");
        return Json(new { success = false, message = $"Error testing transform: {ex.Message}" });
      }
    }

    /// <summary>
    /// Process a transform in the background
    /// </summary>
    public async Task ProcessTransform(string transformId, PerformContext? context = null)
    {
      _logger.LogInformation("Processing transform {TransformId}", transformId);

      // Initialize Hangfire Console
      context?.WriteLine($"<h3>Starting Transform: {transformId}</h3>");

      var sources = GetTransformSources();
      var source = sources.FirstOrDefault(s => s.Id == transformId);

      if (source == null)
      {
        var errorMsg = $"Transform source {transformId} not found";
        _logger.LogWarning(errorMsg);
        context?.WriteLine($"❌ {errorMsg}", ConsoleTextColor.Red);
        return;
      }

      context?.WriteLine($"Transform Source: <b>{source.Title}</b>");
      context?.WriteLine($"File Pattern: <code>{source.FilePattern}</code>");
      context?.WriteLine($"Regex Pattern: <code>{source.RegexPattern}</code>");
      context?.WriteLine($"Mode: {(source.ModifyExistingFile ? "Modify existing files" : "Create new files")}");
      context?.WriteLine();

      try
      {
        // Find files matching the pattern
        var uploadsPath = GetAppPath(UploadsFolder);
        _logger.LogInformation("Using uploads path: {Path}", uploadsPath);

        // Ensure uploads directory exists
        if (!Directory.Exists(uploadsPath))
        {
          _logger.LogWarning("Uploads directory does not exist at {Path}, creating it", uploadsPath);
          Directory.CreateDirectory(uploadsPath);
          context?.WriteLine($"📁 Created uploads directory: {uploadsPath}");
        }
        else
        {
          context?.WriteLine($"📁 Using uploads directory: {uploadsPath}");
        }

        var filePattern = string.IsNullOrWhiteSpace(source.FilePattern) ? "*.*" : source.FilePattern;

        context?.WriteLine($"🔍 Searching for files matching pattern <code>{filePattern}</code> in: {uploadsPath}");

        _logger.LogInformation("Searching for files with pattern '{Pattern}' in directory '{Path}'", filePattern, uploadsPath);
        var files = Directory.EnumerateFiles(uploadsPath, filePattern).ToList();
        _logger.LogInformation("Found {Count} files matching pattern", files.Count);

        if (files.Count == 0)
        {
          context?.WriteLine("❗ No files found matching the pattern", ConsoleTextColor.Yellow);
          return;
        }

        context?.WriteLine($"📄 Found {files.Count} file(s) to process");

        int processedCount = 0;
        // Use the main uploads path instead of a separate transformed directory
        var outputPath = uploadsPath;

        try
        {
          // Ensure we have write access to the output directory
          string testFilePath = Path.Combine(outputPath, $"test_{Guid.NewGuid()}.tmp");
          System.IO.File.WriteAllText(testFilePath, "Test");
          System.IO.File.Delete(testFilePath);
          _logger.LogInformation("Successfully verified write access to output directory");
        }
        catch (Exception dirEx)
        {
          _logger.LogError(dirEx, "Failed to access uploads directory at {Path}", outputPath);
          context?.WriteLine($"❌ Error with output directory: {dirEx.Message}", ConsoleTextColor.Red);
          throw;
        }

        context?.WriteLine($"📂 Output directory: {outputPath}");
        context?.WriteLine();        // Create a progress bar for file processing
        var progressBar = context?.WriteProgressBar();

        // Process each file
        for (int i = 0; i < files.Count; i++)
        {
          var file = files[i];
          var fileName = Path.GetFileName(file);

          // Update progress
          progressBar?.SetValue((int)((i + 1) / (double)files.Count * 100));
          context?.WriteLine($"Processing file {i + 1}/{files.Count}: <b>{fileName}</b>");

          try
          {
            var fileContent = await System.IO.File.ReadAllTextAsync(file);

            // Apply regex pattern
            ValidateRegexPattern(source.RegexPattern);
            var regex = new Regex(source.RegexPattern, RegexOptions.CultureInvariant, RegexTimeout);
            var matches = regex.Matches(fileContent);

            if (matches.Count > 0)
            {
              context?.WriteLine($"  ✅ Found {matches.Count} matches in file", ConsoleTextColor.Green);
              string outputFilePath;

              // Determine if we're modifying the existing file or creating a new one
              if (source.ModifyExistingFile)
              {
                // Use the original file path
                outputFilePath = file;
                _logger.LogInformation("Modifying existing file: {FilePath}", outputFilePath);
                context?.WriteLine($"  🔄 Modifying existing file");
              }
              else
              {
                // Create a new output file directly in the uploads folder with a clear naming convention
                var safeTitle = string.Concat(source.Title.Select(character =>
                  Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Replace(" ", "_");
                var outputFileName = $"transformed_{safeTitle}_{Path.GetFileNameWithoutExtension(file)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt";
                outputFilePath = FilePathSecurity.ResolveFile(outputPath, outputFileName);
                _logger.LogInformation("Creating new transformation output file: {OutputPath}", outputFilePath);
                context?.WriteLine($"  📝 Creating new file: {outputFileName}");
              }

              // Create a new transformed content based on matches
              var transformedContent = new StringBuilder();

              try
              {
                if (source.ModifyExistingFile)
                {
                  // For existing file modification, we'll apply the transformation directly to the content
                  _logger.LogInformation("Modifying existing file: {FilePath}", outputFilePath);
                  context?.WriteLine($"  🔄 Creating backup and modifying existing file");

                  // First, make a backup of the original file in the same uploads directory
                  var backupPath = Path.Combine(outputPath, $"{Path.GetFileNameWithoutExtension(file)}_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}{Path.GetExtension(file)}");

                  try
                  {
                    // Ensure we can write to the file by checking if it's read-only
                    System.IO.FileInfo fileInfo = new System.IO.FileInfo(file);
                    if ((fileInfo.Attributes & System.IO.FileAttributes.ReadOnly) != 0)
                    {
                      fileInfo.Attributes &= ~System.IO.FileAttributes.ReadOnly;
                      _logger.LogInformation("Removed read-only attribute from file: {FilePath}", file);
                      context?.WriteLine($"  ⚠️ Removed read-only attribute from file");
                    }

                    System.IO.File.Copy(file, backupPath, true);
                    _logger.LogInformation("Created backup of original file at: {BackupPath}", backupPath);
                    context?.WriteLine($"  💾 Created backup at: {Path.GetFileName(backupPath)}");
                  }
                  catch (Exception backupEx)
                  {
                    _logger.LogError(backupEx, "Failed to create backup file at {BackupPath}", backupPath);
                    context?.WriteLine($"  ❌ Failed to create backup: {backupEx.Message}", ConsoleTextColor.Red);
                    throw;
                  }

                  // Apply transformations directly to the content
                  string modifiedContent = fileContent;
                  int replacements = 0;

                  // Replace content based on regex matches
                  foreach (Match match in matches)
                  {
                    if (match.Groups.Count > 1)
                    {
                      // Replace with the content from the first capture group
                      string originalText = match.Value;
                      string replacementText = match.Groups[1].Value;
                      modifiedContent = modifiedContent.Replace(originalText, replacementText);
                      replacements++;

                      context?.WriteLine($"    • Replaced: '{originalText.Substring(0, Math.Min(20, originalText.Length))}...' with '{replacementText.Substring(0, Math.Min(20, replacementText.Length))}...'");
                    }
                  }

                  // Write the modified content back to the original file
                  try
                  {
                    await System.IO.File.WriteAllTextAsync(outputFilePath, modifiedContent);
                    _logger.LogInformation("Successfully wrote modified content to file: {FilePath} ({Length} bytes, {Replacements} replacements)",
                        outputFilePath, modifiedContent.Length, replacements);
                    context?.WriteLine($"  ✅ Modified file saved with {replacements} replacements ({modifiedContent.Length} bytes)", ConsoleTextColor.Green);
                  }
                  catch (Exception writeEx)
                  {
                    _logger.LogError(writeEx, "Failed to write modified content to file: {FilePath}", outputFilePath);
                    context?.WriteLine($"  ❌ Failed to save modified file: {writeEx.Message}", ConsoleTextColor.Red);
                    throw;
                  }
                }
                else
                {
                  // For new file creation, format the output with metadata and extracted content
                  _logger.LogInformation("Creating new output file: {FilePath}", outputFilePath);

                  // Add metadata header
                  transformedContent.AppendLine($"# Transformation: {source.Title}");
                  transformedContent.AppendLine($"# Source file: {fileName}");
                  transformedContent.AppendLine($"# Regex pattern: {source.RegexPattern}");
                  transformedContent.AppendLine($"# Execution time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                  transformedContent.AppendLine("# -------------------------------------------");
                  transformedContent.AppendLine();

                  // Process each match
                  int matchCount = 0;
                  foreach (Match match in matches)
                  {
                    matchCount++;
                    transformedContent.AppendLine($"## Match {matchCount}");

                    if (match.Groups.Count > 1)
                    {
                      // Extract data from named capturing groups
                      for (int groupIndex = 1; groupIndex < match.Groups.Count; groupIndex++)
                      {
                        var group = match.Groups[groupIndex];
                        string groupName = regex.GroupNameFromNumber(groupIndex);

                        if (groupName != groupIndex.ToString()) // Named group
                        {
                          transformedContent.AppendLine($"{groupName}: {group.Value}");
                        }
                        else // Numbered group
                        {
                          transformedContent.AppendLine($"Group {groupIndex}: {group.Value}");
                        }
                      }
                    }
                    else
                    {
                      transformedContent.AppendLine(match.Value);
                    }

                    transformedContent.AppendLine("---");
                  }

                  string content = transformedContent.ToString();

                  // Ensure the output directory exists
                  Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath) ?? string.Empty);

                  // Write the transformed content to the new output file
                  try
                  {
                    await System.IO.File.WriteAllTextAsync(outputFilePath, content);

                    // Verify the file was created
                    if (System.IO.File.Exists(outputFilePath))
                    {
                      var fileInfo = new System.IO.FileInfo(outputFilePath);
                      _logger.LogInformation("Successfully wrote new file: {FilePath} ({Length} bytes)", outputFilePath, fileInfo.Length);
                      context?.WriteLine($"  ✅ New file created successfully ({fileInfo.Length} bytes)", ConsoleTextColor.Green);
                      context?.WriteLine($"  📂 File location: {outputFilePath}");
                    }
                    else
                    {
                      _logger.LogError("File was not created at expected path: {FilePath}", outputFilePath);
                      context?.WriteLine($"  ⚠️ File write appeared to succeed but file does not exist at expected location", ConsoleTextColor.Yellow);
                    }
                  }
                  catch (Exception writeEx)
                  {
                    _logger.LogError(writeEx, "Failed to write new file: {FilePath}", outputFilePath);
                    context?.WriteLine($"  ❌ Failed to create new file: {writeEx.Message}", ConsoleTextColor.Red);
                    throw;
                  }
                }
              }
              catch (Exception ex)
              {
                _logger.LogError(ex, "Error during file {Operation} for {FilePath}",
                    source.ModifyExistingFile ? "modification" : "creation", outputFilePath);
                context?.WriteLine($"  ❌ Error during file processing: {ex.Message}", ConsoleTextColor.Red);
                throw; // Rethrow to be caught by the outer catch block
              }
              processedCount++;
              context?.WriteLine($"  📊 Processed file successfully", ConsoleTextColor.Green);
            }
            else
            {
              context?.WriteLine($"  ⚠️ No matches found in file", ConsoleTextColor.Yellow);
            }
          }
          catch (Exception ex)
          {
            _logger.LogError(ex, "Error processing file {FileName} for transform {TransformId}",
                Path.GetFileName(file), transformId);
            context?.WriteLine($"  ❌ Error processing file: {ex.Message}", ConsoleTextColor.Red);
          }
        }

        // Complete the progress bar
        progressBar?.SetValue(100);

        // Update the transform source with last execution details
        source.LastExecutedTime = DateTime.UtcNow;
        source.LastProcessedFileCount = processedCount;

        context?.WriteLine();
        context?.WriteLine($"📊 Summary: Processed {processedCount} of {files.Count} files");

        int sourceIndex = sources.FindIndex(s => s.Id == transformId);
        if (sourceIndex >= 0)
        {
          sources[sourceIndex] = source;
          SaveTransformSources(sources);
          context?.WriteLine("✅ Updated transform source with execution details");
        }

        // Add success activity
        AddTransformActivity(new TransformActivityModel
        {
          TransformId = source.Id,
          TransformTitle = source.Title,
          Status = TransformStatus.Success,
          Message = $"Transform '{source.Title}' completed. Processed {processedCount} file(s).",
          Timestamp = DateTime.UtcNow,
          FilesProcessed = processedCount
        });

        context?.WriteLine("✅ Added success activity record");

        // Sync file metadata to ensure transformed files appear in the Dashboard
        try
        {
          using (var httpClient = CreateHttpClient())
          {
            if (System.Net.ServicePointManager.DefaultConnectionLimit < 10)
            {
              System.Net.ServicePointManager.DefaultConnectionLimit = 10;
            }

            var syncUrl = $"{_serviceUrl}/File/SyncFileMetadata";
            _logger.LogInformation("Syncing file metadata at {Url}", syncUrl);
            context?.WriteLine("📊 Syncing file metadata to ensure files appear in Dashboard...");

            var response = await httpClient.PostAsync(syncUrl, null);
            if (response.IsSuccessStatusCode)
            {
              var result = await response.Content.ReadAsStringAsync();
              _logger.LogInformation("File metadata sync result: {Result}", result);
              context?.WriteLine("✅ File metadata synced successfully");
            }
            else
            {
              _logger.LogWarning("Failed to sync file metadata: {StatusCode}", response.StatusCode);
              context?.WriteLine("⚠️ Failed to sync file metadata", ConsoleTextColor.Yellow);
            }
          }
        }
        catch (Exception syncEx)
        {
          _logger.LogError(syncEx, "Error syncing file metadata");
          context?.WriteLine("⚠️ Error syncing file metadata: " + syncEx.Message, ConsoleTextColor.Yellow);
        }

        context?.WriteLine($"<h4>Transform '{source.Title}' completed successfully!</h4>", ConsoleTextColor.Green);

        _logger.LogInformation("Transform {TransformId} processed {Count} files", transformId, processedCount);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error executing transform {TransformId}", transformId);
        context?.WriteLine($"❌ Error executing transform: {ex.Message}", ConsoleTextColor.Red);

        // Add failure activity
        AddTransformActivity(new TransformActivityModel
        {
          TransformId = source.Id,
          TransformTitle = source.Title,
          Status = TransformStatus.Failed,
          Message = $"Transform '{source.Title}' failed: {ex.Message}",
          Timestamp = DateTime.UtcNow,
          Error = ex.ToString()
        });
      }
    }

    /// <summary>
    /// Schedule a recurring transform job
    /// </summary>
    private void ScheduleTransformJob(TransformModel source)
    {
      if (source.IsActive && source.TransformIntervalMinutes > 0)
      {
        _logger.LogInformation("Scheduling transform job for source {Id} to run every {Interval} minutes",
            source.Id, source.TransformIntervalMinutes);

        // Create a unique job ID for this transform source
        string jobId = $"transform_{source.Id}";

        // Remove any existing scheduled job for this transform
        RecurringJob.RemoveIfExists(jobId);

        // Create appropriate CRON expression based on interval
        string cronExpression;

        if (source.TransformIntervalMinutes < 1)
        {
          // Minimum 1 minute
          cronExpression = "*/1 * * * *";
          _logger.LogWarning("Transform interval too low ({Interval}), using minimum value of 1 minute", source.TransformIntervalMinutes);
        }
        else if (source.TransformIntervalMinutes <= 59)
        {
          // Minutes-based expression for intervals <= 59 minutes
          cronExpression = $"*/{source.TransformIntervalMinutes} * * * *";
        }
        else if (source.TransformIntervalMinutes % 60 == 0)
        {
          // For exact hour intervals, use hour-based expression
          int hours = source.TransformIntervalMinutes / 60;
          cronExpression = $"0 */{hours} * * *";
          _logger.LogInformation("Using hour-based CRON schedule: {Cron} for {Hours} hour(s)", cronExpression, hours);
        }
        else
        {
          // For other intervals, schedule at a specific minute each hour
          // and use the minute value as a marker for the job to check if it should run
          cronExpression = "0 * * * *"; // Run at minute 0 of each hour
          _logger.LogInformation("Using hourly CRON schedule: {Cron} for {Minutes} minutes", cronExpression, source.TransformIntervalMinutes);
        }

        // Schedule the new job with the calculated interval
        _logger.LogInformation("Scheduling job with CRON expression: {CronExpression}", cronExpression);
        RecurringJob.AddOrUpdate<TransformController>(
            jobId,
            x => x.ProcessTransform(source.Id, null),
            cronExpression
        );
      }
    }

    /// <summary>
    /// Get all transform sources
    /// </summary>
    private List<TransformModel> GetTransformSources()
    {
      try
      {
        _logger.LogInformation("Reading transform sources from {Path}", _transformsPath);

        if (!System.IO.File.Exists(_transformsPath))
        {
          _logger.LogWarning("Transform sources file not found at {Path}, creating empty file", _transformsPath);
          System.IO.File.WriteAllText(_transformsPath, "[]");
        }

        var json = System.IO.File.ReadAllText(_transformsPath);
        _logger.LogInformation("Read transform sources JSON ({Length} bytes): {Json}", json.Length, json);

        var sources = JsonSerializer.Deserialize<List<TransformModel>>(json) ?? new List<TransformModel>();
        _logger.LogInformation("Deserialized {Count} transform sources", sources.Count);

        return sources;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error reading transform sources from {Path}", _transformsPath);
        return new List<TransformModel>();
      }
    }

    /// <summary>
    /// Save all transform sources
    /// </summary>
    private void SaveTransformSources(List<TransformModel> sources)
    {
      try
      {
        _logger.LogInformation("Saving {Count} transform sources to {Path}", sources.Count, _transformsPath);

        var json = JsonSerializer.Serialize(sources, new JsonSerializerOptions
        {
          WriteIndented = true
        });

        _logger.LogInformation("JSON content to be saved: {Json}", json);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(_transformsPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
          Directory.CreateDirectory(directory);
          _logger.LogInformation("Created directory: {Directory}", directory);
        }

        System.IO.File.WriteAllText(_transformsPath, json);
        _logger.LogInformation("Successfully saved transform sources to {Path}", _transformsPath);

        // Verify the file was written
        bool fileExists = System.IO.File.Exists(_transformsPath);
        long fileSize = fileExists ? new System.IO.FileInfo(_transformsPath).Length : 0;
        _logger.LogInformation("File exists: {FileExists}, File size: {FileSize} bytes", fileExists, fileSize);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error saving transform sources to {Path}", _transformsPath);
      }
    }

    /// <summary>
    /// Get recent transform activities
    /// </summary>
    private List<TransformActivityModel> GetLatestTransformActivities()
    {
      try
      {
        var json = System.IO.File.ReadAllText(_activitiesPath);
        var activities = JsonSerializer.Deserialize<List<TransformActivityModel>>(json) ?? new List<TransformActivityModel>();

        // Sort by timestamp descending and take the most recent 50
        return activities
            .OrderByDescending(a => a.Timestamp)
            .Take(50)
            .ToList();
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error reading transform activities from {Path}", _activitiesPath);
        return new List<TransformActivityModel>();
      }
    }

    /// <summary>
    /// Add a new transform activity
    /// </summary>
    private void AddTransformActivity(TransformActivityModel activity)
    {
      try
      {
        var activities = GetLatestTransformActivities();

        // Add the new activity
        activities.Add(activity);

        // Keep only the most recent 100 activities
        activities = activities
            .OrderByDescending(a => a.Timestamp)
            .Take(100)
            .ToList();

        var json = JsonSerializer.Serialize(activities, new JsonSerializerOptions
        {
          WriteIndented = true
        });
        System.IO.File.WriteAllText(_activitiesPath, json);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error saving transform activity to {Path}", _activitiesPath);
      }
    }

    /// <summary>
    /// Helper method to get a consistent file path
    /// </summary>
    private string GetAppPath(string relativePath)
    {
      return Path.Combine(_appRootPath, relativePath);
    }

    /// <summary>
    /// Process a direct transformation request from the UI
    /// </summary>
    [HttpPost("ProcessTransform")]
    public async Task<IActionResult> ProcessTransformFiles([FromBody] TransformFilesRequest request)
    {
      if (request == null || request.Files == null || !request.Files.Any())
      {
        return Json(new { success = false, error = "No files provided" });
      }

      if (string.IsNullOrEmpty(request.Pattern))
      {
        return Json(new { success = false, error = "No pattern provided" });
      }

      try
      {
        int transformedCount = 0;
        List<string> transformedFiles = new List<string>();

        foreach (var filename in request.Files)
        {
          string fullPath = FilePathSecurity.ResolveFile(Path.Combine(_appRootPath, UploadsFolder), filename);

          if (!System.IO.File.Exists(fullPath))
          {
            _logger.LogWarning($"File not found: {fullPath}");
            continue;
          }

          // Skip files that aren't text files
          string extension = Path.GetExtension(fullPath).ToLowerInvariant();
          if (!IsTextFile(extension))
          {
            _logger.LogWarning($"Not a text file: {fullPath}");
            continue;
          }

          // Read the file content
          string content = await System.IO.File.ReadAllTextAsync(fullPath);

          // Apply the regex transformation
          ValidateRegexPattern(request.Pattern);
          string transformedContent = Regex.Replace(content, request.Pattern, request.Replacement,
            RegexOptions.CultureInvariant, RegexTimeout);

          // If content changed, save the transformed file
          if (content != transformedContent)
          {
            // Generate a new filename with _transformed suffix
            string newFilename = Path.GetFileNameWithoutExtension(filename) + "_transformed" + Path.GetExtension(filename);
            string newFilePath = Path.Combine(_appRootPath, UploadsFolder, newFilename);

            // Save the transformed file
            await System.IO.File.WriteAllTextAsync(newFilePath, transformedContent);
            transformedCount++;
            transformedFiles.Add(newFilename);

            // Log the transformation
            _logger.LogInformation($"Transformed {filename} to {newFilename}");
          }
        }

        // Sync file metadata to update the file list
        if (transformedCount > 0)
        {
          // Call FileController's SyncFileMetadata endpoint
          using (var httpClient = CreateHttpClient())
          {
            httpClient.BaseAddress = new Uri(_serviceUrl);
            var response = await httpClient.PostAsync("/File/SyncFileMetadata", new StringContent("", Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
            {
              _logger.LogWarning($"Failed to sync file metadata: {response.StatusCode}");
            }
          }
        }

        return Json(new
        {
          success = true,
          transformedCount,
          transformedFiles
        });
      }
      catch (Exception ex)
      {
        _logger.LogError($"Error processing transform: {ex.Message}", ex);
        return Json(new { success = false, error = ex.Message });
      }
    }

    private HttpClient CreateHttpClient() =>
      _httpClientFactory?.CreateClient("ExternalFetch") ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    private static void ValidateRegexPattern(string pattern)
    {
      if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxRegexPatternLength)
        throw new ArgumentException($"Regex patterns must contain between 1 and {MaxRegexPatternLength} characters.", nameof(pattern));
    }

    private bool IsTextFile(string extension)
    {
      string[] textExtensions = { ".txt", ".csv", ".json", ".xml", ".html", ".htm", ".log", ".md", ".js", ".ts", ".css", ".cs", ".c", ".cpp", ".h", ".py", ".java" };
      return textExtensions.Contains(extension);
    }
  }
}

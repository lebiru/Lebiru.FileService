using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Lebiru.FileService.HangfireJobs;
using Lebiru.FileService.Models;
using FileInfo = Lebiru.FileService.Models.FileInfo;
using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Authorization;
using System.Security.Cryptography;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net.Http;
using System.Dynamic;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

/// <summary>
/// Model class for pagination request data
/// </summary>
public class PaginationRequest
{
    /// <summary>
    /// The current page number
    /// </summary>
    public int page { get; set; } = 1;

    /// <summary>
    /// Number of items to display per page
    /// </summary>
    public int itemsPerPage { get; set; } = 10;
}

namespace Lebiru.FileService.Controllers
{
    /// <summary>
    /// Controller for managing file operations including upload, download, and listing
    /// </summary>
    [Route("File")]
    [ApiController]
    [Authorize]
    public class FileController : Controller
    {
        private const string UploadsFolder = "uploads";
        private const string DataFolder = "app-data";
        private readonly CleanupJob _cleanupJob;

        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly string _fileInfoPath;
        private readonly FileServiceConfig _config;
        private readonly IApiMetricsService _metricsService;
        private readonly IUserService _userService;
        private readonly IMimeValidationService _mimeValidationService;
        private readonly ILogger<FileController> _logger;
        private readonly IFileMetadataStore? _metadataStore;
        private readonly IHttpClientFactory? _httpClientFactory;

        private static readonly object _fileLock = new object();

        private List<Models.FileInfo> FileInfos
        {
            get
            {
                lock (_fileLock)
                {
                    if (_metadataStore != null) return _metadataStore.GetAll();
                    if (!System.IO.File.Exists(_fileInfoPath))
                    {
                        return new List<Models.FileInfo>();
                    }
                    try
                    {
                        var json = System.IO.File.ReadAllText(_fileInfoPath);
                        return System.Text.Json.JsonSerializer.Deserialize<List<Models.FileInfo>>(json) ?? new();
                    }
                    catch
                    {
                        return new List<Models.FileInfo>();
                    }
                }
            }
            set
            {
                lock (_fileLock)
                {
                    if (_metadataStore != null)
                    {
                        _metadataStore.Replace(value);
                        return;
                    }
                    var json = System.Text.Json.JsonSerializer.Serialize(value);
                    System.IO.File.WriteAllText(_fileInfoPath, json);
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the FileController
        /// </summary>
        /// <param name="cleanupJob">The cleanup job service for managing file cleanup tasks</param>

        /// <param name="backgroundJobClient">The Hangfire background job client</param>
        /// <param name="configuration">The application configuration</param>
        /// <param name="metricsService">The API metrics tracking service</param>
        /// <param name="userService">The user management service</param>
        /// <param name="mimeValidationService">Service for validating file MIME types</param>
        /// <param name="logger">The logger service</param>
        /// <param name="metadataStore">The cached metadata store, when supplied by dependency injection</param>
        /// <param name="httpClientFactory">Factory for pooled outbound HTTP connections</param>
        public FileController(
            CleanupJob cleanupJob,

            IBackgroundJobClient backgroundJobClient,
            IConfiguration configuration,
            IApiMetricsService metricsService,
            IUserService userService,
            IMimeValidationService mimeValidationService,
            ILogger<FileController> logger,
            IFileMetadataStore? metadataStore = null,
            IHttpClientFactory? httpClientFactory = null)
        {
            _cleanupJob = cleanupJob;

            _backgroundJobClient = backgroundJobClient;
            var dataDir = Path.Combine(Directory.GetCurrentDirectory(), DataFolder);
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }
            _fileInfoPath = Path.Combine(dataDir, "fileInfo.json");
            _config = configuration.GetSection("FileService")?.Get<FileServiceConfig>() ?? new FileServiceConfig();
            _metricsService = metricsService ?? throw new ArgumentNullException(nameof(metricsService));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _mimeValidationService = mimeValidationService ?? throw new ArgumentNullException(nameof(mimeValidationService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metadataStore = metadataStore;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// The home page for the app. Displays current files hosted on FileService.
        /// </summary>
        /// <returns></returns>
        [HttpGet("Home")]
        public IActionResult Index()
        {
            var fileInfos = FileInfos;

            // Default sort: newest first
            fileInfos = fileInfos
                .OrderByDescending(f => f.UploadTime)
                .ToList();

            // Get pagination preferences from session or use defaults
            var currentPage = HttpContext.Session.GetInt32("CurrentPage") ?? 1;
            var pageSize = HttpContext.Session.GetInt32("ItemsPerPage") ?? PaginationModel.PageSizeOptions[1]; // Default to 10

            // Create pagination model
            var pagination = new PaginationModel
            {
                CurrentPage = currentPage,
                PageSize = pageSize,
                TotalItems = fileInfos.Count
            };

            // Ensure current page is valid
            if (pagination.CurrentPage > pagination.TotalPages)
            {
                pagination.CurrentPage = Math.Max(1, pagination.TotalPages);
            }

            // Get the correct page of files
            var skip = (pagination.CurrentPage - 1) * pagination.PageSize;
            var paginatedFiles = fileInfos
                .Skip(skip)
                .Take(pagination.PageSize)
                .ToList();            // Get fresh server space info
            var spaceInfo = GetServerSpaceInfo();
            ViewBag.UsedSpace = FormatBytes(spaceInfo.UsedSpace);
            ViewBag.TotalSpace = FormatBytes(spaceInfo.TotalSpace);
            ViewBag.UsedSpacePercent = Math.Round((double)spaceInfo.UsedSpace / spaceInfo.TotalSpace * 100, 2);
            ViewBag.WarningThresholdPercent = _config.WarningThresholdPercent;
            ViewBag.CriticalThresholdPercent = _config.CriticalThresholdPercent;
            ViewBag.ExpiryOptions = Enum.GetValues<ExpiryOption>();
            ViewBag.MaxFileSizeMB = _config.MaxFileSizeMB;
            ViewBag.MaxDiskSpaceGB = _config.MaxDiskSpaceGB;
            ViewBag.FileCount = fileInfos.Count;
            ViewBag.Pagination = pagination;
            ViewBag.Sort = "upload_desc";

            // Check the Dark Mode setting
            var isDarkMode = HttpContext.Session.GetString("DarkMode") == "true";
            ViewBag.IsDarkMode = isDarkMode;

            // Add API metrics to ViewBag
            ViewBag.UploadCount = _metricsService.UploadCount;
            ViewBag.DownloadCount = _metricsService.DownloadCount;
            ViewBag.DeleteCount = _metricsService.DeleteCount;
            ViewBag.MetricsLastUpdated = _metricsService.LastUpdated;

            return View(paginatedFiles);
        }

        /// <summary>
        /// Gets a paginated list of files for AJAX updates
        /// </summary>
        /// <returns>A partial view with the paginated files</returns>
        [HttpGet("List")]
        public IActionResult List(int page = 1, int itemsPerPage = 10, string sort = "upload_desc")
        {
            // Save pagination preferences to session
            HttpContext.Session.SetInt32("CurrentPage", page);
            HttpContext.Session.SetInt32("ItemsPerPage", itemsPerPage);

            var fileInfos = FileInfos;

            // Apply sorting
            fileInfos = sort switch
            {
                "upload_asc" => fileInfos.OrderBy(f => f.UploadTime).ToList(),
                "name_asc" => fileInfos.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
                "name_desc" => fileInfos.OrderByDescending(f => f.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
                "size_asc" => fileInfos.OrderBy(f => f.FileSize).ToList(),
                "size_desc" => fileInfos.OrderByDescending(f => f.FileSize).ToList(),
                // Expiry: Soonest first puts null (Never) at the end; Latest first treats null as latest (top)
                "expiry_asc" => fileInfos.OrderBy(f => f.ExpiryTime ?? DateTime.MaxValue).ToList(),
                "expiry_desc" => fileInfos.OrderByDescending(f => f.ExpiryTime ?? DateTime.MaxValue).ToList(),
                _ => fileInfos.OrderByDescending(f => f.UploadTime).ToList(), // upload_desc default
            };

            // Ensure valid pagination parameters
            page = Math.Max(1, page);
            itemsPerPage = PaginationModel.PageSizeOptions.Contains(itemsPerPage)
                ? itemsPerPage
                : PaginationModel.PageSizeOptions[1]; // Default to 10

            // Create pagination model
            var pagination = new PaginationModel
            {
                CurrentPage = page,
                PageSize = itemsPerPage,
                TotalItems = fileInfos.Count
            };

            // Get paginated data
            var paginatedFiles = fileInfos
                .Skip((page - 1) * itemsPerPage)
                .Take(itemsPerPage)
                .ToList();

            ViewBag.Pagination = pagination;
            return PartialView("_FileList", paginatedFiles);
        }

        /// <summary>
        /// Gets the total number of files for pagination
        /// </summary>
        /// <returns>The total number of files</returns>
        [HttpGet("GetTotalFiles")]
        public IActionResult GetTotalFiles()
        {
            return Json(FileInfos.Count);
        }

        /// <summary>
        /// Displays the Swagger documentation UI
        /// </summary>
        /// <returns>The Swagger view for API documentation</returns>
        [HttpGet("Swagger")]
        public IActionResult Swagger()
        {
            return View("Swagger");
        }

        /// <summary>
        /// Uploads a file with optional expiry time.
        /// </summary>
        /// <param name="files">The file to upload.</param>
        /// <param name="expiryOption">When the file should expire and be deleted. Defaults to never.</param>
        /// <param name="cancellationToken">Signals that the upload request was aborted.</param>
        /// <returns>A response indicating the success or failure of the operation.</returns>
        [HttpPost("CreateDoc")]
        [HttpPost("Upload")]
        [Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Contributor}")]
        public async Task<IActionResult> Upload(List<IFormFile> files, [FromForm] ExpiryOption expiryOption = ExpiryOption.Never,
            CancellationToken cancellationToken = default)
        {
            if (files == null || files.Count == 0)
                return BadRequest("No files uploaded.");

            // Check file size limits and MIME types
            foreach (var file in files)
            {
                var maxFileSizeBytes = _config.MaxFileSizeMB * 1024L * 1024L;
                if (file.Length > maxFileSizeBytes)
                {
                    return BadRequest($"File '{file.FileName}' exceeds the maximum allowed size of {_config.MaxFileSizeMB} MB");
                }

                // Validate the file's MIME type
                var validationResult = _mimeValidationService.ValidateFileDetailed(file.FileName, file.ContentType);
                if (!validationResult.IsValid)
                {
                    return BadRequest($"Security check failed: {validationResult.Message}");
                }
            }

            var uploadsFolderPath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder);
            if (!Directory.Exists(uploadsFolderPath))
                Directory.CreateDirectory(uploadsFolderPath);

            var fileInfos = FileInfos;
            var totalSpaceUsed = _metadataStore?.UsedSpace ?? GetTotalSpaceUsed(uploadsFolderPath);

            foreach (var file in files)
            {
                var filePath = Path.Combine(uploadsFolderPath, file.FileName);

                // Check if file upload will exceed configured limit
                var maxSpace = _config.MaxDiskSpaceGB * 1024L * 1024L * 1024L;
                if (totalSpaceUsed + file.Length > maxSpace)
                {
                    return BadRequest($"File upload would exceed the maximum allocated space of {_config.MaxDiskSpaceGB} GB.");
                }

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream, cancellationToken);
                }

                // Flush to ensure the file is written to disk
                System.IO.File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow);

                // Add file ownership
                var username = User.Identity?.Name;
                if (username != null)
                {
                    _userService.AddFileToUser(username, filePath);
                }

                var uploadTime = DateTime.UtcNow;
                DateTime? expiryTime = expiryOption switch
                {
                    ExpiryOption.OneMinute => uploadTime.AddMinutes(1),
                    ExpiryOption.OneHour => uploadTime.AddHours(1),
                    ExpiryOption.OneDay => uploadTime.AddDays(1),
                    ExpiryOption.OneWeek => uploadTime.AddDays(7),
                    _ => null
                };

                var fileInfo = new Models.FileInfo
                {
                    FileName = file.FileName,
                    FilePath = filePath,
                    UploadTime = uploadTime,
                    ExpiryTime = expiryTime,
                    FileSize = file.Length,
                    Owner = User.Identity?.Name
                };

                fileInfos.Add(fileInfo);
                totalSpaceUsed += file.Length;

                if (expiryTime.HasValue)
                {
                    _backgroundJobClient.Schedule<ExpiryJob>(
                        job => job.DeleteExpiredFiles(null),
                        expiryTime.Value
                    );
                }
            }

            // Save the updated file information
            FileInfos = fileInfos;

            // Increment upload counter
            _metricsService.IncrementUploadCount();

            return Ok("File uploaded successfully.");
        }

        private string GetMimeType(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension switch
            {
                // Text formats
                ".txt" => "text/plain",
                ".log" => "text/plain",
                ".csv" => "text/csv",
                ".md" => "text/markdown",

                // HTML formats
                ".html" => "text/html",
                ".htm" => "text/html",

                // Image formats
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                ".bmp" => "image/bmp",

                // Document formats
                ".pdf" => "application/pdf",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xls" => "application/vnd.ms-excel",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".ppt" => "application/vnd.ms-powerpoint",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",

                // Code formats
                ".js" => "text/javascript",
                ".json" => "application/json",
                ".css" => "text/css",
                ".xml" => "text/xml",
                ".py" => "text/x-python",
                ".java" => "text/x-java",
                ".cs" => "text/x-csharp",

                // Default for unknown types
                _ => "application/octet-stream"
            };
        }

        /// <summary>
        /// Views a file in the browser with proper MIME type handling
        /// </summary>
        /// <param name="filename">The name of the file to view</param>
        /// <returns>The file content with appropriate MIME type for browser viewing</returns>
        [HttpGet("ViewFile")]
        public IActionResult ViewFile(string filename)
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder, filename);

            if (!System.IO.File.Exists(filePath))
                return NotFound("File not found.");

            var mimeType = GetMimeType(filePath);
            var extension = Path.GetExtension(filename).ToLowerInvariant();

            // For text files, display the content in our custom text viewer
            if (extension == ".txt" || extension == ".log" || extension == ".csv" || extension == ".md" ||
                extension == ".js" || extension == ".css" || extension == ".xml" || extension == ".json" ||
                extension == ".py" || extension == ".java" || extension == ".cs")
            {
                try
                {
                    // Read file content
                    string content = System.IO.File.ReadAllText(filePath);

                    // Return our custom TextView view with the content
                    return View("TextView", content);
                }
                catch (Exception ex)
                {
                    // If there's an issue (like binary file misidentified as text), 
                    // fall back to regular file serving
                    _logger.LogWarning($"Error reading text file {filename}: {ex.Message}");
                }
            }

            // For all other files, use PhysicalFile to allow range requests for media files
            return PhysicalFile(filePath, mimeType, enableRangeProcessing: true);
        }

        /// <summary>
        /// Views a file in the browser in print mode
        /// </summary>
        /// <param name="filename">The name of the file to print</param>
        /// <returns>The file content with appropriate MIME type for printing</returns>
        [HttpGet("PrintFile")]
        public IActionResult PrintFile(string filename)
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder, filename);

            if (!System.IO.File.Exists(filePath))
                return NotFound("File not found.");

            var mimeType = GetMimeType(filePath);

            // Add JavaScript to automatically open print dialog
            ViewBag.Filename = filename;
            ViewBag.MimeType = mimeType;
            ViewBag.FilePath = Url.Action("ViewFile", "File", new { filename });

            return View("PrintView");
        }

        /// <summary>
        /// Makes a copy of the specified file
        /// </summary>
        /// <param name="filename">The name of the file to copy</param>
        /// <returns>Success or error message</returns>
        [HttpPost("CopyFile")]
        public IActionResult CopyFile([FromForm] string filename)
        {
            try
            {
                // Validate input
                if (string.IsNullOrWhiteSpace(filename))
                {
                    return BadRequest("Filename cannot be empty.");
                }

                // Sanitize filename and get paths
                filename = Path.GetFileName(filename);
                var sourcePath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder, filename);

                // Check if source file exists
                if (!System.IO.File.Exists(sourcePath))
                {
                    return NotFound($"File '{filename}' not found.");
                }

                // Check if user has permission to access the file
                var username = User.Identity?.Name;
                if (username == null)
                {
                    return Unauthorized();
                }

                var userRole = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Role)?.Value;
                if (userRole != UserRoles.Admin && !_userService.IsFileOwner(username, sourcePath))
                {
                    return Forbid();
                }

                // Generate new filename with " Copy" suffix
                string filenameWithoutExt = Path.GetFileNameWithoutExtension(filename);
                string extension = Path.GetExtension(filename);
                string newFilename = $"{filenameWithoutExt} Copy{extension}";
                string destPath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder, newFilename);

                // If file with " Copy" suffix exists, add a number
                int counter = 1;
                while (System.IO.File.Exists(destPath))
                {
                    newFilename = $"{filenameWithoutExt} Copy {counter}{extension}";
                    destPath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder, newFilename);
                    counter++;
                }

                // Check if copy will exceed configured disk space limit
                var totalSpaceUsed = GetTotalSpaceUsed(Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder));
                var sourceFileInfo = new System.IO.FileInfo(sourcePath);
                var maxSpace = _config.MaxDiskSpaceGB * 1024L * 1024L * 1024L;
                if (totalSpaceUsed + sourceFileInfo.Length > maxSpace)
                {
                    return BadRequest($"File copy would exceed the maximum allocated space of {_config.MaxDiskSpaceGB} GB.");
                }

                // Copy the file
                System.IO.File.Copy(sourcePath, destPath);

                // Update fileInfo.json
                var fileInfos = FileInfos;
                var sourceFileDetails = fileInfos.FirstOrDefault(f => f.FileName == filename);
                if (sourceFileDetails != null)
                {
                    var newFileInfo = new Models.FileInfo
                    {
                        FileName = newFilename,
                        FilePath = destPath,
                        UploadTime = DateTime.UtcNow,
                        ExpiryTime = sourceFileDetails.ExpiryTime, // Keep the same expiry setting
                        FileSize = sourceFileDetails.FileSize,
                        Owner = User.Identity?.Name
                    };

                    fileInfos.Add(newFileInfo);
                    FileInfos = fileInfos;
                }

                // Add file ownership
                if (username != null)
                {
                    _userService.AddFileToUser(username, destPath);
                }

                // Increment upload counter (copying is like uploading)
                _metricsService.IncrementUploadCount();

                return Ok(new { message = $"File copied successfully as '{newFilename}'", newFilename });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"An error occurred while copying the file: {ex.Message}");
            }
        }

        private long GetTotalSpaceUsed(string directoryPath)
        {
            var directoryInfo = new DirectoryInfo(directoryPath);
            if (!directoryInfo.Exists)
                return 0;

            // Get fresh file info and handle any locked files
            return directoryInfo.GetFiles().Sum(file =>
            {
                try
                {
                    file.Refresh();
                    using (var fs = file.OpenRead())
                    {
                        return fs.Length;
                    }
                }
                catch (IOException)
                {
                    return file.Length;
                }
            });
        }

        /// <summary>
        /// Retrieves a list of uploaded files along with their details and download URIs.
        /// </summary>
        /// <remarks>
        /// This endpoint returns a list of objects containing details about each uploaded file,
        /// including the file name, upload time, expiry time, and a URI for downloading the file.
        /// </remarks>
        /// <returns>A list of objects representing uploaded files.</returns>
        [HttpGet("ListFiles")]
        public IActionResult ListFiles()
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";

            var fileDetails = FileInfos.Select(file =>
            {
                var fileUri = $"{baseUrl}/DownloadFile?filename={Uri.EscapeDataString(file.FileName)}";
                return new
                {
                    file.FileName,
                    file.UploadTime,
                    file.ExpiryTime,
                    file.FileSize,
                    DownloadUri = fileUri
                };
            }).ToList();

            return Ok(fileDetails);
        }


        /// <summary>
        /// Downloads a file.
        /// </summary>
        /// <param name="filename">The name of the file to download.</param>
        /// <returns>The file to download.</returns>
        [HttpGet("DownloadFile")]
        public IActionResult DownloadFile(string filename)
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder, filename);

            if (!System.IO.File.Exists(filePath))
                return NotFound();

            // Only increment download counter for explicit downloads (not views)
            _metricsService.IncrementDownloadCount();

            return PhysicalFile(filePath, "application/octet-stream", filename, enableRangeProcessing: true);
        }

        /// <summary>
        /// Triggers an immediate cleanup of expired files
        /// </summary>
        /// <returns>A confirmation that the cleanup job has been queued</returns>
        [HttpPost("TriggerCleanup")]
        public IActionResult TriggerCleanup()
        {
            // Enqueue both cleanup jobs
            _backgroundJobClient.Enqueue(() => _cleanupJob.Execute(null!));
            _backgroundJobClient.Enqueue<ExpiryJob>(job => job.DeleteExpiredFiles(null));

            return Ok("Cleanup jobs have been enqueued.");
        }

        /// <summary>
        /// Downloads multiple files as a single zip file.
        /// </summary>
        /// <param name="filenames">A pipe-separated list of filenames to include in the zip file.</param>
        /// <returns>The zip file containing the specified files.</returns>
        [HttpPost("DownloadZip")]
        public async Task<IActionResult> DownloadZip([FromForm] string filenames)
        {
            filenames = filenames.Trim();

            if (string.IsNullOrEmpty(filenames))
                return BadRequest("No filenames provided.");

            var fileNamesArray = filenames.Split('|');
            var uploadsFolderPath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder);

            var tempPath = Path.Combine(Path.GetTempPath(), $"lebiru-{Guid.NewGuid():N}.zip");
            try
            {
                await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite,
                                 FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
                {
                    foreach (var fileName in fileNamesArray)
                    {
                        var filePath = Path.Combine(uploadsFolderPath, fileName);
                        if (System.IO.File.Exists(filePath))
                        {
                            var zipEntry = archive.CreateEntry(fileName, CompressionLevel.Fastest);
                            using (var zipStream = zipEntry.Open())
                            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                            {
                                await fileStream.CopyToAsync(zipStream, HttpContext.RequestAborted);
                            }
                        }
                    }
                }

                // Count each file in the zip as a download
                var fileList = filenames.Split('|', StringSplitOptions.RemoveEmptyEntries);
                foreach (var _ in fileList)
                {
                    _metricsService.IncrementDownloadCount();
                }

                HttpContext.Response.OnCompleted(() =>
                {
                    try { System.IO.File.Delete(tempPath); } catch (IOException) { }
                    return Task.CompletedTask;
                });
                return PhysicalFile(tempPath, "application/zip", "LebiruFiles.zip");
            }
            catch
            {
                try { System.IO.File.Delete(tempPath); } catch (IOException) { }
                throw;
            }
        }

        /// <summary>
        /// Retrieves available space on the server.
        /// </summary>
        /// <returns>Information about available space.</returns>
        [HttpGet("AvailableSpace")]
        public IActionResult AvailableSpace()
        {
            var serverSpaceInfo = GetServerSpaceInfo();
            var usedSpacePercent = (double)serverSpaceInfo.UsedSpace / serverSpaceInfo.TotalSpace * 100;

            var response = new
            {
                TotalSpace = FormatBytes(serverSpaceInfo.TotalSpace),
                FreeSpace = FormatBytes(serverSpaceInfo.FreeSpace),
                UsedSpace = FormatBytes(serverSpaceInfo.UsedSpace),
                UsedSpacePercent = Math.Round(usedSpacePercent, 2),
                WarningThresholdPercent = _config.WarningThresholdPercent,
                CriticalThresholdPercent = _config.CriticalThresholdPercent,
                Status = usedSpacePercent >= _config.CriticalThresholdPercent ? "critical" :
                        usedSpacePercent >= _config.WarningThresholdPercent ? "warning" : "normal"
            };

            return Ok(response);
        }

        /// <summary>
        /// Syncs the file metadata with the actual files in the uploads directory.
        /// This ensures all files are properly tracked, including those created by other processes (like the TransformController).
        /// </summary>
        [HttpPost("SyncFileMetadata")]
        public IActionResult SyncFileMetadata()
        {
            try
            {
                var existingFiles = FileInfos;
                var existingFilePaths = existingFiles.Select(f => f.FilePath).ToHashSet();

                var uploadsDirectory = new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder));
                if (!uploadsDirectory.Exists)
                {
                    uploadsDirectory.Create();
                    _logger?.LogInformation("Created uploads directory at {Path}", uploadsDirectory.FullName);
                }

                var filesOnDisk = uploadsDirectory.GetFiles();
                int addedCount = 0;

                foreach (var fileInfo in filesOnDisk)
                {
                    // If the file isn't in our metadata, add it
                    if (!existingFilePaths.Contains(fileInfo.FullName))
                    {
                        var newFile = new Models.FileInfo
                        {
                            FileName = fileInfo.Name,
                            FilePath = fileInfo.FullName,
                            FileSize = fileInfo.Length,
                            UploadTime = fileInfo.CreationTime,
                            ExpiryTime = null // No expiry for newly discovered files
                        };

                        existingFiles.Add(newFile);
                        addedCount++;
                        _logger?.LogInformation("Added missing file to metadata: {FileName}", fileInfo.Name);
                    }
                }

                // Update the file metadata if any files were added
                if (addedCount > 0)
                {
                    FileInfos = existingFiles;
                    _logger?.LogInformation("Synced file metadata: {AddedCount} files added", addedCount);
                }

                return Json(new { success = true, addedCount });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error syncing file metadata");
                return Json(new { success = false, error = ex.Message });
            }
        }

        private ServerSpaceInfo GetServerSpaceInfo()
        {
            // Calculate total space used by uploaded files
            long usedSpace = _metadataStore?.UsedSpace ?? 0;
            if (_metadataStore == null)
            {
            var uploadsDirectory = new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder));
            if (uploadsDirectory.Exists)
            {
                // Get a fresh list of files to ensure we have current data
                var files = uploadsDirectory.GetFiles();
                foreach (var file in files)
                {
                    try
                    {
                        // Refresh the file info to get current size
                        file.Refresh();
                        // Try to open file to ensure we can access it
                        using (var fs = file.OpenRead())
                        {
                            usedSpace += fs.Length;
                        }
                    }
                    catch (IOException)
                    {
                        // If we can't access the file, use the last known size
                        usedSpace += file.Length;
                    }
                }
            }
            }

            // Convert configured GB to bytes (ensure we use long for large numbers)
            long maxDiskSpaceBytes = _config.MaxDiskSpaceGB * 1024L * 1024L * 1024L;

            return new ServerSpaceInfo(maxDiskSpaceBytes)
            {
                UsedSpace = usedSpace
            };

        }

        /// <summary>
        /// Gets the name of the server hosting the application
        /// </summary>
        /// <returns>The server name or an error message if retrieval fails</returns>
        [HttpGet("ServerName")]
        public IActionResult GetServerName()
        {
            try
            {
                var serverName = Environment.MachineName; // Get the server name
                return Ok(serverName);
            }
            catch (Exception)
            {
                return StatusCode(500, "An error occurred while retrieving the server name.");
            }
        }

        /// <summary>
        /// Renames a file on the server and updates all references
        /// </summary>
        /// <param name="oldFilename">The current name of the file</param>
        /// <param name="newFilename">The new name for the file</param>
        /// <returns>Success or error message</returns>
        [HttpPost("RenameFile")]
        public IActionResult RenameFile([FromForm] string oldFilename, [FromForm] string newFilename)
        {
            try
            {
                // Validate input
                if (string.IsNullOrWhiteSpace(newFilename))
                {
                    return BadRequest("New filename cannot be empty.");
                }

                // Ensure the new filename has the same extension to prevent type changing
                var oldExtension = Path.GetExtension(oldFilename);
                var newExtension = Path.GetExtension(newFilename);
                if (string.IsNullOrEmpty(newExtension))
                {
                    // If no extension provided, add the old one
                    newFilename = newFilename + oldExtension;
                }
                else if (!oldExtension.Equals(newExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Changing file extension is not allowed. New filename must have the same extension as the original.");
                }

                var oldFilePath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder, oldFilename);
                var newFilePath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder, newFilename);

                // Check if source file exists
                if (!System.IO.File.Exists(oldFilePath))
                {
                    return NotFound($"File '{oldFilename}' not found.");
                }

                // Check if target file already exists
                if (System.IO.File.Exists(newFilePath))
                {
                    return BadRequest($"File '{newFilename}' already exists. Please choose a different name.");
                }

                // Check if user has permission to modify the file
                var username = User.Identity?.Name;
                if (username == null)
                {
                    return Unauthorized();
                }

                var userRole = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Role)?.Value;
                if (userRole != UserRoles.Admin && !_userService.IsFileOwner(username, oldFilePath))
                {
                    return Forbid();
                }

                // Rename the physical file
                System.IO.File.Move(oldFilePath, newFilePath);

                // Update file references in userInfo.json
                _userService.UpdateFilePath(oldFilePath, newFilePath);

                // Update fileInfo.json
                var fileInfos = FileInfos;
                var fileInfo = fileInfos.FirstOrDefault(f => f.FileName == oldFilename);
                if (fileInfo != null)
                {
                    fileInfo.FileName = newFilename;
                    fileInfo.FilePath = newFilePath;
                    FileInfos = fileInfos;
                }

                return Ok(new { message = "File renamed successfully", newFilename });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"An error occurred while renaming the file: {ex.Message}");
            }
        }

        /// <summary>
        /// Deletes a specific file from the server
        /// </summary>
        /// <param name="filename">The name of the file to delete</param>
        /// <returns>Success or error message</returns>
        [HttpPost("DeleteFile")]
        public IActionResult DeleteFile([FromForm] string filename)
        {
            try
            {
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder, filename);
                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound($"File '{filename}' not found.");
                }

                // Check if user has permission to delete the file
                var username = User.Identity?.Name;
                if (username == null)
                {
                    return Unauthorized();
                }

                var userRole = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Role)?.Value;
                if (userRole != UserRoles.Admin && !_userService.IsFileOwner(username, filePath))
                {
                    return Forbid();
                }

                // Delete the physical file
                System.IO.File.Delete(filePath);

                // Update user file ownership
                _userService.RemoveFileFromUser(filePath);

                // Update fileInfo.json
                var fileInfos = FileInfos;
                fileInfos.RemoveAll(f => f.FileName == filename);
                FileInfos = fileInfos;

                // Increment delete counter
                _metricsService.IncrementDeleteCount();

                return Ok();
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"An error occurred while deleting the file: {ex.Message}");
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
            int counter = 0;
            decimal number = bytes;

            // Convert without rounding until we reach the final unit
            while (number >= 1024)
            {
                number /= 1024;
                counter++;
            }

            // Use n2 format for MB and above, n0 for KB and below
            string format = counter >= 2 ? "n2" : "n0";
            return $"{number.ToString(format)} {suffixes[counter]}";
        }

        /// <summary>
        /// Calculate SHA-256 checksum for a specified file
        /// </summary>
        /// <param name="filename">The name of the file to calculate the checksum for</param>
        /// <returns>SHA-256 checksum of the file as a string</returns>
        private string CalculateSha256Checksum(string filename)
        {
            try
            {
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder, filename);
                if (!System.IO.File.Exists(filePath))
                {
                    return string.Empty;
                }

                using (var stream = System.IO.File.OpenRead(filePath))
                using (var sha256 = SHA256.Create())
                {
                    var hash = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Get the SHA-256 checksum for a file
        /// </summary>
        /// <param name="filename">The name of the file</param>
        /// <returns>JSON object containing the filename and its SHA-256 checksum</returns>
        [HttpGet("Checksum")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetChecksum(string filename)
        {
            if (string.IsNullOrEmpty(filename))
            {
                return BadRequest("Filename cannot be empty");
            }

            var checksum = CalculateSha256Checksum(filename);
            if (string.IsNullOrEmpty(checksum))
            {
                return NotFound($"File '{filename}' not found or could not be accessed");
            }

            return Json(new { filename, checksum });
        }

        /// <summary>
        /// The dedicated upload page for the app. Provides a user-friendly interface for uploading files.
        /// </summary>
        /// <returns>The upload view</returns>
        [HttpGet("Upload")]
        public IActionResult UploadPage()
        {
            ViewBag.ExpiryOptions = Enum.GetValues(typeof(ExpiryOption));
            ViewBag.MaxFileSizeMB = _config.MaxFileSizeMB;
            ViewBag.MaxDiskSpaceGB = _config.MaxDiskSpaceGB;
            ViewBag.IsDarkMode = Request.Cookies.ContainsKey("darkMode") && Request.Cookies["darkMode"] == "true";
            return View("Upload");
        }

        /// <summary>
        /// Update session values for pagination preferences
        /// </summary>
        [HttpPost("UpdateSession")]
        public IActionResult UpdateSession([FromBody] PaginationRequest request)
        {
            //Console.WriteLine($"UpdateSession called: page={request.page}, itemsPerPage={request.itemsPerPage}");
            HttpContext.Session.SetInt32("CurrentPage", request.page);
            HttpContext.Session.SetInt32("ItemsPerPage", request.itemsPerPage);

            // Log the values that were actually stored
            var storedPage = HttpContext.Session.GetInt32("CurrentPage");
            var storedItems = HttpContext.Session.GetInt32("ItemsPerPage");
            //Console.WriteLine($"Session values set: CurrentPage={storedPage}, ItemsPerPage={storedItems}");

            return Ok(new { success = true, currentPage = storedPage, itemsPerPage = storedItems });
        }

        #region Fetch Functionality

        /// <summary>
        /// Display the fetch sources management page
        /// </summary>
        /// <returns>The fetch view with fetch sources information</returns>
        [HttpGet("Fetch")]
        public IActionResult Fetch()
        {
            var model = new FetchViewModel
            {
                FetchSources = GetFetchSources(),
                LatestActivities = GetLatestFetchActivities(10) // Get last 10 activities
            };

            return View("Fetch", model);
        }

        /// <summary>
        /// Display the form to add a new fetch source
        /// </summary>
        /// <returns>The add fetch source view</returns>
        [HttpGet("AddFetchSource")]
        public IActionResult AddFetchSource()
        {
            // Return a new empty model with some defaults
            var model = new FetchSourceModel
            {
                Type = "Gmail",
                IsActive = true,
                UsePassiveFtp = true,
                FetchIntervalMinutes = 60,
                IsRecursive = false,
                DeleteAfterFetch = false,
                EmailAgeInDays = 30
            };

            return View("AddFetchSource", model);
        }

        /// <summary>
        /// Process the form submission to add or update a fetch source
        /// </summary>
        /// <returns>Redirects to the fetch sources list</returns>
        [HttpPost("SaveFetchSource")]
        [ValidateAntiForgeryToken]
        [Consumes("multipart/form-data", "application/x-www-form-urlencoded")]
        public IActionResult SaveFetchSource()
        {
            // Check if this is an edit (ID is provided)
            string formId = Request.Form["Id"].ToString();
            bool isEditing = !string.IsNullOrEmpty(formId);

            // Get existing sources
            var sources = GetFetchSources();
            FetchSourceModel? existingSource = null;

            if (isEditing)
            {
                // Find the existing source
                existingSource = sources.FirstOrDefault(s => s.Id == formId);
                if (existingSource == null)
                {
                    TempData["ErrorMessage"] = "Fetch source not found for editing.";
                    return RedirectToAction("Fetch");
                }
            }

            // Create a new model and manually bind form values to handle checkboxes properly
            var model = new FetchSourceModel
            {
                Name = Request.Form["Name"].ToString(),
                Type = Request.Form["Type"].ToString(),
                // Make ServerUrl conditional for Gmail type
                ServerUrl = Request.Form["Type"] == "Gmail" ? "gmail.googleapis.com" : Request.Form["ServerUrl"].ToString(),
                Username = Request.Form["Username"].ToString(),
                Password = Request.Form["Password"].ToString(),
                RemotePath = Request.Form["RemotePath"].ToString(),
                Port = int.TryParse(Request.Form["Port"], out int port) ? port : 0,
                FilePattern = Request.Form["FilePattern"].ToString(),

                // Handle checkboxes properly - explicitly check for their presence
                IsRecursive = Request.Form.Keys.Contains("IsRecursive"),
                DeleteAfterFetch = Request.Form.Keys.Contains("DeleteAfterFetch"),
                IsActive = Request.Form.Keys.Contains("IsActive"),
                UsePassiveFtp = Request.Form.Keys.Contains("UsePassiveFtp"),
                IgnoreSslErrors = Request.Form.Keys.Contains("IgnoreSslErrors"),
                MarkAsRead = Request.Form.Keys.Contains("MarkAsRead"),
                ArchiveAfterFetch = Request.Form.Keys.Contains("ArchiveAfterFetch"),
                IncludeEmailBody = Request.Form.Keys.Contains("IncludeEmailBody"),

                // Parse other numeric values
                FetchIntervalMinutes = int.TryParse(Request.Form["FetchIntervalMinutes"], out int interval) && interval > 0 ? interval : 60,
                EmailAgeInDays = int.TryParse(Request.Form["EmailAgeInDays"], out int age) && age > 0 ? age : 30,

                // Get Gmail-specific properties
                EmailSearchQuery = Request.Form["EmailSearchQuery"].ToString(),
                AttachmentTypes = Request.Form["AttachmentTypes"].ToString(),
                OAuthAccessToken = Request.Form["OAuthAccessToken"].ToString(),
                OAuthRefreshToken = Request.Form["OAuthRefreshToken"].ToString()
            };

            // Set the model state to valid initially
            ModelState.Clear();

            // Validate the model manually
            if (string.IsNullOrEmpty(model.Name))
            {
                ModelState.AddModelError("Name", "Name is required");
            }

            if (string.IsNullOrEmpty(model.Type))
            {
                ModelState.AddModelError("Type", "Type is required");
            }

            if (model.FetchIntervalMinutes <= 0)
            {
                ModelState.AddModelError("FetchIntervalMinutes", "Fetch interval must be greater than 0 minutes");
                // Set a default value to avoid CRON errors
                model.FetchIntervalMinutes = 60;
            }

            // For Gmail type, we don't need to validate ServerUrl
            if (model.Type == "Gmail")
            {
                // Ensure ServerUrl is always set for Gmail
                model.ServerUrl = "gmail.googleapis.com";
            }
            else if (string.IsNullOrEmpty(model.ServerUrl))
            {
                // Only validate ServerUrl for non-Gmail sources
                ModelState.AddModelError("ServerUrl", "Server URL is required");
            }

            if (!ModelState.IsValid)
            {
                return View("AddFetchSource", model);
            }

            // For updating existing source
            if (isEditing)
            {
                // Preserve the original ID and creation timestamp
                model.Id = formId;
                model.CreatedAt = existingSource!.CreatedAt;
            }
            else
            {
                // For new sources, generate a new ID and set creation time
                model.Id = Guid.NewGuid().ToString();
                model.CreatedAt = DateTime.UtcNow;
            }

            // For Gmail, check if we have tokens in the session
            if (model.Type == "Gmail" && string.IsNullOrEmpty(model.OAuthAccessToken))
            {
                var accessToken = HttpContext.Session.GetString("GmailOAuthAccessToken");
                var refreshToken = HttpContext.Session.GetString("GmailOAuthRefreshToken");

                if (!string.IsNullOrEmpty(accessToken) && !string.IsNullOrEmpty(refreshToken))
                {
                    model.OAuthAccessToken = accessToken;
                    model.OAuthRefreshToken = refreshToken;
                }
                else
                {
                    ModelState.AddModelError("", "Gmail authorization is required. Please click the 'Authorize with Gmail' button.");
                    return View("AddFetchSource", model);
                }
            }

            // Encrypt password if provided
            if (!string.IsNullOrEmpty(model.Password))
            {
                model.Password = EncryptPassword(model.Password);
            }

            // Encrypt OAuth tokens if provided
            if (!string.IsNullOrEmpty(model.OAuthAccessToken))
            {
                model.OAuthAccessToken = EncryptPassword(model.OAuthAccessToken);
            }

            if (!string.IsNullOrEmpty(model.OAuthRefreshToken))
            {
                model.OAuthRefreshToken = EncryptPassword(model.OAuthRefreshToken);
            }

            // Update existing source or add new one
            if (isEditing)
            {
                // Remove the existing source and add the updated one
                sources.Remove(existingSource!);
                sources.Add(model);

                // Preserve existing tokens if not provided in the form
                if (string.IsNullOrEmpty(model.OAuthAccessToken) && !string.IsNullOrEmpty(existingSource!.OAuthAccessToken))
                {
                    model.OAuthAccessToken = existingSource.OAuthAccessToken;
                }

                if (string.IsNullOrEmpty(model.OAuthRefreshToken) && !string.IsNullOrEmpty(existingSource!.OAuthRefreshToken))
                {
                    model.OAuthRefreshToken = existingSource.OAuthRefreshToken;
                }

                if (string.IsNullOrEmpty(model.Password) && !string.IsNullOrEmpty(existingSource!.Password))
                {
                    model.Password = existingSource.Password;
                }
            }
            else
            {
                // Just add the new source
                sources.Add(model);
            }

            SaveFetchSources(sources);

            // Schedule the fetch job if it's active
            if (model.IsActive && model.FetchIntervalMinutes > 0)
            {
                ScheduleFetchJob(model);
            }

            // Add success message and redirect
            if (HttpContext.RequestServices != null && HttpContext.RequestServices.GetService<ITempDataDictionaryFactory>() != null && isEditing)
            {
                TempData["SuccessMessage"] = $"Fetch source '{model.Name}' was successfully updated.";
            }
            else if (HttpContext.RequestServices != null && HttpContext.RequestServices.GetService<ITempDataDictionaryFactory>() != null)
            {
                TempData["SuccessMessage"] = $"Fetch source '{model.Name}' was successfully added.";
            }
            return RedirectToAction("Fetch");
        }

        /// <summary>
        /// Display the form to edit an existing fetch source
        /// </summary>
        /// <param name="id">The ID of the fetch source to edit</param>
        /// <returns>The edit fetch source view</returns>
        [HttpGet("EditFetchSource/{id}")]
        public IActionResult EditFetchSource(string id)
        {
            var sources = GetFetchSources();
            var source = sources.FirstOrDefault(s => s.Id == id);

            if (source == null)
            {
                if (HttpContext.RequestServices != null && HttpContext.RequestServices.GetService<ITempDataDictionaryFactory>() != null)
                    TempData["ErrorMessage"] = "Fetch source not found.";
                return RedirectToAction("Fetch");
            }

            // Don't send the encrypted password to the client
            source.Password = string.Empty;

            return View("AddFetchSource", source);
        }

        /// <summary>
        /// Process the delete request for a fetch source
        /// </summary>
        /// <param name="id">The ID of the fetch source to delete</param>
        /// <returns>Redirects to the fetch sources list</returns>
        [HttpPost("DeleteFetchSource")]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteFetchSource([FromForm] string id)
        {
            _logger.LogInformation("DeleteFetchSource called with id: {Id}", id ?? "null");

            if (string.IsNullOrEmpty(id))
            {
                TempData["ErrorMessage"] = "Fetch source ID is required.";
                return RedirectToAction("Fetch");
            }

            var sources = GetFetchSources();
            var source = sources.FirstOrDefault(s => s.Id == id);

            if (source == null)
            {
                if (HttpContext.RequestServices != null && HttpContext.RequestServices.GetService<ITempDataDictionaryFactory>() != null)
                    TempData["ErrorMessage"] = "Fetch source not found.";
                return RedirectToAction("Fetch");
            }

            // Remove the source
            sources.Remove(source);
            SaveFetchSources(sources);

            TempData["SuccessMessage"] = $"Fetch source '{source.Name}' was successfully deleted.";
            return RedirectToAction("Fetch");
        }

        /// <summary>
        /// Test the connection to a fetch source
        /// </summary>
        /// <param name="fetchSourceId">The ID of the fetch source to test, or form data for a new source</param>
        /// <returns>JSON result with connection test status</returns>
        [HttpPost("TestFetchConnection")]
        public async Task<IActionResult> TestFetchConnection(string? fetchSourceId = null)
        {
            FetchSourceModel source = new FetchSourceModel();

            // If fetchSourceId is a GUID, look for an existing source
            if (!string.IsNullOrEmpty(fetchSourceId) && Guid.TryParse(fetchSourceId, out _))
            {
                var sources = GetFetchSources();
                var foundSource = sources.FirstOrDefault(s => s.Id == fetchSourceId);
                if (foundSource != null)
                {
                    source = foundSource;
                }

                if (source == null)
                {
                    return Json(new { success = false, message = "Fetch source not found." });
                }
            }
            else
            {
                // Otherwise, bind from form data for a new source being tested
                try
                {
                    // Create a model from form data
                    source = new FetchSourceModel
                    {
                        Name = Request.Form["Name"].ToString() ?? string.Empty,
                        Type = Request.Form["Type"].ToString() ?? string.Empty,
                        ServerUrl = Request.Form["ServerUrl"].ToString() ?? string.Empty,
                        Username = Request.Form["Username"].ToString(),
                        Password = Request.Form["Password"].ToString(),
                        RemotePath = Request.Form["RemotePath"].ToString(),
                        Port = int.TryParse(Request.Form["Port"], out int port) ? port : 0
                    };

                    // Add Gmail specific properties if needed
                    if (source.Type == "Gmail")
                    {
                        source.EmailSearchQuery = Request.Form["EmailSearchQuery"].ToString();
                        source.EmailAgeInDays = int.TryParse(Request.Form["EmailAgeInDays"], out int days) ? days : 30;
                        source.AttachmentTypes = Request.Form["AttachmentTypes"].ToString();
                        source.MarkAsRead = Request.Form["MarkAsRead"] == "on" || Request.Form["MarkAsRead"] == "true";
                        source.ArchiveAfterFetch = Request.Form["ArchiveAfterFetch"] == "on" || Request.Form["ArchiveAfterFetch"] == "true";
                        source.IncludeEmailBody = Request.Form["IncludeEmailBody"] == "on" || Request.Form["IncludeEmailBody"] == "true";

                        // Get OAuth tokens
                        source.OAuthAccessToken = Request.Form["OAuthAccessToken"].ToString();
                        source.OAuthRefreshToken = Request.Form["OAuthRefreshToken"].ToString();
                    }
                }
                catch
                {
                    return Json(new { success = false, message = "Invalid form data." });
                }
            }

            // Test the connection based on the type
            try
            {
                switch (source.Type)
                {
                    case "Gmail":
                        return await TestGmailConnection(source);
                    case "FTP":
                        return TestFtpConnection(source);
                    case "SFTP":
                        return Json(new { success = false, message = "SFTP connection testing not implemented yet." });
                    case "HTTP":
                    case "WebDAV":
                        return await TestHttpConnection(source);
                    case "NetworkShare":
                        return Json(new { success = false, message = "Network share testing not implemented yet." });
                    default:
                        return Json(new { success = false, message = "Unknown source type." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing connection to {Type} source at {Url}", source.Type, source.ServerUrl);
                return Json(new { success = false, message = $"Connection test failed: {ex.Message}" });
            }
        }

        /// <summary>
        /// Execute a fetch operation for a specific source
        /// </summary>
        /// <param name="fetchSourceId">The ID of the fetch source</param>
        /// <returns>Redirects to the fetch sources list</returns>
        [HttpPost("ExecuteFetch")]
        [ValidateAntiForgeryToken]
        public IActionResult ExecuteFetch([FromForm] string fetchSourceId)
        {
            // Log that we've received a request
            Console.WriteLine($"ExecuteFetch called with fetchSourceId: {fetchSourceId ?? "null"}");

            if (string.IsNullOrEmpty(fetchSourceId))
            {
                TempData["ErrorMessage"] = "Fetch source ID is required.";
                return RedirectToAction("Fetch");
            }

            var sources = GetFetchSources();
            var source = sources.FirstOrDefault(s => s.Id == fetchSourceId);

            if (source == null) return RedirectToAction("Fetch");

            try
            {
                // Queue a background job to fetch files
                _backgroundJobClient.Enqueue<FileController>(x => x.FetchFilesFromSource(source.Id));

                TempData["SuccessMessage"] = $"Fetch operation for '{source.Name}' has been scheduled.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scheduling fetch for source {Name} ({Id})", source.Name, source.Id);
                TempData["ErrorMessage"] = $"Failed to schedule fetch: {ex.Message}";
            }

            return RedirectToAction("Fetch");
        }

        #region Fetch Helper Methods

        /// <summary>
        /// Get the list of fetch sources from storage
        /// </summary>
        /// <returns>List of fetch source models</returns>
        private List<FetchSourceModel> GetFetchSources()
        {
            var filePath = Path.Combine(DataFolder, "fetchSources.json");
            if (!System.IO.File.Exists(filePath))
            {
                return new List<FetchSourceModel>();
            }

            int retries = 5;
            Exception? lastException = null;

            for (int i = 0; i < retries; i++)
            {
                try
                {
                    // Use FileShare.ReadWrite to allow concurrent readers
                    using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fileStream))
                    {
                        var json = reader.ReadToEnd();
                        var sources = System.Text.Json.JsonSerializer.Deserialize<List<FetchSourceModel>>(json);
                        return sources ?? new List<FetchSourceModel>();
                    }
                }
                catch (IOException ex)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Failed to read fetch sources (attempt {Attempt} of {MaxRetries})", i + 1, retries);

                    // Wait before retrying
                    if (i < retries - 1)
                    {
                        // Retry immediately; file operations are short and blocking a request thread worsens saturation.
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogError(ex, "Error deserializing fetch sources JSON");
                    return new List<FetchSourceModel>();
                }
            }

            _logger.LogError(lastException, "Failed to read fetch sources after {Retries} attempts", retries);
            return new List<FetchSourceModel>();
        }

        /// <summary>
        /// Save the list of fetch sources to storage
        /// </summary>
        /// <param name="sources">The list of fetch sources to save</param>
        private void SaveFetchSources(List<FetchSourceModel> sources)
        {
            var filePath = Path.Combine(DataFolder, "fetchSources.json");
            var json = System.Text.Json.JsonSerializer.Serialize(sources);

            // Use a file lock to prevent concurrent access issues
            int retries = 5;
            Exception? lastException = null;

            for (int i = 0; i < retries; i++)
            {
                try
                {
                    // Use FileShare.Read to allow other processes to read the file while we're writing
                    using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(fileStream))
                    {
                        writer.Write(json);
                        writer.Flush();
                        return; // Success, exit the method
                    }
                }
                catch (IOException ex)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Failed to save fetch sources (attempt {Attempt} of {MaxRetries})", i + 1, retries);

                    // Wait before retrying
                    if (i < retries - 1)
                    {
                        // Retry immediately; file operations are short and blocking a request thread worsens saturation.
                    }
                }
            }

            _logger.LogError(lastException, "Failed to save fetch sources after {Retries} attempts", retries);
            throw new InvalidOperationException("Could not save fetch sources due to file access issues", lastException);
        }

        /// <summary>
        /// Get the list of recent fetch activities
        /// </summary>
        /// <param name="count">Maximum number of activities to return</param>
        /// <returns>List of fetch activity models</returns>
        private List<FetchActivityModel> GetLatestFetchActivities(int count)
        {
            var filePath = Path.Combine(DataFolder, "fetchActivities.json");
            if (!System.IO.File.Exists(filePath))
            {
                return new List<FetchActivityModel>();
            }

            try
            {
                var json = System.IO.File.ReadAllText(filePath);
                var activities = System.Text.Json.JsonSerializer.Deserialize<List<FetchActivityModel>>(json) ?? new List<FetchActivityModel>();
                return activities.OrderByDescending(a => a.Timestamp).Take(count).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading fetch activities");
                return new List<FetchActivityModel>();
            }
        }

        /// <summary>
        /// Add a new fetch activity record
        /// </summary>
        /// <param name="activity">The activity to add</param>
        private void AddFetchActivity(FetchActivityModel activity)
        {
            var filePath = Path.Combine(DataFolder, "fetchActivities.json");
            var activities = new List<FetchActivityModel>();

            if (System.IO.File.Exists(filePath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(filePath);
                    activities = System.Text.Json.JsonSerializer.Deserialize<List<FetchActivityModel>>(json) ?? new List<FetchActivityModel>();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading fetch activities for adding new activity");
                }
            }

            // Add new activity
            activities.Add(activity);

            // Keep only the last 100 activities
            if (activities.Count > 100)
            {
                activities = activities.OrderByDescending(a => a.Timestamp).Take(100).ToList();
            }

            // Save back
            var newJson = System.Text.Json.JsonSerializer.Serialize(activities);
            System.IO.File.WriteAllText(filePath, newJson);
        }

        /// <summary>
        /// Schedule a periodic fetch job for a source
        /// </summary>
        /// <param name="source">The fetch source to schedule</param>
        private void ScheduleFetchJob(FetchSourceModel source)
        {
            if (source.IsActive && source.FetchIntervalMinutes > 0)
            {
                // Calculate appropriate CRON expression based on interval
                string cronExpression;

                // Ensure the interval is at least 1 minute
                int interval = Math.Max(1, source.FetchIntervalMinutes);

                if (interval < 60)
                {
                    // For less than hourly intervals, use minutes (between 1-59)
                    cronExpression = $"*/{interval} * * * *";
                }
                else if (interval < 1440) // Less than daily
                {
                    // For hourly or multi-hour intervals
                    int hours = interval / 60;
                    int minutes = interval % 60;

                    if (minutes == 0)
                    {
                        // Exact hours
                        cronExpression = hours == 1 ? "0 * * * *" : $"0 */{hours} * * *";
                    }
                    else
                    {
                        // Start at minute 0 and repeat at specific minute intervals
                        // For non-standard intervals, we'll run at specific minutes of specific hours
                        cronExpression = $"{minutes} */{hours} * * *";
                    }
                }
                else // Daily or more
                {
                    // For daily or multi-day intervals (capped at weekly)
                    int days = Math.Min(7, interval / 1440);

                    if (days == 1)
                    {
                        // Daily at midnight
                        cronExpression = "0 0 * * *";
                    }
                    else if (days == 7)
                    {
                        // Weekly on Sunday
                        cronExpression = "0 0 * * 0";
                    }
                    else
                    {
                        // Every N days
                        cronExpression = $"0 0 */{days} * *";
                    }
                }

                // Create a recurring job with the source ID as the job ID for easy management
                RecurringJob.AddOrUpdate<FileController>(
                    $"fetch_{source.Id}",
                    x => x.FetchFilesFromSource(source.Id),
                    cronExpression
                );
            }
            else
            {
                // Remove any existing job
                RecurringJob.RemoveIfExists($"fetch_{source.Id}");
            }
        }

        /// <summary>
        /// Test an FTP connection
        /// </summary>
        /// <param name="source">The fetch source with FTP connection details</param>
        /// <returns>JSON result with connection test status</returns>
        private IActionResult TestFtpConnection(FetchSourceModel source)
        {
            try
            {
                // Create FTP request
                string url = source.ServerUrl;
                if (!url.StartsWith("ftp://"))
                {
                    url = $"ftp://{url}";
                }

                // Add the path if specified
                if (!string.IsNullOrEmpty(source.RemotePath))
                {
                    url = url.TrimEnd('/') + '/' + source.RemotePath.TrimStart('/');
                }

                // Using modern HttpClient for testing connections
                // Note: This is a simplified simulation for demo purposes
                // In a real application, you would use a proper FTP client library like FluentFTP

                _logger.LogInformation("Testing FTP connection to {url}", url);

                // Simulate successful connection
                // In a real implementation, use a proper FTP client library
                var success = !string.IsNullOrEmpty(url);

                if (success)
                {
                    return Json(new { success = true, message = $"Connected successfully to {url}" });
                }
                else
                {
                    return Json(new { success = false, message = $"Failed to connect to {url}" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"FTP Connection failed: {ex.Message}" });
            }
        }

        /// <summary>
        /// Test an HTTP connection
        /// </summary>
        /// <param name="source">The fetch source with HTTP connection details</param>
        /// <returns>JSON result with connection test status</returns>
        private async Task<IActionResult> TestHttpConnection(FetchSourceModel source)
        {
            if (_httpClientFactory == null)
            {
                dynamic unavailable = new ExpandoObject();
                unavailable.success = false;
                unavailable.message = "HTTP client factory is unavailable.";
                return Json(unavailable);
            }
            try
            {
                string url = source.ServerUrl;
                if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                {
                    url = $"https://{url}";
                }

                // Add the path if specified
                if (!string.IsNullOrEmpty(source.RemotePath))
                {
                    url = url.TrimEnd('/') + '/' + source.RemotePath.TrimStart('/');
                }

                using (var handler = new HttpClientHandler())
                {
                    // Ignore SSL errors if requested
                    if (source.IgnoreSslErrors)
                    {
                        handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
                    }

                    // Add credentials if provided
                    if (!string.IsNullOrEmpty(source.Username))
                    {
                        handler.Credentials = new NetworkCredential(source.Username, source.Password);
                    }

                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(10);
                        var response = await client.GetAsync(url, HttpContext.RequestAborted);
                        return Json(new
                        {
                            success = response.IsSuccessStatusCode,
                            message = response.IsSuccessStatusCode
                                ? $"Connected successfully. Status: {response.StatusCode}"
                                : $"HTTP Error: {response.StatusCode}"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"HTTP Connection failed: {ex.Message}" });
            }
        }

        /// <summary>
        /// Encrypt a password for secure storage
        /// </summary>
        /// <param name="password">The plaintext password to encrypt</param>
        /// <returns>Encrypted password string</returns>
        private string EncryptPassword(string password)
        {
            try
            {
                // Simple encryption for demo purposes
                // In a real application, use a more secure method
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(password);
                return Convert.ToBase64String(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Decrypt a stored password
        /// </summary>
        /// <param name="encryptedPassword">The encrypted password string</param>
        /// <returns>Decrypted plaintext password</returns>
        private string DecryptPassword(string encryptedPassword)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(encryptedPassword);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Fetch files from a specified source
        /// This method is called by Hangfire jobs
        /// </summary>
        /// <param name="sourceId">The ID of the fetch source</param>
        public async Task FetchFilesFromSource(string sourceId)
        {
            var sources = GetFetchSources();
            var source = sources.FirstOrDefault(s => s.Id == sourceId);

            if (source == null)
            {
                _logger.LogError("Fetch source with ID {SourceId} not found", sourceId);
                return;
            }

            var activity = new FetchActivityModel
            {
                Id = Guid.NewGuid().ToString(),
                FetchSourceId = sourceId,
                FetchSourceName = source.Name,
                Timestamp = DateTime.UtcNow,
                Status = FetchStatus.InProgress
            };

            try
            {
                AddFetchActivity(activity);

                // Execute the fetch based on the source type
                switch (source.Type)
                {
                    case "Gmail":
                        await FetchFromGmail(source, activity);
                        break;
                    case "FTP":
                        FetchFromFtp(source, activity);
                        break;
                    case "SFTP":
                        activity.Status = FetchStatus.Failed;
                        activity.Message = "SFTP fetching not implemented yet";
                        break;
                    case "HTTP":
                    case "WebDAV":
                        FetchFromHttp(source, activity);
                        break;
                    case "NetworkShare":
                        activity.Status = FetchStatus.Failed;
                        activity.Message = "Network share fetching not implemented yet";
                        break;
                    default:
                        activity.Status = FetchStatus.Failed;
                        activity.Message = "Unknown source type";
                        break;
                }
            }
            catch (Exception ex)
            {
                activity.Status = FetchStatus.Failed;
                activity.Message = $"Error: {ex.Message}";
                _logger.LogError(ex, "Error fetching from {Type} source {Name}", source.Type, source.Name);
            }
            finally
            {
                // Update the activity with final status
                if (activity.Status == FetchStatus.InProgress)
                {
                    activity.Status = FetchStatus.Failed;
                    activity.Message = "Fetch did not complete properly";
                }

                // Update the source's last fetch time and count
                source.LastFetchTime = DateTime.UtcNow;
                source.LastFetchFileCount = activity.FetchedFileCount;
                SaveFetchSources(sources);

                // Update the activity record
                AddFetchActivity(activity);
            }
        }

        /// <summary>
        /// Fetch files from an FTP server
        /// </summary>
        /// <param name="source">The fetch source with FTP details</param>
        /// <param name="activity">The activity record to update</param>
        private void FetchFromFtp(FetchSourceModel source, FetchActivityModel activity)
        {
            // TODO: Implement FTP fetching
            activity.Status = FetchStatus.Success;
            activity.Message = "FTP fetching simulation successful";
            activity.FetchedFileCount = 0;
        }

        /// <summary>
        /// Fetch files from an HTTP URL
        /// </summary>
        /// <param name="source">The fetch source with HTTP details</param>
        /// <param name="activity">The activity record to update</param>
        private void FetchFromHttp(FetchSourceModel source, FetchActivityModel activity)
        {
            // TODO: Implement HTTP fetching
            activity.Status = FetchStatus.Success;
            activity.Message = "HTTP fetching simulation successful";
            activity.FetchedFileCount = 0;
        }

        #endregion

        #region Gmail OAuth

        /// <summary>
        /// Test a Gmail connection using OAuth tokens
        /// </summary>
        /// <param name="source">The Gmail fetch source to test</param>
        /// <returns>Json result indicating success or failure</returns>
        private async Task<IActionResult> TestGmailConnection(FetchSourceModel source)
        {
            try
            {
                // Check if we have OAuth tokens
                var accessToken = source.OAuthAccessToken;

                if (string.IsNullOrEmpty(accessToken))
                {
                    // Check if we have tokens in the session
                    accessToken = HttpContext.Session.GetString("GmailOAuthAccessToken");
                }

                if (string.IsNullOrEmpty(accessToken))
                {
                    return Json(new
                    {
                        success = false,
                        message = "Gmail authorization required. Please click 'Authorize with Gmail' button."
                    });
                }

                // Try to validate the tokens by making a call to Gmail API
                using var client = CreateHttpClient("GmailApi");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", accessToken);

                // Log the token for debugging (just the first few chars)
                if (!string.IsNullOrEmpty(accessToken) && accessToken.Length > 10)
                {
                    _logger.LogInformation("Testing Gmail connection with token starting with: {TokenPrefix}...",
                        accessToken.Substring(0, 10));
                }

                // Make a simple call to Gmail API to test the connection
                var response = await client.GetAsync("https://www.googleapis.com/gmail/v1/users/me/profile");

                // Get detailed response content for debugging
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogInformation("Gmail API response code: {StatusCode}", response.StatusCode);

                if (response.IsSuccessStatusCode)
                {
                    // Parse the response to get user info for a more personalized message
                    try
                    {
                        var profileData = JsonSerializer.Deserialize<JsonElement>(responseContent);
                        string email = "";

                        if (profileData.TryGetProperty("emailAddress", out var emailElement))
                        {
                            email = emailElement.GetString() ?? "";
                        }

                        var successMessage = string.IsNullOrEmpty(email)
                            ? "Successfully connected to Gmail!"
                            : $"Successfully connected to Gmail account: {email}";

                        return Json(new { success = true, message = successMessage });
                    }
                    catch
                    {
                        // Fall back to generic message if parsing fails
                        return Json(new { success = true, message = "Successfully connected to Gmail!" });
                    }
                }

                // If token is expired, we could try refreshing it
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("Gmail authentication failed: Unauthorized (401). Token may be expired.");
                    return Json(new
                    {
                        success = false,
                        message = "Gmail authorization token is expired or invalid. Please re-authorize your Gmail account.",
                        requireReauth = true
                    });
                }

                // Handle specific common error cases with friendly messages
                string errorMessage = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Forbidden => "Access denied by Gmail. Your account may have restricted access or 2-factor authentication requirements.",
                    System.Net.HttpStatusCode.BadRequest => "Invalid request to Gmail API. Please check the connection details and try again.",
                    System.Net.HttpStatusCode.NotFound => "Gmail API endpoint not found. The service may be temporarily unavailable.",
                    System.Net.HttpStatusCode.InternalServerError => "Gmail service error. Please try again later.",
                    _ => $"Failed to connect to Gmail. Status: {response.StatusCode}"
                };

                _logger.LogWarning("Gmail API test connection failed: {StatusCode}, Response: {Response}",
                    response.StatusCode, responseContent);

                return Json(new
                {
                    success = false,
                    message = errorMessage,
                    details = responseContent.Length > 200 ? responseContent.Substring(0, 200) + "..." : responseContent
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing Gmail connection");

                // Provide more specific error messages based on exception type
                string errorMessage = ex switch
                {
                    HttpRequestException httpEx => $"Network error connecting to Gmail: {httpEx.Message}",
                    TaskCanceledException => "Connection to Gmail timed out. Please check your network and try again.",
                    JsonException jsonEx => $"Error parsing Gmail response: {jsonEx.Message}",
                    _ => $"Error testing Gmail connection: {ex.Message}"
                };

                return Json(new
                {
                    success = false,
                    message = errorMessage,
                    exceptionType = ex.GetType().Name
                });
            }
        }

        /// <summary>
        /// Redirects to the Gmail OAuth controller to initiate the authorization flow
        /// </summary>
        [HttpGet("GmailOAuth")]
        public IActionResult GmailOAuth()
        {
            return RedirectToAction("Authorize", "GmailOAuth");
        }

        /// <summary>
        /// Gets the OAuth status from the session for the Gmail connection
        /// </summary>
        [HttpGet("GetGmailOAuthStatus")]
        public IActionResult GetGmailOAuthStatus()
        {
            // Check for OAuth tokens in the session
            var accessToken = HttpContext.Session.GetString("GmailOAuthAccessToken");
            var refreshToken = HttpContext.Session.GetString("GmailOAuthRefreshToken");

            if (!string.IsNullOrEmpty(accessToken) && !string.IsNullOrEmpty(refreshToken))
            {
                return Json(new
                {
                    success = true,
                    accessToken,
                    refreshToken
                });
            }

            return Json(new { success = false });
        }

        /// <summary>
        /// Fetch files from Gmail using the Gmail API
        /// </summary>
        /// <param name="source">The fetch source with Gmail details</param>
        /// <param name="activity">The activity record to update</param>
        /// <returns>A Task that completes when fetching is done</returns>
        private async Task FetchFromGmail(FetchSourceModel source, FetchActivityModel activity)
        {
            try
            {
                // Log the start of the operation
                _logger.LogInformation("Starting Gmail fetch for source {SourceId} ({SourceName})", source.Id, source.Name);
                activity.Message = "Starting Gmail fetch...";

                // Initialize fetch file count
                activity.FetchedFileCount = 0;

                // Log the configuration for debugging
                _logger.LogInformation("Gmail fetch configuration: EmailSearchQuery={Query}, AttachmentTypes={Types}, Age={Age}d, MarkAsRead={MarkAsRead}, ArchiveAfterFetch={Archive}",
                    source.EmailSearchQuery ?? "null",
                    source.AttachmentTypes ?? "null",
                    source.EmailAgeInDays,
                    source.MarkAsRead,
                    source.ArchiveAfterFetch);

                if (string.IsNullOrEmpty(source.OAuthAccessToken) || string.IsNullOrEmpty(source.OAuthRefreshToken))
                {
                    activity.Status = FetchStatus.Failed;
                    activity.Message = "OAuth tokens are missing. Please re-authorize the Gmail account.";
                    return;
                }

                // Decrypt the tokens
                string accessToken = DecryptPassword(source.OAuthAccessToken);
                string refreshToken = DecryptPassword(source.OAuthRefreshToken);

                // Set up HTTP client for Gmail API requests
                using var httpClient = CreateHttpClient("GmailApi");
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                httpClient.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                // Build search query from source configuration
                string searchQuery = BuildGmailSearchQuery(source);
                _logger.LogInformation("Gmail search query: {Query}", searchQuery);

                // Encode the query for URL
                string encodedQuery = Uri.EscapeDataString(searchQuery);

                // Fetch emails matching search criteria
                var response = await httpClient.GetAsync(
                    $"https://www.googleapis.com/gmail/v1/users/me/messages?q={encodedQuery}&maxResults=25");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Gmail API error: {StatusCode}, Content: {Content}",
                        response.StatusCode, errorContent);

                    // Check if token expired (401 Unauthorized)
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        activity.Status = FetchStatus.Failed;
                        activity.Message = "Gmail authorization expired. Please re-authorize the account.";
                        return;
                    }

                    activity.Status = FetchStatus.Failed;
                    activity.Message = $"Gmail API error: {response.StatusCode}";
                    return;
                }

                // Parse the response
                var content = await response.Content.ReadAsStringAsync();
                var messagesResponse = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(content);

                // Check if we have messages
                if (!messagesResponse.TryGetProperty("messages", out var messages))
                {
                    _logger.LogInformation("No emails found matching criteria for source {SourceId}", source.Id);
                    activity.Status = FetchStatus.Success;
                    activity.Message = "No matching emails found.";
                    activity.FetchedFileCount = 0;
                    return;
                }

                // Log the number of emails found
                int emailCount = messages.GetArrayLength();
                _logger.LogInformation("Found {Count} emails matching criteria for source {SourceId}", emailCount, source.Id);

                // Process each email
                int processedCount = 0;
                foreach (var message in messages.EnumerateArray())
                {
                    if (processedCount >= 25) // Safety limit
                        break;

                    string messageId = message.GetProperty("id").GetString() ?? "";
                    if (string.IsNullOrEmpty(messageId))
                        continue;

                    // Fetch full email data
                    var emailResponse = await httpClient.GetAsync(
                        $"https://www.googleapis.com/gmail/v1/users/me/messages/{messageId}?format=full");

                    if (!emailResponse.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to fetch email {MessageId}: {StatusCode}",
                            messageId, emailResponse.StatusCode);
                        continue;
                    }

                    var emailContent = await emailResponse.Content.ReadAsStringAsync();
                    var email = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(emailContent);

                    // Process the email
                    bool processed = await ProcessGmailEmail(email, source, activity, accessToken);
                    if (processed)
                        processedCount++;

                    // Mark as read if configured and successfully processed
                    if (processed && source.MarkAsRead)
                    {
                        await MarkGmailEmailAsRead(messageId, httpClient);
                    }

                    // Archive the email if configured and successfully processed
                    if (processed && source.ArchiveAfterFetch)
                    {
                        await ArchiveGmailEmail(messageId, httpClient);
                    }
                }

                activity.Status = FetchStatus.Success;
                activity.Message = $"Successfully fetched {activity.FetchedFileCount} files from Gmail";
                _logger.LogInformation("Gmail fetch completed for {SourceId}: {FileCount} files fetched",
                    source.Id, activity.FetchedFileCount);
            }
            catch (Exception ex)
            {
                activity.Status = FetchStatus.Failed;
                activity.Message = $"Gmail fetch failed: {ex.Message}";
                _logger.LogError(ex, "Error during Gmail fetch for source {SourceId}", source.Id);
            }
        }

        /// <summary>
        /// Build a Gmail search query from source configuration
        /// </summary>
        /// <param name="source">The fetch source with Gmail details</param>
        /// <returns>Gmail search query string</returns>
        private string BuildGmailSearchQuery(FetchSourceModel source)
        {
            var queryParts = new List<string>();

            // Add user-specified search query if provided
            if (!string.IsNullOrWhiteSpace(source.EmailSearchQuery))
            {
                queryParts.Add($"({source.EmailSearchQuery})");
            }

            // Filter by attachment type if specified
            if (!string.IsNullOrWhiteSpace(source.AttachmentTypes))
            {
                var attachmentTypes = source.AttachmentTypes.Split(',', ';')
                    .Select(t => t.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                if (attachmentTypes.Any())
                {
                    var attachmentFilters = attachmentTypes
                        .Select(t => $"filename:.{t}")
                        .ToList();

                    queryParts.Add($"has:attachment ({string.Join(" OR ", attachmentFilters)})");
                }
                else
                {
                    queryParts.Add("has:attachment");
                }
            }

            // Filter by age
            if (source.EmailAgeInDays > 0)
            {
                queryParts.Add($"newer_than:{source.EmailAgeInDays}d");
            }

            // Always filter for unread if we're going to mark as read
            if (source.MarkAsRead)
            {
                queryParts.Add("is:unread");
            }

            // If no filters specified, default to recent emails with attachments
            if (!queryParts.Any())
            {
                queryParts.Add("has:attachment newer_than:7d");
            }

            return string.Join(" ", queryParts);
        }

        /// <summary>
        /// Process a Gmail email and save any attachments
        /// </summary>
        /// <param name="email">The email data from the Gmail API</param>
        /// <param name="source">The fetch source with Gmail details</param>
        /// <param name="activity">The activity record to update</param>
        /// <param name="accessToken">The OAuth access token for Gmail API</param>
        /// <returns>True if any files were processed from the email</returns>
        private async Task<bool> ProcessGmailEmail(JsonElement email, FetchSourceModel source, FetchActivityModel activity, string accessToken)
        {
            try
            {
                // Extract email data
                string subject = "";
                string from = "";
                string date = "";
                string snippet = "";

                // Get message ID
                string messageId = email.GetProperty("id").GetString() ?? "";

                // Parse headers
                var headers = email.GetProperty("payload").GetProperty("headers").EnumerateArray();
                foreach (var header in headers)
                {
                    string name = header.GetProperty("name").GetString() ?? "";
                    string value = header.GetProperty("value").GetString() ?? "";

                    switch (name.ToLower())
                    {
                        case "subject":
                            subject = value;
                            break;
                        case "from":
                            from = value;
                            break;
                        case "date":
                            date = value;
                            break;
                    }
                }

                // Get snippet (preview of email body)
                if (email.TryGetProperty("snippet", out var snippetElement))
                {
                    snippet = snippetElement.GetString() ?? "";
                }

                // Clean subject for filename
                string safeSubject = string.IsNullOrEmpty(subject) ? "No_Subject" :
                    new string(subject.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

                // Truncate if too long
                if (safeSubject.Length > 50)
                    safeSubject = safeSubject.Substring(0, 50) + "...";

                _logger.LogInformation("Processing email: {Subject}", subject);

                // Get email parts recursively
                var parts = new List<JsonElement>();
                CollectParts(email.GetProperty("payload"), parts);

                // Check if we should include the email body in a text file
                string bodyText = "";
                if (source.IncludeEmailBody)
                {
                    // Extract plaintext body
                    foreach (var part in parts)
                    {
                        if (!part.TryGetProperty("mimeType", out var mimeType))
                            continue;

                        string mimeTypeStr = mimeType.GetString() ?? "";
                        if (mimeTypeStr == "text/plain" && part.TryGetProperty("body", out var body))
                        {
                            if (body.TryGetProperty("data", out var data))
                            {
                                string encodedData = data.GetString() ?? "";
                                if (!string.IsNullOrEmpty(encodedData))
                                {
                                    // Base64 URL encoding uses '-' and '_' instead of '+' and '/'
                                    encodedData = encodedData.Replace('-', '+').Replace('_', '/');
                                    byte[] decodedBytes = Convert.FromBase64String(encodedData);
                                    bodyText = System.Text.Encoding.UTF8.GetString(decodedBytes);
                                    break;
                                }
                            }
                        }
                    }

                    // If we found body text, save it as a file
                    if (!string.IsNullOrEmpty(bodyText))
                    {
                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        string emailFileName = $"Email_{timestamp}_{safeSubject}.txt";
                        string emailFilePath = Path.Combine(UploadsFolder, emailFileName);

                        // Format email content
                        string emailContent = $"From: {from}\nDate: {date}\nSubject: {subject}\n\n{bodyText}";

                        // Save to file
                        await System.IO.File.WriteAllTextAsync(emailFilePath, emailContent);

                        // Add to file system
                        var emailFileInfo = new Models.FileInfo
                        {
                            FileName = emailFileName,
                            FilePath = emailFilePath,
                            UploadTime = DateTime.UtcNow,
                            FileSize = emailContent.Length,
                            Owner = "Gmail Fetch"
                        };

                        var existingFiles = FileInfos;
                        existingFiles.Add(emailFileInfo);
                        FileInfos = existingFiles;

                        activity.FetchedFileCount++;
                    }
                }

                // Find and save attachments
                bool foundAttachments = false;
                foreach (var part in parts)
                {
                    // Check if this part has a filename (attachment)
                    if (!part.TryGetProperty("filename", out var filenameElement))
                        continue;

                    string filename = filenameElement.GetString() ?? "";
                    if (string.IsNullOrEmpty(filename))
                        continue;

                    // Get MIME type
                    string mimeType = "application/octet-stream";
                    if (part.TryGetProperty("mimeType", out var mimeTypeElement))
                    {
                        mimeType = mimeTypeElement.GetString() ?? mimeType;
                    }

                    // Check if attachment matches the filter
                    if (!string.IsNullOrEmpty(source.AttachmentTypes))
                    {
                        var allowedTypes = source.AttachmentTypes.Split(',', ';')
                            .Select(t => t.Trim().ToLower())
                            .Where(t => !string.IsNullOrEmpty(t))
                            .ToList();

                        if (allowedTypes.Any())
                        {
                            string extension = Path.GetExtension(filename).ToLower().TrimStart('.');
                            if (!allowedTypes.Contains(extension))
                            {
                                _logger.LogInformation("Skipping attachment {Filename} - not matching filter", filename);
                                continue;
                            }
                        }
                    }

                    // Ensure unique filename
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string safeFilename = new string(filename.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
                    string uniqueFilename = $"{timestamp}_{safeFilename}";
                    string filePath = Path.Combine(UploadsFolder, uniqueFilename);

                    // Check if attachment data is available
                    if (!part.TryGetProperty("body", out var body) ||
                        !body.TryGetProperty("attachmentId", out var attachmentId))
                    {
                        _logger.LogWarning("Attachment {Filename} has no attachmentId", filename);
                        continue;
                    }

                    string attachmentIdStr = attachmentId.GetString() ?? "";
                    if (string.IsNullOrEmpty(attachmentIdStr))
                        continue;

                    try
                    {
                        // Fetch attachment data
                        using var httpClient = CreateHttpClient("GmailApi");
                        httpClient.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                        var attachmentResponse = await httpClient.GetAsync(
                            $"https://www.googleapis.com/gmail/v1/users/me/messages/{messageId}/attachments/{attachmentIdStr}");

                        if (!attachmentResponse.IsSuccessStatusCode)
                        {
                            _logger.LogWarning("Failed to fetch attachment {Filename}: {StatusCode}",
                                filename, attachmentResponse.StatusCode);
                            continue;
                        }

                        var attachmentContent = await attachmentResponse.Content.ReadAsStringAsync();
                        var attachmentData = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(attachmentContent);

                        if (attachmentData.TryGetProperty("data", out var data))
                        {
                            string encodedData = data.GetString() ?? "";
                            if (!string.IsNullOrEmpty(encodedData))
                            {
                                // Base64 URL encoding uses '-' and '_' instead of '+' and '/'
                                encodedData = encodedData.Replace('-', '+').Replace('_', '/');
                                byte[] fileData = Convert.FromBase64String(encodedData);

                                // Save attachment to file
                                await System.IO.File.WriteAllBytesAsync(filePath, fileData);

                                // Add to file system
                                var fileInfo = new Models.FileInfo
                                {
                                    FileName = uniqueFilename,
                                    FilePath = filePath,
                                    UploadTime = DateTime.UtcNow,
                                    FileSize = fileData.Length,
                                    Owner = "Gmail Fetch"
                                };

                                var existingFiles = FileInfos;
                                existingFiles.Add(fileInfo);
                                FileInfos = existingFiles;

                                activity.FetchedFileCount++;
                                foundAttachments = true;

                                _logger.LogInformation("Saved attachment: {Filename}, Size: {Size} bytes",
                                    uniqueFilename, fileData.Length);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error downloading attachment {Filename}", filename);
                    }
                }

                return foundAttachments || (!string.IsNullOrEmpty(bodyText) && source.IncludeEmailBody);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Gmail email");
                return false;
            }
        }

        /// <summary>
        /// Recursively collect all parts from an email payload
        /// </summary>
        private void CollectParts(JsonElement payload, List<JsonElement> parts)
        {
            // Add the current part
            parts.Add(payload);

            // Check for nested parts
            if (payload.TryGetProperty("parts", out var nestedParts))
            {
                foreach (var part in nestedParts.EnumerateArray())
                {
                    CollectParts(part, parts);
                }
            }
        }

        /// <summary>
        /// Mark a Gmail email as read
        /// </summary>
        private HttpClient CreateHttpClient(string name) =>
            _httpClientFactory?.CreateClient(name) ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private async Task MarkGmailEmailAsRead(string messageId, HttpClient httpClient)
        {
            try
            {
                var modifyRequest = new
                {
                    removeLabelIds = new[] { "UNREAD" }
                };

                var content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(modifyRequest),
                    System.Text.Encoding.UTF8,
                    "application/json");

                var response = await httpClient.PostAsync(
                    $"https://www.googleapis.com/gmail/v1/users/me/messages/{messageId}/modify", content);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to mark email as read: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking email as read");
            }
        }

        /// <summary>
        /// Archive a Gmail email (remove from inbox)
        /// </summary>
        private async Task ArchiveGmailEmail(string messageId, HttpClient httpClient)
        {
            try
            {
                var modifyRequest = new
                {
                    removeLabelIds = new[] { "INBOX" }
                };

                var content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(modifyRequest),
                    System.Text.Encoding.UTF8,
                    "application/json");

                var response = await httpClient.PostAsync(
                    $"https://www.googleapis.com/gmail/v1/users/me/messages/{messageId}/modify", content);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to archive email: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error archiving email");
            }
        }

        #endregion

        #endregion
    }
}

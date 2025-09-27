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

        private static readonly object _fileLock = new object();

        private List<Models.FileInfo> FileInfos
        {
            get
            {
                lock (_fileLock)
                {
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
        public FileController(
            CleanupJob cleanupJob,
            IBackgroundJobClient backgroundJobClient,
            IConfiguration configuration,
            IApiMetricsService metricsService,
            IUserService userService,
            IMimeValidationService mimeValidationService,
            ILogger<FileController> logger)
        {
            _cleanupJob = cleanupJob;
            _backgroundJobClient = backgroundJobClient;
            var dataDir = Path.Combine(Directory.GetCurrentDirectory(), DataFolder);
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }
            _fileInfoPath = Path.Combine(dataDir, "fileInfo.json");
            _config = configuration.GetSection("FileService").Get<FileServiceConfig>() ?? new FileServiceConfig();
            _metricsService = metricsService ?? throw new ArgumentNullException(nameof(metricsService));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _mimeValidationService = mimeValidationService ?? throw new ArgumentNullException(nameof(mimeValidationService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// The home page for the app. Displays current files hosted on FileService.
        /// </summary>
        /// <returns></returns>
        [HttpGet("Home")]
        public IActionResult Index()
        {
            var serverSpaceInfo = GetServerSpaceInfo();
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
        /// <returns>A response indicating the success or failure of the operation.</returns>
        [HttpPost("CreateDoc")]
        [HttpPost("Upload")]
        [Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Contributor}")]
        public async Task<IActionResult> Upload(List<IFormFile> files, [FromForm] ExpiryOption expiryOption = ExpiryOption.Never)
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

            foreach (var file in files)
            {
                var filePath = Path.Combine(uploadsFolderPath, file.FileName);

                // Check if file upload will exceed configured limit
                var totalSpaceUsed = GetTotalSpaceUsed(uploadsFolderPath);
                var maxSpace = _config.MaxDiskSpaceGB * 1024L * 1024L * 1024L;
                if (totalSpaceUsed + file.Length > maxSpace)
                {
                    return BadRequest($"File upload would exceed the maximum allocated space of {_config.MaxDiskSpaceGB} GB.");
                }

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
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

            var memory = new MemoryStream();
            using (var stream = new FileStream(filePath, FileMode.Open))
            {
                stream.CopyTo(memory);
            }
            memory.Position = 0;

            // Only increment download counter for explicit downloads (not views)
            _metricsService.IncrementDownloadCount();

            // Force download by using application/octet-stream
            return File(memory, "application/octet-stream", filename);
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

            using (var memoryStream = new MemoryStream())
            {
                using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
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
                                await fileStream.CopyToAsync(zipStream);
                            }
                        }
                    }
                }

                memoryStream.Seek(0, SeekOrigin.Begin);

                // Count each file in the zip as a download
                var fileList = filenames.Split('|', StringSplitOptions.RemoveEmptyEntries);
                foreach (var _ in fileList)
                {
                    _metricsService.IncrementDownloadCount();
                }

                return File(memoryStream.ToArray(), "application/zip", $"LebiruFiles.zip");
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

        private ServerSpaceInfo GetServerSpaceInfo()
        {
            // Calculate total space used by uploaded files
            long usedSpace = 0;
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
    }
}

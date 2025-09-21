using Lebiru.FileService.Models;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Lebiru.FileService.HangfireJobs;

namespace Lebiru.FileService.Controllers
{
    /// <summary>
    /// Controller for managing file operations including upload, download, and listing
    /// </summary>
    [Route("File")]
    [ApiController]
    [Microsoft.AspNetCore.Authorization.Authorize]
    public class FileController : Controller
    {
        private const string UploadsFolder = "uploads";
        private readonly CleanupJob _cleanupJob;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly string _fileInfoPath;
        private readonly FileServiceConfig _config;

        private List<Models.FileInfo> FileInfos
        {
            get
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
            set
            {
                var json = System.Text.Json.JsonSerializer.Serialize(value);
                System.IO.File.WriteAllText(_fileInfoPath, json);
            }
        }

        /// <summary>
        /// Initializes a new instance of the FileController
        /// </summary>
        /// <param name="cleanupJob">The cleanup job service for managing file cleanup tasks</param>
        /// <param name="backgroundJobClient">The Hangfire background job client</param>
        /// <param name="configuration">The application configuration</param>
        public FileController(CleanupJob cleanupJob, IBackgroundJobClient backgroundJobClient, IConfiguration configuration)
        {
            _cleanupJob = cleanupJob;
            _backgroundJobClient = backgroundJobClient;
            _fileInfoPath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder, "fileInfo.json");
            _config = configuration.GetSection("FileService").Get<FileServiceConfig>() ?? new FileServiceConfig();
        }

        /// <summary>
        /// The home page for the app. Displays current files hosted on FileService.
        /// </summary>
        /// <returns></returns>
        [HttpGet("Home")]
        public IActionResult Index()
        {
            var serverSpaceInfo = GetServerSpaceInfo();
            ViewBag.UsedSpace = FormatBytes(serverSpaceInfo.UsedSpace);
            ViewBag.TotalSpace = FormatBytes(serverSpaceInfo.TotalSpace);
            ViewBag.ExpiryOptions = Enum.GetValues<ExpiryOption>();
            ViewBag.FileCount = FileInfos.Count;

            // Check the Dark Mode setting
            var isDarkMode = HttpContext.Session.GetString("DarkMode") == "true";
            ViewBag.IsDarkMode = isDarkMode;

            return View(FileInfos);
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
        public async Task<IActionResult> Upload(List<IFormFile> files, [FromForm] ExpiryOption expiryOption = ExpiryOption.Never)
        {
            if (files == null || files.Count == 0)
                return BadRequest("No files uploaded.");

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
                    FileSize = file.Length
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
            
            return Ok("File uploaded successfully.");
        }

        private string GetMimeType(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension switch
            {
                ".txt" => "text/plain",
                ".html" => "text/html",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".pdf" => "application/pdf",
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
            return PhysicalFile(filePath, mimeType, enableRangeProcessing: true);
        }

        private long GetTotalSpaceUsed(string directoryPath)
        {
            var directoryInfo = new DirectoryInfo(directoryPath);
            if (!directoryInfo.Exists)
                return 0;

            return directoryInfo.GetFiles().Sum(file => file.Length);
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
            BackgroundJob.Enqueue(() => _cleanupJob.Execute(null!));
            BackgroundJob.Enqueue<ExpiryJob>(job => job.DeleteExpiredFiles(null));
            
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

            var response = new
            {
                TotalSpace = FormatBytes(serverSpaceInfo.TotalSpace),
                FreeSpace = FormatBytes(serverSpaceInfo.FreeSpace),
                UsedSpace = FormatBytes(serverSpaceInfo.UsedSpace)
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
                foreach (var file in uploadsDirectory.GetFiles())
                {
                    usedSpace += file.Length;
                }
            }

            // Convert configured GB to bytes
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

                // Delete the physical file
                System.IO.File.Delete(filePath);

                // Update fileInfo.json
                var fileInfos = FileInfos;
                fileInfos.RemoveAll(f => f.FileName == filename);
                FileInfos = fileInfos;

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
            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }

            return $"{number:n1} {suffixes[counter]}";
        }


    }


}

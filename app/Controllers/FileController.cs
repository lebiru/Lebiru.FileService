using Lebiru.FileService.Models;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using Hangfire;

namespace Lebiru.FileService.Controllers
{
    [Route("File")]
    [ApiController]
    public class FileController : Controller
    {
        private static readonly List<(string FileName, DateTime UploadTime)> UploadedFiles = new List<(string FileName, DateTime UploadTime)>();
        private const string UploadsFolder = "uploads";
        private readonly CleanupJob _cleanupJob;

        public FileController(CleanupJob cleanupJob)
        {
            _cleanupJob = cleanupJob;
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
            return View(UploadedFiles);
        }

        [HttpGet("Swagger")]
        public IActionResult Swagger()
        {
            return View("Swagger");
        }

        /// <summary>
        /// Uploads a file.
        /// </summary>
        /// <param name="files">The file to upload.</param>
        /// <returns>A response indicating the success or failure of the operation.</returns>
        [HttpPost("CreateDoc")]
        public async Task<IActionResult> Upload(List<IFormFile> files)
        {
            if (files == null || files.Count == 0)
                return BadRequest("No files uploaded.");

            foreach (var file in files)
            {
                var uploadsFolderPath = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder);
                if (!Directory.Exists(uploadsFolderPath))
                    Directory.CreateDirectory(uploadsFolderPath);

                var filePath = Path.Combine(uploadsFolderPath, file.FileName);

                // Check if file upload will exceed soft limit (2 GB)
                var totalSpaceUsed = GetTotalSpaceUsed(uploadsFolderPath);
                if (totalSpaceUsed + file.Length > (2L * 1024L * 1024L * 1024L)) // 2 GB in bytes
                {
                    return BadRequest("File upload exceeds the maximum allocated space (2 GB).");
                }

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var fileUploadTime = DateTime.UtcNow; // Record the upload time
                UploadedFiles.Add((file.FileName, fileUploadTime));
            }

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
        /// Retrieves a list of uploaded files along with their upload times and download URIs.
        /// </summary>
        /// <remarks>
        /// This endpoint returns a list of objects containing details about each uploaded file,
        /// including the file name, upload time, and a URI for downloading the file.
        /// </remarks>
        /// <returns>A list of objects representing uploaded files.</returns>
        [HttpGet("ListFiles")]
        public IActionResult ListFiles()
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";

            var fileDetails = UploadedFiles.Select(file =>
            {
                var fileUri = $"{baseUrl}/DownloadFile?filename={Uri.EscapeDataString(file.FileName)}";
                return new
                {
                    FileName = file.FileName,
                    FileUploadTime = file.UploadTime,
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

        [HttpPost("TriggerCleanup")]
        public IActionResult TriggerCleanup()
        {
            // Enqueue the cleanup job
            BackgroundJob.Enqueue(() => _cleanupJob.Execute(null));
            UploadedFiles.Clear();
            return Ok("Cleanup job has been enqueued.");
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
            var drive = new DriveInfo(Path.GetPathRoot(Directory.GetCurrentDirectory()));
            long totalSpace = drive.TotalSize;
            long freeSpace = drive.TotalFreeSpace;

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

            return new ServerSpaceInfo()
            {
                TotalSpace = totalSpace,
                FreeSpace = freeSpace,
                UsedSpace = usedSpace
            };

        }

        [HttpGet("ServerName")]
        public IActionResult GetServerName()
        {
            try
            {
                var serverName = Environment.MachineName; // Get the server name
                return Ok(serverName);
            }
            catch (Exception ex)
            {
                // Log the exception
                return StatusCode(500, "An error occurred while retrieving the server name.");
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

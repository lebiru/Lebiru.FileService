using Microsoft.AspNetCore.Mvc;

namespace Lebiru.FileService.Controllers
{
    [Route("File")]
    [ApiController]
    public class FileController : Controller
    {
        private static readonly List<(string FileName, DateTime UploadTime)> UploadedFiles = new List<(string FileName, DateTime UploadTime)>();
        private const string UploadsFolder = "uploads";

        /// <summary>
        /// The home page for the app. Displays current files hosted on FileService.
        /// </summary>
        /// <returns></returns>
        [HttpGet("Home")]
        public IActionResult Index()
        {
            return View(UploadedFiles);
        }

        /// <summary>
        /// Uploads a file.
        /// </summary>
        /// <param name="file">The file to upload.</param>
        /// <returns>A response indicating the success or failure of the operation.</returns>
        [HttpPost("CreateDoc")]
        public async Task<IActionResult> Upload(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded.");

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

            return Ok("File uploaded successfully.");
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

        /// <summary>
        /// Retrieves available space on the server.
        /// </summary>
        /// <returns>Information about available space.</returns>
        [HttpGet("AvailableSpace")]
        public IActionResult AvailableSpace()
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

            var response = new
            {
                TotalSpace = FormatBytes(totalSpace),
                FreeSpace = FormatBytes(freeSpace),
                UsedSpace = FormatBytes(usedSpace)
            };

            return Ok(response);
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

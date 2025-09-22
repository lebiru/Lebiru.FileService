using System.ComponentModel.DataAnnotations;

namespace Lebiru.FileService.Models
{
    /// <summary>
    /// Represents a user in the system with authentication and role information
    /// </summary>
    public class UserModel
    {
        /// <summary>
        /// The unique username for the user
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// The user's password (should be hashed in production)
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// The user's role determining their access level (Admin/Contributor/Viewer)
        /// </summary>
        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// List of files owned by this user
        /// </summary>
        public List<string> OwnedFiles { get; set; } = new();
    }

    /// <summary>
    /// Contains constants for the available user roles in the system
    /// </summary>
    public static class UserRoles
    {
        /// <summary>
        /// Full access to all features including user management
        /// </summary>
        public const string Admin = "Admin";

        /// <summary>
        /// Can upload and manage their own files
        /// </summary>
        public const string Contributor = "Contributor";

        /// <summary>
        /// Can only view and download files
        /// </summary>
        public const string Viewer = "Viewer";

        /// <summary>
        /// Array containing all available roles
        /// </summary>
        public static readonly string[] AllRoles = new[] { Admin, Contributor, Viewer };
    }
}
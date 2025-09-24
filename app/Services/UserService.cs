using System.Security.Cryptography;
using System.Text.Json;
using Lebiru.FileService.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

namespace Lebiru.FileService.Services
{
    /// <summary>
    /// Service interface for managing users, authentication, and file ownership
    /// </summary>
    public interface IUserService
    {
        /// <summary>
        /// Gets a list of all users in the system
        /// </summary>
        /// <returns>List of all users</returns>
        List<UserModel> GetAllUsers();

        /// <summary>
        /// Gets a user by their username
        /// </summary>
        /// <param name="username">The username to look up</param>
        /// <returns>The user if found, null otherwise</returns>
        UserModel? GetUser(string username);

        /// <summary>
        /// Validates a user's credentials
        /// </summary>
        /// <param name="username">The username to validate</param>
        /// <param name="password">The password to validate</param>
        /// <returns>True if credentials are valid, false otherwise</returns>
        bool ValidateUser(string username, string password);

        /// <summary>
        /// Creates a ClaimsPrincipal for the given user for authentication
        /// </summary>
        /// <param name="username">The username to create claims for</param>
        /// <returns>A ClaimsPrincipal containing the user's claims</returns>
        ClaimsPrincipal CreateClaimsPrincipal(string username);

        /// <summary>
        /// Adds a new user to the system
        /// </summary>
        /// <param name="username">The username for the new user</param>
        /// <param name="password">The password for the new user</param>
        /// <param name="role">The role to assign to the new user</param>
        void AddUser(string username, string password, string role);

        /// <summary>
        /// Generates a random password for new users
        /// </summary>
        /// <returns>A randomly generated password string</returns>
        string GenerateRandomPassword();

        /// <summary>
        /// Checks if a user owns a specific file
        /// </summary>
        /// <param name="username">The username to check</param>
        /// <param name="filePath">The path of the file to check ownership of</param>
        /// <returns>True if the user owns the file, false otherwise</returns>
        bool IsFileOwner(string username, string filePath);

        /// <summary>
        /// Associates a file with a user as its owner
        /// </summary>
        /// <param name="username">The username of the owner</param>
        /// <param name="filePath">The path of the file to associate</param>
        void AddFileToUser(string username, string filePath);

        /// <summary>
        /// Removes a file's ownership association from all users
        /// </summary>
        /// <param name="filePath">The path of the file to remove</param>
        void RemoveFileFromUser(string filePath);
        
        /// <summary>
        /// Updates a file path in all user records when a file is renamed
        /// </summary>
        /// <param name="oldFilePath">The old file path</param>
        /// <param name="newFilePath">The new file path</param>
        void UpdateFilePath(string oldFilePath, string newFilePath);
    }

    /// <summary>
    /// Implementation of the user management service
    /// </summary>
    public class UserService : IUserService
    {
        private readonly List<UserModel> _users = new();
        private readonly ILogger<UserService> _logger;
        private readonly string _filePath;
        private readonly object _sync = new();
        private const int PasswordLength = 32;

        /// <summary>
        /// Initializes a new instance of the UserService class
        /// </summary>
        public UserService(ILogger<UserService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            // Set up persistence file path
            var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "app-data");
            Directory.CreateDirectory(dataDir);
            _filePath = Path.Combine(dataDir, "userInfo.json");
            
            // Load existing users or create defaults
            Load();
            if (!_users.Any())
            {
                CreateDefaultUsers();
            }
        }

        private void CreateDefaultUsers()
        {
            try
            {
                // Admin user
                if (GetUser("admin") == null)
                {
                    var adminPassword = GenerateRandomPassword();
                    AddUser("admin", adminPassword, UserRoles.Admin);
                    _logger.LogInformation("Created admin user with password: {Password}", adminPassword);
                }

                // Default contributor
                if (GetUser("contributor") == null)
                {
                    var contributorPassword = GenerateRandomPassword();
                    AddUser("contributor", contributorPassword, UserRoles.Contributor);
                    _logger.LogInformation("Created contributor user with password: {Password}", contributorPassword);
                }

                // Default viewer
                if (GetUser("viewer") == null)
                {
                    var viewerPassword = GenerateRandomPassword();
                    AddUser("viewer", viewerPassword, UserRoles.Viewer);
                    _logger.LogInformation("Created viewer user with password: {Password}", viewerPassword);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating default users");
                throw;
            }
        }

        /// <inheritdoc />
        public List<UserModel> GetAllUsers() => _users;

        /// <inheritdoc />
        public UserModel? GetUser(string username)
            => _users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

        /// <inheritdoc />
        public bool ValidateUser(string username, string password)
        {
            var user = GetUser(username);
            if (user == null) return false;

            return user.Password == password; // In production, use proper password hashing
        }

        /// <inheritdoc />
        public ClaimsPrincipal CreateClaimsPrincipal(string username)
        {
            var user = GetUser(username);
            if (user == null) throw new ArgumentException("User not found", nameof(username));

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role)
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            return new ClaimsPrincipal(identity);
        }

        /// <inheritdoc />
        public void AddUser(string username, string password, string role)
        {
            if (_users.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Username already exists", nameof(username));

            _users.Add(new UserModel
            {
                Username = username,
                Password = password,
                Role = role,
                OwnedFiles = new List<string>()
            });
            Save();
        }

        /// <inheritdoc />
        public string GenerateRandomPassword()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*";
            var randomBytes = new byte[PasswordLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomBytes);
            }

            return new string(randomBytes.Select(b => chars[b % chars.Length]).ToArray());
        }

        /// <inheritdoc />
        public bool IsFileOwner(string username, string filePath)
        {
            var user = GetUser(username);
            return user?.OwnedFiles.Contains(filePath) ?? false;
        }

        /// <inheritdoc />
        public void AddFileToUser(string username, string filePath)
        {
            var user = GetUser(username);
            if (user != null && !user.OwnedFiles.Contains(filePath))
            {
                user.OwnedFiles.Add(filePath);
                Save();
            }
        }

        /// <inheritdoc />
        public void RemoveFileFromUser(string filePath)
        {
            foreach (var user in _users)
            {
                user.OwnedFiles.Remove(filePath);
            }
            Save();
        }
        
        /// <inheritdoc />
        public void UpdateFilePath(string oldFilePath, string newFilePath)
        {
            bool updated = false;
            
            foreach (var user in _users)
            {
                int index = user.OwnedFiles.IndexOf(oldFilePath);
                if (index >= 0)
                {
                    user.OwnedFiles[index] = newFilePath;
                    updated = true;
                }
            }
            
            if (updated)
            {
                Save();
            }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var users = JsonSerializer.Deserialize<List<UserModel>>(json);
                    if (users != null)
                    {
                        _users.Clear();
                        _users.AddRange(users);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading user data");
                // Start fresh if load fails
                _users.Clear();
            }
        }

        private void Save()
        {
            try
            {
                lock (_sync)
                {
                    var json = JsonSerializer.Serialize(_users);
                    File.WriteAllText(_filePath, json);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving user data");
                // Continue even if save fails to avoid disrupting operations
            }
        }
    }
}
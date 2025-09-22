using System.ComponentModel.DataAnnotations;

namespace Lebiru.FileService.Models
{
    /// <summary>
    /// Model for pagination settings and state
    /// </summary>
    public class PaginationModel
    {
        /// <summary>
        /// Current page number (1-based)
        /// </summary>
        public int CurrentPage { get; set; } = 1;

        /// <summary>
        /// Number of items per page
        /// </summary>
        public int PageSize { get; set; } = 10;

        /// <summary>
        /// Total number of items
        /// </summary>
        public int TotalItems { get; set; }

        /// <summary>
        /// Calculate total number of pages
        /// </summary>
        public int TotalPages => (int)Math.Ceiling(TotalItems / (double)PageSize);

        /// <summary>
        /// Check if there is a previous page
        /// </summary>
        public bool HasPreviousPage => CurrentPage > 1;

        /// <summary>
        /// Check if there is a next page
        /// </summary>
        public bool HasNextPage => CurrentPage < TotalPages;

        /// <summary>
        /// Available page size options
        /// </summary>
        public static readonly int[] PageSizeOptions = new[] { 5, 10, 20, 50 };
    }
}
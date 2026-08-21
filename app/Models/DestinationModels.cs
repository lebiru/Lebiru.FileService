#pragma warning disable CS1591
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Lebiru.FileService.Models;

/// <summary>Supported outbound destination strategies.</summary>
public enum DestinationType { S3, Email, Ftp }

/// <summary>Lifecycle state of one file delivery attempt.</summary>
public enum DeliveryStatus { Pending, InProgress, Succeeded, Failed, Cancelled }

/// <summary>Persisted user-owned destination configuration.</summary>
public sealed class DestinationModel
{
    public Guid Id { get; set; }
    public required string OwnerUserId { get; set; }
    public required string Name { get; set; }
    public DestinationType Type { get; set; }
    public bool IsEnabled { get; set; } = true;
    public required string ConfigurationJson { get; set; }
    public string? ProtectedCredentials { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastTestedAt { get; set; }
    public bool? LastTestSucceeded { get; set; }
}

/// <summary>Persisted safe delivery history.</summary>
public sealed class DeliveryModel
{
    public Guid Id { get; set; }
    public required string OwnerUserId { get; set; }
    public Guid FileId { get; set; }
    public Guid DestinationId { get; set; }
    public DestinationType DestinationType { get; set; }
    public DeliveryStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long BytesTransferred { get; set; }
    public int AttemptCount { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>Creates or updates a destination. Credentials are write-only.</summary>
public sealed class DestinationUpsertRequest
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;
    public DestinationType Type { get; set; }
    public bool IsEnabled { get; set; } = true;
    [Required]
    public JsonElement Configuration { get; set; }
    public JsonElement? Credentials { get; set; }
}

/// <summary>Safe destination metadata returned in lists.</summary>
public sealed record DestinationSummary(Guid Id, string Name, DestinationType Type, bool IsEnabled,
    DateTime CreatedAt, DateTime UpdatedAt, bool CredentialsConfigured, DateTime? LastTestedAt,
    bool? LastTestSucceeded);

/// <summary>Safe owned destination details; credentials are intentionally absent.</summary>
public sealed record DestinationDetails(Guid Id, string Name, DestinationType Type, bool IsEnabled,
    JsonElement Configuration, DateTime CreatedAt, DateTime UpdatedAt, bool CredentialsConfigured,
    DateTime? LastTestedAt, bool? LastTestSucceeded);

/// <summary>Requests delivery of one file to one owned destination.</summary>
public sealed class DeliverFileRequest
{
    [Required]
    public Guid DestinationId { get; set; }
}

/// <summary>A safe destination connection-test result.</summary>
public sealed record DestinationTestResult(bool Success, string? Error = null);

/// <summary>Configures bounded outbound delivery execution.</summary>
public sealed class DestinationOptions
{
    public const string SectionName = "Destinations";
    [Range(1, 600)] public int TimeoutSeconds { get; set; } = 120;
    [Range(0, 5)] public int MaxRetries { get; set; } = 2;
    [Range(1, 10)] public int MaxConcurrentDeliveriesPerUser { get; set; } = 2;
    [Range(1, 120)] public int RequestsPerMinute { get; set; } = 20;
    [Range(1024, 100 * 1024 * 1024)] public long MaxEmailAttachmentBytes { get; set; } = 20 * 1024 * 1024;
}

public sealed record S3DestinationConfiguration(string Bucket, string Region, string? Prefix);
public sealed record S3DestinationCredentials(string AccessKey, string SecretKey, string? SessionToken);
public sealed record EmailDestinationConfiguration(string Host, int Port, bool UseTls, string From,
    string To, string? Subject, string? Body);
public sealed record EmailDestinationCredentials(string? Username, string? Password);
public sealed record FtpDestinationConfiguration(string Host, int Port, bool UseTls, string RemotePath);
public sealed record FtpDestinationCredentials(string Username, string Password);
#pragma warning restore CS1591

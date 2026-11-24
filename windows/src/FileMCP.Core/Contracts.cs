using System.Text.Json.Nodes;

namespace FileMCP.Core;

public static class FileMcpConstants
{
    public const string ProtocolFallback = "2025-03-26";
    public const string LatestLegacyProtocolVersion = "2025-11-25";
    public const string ModernProtocolVersion = "2026-07-28";
    public static readonly string[] LegacySupportedVersions = ["2025-03-26", "2025-06-18", "2025-11-25"];
    public static readonly string[] AllSupportedVersions = [ModernProtocolVersion, "2025-11-25", "2025-06-18", "2025-03-26"];
    public const string ServerName = "chat-to-computer-mcp";
    public const string ServerVersion = "0.4.4-windows";
    public const string LocalAuthHeaderName = "X-FileMCP-Local-Token";

    public const int MaxFileBytes = 5_000_000;
    public const int MaxWriteBytes = 5_000_000;
    public const int MaxPatchAggregateBytes = 32_000_000;
    public const int MaxCharsReturned = 40_000;
    public const int MaxListEntries = 1_000;
    public const int MaxSearchHeadLimit = 1_000;
    public const int DefaultSearchHeadLimit = 100;
    public const int MaxSearchOffset = 1_000_000;
    public const int MaxSearchPatternChars = 1_000;
    public const int MaxSearchGlobChars = 500;
    public const int MaxSearchContextLines = 10;
    public const int MaxSearchCodeFiles = 2_000;
    public const long MaxSearchCodeBytes = 50_000_000;
    public const int MaxSearchPreviewLineChars = 1_000;
    public const int MaxSearchPreviewChars = 60_000;
    public const int MaxReadRangeLines = 1_000;
    public const int MaxReadRangeChars = 80_000;
    public const int MaxBatchReadOperations = 16;
    public const int MaxSearchCodeQueries = 6;
    public const int MaxSearchCodeResultsPerQuery = 10;
    public const int MaxSearchCodeQueryChars = 500;
    public const int MaxRepoOverviewManifestResults = 100;
    public const int MaxRepoOverviewExtensionResults = 20;
    public const int MaxRepoOverviewDirectoryScanEntries = 50_000;
    public const int MaxToolProcessOutputBytes = 100_000;
    public const int MaxGitSafetyOutputBytes = 2_000_000;
    public const int MaxHttpRequestHeaderBytes = 64_000;
    public const int MaxHttpRequestBodyBytes = 8_000_000;
}

public sealed record WorkspacePermissions(bool AllowWrite, bool AllowShell, bool AllowGitPush, bool AllowCodexHistory)
{
    public static WorkspacePermissions ReadOnly { get; } = new(false, false, false, false);
    public string Fingerprint => string.Concat(new[] { AllowWrite, AllowShell, AllowGitPush, AllowCodexHistory }.Select(value => value ? "1" : "0"));
    public string Summary => !AllowWrite ? "Read only" : "Files read/write" +
        (AllowShell ? " · Shell" : "") + (AllowGitPush ? " · Git push" : "") + (AllowCodexHistory ? " · Codex import" : "");
}
public sealed record ApprovedWorkspace(string Id, string Name, string Path, WorkspacePermissions? Permissions = null);
public sealed record CloudflareNamedTunnel(string Id, string Name);
public sealed record DesktopCommandInfo(string WorkspaceId, string WorkspaceName,
    string SessionId, string RequestId, string Cwd, string State, bool Active);

public sealed record LocalMcpConfiguration(
    string TunnelId,
    string ApiKey,
    string Profile,
    ushort Port,
    string AllowedDirectory,
    string HealthAddress,
    string GitUserName,
    string GitUserEmail,
    bool EnableCommands,
    IReadOnlyList<ApprovedWorkspace>? AdditionalWorkspaces = null,
    int MaxConcurrentCommands = 8,
    string Provider = "openai",
    string CloudflareHostname = "",
    string CloudflareToken = "",
    string CloudflarePairingCode = "",
    string CloudflareMode = "token",
    string CloudflareTunnelName = "",
    bool EnableGitPush = false,
    bool EnableCodexHistory = false,
    bool CaptureCommandHistoryContent = false,
    WorkspacePermissions? DefaultWorkspacePermissions = null);

public enum LocalMcpRuntimeStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed,
}

public sealed record LocalMcpRuntimeState(LocalMcpRuntimeStatus Status, string? Error = null)
{
    public static LocalMcpRuntimeState Stopped { get; } = new(LocalMcpRuntimeStatus.Stopped);
    public static LocalMcpRuntimeState Starting { get; } = new(LocalMcpRuntimeStatus.Starting);
    public static LocalMcpRuntimeState Running { get; } = new(LocalMcpRuntimeStatus.Running);
    public static LocalMcpRuntimeState Stopping { get; } = new(LocalMcpRuntimeStatus.Stopping);
    public static LocalMcpRuntimeState Failed(string message) => new(LocalMcpRuntimeStatus.Failed, message);
}

public sealed class FileMcpException(string message) : Exception(message);

internal sealed record ToolCallOutput(JsonArray Content, JsonObject StructuredContent);

public sealed class FileMcpSettings
{
    public string TunnelId { get; set; } = "";
    public string Profile { get; set; } = "chat-to-computer-mcp";
    public int Port { get; set; } = 8008;
    public string AllowedDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ChatToComputerMCP");
    public string HealthAddress { get; set; } = "127.0.0.1:0";
    public string GitUserName { get; set; } = "";
    public string GitUserEmail { get; set; } = "";
    public bool EnableCommands { get; set; }
    public bool EnableGitPush { get; set; }
    public bool EnableCodexHistory { get; set; }
    public bool CaptureCommandHistoryContent { get; set; }
    public WorkspacePermissions? DefaultWorkspacePermissions { get; set; }
    public int MaxConcurrentCommands { get; set; } = 8;
    public List<ApprovedWorkspace> AdditionalWorkspaces { get; set; } = [];
    public string Provider { get; set; } = "openai";
    public string CloudflareHostname { get; set; } = "";
    public string CloudflareMode { get; set; } = "token";
    public string CloudflareTunnelName { get; set; } = "";
}

using ACC.ApiClient.Entities;

namespace ACC.Backup.Entities;

public class ProjectBackup : Project
{
    public ProjectBackup(ApiClient.ApiClient apiClient) : base(apiClient)
    {
    }

    public DateTime? BackupStartedAt { get; set; }
    public DateTime? BackupFinishedAt { get; set; }

    /// <summary>
    /// Set when enumeration threw, leaving this project's folder tree incomplete. Such a tree must
    /// not be treated as the authoritative file list: its previous manifest entries are carried
    /// forward untouched and its files are exempt from obsolete-file cleanup.
    /// </summary>
    public bool EnumerationFailed { get; set; }
}
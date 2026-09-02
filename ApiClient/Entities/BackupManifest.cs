using Newtonsoft.Json;

namespace ACC.ApiClient.Entities;

public class BackupManifest
{
    // Sorted snapshot of Files.Keys, used to resolve "every file under this folder" in O(log n + k).
    // Built explicitly via BuildFolderIndex so concurrent enumeration threads never race on it.
    private string[]? _sortedFileKeys;

    public BackupManifest()
    {
        // Use case-insensitive dictionary for cross-platform compatibility
        Files = new Dictionary<string, FileManifestEntry>(StringComparer.OrdinalIgnoreCase);
        Folders = new Dictionary<string, FolderManifestEntry>(StringComparer.OrdinalIgnoreCase);
        BackupDate = DateTime.Now;
        BackupDirectory = string.Empty;
    }

    [JsonProperty("backupDate")] public DateTime BackupDate { get; set; }

    [JsonProperty("backupDirectory")] public string BackupDirectory { get; set; }

    [JsonProperty("files")] public Dictionary<string, FileManifestEntry> Files { get; set; }

    /// <summary>
    /// Folder rollup timestamps keyed by folder id, used to skip enumerating subtrees that have not
    /// changed since the previous backup. Absent in manifests written by earlier versions, in which case
    /// pruning is disabled and the walk falls back to enumerating every folder.
    /// </summary>
    [JsonProperty("folders")] public Dictionary<string, FolderManifestEntry> Folders { get; set; }

    public void AddFile(string relativePath, FileManifestEntry entry)
    {
        Files[relativePath] = entry;
    }

    public bool TryGetFile(string relativePath, out FileManifestEntry entry)
    {
        return Files.TryGetValue(relativePath, out entry);
    }

    public void AddFolder(FolderManifestEntry entry)
    {
        Folders[entry.FolderId] = entry;
    }

    public bool TryGetFolder(string folderId, out FolderManifestEntry entry)
    {
        return Folders.TryGetValue(folderId, out entry);
    }

    /// <summary>
    /// Prepares the prefix index used by <see cref="GetFilesUnder" />. Call once, before any
    /// concurrent reads.
    /// </summary>
    public void BuildFolderIndex()
    {
        _sortedFileKeys = Files.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Returns every manifest file entry located at or below the supplied folder path. Paths are
    /// manifest-relative and forward-slash normalised, e.g. "My Project/Project Files/Drawings".
    /// </summary>
    public IEnumerable<KeyValuePair<string, FileManifestEntry>> GetFilesUnder(string folderRelativePath)
    {
        if (string.IsNullOrEmpty(folderRelativePath)) yield break;
        var keys = _sortedFileKeys;
        if (keys is null)
        {
            BuildFolderIndex();
            keys = _sortedFileKeys!;
        }

        var prefix = folderRelativePath.EndsWith('/') ? folderRelativePath : folderRelativePath + "/";
        for (var i = LowerBound(keys, prefix); i < keys.Length; i++)
        {
            var key = keys[i];
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) yield break;
            if (Files.TryGetValue(key, out var entry)) yield return new KeyValuePair<string, FileManifestEntry>(key, entry);
        }
    }

    /// <summary>
    /// True when the manifest key sits at or below one of the supplied folder paths. Walks up the
    /// key's own parents rather than testing every folder path, so the cost is bounded by path depth
    /// instead of the number of folders being tested against.
    /// </summary>
    public static bool IsUnderAnyFolder(string manifestKey, ICollection<string> folderPaths)
    {
        if (folderPaths.Count == 0 || string.IsNullOrEmpty(manifestKey)) return false;

        var index = manifestKey.LastIndexOf('/');
        while (index > 0)
        {
            var parent = manifestKey[..index];
            if (folderPaths.Contains(parent)) return true;
            index = parent.LastIndexOf('/');
        }

        return false;
    }

    // Index of the first key that is not ordered before prefix.
    private static int LowerBound(string[] keys, string prefix)
    {
        int low = 0, high = keys.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (StringComparer.OrdinalIgnoreCase.Compare(keys[mid], prefix) < 0)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    public void SaveToFile(string filePath)
    {
        var json = JsonConvert.SerializeObject(this, Formatting.Indented);
        System.IO.File.WriteAllText(filePath, json);
    }

    public static BackupManifest LoadFromFile(string filePath)
    {
        if (!System.IO.File.Exists(filePath))
            return null;

        var json = System.IO.File.ReadAllText(filePath);
        var manifest = JsonConvert.DeserializeObject<BackupManifest>(json);

        // Ensure the dictionaries are case-insensitive after deserialization
        if (manifest != null && manifest.Files != null)
        {
            var caseInsensitiveFiles = new Dictionary<string, FileManifestEntry>(
                manifest.Files, StringComparer.OrdinalIgnoreCase);
            manifest.Files = caseInsensitiveFiles;
        }

        if (manifest != null)
            manifest.Folders = manifest.Folders is null
                ? new Dictionary<string, FolderManifestEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, FolderManifestEntry>(manifest.Folders, StringComparer.OrdinalIgnoreCase);

        return manifest;
    }
}

public class FileManifestEntry
{
    [JsonProperty("fileId")] public string FileId { get; set; }

    [JsonProperty("versionNumber")] public int VersionNumber { get; set; }

    [JsonProperty("lastModifiedTime")] public DateTime LastModifiedTime { get; set; }

    [JsonProperty("storageSize")] public long StorageSize { get; set; }

    [JsonProperty("name")] public string Name { get; set; }

    [JsonProperty("projectId")] public string ProjectId { get; set; }

    public bool IsEquivalentTo(FileManifestEntry other)
    {
        if (other == null) return false;

        return FileId == other.FileId &&
               VersionNumber == other.VersionNumber &&
               LastModifiedTime == other.LastModifiedTime &&
               StorageSize == other.StorageSize;
    }
}

public class FolderManifestEntry
{
    [JsonProperty("folderId")] public string FolderId { get; set; }

    [JsonProperty("projectId")] public string ProjectId { get; set; }

    /// <summary>
    /// Manifest-relative, forward-slash normalised folder path including the sanitized project name,
    /// so it shares a prefix with the keys of <see cref="BackupManifest.Files" />.
    /// </summary>
    [JsonProperty("path")] public string Path { get; set; }

    /// <summary>
    /// Autodesk's rollup of the newest modification anywhere in this subtree. Additions and edits
    /// propagate up to every ancestor immediately, so an unchanged value means nothing was added or
    /// modified below this folder.
    /// It does NOT move when a file or folder is deleted or moved out -- a documented Autodesk
    /// limitation -- so an unchanged rollup is not proof that nothing was removed.
    /// </summary>
    [JsonProperty("lastModifiedTimeRollup")]
    public DateTime LastModifiedTimeRollup { get; set; }
}

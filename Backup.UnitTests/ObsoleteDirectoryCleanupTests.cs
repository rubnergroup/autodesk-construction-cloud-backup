using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using ACC.ApiClient.Entities;
using ACC.Backup.Entities;
using FluentAssertions;
using Library.Logger;
using NSubstitute;
using Xunit;

namespace ACC.Backup.UnitTests;

/// <summary>
/// Obsolete-file cleanup may only remove directories the cloud no longer has. A folder that still
/// exists in ACC keeps its directory even when every file in it was deleted.
/// </summary>
public class ObsoleteDirectoryCleanupTests : IDisposable
{
    private const string ProjectId = "b.62185181-412c-4c01-b45c-6fcd429e58b2";
    private const string ProjectName = "Test Project";
    private const string RootFolderName = "Project Files";
    private const string ProjectFilesPath = ProjectName + "/" + RootFolderName;

    private readonly string _backupDirectory;

    public ObsoleteDirectoryCleanupTests()
    {
        _backupDirectory = Path.Combine(Path.GetTempPath(), $"acc-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_backupDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_backupDirectory)) Directory.Delete(_backupDirectory, true);
    }

    [Fact]
    public void CleanupObsoleteFiles_Should_KeepTheDirectoryOfAFolderThatStillExistsInTheCloud()
    {
        var (project, root) = BuildProject();
        AddSubfolder(root, "Drawings"); // still in ACC, but every file in it was removed

        var manifest = BuildManifest(
            $"{ProjectFilesPath}/Drawings/old.rvt",
            $"{ProjectFilesPath}/Removed/gone.rvt");
        WriteBackedUpFile($"{ProjectFilesPath}/Drawings/old.rvt");
        WriteBackedUpFile($"{ProjectFilesPath}/Removed/gone.rvt");

        InvokeCleanup(BuildBackup(manifest), new List<ProjectBackup> { project });

        BackedUpFileExists($"{ProjectFilesPath}/Drawings/old.rvt").Should().BeFalse();
        BackedUpFileExists($"{ProjectFilesPath}/Removed/gone.rvt").Should().BeFalse();
        DirectoryExists($"{ProjectFilesPath}/Drawings").Should().BeTrue();
        DirectoryExists($"{ProjectFilesPath}/Removed").Should().BeFalse();
        DirectoryExists(ProjectFilesPath).Should().BeTrue();
        DirectoryExists(ProjectName).Should().BeTrue();
    }

    [Fact]
    public void CleanupObsoleteFiles_Should_RemoveTheWholeChainOfDirectoriesTheCloudNoLongerHas()
    {
        var (project, _) = BuildProject();

        var manifest = BuildManifest($"{ProjectFilesPath}/Removed/Nested/gone.rvt");
        WriteBackedUpFile($"{ProjectFilesPath}/Removed/Nested/gone.rvt");

        InvokeCleanup(BuildBackup(manifest), new List<ProjectBackup> { project });

        DirectoryExists($"{ProjectFilesPath}/Removed/Nested").Should().BeFalse();
        DirectoryExists($"{ProjectFilesPath}/Removed").Should().BeFalse();
        DirectoryExists(ProjectFilesPath).Should().BeTrue();
    }

    [Fact]
    public void CleanupObsoleteFiles_Should_LeaveEmptyDirectoriesUnrelatedToTheDeletedFilesAlone()
    {
        var (project, root) = BuildProject();
        AddSubfolder(root, "Drawings");

        var manifest = BuildManifest($"{ProjectFilesPath}/Removed/gone.rvt");
        WriteBackedUpFile($"{ProjectFilesPath}/Removed/gone.rvt");
        Directory.CreateDirectory(FullPath($"{ProjectFilesPath}/Drawings"));
        Directory.CreateDirectory(FullPath("Unrelated/Empty"));

        InvokeCleanup(BuildBackup(manifest), new List<ProjectBackup> { project });

        DirectoryExists($"{ProjectFilesPath}/Drawings").Should().BeTrue();
        DirectoryExists("Unrelated/Empty").Should().BeTrue();
        DirectoryExists($"{ProjectFilesPath}/Removed").Should().BeFalse();
    }

    [Fact]
    public void CleanupObsoleteFiles_Should_NotTouchAnythingBehindAPrunedFolder()
    {
        var (project, root) = BuildProject();
        var pruned = AddSubfolder(root, "Unchanged");
        pruned.Pruned = true; // its subtree was never enumerated, so nothing in it is obsolete

        var manifest = BuildManifest(
            $"{ProjectFilesPath}/Unchanged/Nested/kept.rvt",
            $"{ProjectFilesPath}/Removed/gone.rvt");
        WriteBackedUpFile($"{ProjectFilesPath}/Unchanged/Nested/kept.rvt");
        WriteBackedUpFile($"{ProjectFilesPath}/Removed/gone.rvt");

        InvokeCleanup(BuildBackup(manifest), new List<ProjectBackup> { project });

        BackedUpFileExists($"{ProjectFilesPath}/Unchanged/Nested/kept.rvt").Should().BeTrue();
        DirectoryExists($"{ProjectFilesPath}/Unchanged/Nested").Should().BeTrue();
        DirectoryExists($"{ProjectFilesPath}/Removed").Should().BeFalse();
    }

    [Fact]
    public void CleanupObsoleteFiles_Should_NotTouchAProjectThatFailedToEnumerate()
    {
        var (project, _) = BuildProject();
        project.EnumerationFailed = true;

        var manifest = BuildManifest($"{ProjectFilesPath}/Drawings/still-there.rvt");
        WriteBackedUpFile($"{ProjectFilesPath}/Drawings/still-there.rvt");

        InvokeCleanup(BuildBackup(manifest), new List<ProjectBackup> { project });

        BackedUpFileExists($"{ProjectFilesPath}/Drawings/still-there.rvt").Should().BeTrue();
        DirectoryExists($"{ProjectFilesPath}/Drawings").Should().BeTrue();
    }

    private ACC.Backup.Backup BuildBackup(BackupManifest previousManifest)
    {
        var config = new BackupConfiguration(new[]
        {
            "--backupdirectory", _backupDirectory,
            "--clientid", "clientId",
            "--clientsecret", "clientSecret",
            "--accountid", "9b3cc923-d920-4fee-ae91-5e9c8e4040rb",
            "--backupstorotate", "1"
        })
        {
            BackupDirectory = _backupDirectory
        };

        var backup = new ACC.Backup.Backup(config, Substitute.For<ILogger>());
        GetField("_previousBackupManifest").SetValue(backup, previousManifest);
        var prefixes = (Dictionary<string, string>)GetField("_projectPathPrefixes").GetValue(backup)!;
        prefixes[ProjectId] = ProjectName;
        return backup;
    }

    private static FieldInfo GetField(string name)
    {
        return typeof(ACC.Backup.Backup).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

    private static void InvokeCleanup(ACC.Backup.Backup backup, List<ProjectBackup> projects)
    {
        var method = typeof(ACC.Backup.Backup)
            .GetMethod("CleanupObsoleteFiles", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((Task)method.Invoke(backup, new object[] { projects })!).GetAwaiter().GetResult();
    }

    private static (ProjectBackup project, Folder root) BuildProject()
    {
        var root = new Folder(null!)
        {
            Name = RootFolderName,
            FolderId = "urn:adsk.wipprod:fs.folder:co.root",
            ProjectId = ProjectId
        };
        var project = new ProjectBackup(null!)
        {
            Name = ProjectName,
            ProjectId = ProjectId,
            RootFolderId = root.FolderId,
            RootFolder = root
        };
        return (project, root);
    }

    private static Folder AddSubfolder(Folder parent, string name)
    {
        var folder = new Folder(null!)
        {
            Name = name,
            FolderId = $"{parent.FolderId}.{name}",
            ProjectId = ProjectId,
            ParentFolderId = parent.FolderId,
            ParentFolder = parent
        };
        parent.Subfolders.Add(folder);
        return folder;
    }

    private static BackupManifest BuildManifest(params string[] manifestKeys)
    {
        var manifest = new BackupManifest { BackupDirectory = string.Empty };
        foreach (var key in manifestKeys)
            manifest.AddFile(key, new FileManifestEntry
            {
                FileId = key,
                Name = key[(key.LastIndexOf('/') + 1)..],
                ProjectId = ProjectId,
                VersionNumber = 1,
                StorageSize = 4,
                LastModifiedTime = new DateTime(2024, 5, 5, 10, 0, 0, DateTimeKind.Utc)
            });
        return manifest;
    }

    private string FullPath(string relativePath)
    {
        return Path.Combine(_backupDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private void WriteBackedUpFile(string manifestKey)
    {
        var fullPath = FullPath(manifestKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        System.IO.File.WriteAllText(fullPath, "data");
    }

    private bool BackedUpFileExists(string manifestKey)
    {
        return System.IO.File.Exists(FullPath(manifestKey));
    }

    private bool DirectoryExists(string relativePath)
    {
        return Directory.Exists(FullPath(relativePath));
    }
}

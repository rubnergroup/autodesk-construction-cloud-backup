using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ACC.ApiClient;
using ACC.ApiClient.Entities;
using File = System.IO.File;
using FluentAssertions;
using Library.Testing;
using Xunit;

namespace ApiClient.UnitTests;

/// <summary>
/// Covers the LastModifiedTimeRollup pruning that lets an incremental backup skip enumerating
/// folder subtrees Autodesk reports as unchanged. Responses are built inline rather than read from
/// the ExampleRestApiResponses fixtures so these tests run on any platform.
/// </summary>
public class RollupPruningTests : IDisposable
{
    private const string ClientId = "testClientId";
    private const string ClientSecret = "testClientSecret";
    private const string AccountId = "48a4d1eb-a370-42fe-89c9-4dd9e2ad9d41";
    private const string ProjectId = "b.62185181-412c-4c01-b45c-6fcd429e58b2";
    private const string ProjectDirectoryName = "Test Project";
    private const string RootFolderId = "urn:adsk.wipprod:fs.folder:co.root";
    private const string SubFolderId = "urn:adsk.wipprod:fs.folder:co.sub";
    private const string SubFolderManifestPath = ProjectDirectoryName + "/Project Files/Sub";
    private const string FileManifestKey = SubFolderManifestPath + "/drawing.rvt";
    private const long FileStorageSize = 2048;

    private static readonly DateTime UnchangedRollup = new(2024, 5, 5, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ChangedRollup = new(2024, 9, 9, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _backupDirectory;

    public RollupPruningTests()
    {
        _backupDirectory = Path.Combine(Path.GetTempPath(), "acc-rollup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_backupDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_backupDirectory)) Directory.Delete(_backupDirectory, true);
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }

    [Fact]
    public async Task GetFolderContentsRecursively_WhenRollupUnchanged_Should_SkipSubfolderEnumeration()
    {
        WriteBackedUpFile(FileManifestKey, FileStorageSize);
        var (sut, messageHandler) = BuildApiClient(BuildManifest(UnchangedRollup));

        var rootFolder = await sut.GetFolder(ProjectId, RootFolderId);
        await rootFolder.GetContentsRecursively();

        var subFolder = rootFolder.Subfolders.Single();
        subFolder.Pruned.Should().BeTrue();
        subFolder.Files.Should().BeEmpty();

        // Authenticate + GetFolder + root contents. The subfolder's contents endpoint is never hit.
        messageHandler.NumberOfCalls.Should().Be(3);
        RequestedSubfolderContents(messageHandler).Should().BeFalse();

        var stats = sut.GetIncrementalStats();
        stats.foldersPruned.Should().Be(1);
        stats.filesSkipped.Should().Be(1);
        stats.bytesSkipped.Should().Be(FileStorageSize);
    }

    [Fact]
    public async Task GetFolderContentsRecursively_WhenRollupChanged_Should_EnumerateSubfolder()
    {
        WriteBackedUpFile(FileManifestKey, FileStorageSize);
        // The manifest records a different rollup than the API now reports, so the subtree is stale.
        var (sut, messageHandler) = BuildApiClient(BuildManifest(ChangedRollup));

        var rootFolder = await sut.GetFolder(ProjectId, RootFolderId);
        await rootFolder.GetContentsRecursively();

        var subFolder = rootFolder.Subfolders.Single();
        subFolder.Pruned.Should().BeFalse();
        subFolder.Files.Should().HaveCount(1);
        messageHandler.NumberOfCalls.Should().Be(4);
        RequestedSubfolderContents(messageHandler).Should().BeTrue();
        sut.GetIncrementalStats().foldersPruned.Should().Be(0);
    }

    [Fact]
    public async Task GetFolderContentsRecursively_WhenBackedUpFileMissing_Should_EnumerateSubfolder()
    {
        // Rollup matches, but our copy of the file is gone. Pruning would hide that forever.
        var (sut, messageHandler) = BuildApiClient(BuildManifest(UnchangedRollup));

        var rootFolder = await sut.GetFolder(ProjectId, RootFolderId);
        await rootFolder.GetContentsRecursively();

        rootFolder.Subfolders.Single().Pruned.Should().BeFalse();
        messageHandler.NumberOfCalls.Should().Be(4);
        RequestedSubfolderContents(messageHandler).Should().BeTrue();
    }

    [Fact]
    public async Task GetFolderContentsRecursively_WhenBackedUpFileTruncated_Should_EnumerateSubfolder()
    {
        WriteBackedUpFile(FileManifestKey, FileStorageSize / 2);
        var (sut, messageHandler) = BuildApiClient(BuildManifest(UnchangedRollup));

        var rootFolder = await sut.GetFolder(ProjectId, RootFolderId);
        await rootFolder.GetContentsRecursively();

        rootFolder.Subfolders.Single().Pruned.Should().BeFalse();
        RequestedSubfolderContents(messageHandler).Should().BeTrue();
    }

    [Fact]
    public async Task GetFolderContentsRecursively_WhenFolderPathChanged_Should_EnumerateSubfolder()
    {
        // Same folder id and rollup, but the project now backs up under a different directory name,
        // so the files recorded in the manifest are not where the manifest says they are.
        WriteBackedUpFile(FileManifestKey, FileStorageSize);
        var (sut, messageHandler) = BuildApiClient(
            BuildManifest(UnchangedRollup),
            projectDirectoryName: "Test Project Renamed");

        var rootFolder = await sut.GetFolder(ProjectId, RootFolderId);
        await rootFolder.GetContentsRecursively();

        rootFolder.Subfolders.Single().Pruned.Should().BeFalse();
        RequestedSubfolderContents(messageHandler).Should().BeTrue();
    }

    [Fact]
    public async Task GetFolderContentsRecursively_WhenPruningDisabled_Should_EnumerateSubfolder()
    {
        WriteBackedUpFile(FileManifestKey, FileStorageSize);
        var (sut, messageHandler) = BuildApiClient(BuildManifest(UnchangedRollup), pruningEnabled: false);

        var rootFolder = await sut.GetFolder(ProjectId, RootFolderId);
        await rootFolder.GetContentsRecursively();

        rootFolder.Subfolders.Single().Pruned.Should().BeFalse();
        RequestedSubfolderContents(messageHandler).Should().BeTrue();
    }

    [Fact]
    public async Task GetFolderContentsRecursively_WithManifestWithoutFolderRollups_Should_EnumerateSubfolder()
    {
        // Manifests written before folder rollups existed must still work, just without pruning.
        WriteBackedUpFile(FileManifestKey, FileStorageSize);
        var manifest = BuildManifest(UnchangedRollup);
        manifest.Folders.Clear();
        var (sut, messageHandler) = BuildApiClient(manifest);

        var rootFolder = await sut.GetFolder(ProjectId, RootFolderId);
        await rootFolder.GetContentsRecursively();

        rootFolder.Subfolders.Single().Pruned.Should().BeFalse();
        RequestedSubfolderContents(messageHandler).Should().BeTrue();
    }

    [Fact]
    public void GetFilesUnder_Should_ReturnOnlyDescendantsOfTheGivenFolder()
    {
        var manifest = new BackupManifest();
        manifest.AddFile("Proj/Files/Sub/a.rvt", new FileManifestEntry { StorageSize = 1 });
        manifest.AddFile("Proj/Files/Sub/Deep/b.rvt", new FileManifestEntry { StorageSize = 2 });
        manifest.AddFile("Proj/Files/Sub-Sibling/c.rvt", new FileManifestEntry { StorageSize = 3 });
        manifest.AddFile("Proj/Files/d.rvt", new FileManifestEntry { StorageSize = 4 });
        manifest.BuildFolderIndex();

        var keys = manifest.GetFilesUnder("Proj/Files/Sub").Select(kvp => kvp.Key).ToList();

        keys.Should().BeEquivalentTo("Proj/Files/Sub/a.rvt", "Proj/Files/Sub/Deep/b.rvt");
    }

    [Theory]
    // This predicate is what stops obsolete-file cleanup from deleting files inside a skipped
    // subtree, so both directions matter.
    [InlineData("Proj/Files/Sub/a.rvt", true)]
    [InlineData("Proj/Files/Sub/Deep/Deeper/b.rvt", true)]
    [InlineData("proj/files/sub/a.rvt", true)]
    [InlineData("Proj/Files/Sub-Sibling/c.rvt", false)]
    [InlineData("Proj/Files/SubOther/c.rvt", false)]
    [InlineData("Proj/Files/d.rvt", false)]
    [InlineData("Other/Files/Sub/e.rvt", false)]
    public void IsUnderAnyFolder_Should_MatchOnlyDescendantsOfProtectedFolders(string manifestKey, bool expected)
    {
        var protectedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Proj/Files/Sub" };

        BackupManifest.IsUnderAnyFolder(manifestKey, protectedFolders).Should().Be(expected);
    }

    [Fact]
    public void IsUnderAnyFolder_WithNoProtectedFolders_Should_ReturnFalse()
    {
        BackupManifest.IsUnderAnyFolder("Proj/Files/Sub/a.rvt", new HashSet<string>()).Should().BeFalse();
    }

    [Fact]
    public void SaveAndLoad_Should_RoundTripFolderRollups()
    {
        var manifestPath = Path.Combine(_backupDirectory, "BackupManifest.json");
        var manifest = BuildManifest(UnchangedRollup);
        manifest.SaveToFile(manifestPath);

        var loaded = BackupManifest.LoadFromFile(manifestPath);

        loaded.Should().NotBeNull();
        loaded!.TryGetFolder(SubFolderId, out var entry).Should().BeTrue();
        entry.LastModifiedTimeRollup.Should().Be(UnchangedRollup);
        entry.Path.Should().Be(SubFolderManifestPath);
    }

    [Fact]
    public void LoadFromFile_WithoutFoldersSection_Should_YieldEmptyFolderDictionary()
    {
        var manifestPath = Path.Combine(_backupDirectory, "LegacyManifest.json");
        File.WriteAllText(manifestPath,
            """
            {
              "backupDate": "2024-01-01T00:00:00Z",
              "backupDirectory": "C:\\Backup",
              "files": { "Proj/Files/a.rvt": { "fileId": "abc", "versionNumber": 1, "storageSize": 10 } }
            }
            """);

        var loaded = BackupManifest.LoadFromFile(manifestPath);

        loaded.Should().NotBeNull();
        loaded!.Folders.Should().NotBeNull().And.BeEmpty();
        loaded.Files.Should().ContainKey("Proj/Files/a.rvt");
    }

    private void WriteBackedUpFile(string manifestKey, long sizeInBytes)
    {
        var path = Path.Combine(_backupDirectory, manifestKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[sizeInBytes]);
    }

    private BackupManifest BuildManifest(DateTime subFolderRollup)
    {
        var manifest = new BackupManifest { BackupDirectory = _backupDirectory };
        manifest.AddFile(FileManifestKey, new FileManifestEntry
        {
            FileId = "urn:adsk.wipprod:dm.lineage:file",
            VersionNumber = 1,
            LastModifiedTime = UnchangedRollup,
            StorageSize = FileStorageSize,
            Name = "drawing.rvt",
            ProjectId = ProjectId
        });
        manifest.AddFolder(new FolderManifestEntry
        {
            FolderId = SubFolderId,
            ProjectId = ProjectId,
            Path = SubFolderManifestPath,
            LastModifiedTimeRollup = subFolderRollup
        });
        return manifest;
    }

    private static bool RequestedSubfolderContents(MockHttpMessageHandler messageHandler)
    {
        return messageHandler.HttpRequestMessages.Any(m =>
            m.RequestUri!.ToString().Contains(SubFolderId) &&
            m.RequestUri.ToString().EndsWith("/contents"));
    }

    private static (ACC.ApiClient.ApiClient sut, MockHttpMessageHandler messageHandler) BuildApiClient(
        BackupManifest manifest,
        bool pruningEnabled = true,
        string projectDirectoryName = ProjectDirectoryName)
    {
        var folderEndpoint = $"https://developer.api.autodesk.com/data/v1/projects/{ProjectId}/folders/{RootFolderId}";
        var subFolderContentsEndpoint =
            $"https://developer.api.autodesk.com/data/v1/projects/{ProjectId}/folders/{SubFolderId}/contents";

        var messageHandler = new MockHttpMessageHandler(new List<MockHttpMessageHandlerMapping>
        {
            new()
            {
                RequestUri = new Uri("https://developer.api.autodesk.com/authentication/v2/token"),
                Response = """{"access_token":"token","token_type":"Bearer","expires_in":3599}""",
                StatusCode = HttpStatusCode.OK
            },
            new()
            {
                RequestUri = new Uri(folderEndpoint),
                Response = RootFolderResponse(),
                StatusCode = HttpStatusCode.OK
            },
            new()
            {
                RequestUri = new Uri($"{folderEndpoint}/contents"),
                Response = RootFolderContentsResponse(),
                StatusCode = HttpStatusCode.OK
            },
            new()
            {
                RequestUri = new Uri(subFolderContentsEndpoint),
                Response = SubFolderContentsResponse(),
                StatusCode = HttpStatusCode.OK
            }
        });

        var sut = TwoLeggedApiClient
            .Configure()
            .WithClientId(ClientId)
            .AndClientSecret(ClientSecret)
            .ForAccount(AccountId)
            .WithOptions(options =>
            {
                options.HttpClient = new HttpClient(messageHandler);
                options.RetryAttempts = 1;
                options.InitialRetryInSeconds = 1;
            })
            .Create();

        sut.SetPreviousBackupManifest(manifest);
        sut.SetProjectPathPrefixes(new Dictionary<string, string> { [ProjectId] = projectDirectoryName });
        sut.PruneUnchangedFolders = pruningEnabled;
        return (sut, messageHandler);
    }

    private static string RootFolderResponse()
    {
        return $$"""
                 {
                   "data": {
                     "type": "folders",
                     "id": "{{RootFolderId}}",
                     "attributes": {
                       "name": "Project Files",
                       "displayName": "Project Files",
                       "createTime": "2023-01-01T00:00:00.0000000Z",
                       "lastModifiedTime": "2024-05-05T10:00:00.0000000Z",
                       "lastModifiedTimeRollup": "2024-05-05T10:00:00.0000000Z",
                       "objectCount": 1,
                       "hidden": false
                     },
                     "relationships": {
                       "parent": { "data": { "type": "folders", "id": "urn:adsk.wipprod:fs.folder:co.parent-g" } }
                     }
                   }
                 }
                 """;
    }

    private static string RootFolderContentsResponse()
    {
        return $$"""
                 {
                   "links": { "self": { "href": "https://developer.api.autodesk.com/self" } },
                   "data": [
                     {
                       "type": "folders",
                       "id": "{{SubFolderId}}",
                       "attributes": {
                         "name": "Sub",
                         "displayName": "Sub",
                         "createTime": "2023-01-01T00:00:00.0000000Z",
                         "lastModifiedTime": "2024-05-05T10:00:00.0000000Z",
                         "lastModifiedTimeRollup": "2024-05-05T10:00:00.0000000Z",
                         "objectCount": 1,
                         "hidden": false
                       },
                       "relationships": {
                         "parent": { "data": { "type": "folders", "id": "{{RootFolderId}}" } }
                       }
                     }
                   ],
                   "included": []
                 }
                 """;
    }

    private static string SubFolderContentsResponse()
    {
        return $$"""
                 {
                   "links": { "self": { "href": "https://developer.api.autodesk.com/self" } },
                   "data": [],
                   "included": [
                     {
                       "type": "versions",
                       "id": "urn:adsk.wipprod:fs.file:vf.abc?version=1",
                       "attributes": {
                         "name": "drawing.rvt",
                         "displayName": "drawing.rvt",
                         "createTime": "2023-01-01T00:00:00.0000000Z",
                         "lastModifiedTime": "2024-05-05T10:00:00.0000000Z",
                         "versionNumber": 1,
                         "storageSize": {{FileStorageSize}},
                         "fileType": "rvt",
                         "hidden": false,
                         "reserved": false
                       },
                       "relationships": {
                         "storage": {
                           "data": { "type": "objects", "id": "urn:adsk.objects:os.object:wip.dm.prod/abc.rvt" },
                           "meta": {
                             "link": {
                               "href": "https://developer.api.autodesk.com/oss/v2/buckets/wip.dm.prod/objects/abc.rvt"
                             }
                           }
                         }
                       }
                     }
                   ]
                 }
                 """;
    }
}

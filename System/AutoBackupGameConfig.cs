using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Microsoft.VisualBasic.FileIO;
using Newtonsoft.Json;
using OmenTools.Dalamud;
using OmenTools.OmenService;
using SearchOption = System.IO.SearchOption;

namespace DailyRoutines.ModulesPublic;

public class AutoBackupGameConfig : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoBackupGameConfigTitle"),
        Description = Lang.Get("AutoBackupGameConfigDescription"),
        Category    = ModuleCategory.System
    };

    private Config     config     = null!;
    private HttpClient httpClient = null!;

    private bool isConflictPending;
    private bool isPopupOpen;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TaskHelper = new() { TimeoutMS = 300_000 };
        httpClient = HTTPClientHelper.Instance().Get("AutoBackupGameConfig.Gist");

        WindowManager.Instance().PostDraw += OnDraw;
        GameState.Instance().Login        += OnLogin;
    }

    protected override void Uninit()
    {
        GameState.Instance().Login        -= OnLogin;
        WindowManager.Instance().PostDraw -= OnDraw;
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Heading1(Lang.Get("AutoBackupGameConfig-DataFolderPath")))
        {
            ImGui.SetNextItemWidth(400f * GlobalUIScale);
            ImGui.InputText("###AutoBackupGameConfig-DataFolderInput", ref config.DataFolderPath);

            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGuiOm.HelpMarker(Lang.Get("AutoBackupGameConfig-DataFolderHelp"));

            var folderValid = IsDataFolderValid(config.DataFolderPath);
            ImGui.TextColored
            (
                folderValid ?
                    KnownColor.LawnGreen.ToUInt() :
                    KnownColor.Red.ToUInt(),
                folderValid ?
                    Lang.Get("Valid") :
                    Lang.Get("Invalid")
            );
        }

        ImGui.NewLine();

        using (ImRaii.Heading1("GitHub Personal Access Token (Classic)"))
        {
            ImGui.SetNextItemWidth(400f * GlobalUIScale);
            var previousToken = config.Token;
            ImGui.InputText("###AutoBackupGameConfig-TokenInput", ref config.Token, 128, ImGuiInputTextFlags.Password);

            if (config.Token != previousToken)
            {
                config.GistID         = string.Empty;
                config.LastBackupTime = DateTimeOffset.MinValue;
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGuiOm.HelpMarker(Lang.Get("AutoBackupGameConfig-TokenHelp"));
        }

        ImGui.NewLine();

        using (ImRaii.Heading1(Lang.Get("AutoBackupGameConfig-ManualOperate")))
        {
            if (ImGui.Button(Lang.Get("AutoBackupGameConfig-Backup")))
                EnqueueOperation(UploadAsync, "上传覆盖云端");

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("AutoBackupGameConfig-Restore")))
                EnqueueOperation(RestoreAsync, "从云端恢复");
        }

        if (!string.IsNullOrEmpty(config.GistID))
        {
            ImGui.NewLine();

            ImGui.TextDisabled($"Gist ID：{config.GistID}");

            if (config.LastBackupTime != DateTimeOffset.MinValue)
                ImGui.TextDisabled($"{Lang.Get("AutoBackupGameConfig-LastBackupTime")}：{config.LastBackupTime.ToLocalTime()}");
        }
    }

    private void OnDraw()
    {
        if (isConflictPending)
        {
            isPopupOpen       = true;
            isConflictPending = false;
        }

        using var popup = ImGuiOm.PopupModal
        (
            Lang.Get("AutoBackupGameConfig-Conflict-Title"),
            ref isPopupOpen
        );
        if (!popup) return;

        ImGui.TextWrapped(Lang.Get("AutoBackupGameConfig-Conflict-Message"));
        ImGui.Spacing();

        if (ImGui.Button(Lang.Get("AutoBackupGameConfig-Backup")))
        {
            isPopupOpen = false;
            EnqueueOperation(UploadAsync, "上传覆盖云端");
        }

        ImGui.SameLine();

        if (ImGui.Button(Lang.Get("AutoBackupGameConfig-Restore")))
        {
            isPopupOpen = false;
            EnqueueOperation(RestoreAsync, "从云端恢复");
        }

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("Cancel")))
            isPopupOpen = false;
    }

    private void OnLogin() => EnqueueOperation(CheckAndBackupAsync, "登录后备份检查", false);

    private void EnqueueOperation
    (
        Func<OperationContext, CancellationToken, Task> action,
        string                                          name,
        bool                                            notify = true
    )
    {
        var operation = new OperationContext
        {
            DataFolderPath = config.DataFolderPath,
            Token          = config.Token,
            GistID         = config.GistID,
            Description    = GetDescription()
        };

        if (notify)
        {
            var message = Lang.Get("AutoBackupGameConfig-Notification-StartOperation");
            NotifyHelper.Toast(message);
            NotifyHelper.Instance().Chat(message);
        }

        TaskHelper.Abort();
        TaskHelper.EnqueueAsync
        (
            async ct =>
            {
                try
                {
                    await action(operation, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    HandleError(name, ex);
                }
            },
            name
        );
    }

    private async Task<string?> EnsureGistIDAsync
    (
        OperationContext  operation,
        CancellationToken ct
    )
    {
        if (!string.IsNullOrEmpty(operation.GistID))
            return operation.GistID;

        var foundID = await FindGistAsync(operation, ct);
        if (string.IsNullOrEmpty(foundID))
            return null;

        operation.GistID = foundID;
        SaveOperationConfig(operation);
        return foundID;
    }

    private async Task CheckAndBackupAsync
    (
        OperationContext  operation,
        CancellationToken ct
    )
    {
        if (!IsDataFolderValid(operation.DataFolderPath) || string.IsNullOrWhiteSpace(operation.Token))
            return;

        var backup = await Task.Run(() => CreateBackup(operation.DataFolderPath, ct), ct);
        if (backup.Manifest.Files.Count == 0)
            return;

        var gistID = await EnsureGistIDAsync(operation, ct);

        if (gistID != null)
        {
            var cloudManifest = await DownloadManifestAsync(operation, ct);

            if (cloudManifest != null)
            {
                if (RequiresDecision(backup.Manifest.Files, cloudManifest, Environment.MachineName, out var diffCount))
                {
                    isConflictPending = true;
                    return;
                }

                if (diffCount == 0)
                    return;
            }
        }

        await UploadBackupAsync(operation, backup, ct);
    }

    private async Task UploadAsync
    (
        OperationContext  operation,
        CancellationToken ct
    )
    {
        if (!IsDataFolderValid(operation.DataFolderPath) || string.IsNullOrWhiteSpace(operation.Token))
            return;

        var backup = await Task.Run(() => CreateBackup(operation.DataFolderPath, ct), ct);
        if (backup.Manifest.Files.Count == 0)
            return;

        await UploadBackupAsync(operation, backup, ct);
    }

    private async Task UploadBackupAsync
    (
        OperationContext                         operation,
        (GistManifest Manifest, byte[] ZipBytes) backup,
        CancellationToken                        ct
    )
    {
        var payload = await Task.Run
                      (
                          () => JsonConvert.SerializeObject
                          (
                              new
                              {
                                  description = operation.Description,
                                  @public     = false,
                                  files = new Dictionary<string, object>
                                  {
                                      ["manifest.json"] = new
                                      {
                                          content = JsonConvert.SerializeObject(backup.Manifest, Formatting.Indented)
                                      },
                                      ["backup.zip"] = new
                                      {
                                          content = Convert.ToBase64String(backup.ZipBytes)
                                      }
                                  }
                              }
                          ),
                          ct
                      );

        await SaveGistAsync(operation, payload, ct);
        SaveOperationConfig(operation, StandardTimeManager.Instance().UTCNowOffset);

        var message = Lang.Get("AutoBackupGameConfig-Notification-UploadSuccess");
        NotifyHelper.Instance().Chat(message);
        NotifyHelper.Toast(message);
    }

    private async Task RestoreAsync
    (
        OperationContext  operation,
        CancellationToken ct
    )
    {
        if (!IsDataFolderValid(operation.DataFolderPath))
        {
            var message = Lang.Get("AutoBackupGameConfig-Notification-InvalidFolder");
            NotifyHelper.Instance().ChatError(message);
            NotifyHelper.ToastError(message);
            return;
        }

        if (string.IsNullOrWhiteSpace(operation.Token))
            return;

        if (await EnsureGistIDAsync(operation, ct) == null)
        {
            var message = Lang.Get("AutoBackupGameConfig-Notification-NoGist");
            NotifyHelper.Instance().ChatError(message);
            NotifyHelper.ToastError(message);
            return;
        }

        var zipBytes = await DownloadBackupBytesAsync(operation, ct);
        await Task.Run(() => RestoreBackupAsync(operation.DataFolderPath, zipBytes, ct), ct);

        var successMessage = Lang.Get("AutoBackupGameConfig-Notification-RestoreSuccess");
        NotifyHelper.Instance().Chat(successMessage);
        NotifyHelper.Toast(successMessage);
    }

    private void SaveOperationConfig
    (
        OperationContext operation,
        DateTimeOffset?  backupTime = null
    )
    {
        if (config.Token != operation.Token || config.DataFolderPath != operation.DataFolderPath)
            return;

        config.GistID = operation.GistID;
        if (backupTime.HasValue)
            config.LastBackupTime = backupTime.Value;

        config.Save(this);
    }

    private async Task SaveGistAsync
    (
        OperationContext  operation,
        string            payload,
        CancellationToken ct
    )
    {
        if (await EnsureGistIDAsync(operation, ct) != null)
        {
            using var response = await SendGistRequestAsync(operation, HttpMethod.Patch, $"{GIST_API}/{operation.GistID}", payload, ct);

            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                if (!response.IsSuccessStatusCode)
                    throw new Exception(Lang.Get("AutoBackupGameConfig-Error", $"更新失败（HTTP {(int)response.StatusCode}）"));

                return;
            }
        }

        using var createResponse = await SendGistRequestAsync(operation, HttpMethod.Post, GIST_API, payload, ct);
        if (!createResponse.IsSuccessStatusCode)
            throw new Exception(Lang.Get("AutoBackupGameConfig-Error", $"创建失败（HTTP {(int)createResponse.StatusCode}）"));

        var gist = JsonConvert.DeserializeObject<Gist>(await createResponse.Content.ReadAsStringAsync(ct));
        if (string.IsNullOrEmpty(gist?.ID))
            throw new Exception(Lang.Get("AutoBackupGameConfig-Error-NoData"));

        operation.GistID = gist.ID;
    }

    private async Task<HttpResponseMessage> SendGistRequestAsync
    (
        OperationContext  operation,
        HttpMethod        method,
        string            url,
        string?           payload,
        CancellationToken ct
    )
    {
        using var request = CreateGistRequest(method, url, operation.Token, payload);
        return await httpClient.SendAsync(request, ct);
    }

    private async Task<GistManifest?> DownloadManifestAsync
    (
        OperationContext  operation,
        CancellationToken ct
    )
    {
        var gist = await GetGistAsync(operation, ct);
        if (gist == null)
            return null;

        var manifestContent = gist.Files?["manifest.json"].Content;
        return string.IsNullOrEmpty(manifestContent) ?
                   null :
                   JsonConvert.DeserializeObject<GistManifest>(manifestContent);
    }

    private async Task<string?> FindGistAsync
    (
        OperationContext  operation,
        CancellationToken ct
    )
    {
        for (var page = 1;; page++)
        {
            using var response = await SendGistRequestAsync(operation, HttpMethod.Get, $"{GIST_API}?per_page=100&page={page}", null, ct);
            if (!response.IsSuccessStatusCode)
                throw new Exception(Lang.Get("AutoBackupGameConfig-Error", $"获取列表失败（HTTP {(int)response.StatusCode}）"));

            var gists  = JsonConvert.DeserializeObject<List<Gist>>(await response.Content.ReadAsStringAsync(ct)) ?? [];
            var gistID = gists.FirstOrDefault(gist => gist.Description == operation.Description)?.ID;
            if (gistID != null)
                return gistID;

            if (gists.Count == 0)
                return null;
        }
    }

    private async Task<Gist?> GetGistAsync
    (
        OperationContext  operation,
        CancellationToken ct
    )
    {
        using var response = await SendGistRequestAsync(operation, HttpMethod.Get, $"{GIST_API}/{operation.GistID}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        return response.IsSuccessStatusCode ?
                   JsonConvert.DeserializeObject<Gist>(await response.Content.ReadAsStringAsync(ct)) :
                   throw new Exception(Lang.Get("AutoBackupGameConfig-Error", $"连接失败（HTTP {(int)response.StatusCode}）"));
    }

    private async Task<byte[]> DownloadBackupBytesAsync
    (
        OperationContext  operation,
        CancellationToken ct
    )
    {
        var gist   = await GetGistAsync(operation, ct) ?? throw new Exception(Lang.Get("AutoBackupGameConfig-Error-NoData"));
        var rawURL = gist.Files?["backup.zip"].RawURL;
        if (string.IsNullOrEmpty(rawURL))
            throw new Exception(Lang.Get("AutoBackupGameConfig-Error-NoData"));

        using var response = await SendGistRequestAsync(operation, HttpMethod.Get, rawURL, null, ct);
        if (!response.IsSuccessStatusCode)
            throw new Exception(Lang.Get("AutoBackupGameConfig-Error", $"下载失败（HTTP {(int)response.StatusCode}）"));

        var content = await response.Content.ReadAsStringAsync(ct);
        return await Task.Run(() => Convert.FromBase64String(content), ct);
    }

    private static HttpRequestMessage CreateGistRequest
    (
        HttpMethod method,
        string     url,
        string     token,
        string?    json = null
    )
    {
        var request = new HttpRequestMessage(method, url);

        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.TryParseAdd(USER_AGENT);

        return request;
    }

    private static (GistManifest Manifest, byte[] ZipBytes) CreateBackup
    (
        string            folder,
        CancellationToken ct
    )
    {
        var manifest = new GistManifest
        {
            Files   = new(StringComparer.Ordinal),
            Machine = Environment.MachineName
        };
        using var stream = new MemoryStream();
        var       buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                foreach (var (relativePath, filePath) in EnumerateBackupFiles(folder))
                {
                    ct.ThrowIfCancellationRequested();
                    using var fileStream  = File.OpenRead(filePath);
                    using var entryStream = archive.CreateEntry(relativePath, CompressionLevel.Optimal).Open();
                    using var hash        = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                    int bytesRead;

                    while ((bytesRead = fileStream.Read(buffer)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        hash.AppendData(buffer, 0, bytesRead);
                        entryStream.Write(buffer, 0, bytesRead);
                    }

                    manifest.Files.Add(relativePath, Convert.ToHexString(hash.GetHashAndReset()));
                }
            }

            ct.ThrowIfCancellationRequested();
            return (manifest, stream.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task RestoreBackupAsync
    (
        string            folder,
        byte[]            zipBytes,
        CancellationToken ct
    )
    {
        var             folderFull     = Path.GetFullPath(folder);
        var             stagingFolder  = Path.Join(folderFull, $".DailyRoutines-Restore-{Guid.NewGuid():N}");
        using var       memoryStream   = new MemoryStream(zipBytes);
        await using var archive        = new ZipArchive(memoryStream, ZipArchiveMode.Read);
        var             files          = new List<(string StagedPath, string TargetPath, string OriginalPath)>(archive.Entries.Count);
        var             committedFiles = new List<(string StagedPath, string TargetPath, string? OriginalPath)>(archive.Entries.Count);
        var             targets        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/'))
                    continue;

                var targetPath   = Path.GetFullPath(Path.Join(folderFull, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                var relativePath = Path.GetRelativePath(folderFull, targetPath);
                if (Path.IsPathRooted(relativePath)                                                       ||
                    relativePath == ".."                                                                  ||
                    relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    !targets.Add(targetPath))
                    throw new InvalidDataException($"备份文件路径无效：{entry.FullName}");

                var stagedPath   = Path.Join(stagingFolder, "restored", relativePath);
                var originalPath = Path.Join(stagingFolder, "original", relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath));
                Directory.CreateDirectory(Path.GetDirectoryName(originalPath));

                await using var entryStream = await entry.OpenAsync(ct);
                await using var fileStream = new FileStream
                (
                    stagedPath,
                    new FileStreamOptions
                    {
                        Mode    = FileMode.CreateNew,
                        Access  = FileAccess.Write,
                        Share   = FileShare.None,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                    }
                );
                await entryStream.CopyToAsync(fileStream, ct);
                files.Add((stagedPath, targetPath, originalPath));
            }

            ct.ThrowIfCancellationRequested();

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(file.TargetPath));

                if (File.Exists(file.TargetPath))
                {
                    File.Replace(file.StagedPath, file.TargetPath, file.OriginalPath);
                    committedFiles.Add(file);
                }
                else
                {
                    File.Move(file.StagedPath, file.TargetPath);
                    committedFiles.Add((file.StagedPath, file.TargetPath, null));
                }
            }
        }
        catch (Exception ex)
        {
            var errors = new List<Exception> { ex };

            for (var index = committedFiles.Count - 1; index >= 0; index--)
            {
                var file = committedFiles[index];

                try
                {
                    if (file.OriginalPath != null)
                        File.Move(file.OriginalPath, file.TargetPath, true);
                    else
                        File.Move(file.TargetPath, file.StagedPath);
                }
                catch (Exception rollbackError)
                {
                    errors.Add(rollbackError);
                }
            }

            if (errors.Count > 1)
                throw new AggregateException("恢复配置及还原原文件时发生错误，原文件保留在恢复临时目录或回收站中。", errors);

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingFolder))
            {
                FileSystem.DeleteDirectory(stagingFolder, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
        }
    }

    private static IEnumerable<(string RelativePath, string FullPath)> EnumerateBackupFiles
    (
        string folder
    )
    {
        foreach (var filePath in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(filePath);

            if (fileName.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                continue;

            if (fileName is "FFXIV.cfg" or "MACROSYS.dat" ||
                (fileName.StartsWith("FFXIV_CHARA_") && fileName.EndsWith(".dat")))
                yield return (fileName, filePath);
        }

        foreach (var roleDirectory in Directory.EnumerateDirectories(folder))
        {
            var roleName = Path.GetFileName(roleDirectory);
            if (!roleName.StartsWith("FFXIV_CHR")) continue;

            foreach (var filePath in Directory.EnumerateFiles(roleDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(folder, filePath);

                if (ContainsLogDirectory(relativePath))
                    continue;

                if (Path.GetFileName(relativePath).EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return (relativePath.Replace('\\', '/'), filePath);
            }
        }
    }

    private static bool ContainsLogDirectory
    (
        string relativePath
    )
    {
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Equals("log", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool RequiresDecision
    (
        Dictionary<string, string> localManifest,
        GistManifest               cloudManifest,
        string                     currentMachine,
        out int                    diffCount
    )
    {
        diffCount = 0;

        var cloudFromOtherMachine = !cloudManifest.Machine.Equals(currentMachine, StringComparison.Ordinal);
        var requiresDecision      = false;

        foreach (var (path, hash) in localManifest)
        {
            if (cloudManifest.Files.TryGetValue(path, out var cloudHash) &&
                cloudHash == hash)
                continue;

            diffCount++;

            if (cloudFromOtherMachine)
                requiresDecision = true;
        }

        foreach (var path in cloudManifest.Files.Keys)
        {
            if (localManifest.ContainsKey(path)) continue;

            diffCount++;

            requiresDecision = true;
        }

        return requiresDecision;
    }

    private static void HandleError
    (
        string    logMessage,
        Exception ex
    )
    {
        DLog.Error(logMessage, ex);

        var message = Lang.Get("AutoBackupGameConfig-Notification-Failed");
        NotifyHelper.Instance().ChatError(message);
        NotifyHelper.ToastError(message);
    }

    private static bool IsDataFolderValid
    (
        string folder
    ) =>
        !string.IsNullOrWhiteSpace(folder) &&
        Directory.Exists(folder)           &&
        File.Exists(Path.Join(folder, "FFXIV.cfg"));

    private static string GetDescription()
    {
        var clientAbbr = "GL";
        if (GameState.IsCN)
            clientAbbr = "CN";
        if (GameState.IsKR)
            clientAbbr = "KR";
        if (GameState.IsTC)
            clientAbbr = "TC";

        return $"{GIST_DESCRIPTION} - {clientAbbr}";
    }

    #region 常量

    private const string GIST_API         = "https://api.github.com/gists";
    private const string GIST_DESCRIPTION = "FFXIV Game Config Backup (DailyRoutines)";
    private const string USER_AGENT       = "DailyRoutines-AutoBackupGameConfig";

    #endregion

    private class OperationContext
    {
        public required string DataFolderPath { get; init; }

        public required string Token { get; init; }

        public required string Description { get; init; }

        public required string GistID { get; set; }
    }

    private class GistFile
    {
        [JsonProperty("raw_url")]
        public string? RawURL { get; set; }

        public string? Content { get; set; }
    }

    private class Gist
    {
        public string? ID { get; set; }

        public string? Description { get; set; }

        public Dictionary<string, GistFile>? Files { get; set; }
    }

    private class GistManifest
    {
        public Dictionary<string, string> Files   = [];
        public string                     Machine = string.Empty;
    }

    private class Config : ModuleConfig
    {
        public string DataFolderPath = string.Empty;
        public string Token          = string.Empty;
        public string GistID         = string.Empty;

        public DateTimeOffset LastBackupTime = DateTimeOffset.MinValue;
    }
}

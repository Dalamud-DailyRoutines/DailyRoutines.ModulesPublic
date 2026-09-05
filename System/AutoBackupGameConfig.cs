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
using Newtonsoft.Json;
using OmenTools.Dalamud;
using OmenTools.OmenService;

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

    // 界面
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

            var folderValid = IsDataFolderValid();
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

        using (ImRaii.Heading1("GitHub Personal Acess Token (Classic)"))
        {
            ImGui.SetNextItemWidth(400f * GlobalUIScale);
            ImGui.InputText("###AutoBackupGameConfig-TokenInput", ref config.Token, 128, ImGuiInputTextFlags.Password);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGuiOm.HelpMarker(Lang.Get("AutoBackupGameConfig-TokenHelp"));
        }

        ImGui.NewLine();

        using (ImRaii.Heading1(Lang.Get("AutoBackupGameConfig-ManualOperate")))
        {
            if (ImGui.Button(Lang.Get("AutoBackupGameConfig-Backup")))
                EnqueueUpload();

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("AutoBackupGameConfig-Restore")))
                EnqueueRestore();
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
            EnqueueUpload();
        }

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("AutoBackupGameConfig-Restore")))
        {
            isPopupOpen = false;
            EnqueueRestore();
        }

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("Cancel")))
            isPopupOpen = false;
    }

    private void OnLogin()
    {
        TaskHelper.Abort();
        TaskHelper.EnqueueAsync(CheckAndBackupAsync, "登录后备份检查");
    }
    
    private void EnqueueUpload()
    {
        var message = Lang.Get("AutoBackupGameConfig-Notification-StartOperation");
        NotifyHelper.Toast(message);
        NotifyHelper.Instance().Chat(message);
        
        TaskHelper.Abort();
        TaskHelper.EnqueueAsync(UploadAsync, "上传覆盖云端");
    }
    
    private void EnqueueRestore()
    {
        var message = Lang.Get("AutoBackupGameConfig-Notification-StartOperation");
        NotifyHelper.Toast(message);
        NotifyHelper.Instance().Chat(message);
        
        TaskHelper.Abort();
        TaskHelper.EnqueueAsync(RestoreAsync, "从云端恢复");
    }

    private async Task<string?> EnsureGistIDAsync
    (
        CancellationToken ct
    )
    {
        if (!string.IsNullOrEmpty(config.GistID))
            return config.GistID;

        var foundID = await FindGistAsync(ct);
        if (string.IsNullOrEmpty(foundID))
            return null;

        config.GistID = foundID;
        config.Save(this);

        return foundID;
    }

    private async Task CheckAndBackupAsync
    (
        CancellationToken ct
    )
    {
        try
        {
            if (!IsDataFolderValid() ||
                string.IsNullOrWhiteSpace(config.Token))
                return;

            var localManifest = BuildManifest(config.DataFolderPath);
            if (localManifest.Count == 0)
                return;

            if (await EnsureGistIDAsync(ct) == null)
            {
                await UploadAsync(ct);
                return;
            }

            var cloudManifest = await DownloadManifestAsync(ct);
            if (cloudManifest == null)
            {
                await UploadAsync(ct);
                return;
            }

            if (RequiresDecision(localManifest, cloudManifest, Environment.MachineName, out var diffCount))
            {
                isConflictPending = true;
                return;
            }

            if (diffCount > 0)
                await UploadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            HandleError("登录后备份检查失败", ex);
        }
    }
    
    private async Task UploadAsync
    (
        CancellationToken ct
    )
    {
        try
        {
            if (!IsDataFolderValid()) return;

            var manifest = BuildManifest(config.DataFolderPath);
            if (manifest.Count == 0) return;

            var zipBytes = CreateBackupZip(config.DataFolderPath, manifest);
            var files = new Dictionary<string, object>
            {
                ["manifest.json"] = new
                {
                    content = JsonConvert.SerializeObject
                    (
                        new GistManifest
                        {
                            Files   = manifest,
                            Machine = Environment.MachineName
                        },
                        Formatting.Indented
                    )
                },
                ["backup.zip"] = new { content = Convert.ToBase64String(zipBytes) }
            };

            var payload = JsonConvert.SerializeObject
            (
                new
                {
                    description = GetDescription(),
                    @public     = false,
                    files
                }
            );

            await SaveGistAsync(payload, ct);
            config.LastBackupTime = StandardTimeManager.Instance().UTCNowOffset;
            config.Save(this);

            var message = Lang.Get("AutoBackupGameConfig-Notification-UploadSuccess");
            NotifyHelper.Instance().Chat(message);
            NotifyHelper.Toast(message);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            HandleError("备份至 GitHub Gist 失败", ex);
        }
    }
    
    private async Task RestoreAsync
    (
        CancellationToken ct
    )
    {
        try
        {
            if (!IsDataFolderValid())
            {
                var message = Lang.Get("AutoBackupGameConfig-Notification-InvalidFolder");
                NotifyHelper.Instance().ChatError(message);
                NotifyHelper.ToastError(message);
                return;
            }

            if (string.IsNullOrWhiteSpace(config.Token)) return;

            if (await EnsureGistIDAsync(ct) == null)
            {
                var message = Lang.Get("AutoBackupGameConfig-Notification-NoGist");
                NotifyHelper.Instance().ChatError(message);
                NotifyHelper.ToastError(message);
                return;
            }

            var zipBytes = await DownloadBackupBytesAsync(ct);

            using var       memoryStream = new MemoryStream(zipBytes);
            await using var archive      = new ZipArchive(memoryStream, ZipArchiveMode.Read);

            var folderFull = Path.GetFullPath(config.DataFolderPath);

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                    continue;

                var entryPath = Path.GetFullPath(Path.Join(folderFull, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));

                if (Path.GetRelativePath(folderFull, entryPath).StartsWith(".."))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);

                await using var entryStream = await entry.OpenAsync(ct);
                await using var fileStream  = new FileStream(entryPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                await entryStream.CopyToAsync(fileStream, ct);
            }

            var successMessage = Lang.Get("AutoBackupGameConfig-Notification-RestoreSuccess");
            NotifyHelper.Instance().Chat(successMessage);
            NotifyHelper.Toast(successMessage);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            HandleError("从云端恢复失败", ex);
        }
    }

    private async Task SaveGistAsync
    (
        string            payload,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(config.GistID))
        {
            await CreateGistAsync(payload, ct);
            return;
        }

        var response = await SendGistRequestAsync(HttpMethod.Patch, $"{GIST_API}/{config.GistID}", payload, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            config.GistID = string.Empty;
            config.Save(this);
            await CreateGistAsync(payload, ct);
            return;
        }

        if (!response.IsSuccessStatusCode)
            throw new Exception(Lang.Get("AutoBackupGameConfig-Error", $"更新失败（HTTP {(int)response.StatusCode}）"));
    }

    private async Task<HttpResponseMessage> SendGistRequestAsync
    (
        HttpMethod        method,
        string            url,
        string?           payload,
        CancellationToken ct
    )
    {
        using var request = CreateGistRequest(method, url, config.Token, payload);
        return await httpClient.SendAsync(request, ct);
    }

    private async Task CreateGistAsync
    (
        string            payload,
        CancellationToken ct
    )
    {
        using var request  = CreateGistRequest(HttpMethod.Post, $"{GIST_API}", config.Token, payload);
        using var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            throw new Exception(Lang.Get("AutoBackupGameConfig-Error", $"创建失败（HTTP {(int)response.StatusCode}）"));

        var gist = JsonConvert.DeserializeObject<Gist>(await response.Content.ReadAsStringAsync(ct));
        config.GistID = gist?.ID ?? string.Empty;
        config.Save(this);
    }

    private async Task<GistManifest?> DownloadManifestAsync
    (
        CancellationToken ct
    )
    {
        var gist = await GetGistAsync($"{GIST_API}/{config.GistID}", ct);
        if (gist == null)
            return null;

        var manifestContent = gist.Files?["manifest.json"]?.Content;
        if (string.IsNullOrEmpty(manifestContent))
            return null;

        return JsonConvert.DeserializeObject<GistManifest>(manifestContent);
    }

    private async Task<string?> FindGistAsync
    (
        CancellationToken ct
    )
    {
        for (var page = 1;; page++)
        {
            var response = await SendGistRequestAsync(HttpMethod.Get, $"{GIST_API}?per_page=100&page={page}", null, ct);

            if (!response.IsSuccessStatusCode)
                throw new Exception(Lang.Get("AutoBackupGameConfig-Error", $"获取列表失败（HTTP {(int)response.StatusCode}）"));

            var gists = JsonConvert.DeserializeObject<List<Gist>>(await response.Content.ReadAsStringAsync(ct)) ?? [];

            var gistID = gists.FirstOrDefault(gist => gist.Description == GetDescription())?.ID;
            if (gistID != null)
                return gistID;

            if (gists.Count == 0)
                return null;
        }
    }

    private async Task<Gist?> GetGistAsync
    (
        string          url,
        CancellationToken ct
    )
    {
        var response = await SendGistRequestAsync(HttpMethod.Get, url, null, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
            throw new Exception(Lang.Get("AutoBackupGameConfig-Error", $"连接失败（HTTP {(int)response.StatusCode}）"));

        return JsonConvert.DeserializeObject<Gist>(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<byte[]> DownloadBackupBytesAsync
    (
        CancellationToken ct
    )
    {
        var gist = await GetGistAsync($"{GIST_API}/{config.GistID}", ct) ??
                   throw new Exception(Lang.Get("AutoBackupGameConfig-Error-NoData"));

        var rawURL = gist.Files?["backup.zip"]?.RawURL;
        if (string.IsNullOrEmpty(rawURL))
            throw new Exception(Lang.Get("AutoBackupGameConfig-Error-NoData"));

        var response = await SendGistRequestAsync(HttpMethod.Get, rawURL, null, ct);

        if (!response.IsSuccessStatusCode)
            throw new Exception(Lang.Get("AutoBackupGameConfig-Error", $"下载失败（HTTP {(int)response.StatusCode}）"));

        return Convert.FromBase64String(await response.Content.ReadAsStringAsync(ct));
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

    private static Dictionary<string, string> BuildManifest
    (
        string folder
    )
    {
        var manifest = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (relativePath, filePath) in EnumerateBackupFiles(folder))
            manifest[relativePath] = ComputeFileHash(filePath);

        return manifest;
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

    private static string ComputeFileHash
    (
        string filePath
    )
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static byte[] CreateBackupZip
    (
        string                     folder,
        Dictionary<string, string> manifest
    )
    {
        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (relativePath, _) in manifest)
            {
                var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);

                using var entryStream = entry.Open();
                using var fileStream  = File.OpenRead(Path.Join(folder, relativePath));
                fileStream.CopyTo(entryStream);
            }
        }

        return stream.ToArray();
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

    private bool IsDataFolderValid() =>
        !string.IsNullOrWhiteSpace(config.DataFolderPath) &&
        Directory.Exists(config.DataFolderPath)           &&
        File.Exists(Path.Join(config.DataFolderPath, "FFXIV.cfg"));

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

    private const string GIST_API          = "https://api.github.com/gists";
    private const string GIST_DESCRIPTION  = "FFXIV Game Config Backup (DailyRoutines)";
    private const string USER_AGENT        = "DailyRoutines-AutoBackupGameConfig";

    #endregion

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

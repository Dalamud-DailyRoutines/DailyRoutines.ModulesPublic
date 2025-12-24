using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using DailyRoutines.Abstracts;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class BetterFateProgressUI : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("BetterFateProgressUITitle"),
        Description = GetLoc("BetterFateProgressUIDescription"),
        Category    = ModuleCategories.UIOptimization,
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static CancellationTokenSource? CancelSource;
    
    private static readonly Dictionary<uint, uint> AchievementToZone = new()
    {
        { 2343, 813  }, // 雷克兰德
        { 2345, 815  }, // 安穆·艾兰
        { 2346, 816  }, // 伊尔美格
        { 2344, 814  }, // 珂露西亚岛
        { 2347, 817  }, // 拉凯提卡大森林
        { 2348, 818  }, // 黑风海
        { 3022, 956  }, // 迷津
        { 3023, 957  }, // 萨维奈岛
        { 3024, 958  }, // 加雷马
        { 3025, 959  }, // 叹息海
        { 3026, 961  }, // 厄尔庇斯
        { 3027, 960  }, // 天外天垓
        { 3559, 1187 }, // 奥阔帕恰山
        { 3560, 1188 }, // 克扎玛乌卡湿地
        { 3561, 1189 }, // 亚克特尔树海
        { 3562, 1190 }, // 夏劳尼荒野
        { 3563, 1191 }, // 遗产之地
        { 3564, 1192 }, // 活着的记忆
    };

    private static readonly Dictionary<uint, List<ZoneFateProgressInfo>> VersionToZoneInfos = [];
    
    private static readonly Vector2 ChildSize = ScaledVector2(450f, 150f);

    private static int     BicolorGemAmount;
    private static uint    BicolorGemCap;
    private static Vector2 BicolorGemComponentSize;

    private static bool IsWindowUnlock;

    protected override void Init()
    {
        CancelSource ??= new();

        BuildZoneInfos();
        ObtainAllFateProgress();

        BicolorGemCap = LuminaGetter.GetRow<Item>(26807)!.Value.StackSize;

        Overlay ??= new Overlay(this);
        Overlay.Flags &= ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Overlay.SizeConstraints = new() { MinimumSize = ChildSize, };
        Overlay.WindowName = $"{LuminaGetter.GetRow<Addon>(3933)!.Value.Text.ExtractText()}###BetterFateProgressUI";

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "FateProgress", OnAddon);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("BetterFateProgressUI-UnlockWindow"), ref IsWindowUnlock))
        {
            if (IsWindowUnlock)
            {
                Overlay.Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;
                Overlay.Flags &= ~ImGuiWindowFlags.NoResize;
            }
            else
                Overlay.Flags |= ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize;
        }
    }

    protected override unsafe void OverlayPreDraw()
    {
        if (!Throttler.Throttle("BetterFateProgress-Refresh", 10_000)) return;

        ObtainAllFateProgress();
        BicolorGemAmount = InventoryManager.Instance()->GetInventoryItemCount(26807);
    }

    protected override void OverlayOnOpen() => ObtainAllFateProgress();

    protected override void OverlayUI()
    {
        using var fontPush = FontManager.UIFont120.Push();
        DrawBicolorGemComponent();
        DrawFateProgressTabs();
    }

    private static void DrawBicolorGemComponent()
    {
        var originalPos = ImGui.GetCursorPos();
        ImGui.SetCursorPos(BicolorGemComponentSize with 
                               { X = ImGui.GetWindowSize().X - BicolorGemComponentSize.X - (ImGui.GetStyle().ItemSpacing.X * 2) });
        using (ImRaii.Group())
        {
            ImGui.Image(ImageHelper.GetGameIcon(LuminaGetter.GetRow<Item>(26807)!.Value.Icon).Handle, ScaledVector2(24f));

            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (2f * GlobalFontScale));
            ImGui.Text($"{BicolorGemAmount}/{BicolorGemCap}");
        }
        BicolorGemComponentSize = ImGui.GetItemRectSize();
        ImGui.SetCursorPos(originalPos);
    }

    private static void DrawFateProgressTabs()
    {
        using var group = ImRaii.Group();
        using var bar = ImRaii.TabBar("FateProgressTab");
        if (!bar) return;

        foreach (var version in VersionToZoneInfos.Keys)
            DrawFateProgressTabItem(version);
    }

    private static void DrawFateProgressTabItem(uint version)
    {
        if (!VersionToZoneInfos.TryGetValue(version, out var zoneInfos)) return;
        
        using var item = ImRaii.TabItem($"{version + 5}.0");
        if (!item) return;

        var counter = 0;
        foreach (var zoneInfo in zoneInfos)
        {
            zoneInfo.Draw();
            
            if (counter % 2 == 0) 
                ImGui.SameLine();
            
            counter++;
        }
    }

    private static void BuildZoneInfos()
    {
        VersionToZoneInfos.Clear();
        
        var counter = 0;
        var currentVersion = 0U;
        
        foreach (var (achievementID, zoneID) in AchievementToZone)
        {
            if (!LuminaGetter.TryGetRow<TerritoryType>(zoneID, out var zoneRow)) continue;
            
            var version = (achievementID >= 3559) ? 2U : (achievementID >= 3022) ? 1U : 0U;
            
            if (currentVersion != version)
            {
                counter = 0;
                currentVersion = version;
            }
            
            var aetheryteID = zoneRow.Aetheryte.RowId;
            var zoneInfo    = new ZoneFateProgressInfo(version, (uint)counter, achievementID, zoneID, aetheryteID);
            
            VersionToZoneInfos.TryAdd(version, []);
            VersionToZoneInfos[version].Add(zoneInfo);
            
            counter++;
        }
        
        RefreshBackgroundTextures();
    }

    private static void RefreshBackgroundTextures()
    {
        DService.Framework.Run(() =>
        {
            const string uldPath = "ui/uld/FateProgress.uld";
            
            foreach (var (version, zoneInfos) in VersionToZoneInfos)
            {
                var texturePath = $"ui/uld/FlyingPermission{version + 3}_hr1.tex";
                
                for (var i = 0; i < zoneInfos.Count; i++)
                {
                    var texture = DService.PI.UiBuilder.LoadUld(uldPath).LoadTexturePart(texturePath, i);
                    zoneInfos[i].SetBackgroundTexture(texture);
                }
            }
        }, CancelSource.Token);
    }

    private static void ObtainAllFateProgress()
    {
        foreach (var achivement in AchievementToZone.Keys)
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.RequestAchievement, achivement);
    }

    private unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        addon->Close(true);
        Overlay.IsOpen ^= true;
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        
        CancelSource?.Cancel();
        CancelSource?.Dispose();
        CancelSource = null;
    }

    private class ZoneFateProgressInfo(uint version, uint counter, uint achievementID, uint zoneID, uint aetheryteID)
    {
        public uint Version       { get; init; } = version;
        public uint Counter       { get; init; } = counter;
        public uint AchievementID { get; init; } = achievementID;
        public uint ZoneID        { get; init; } = zoneID;
        public uint AetheryteID   { get; init; } = aetheryteID;

        private IDalamudTextureWrap? backgroundTexture;

        public void SetBackgroundTexture(IDalamudTextureWrap texture) => 
            backgroundTexture = texture;

        public void Draw()
        {
            if (!LuminaGetter.TryGetRow<TerritoryType>(ZoneID, out var zoneRow)) return;
            
            using (var child = ImRaii.Child($"{ToString()}", ChildSize, true,
                                            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (child)
                {
                    DrawBackgroundImage();
                    DrawZoneName(zoneRow.ExtractPlaceName());
                    DrawFateProgress();
                }
            }

            HandleInteraction(zoneRow);
        }

        private void DrawBackgroundImage()
        {
            if (backgroundTexture == null) return;
            
            var originalCursorPos = ImGui.GetCursorPos();
            ImGui.SetCursorPos(originalCursorPos - ScaledVector2(10f, 4));
            
            ImGui.Image(backgroundTexture.Handle, ImGui.GetWindowSize() + ScaledVector2(10f, 4f));
            
            ImGui.SetCursorPos(originalCursorPos);
        }

        private static void DrawZoneName(string name)
        {
            ImGui.SetWindowFontScale(1.05f);
            
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (8f * GlobalFontScale));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (4f * GlobalFontScale));
            ImGui.Text(name);
            
            ImGui.SetWindowFontScale(1f);
        }

        private void DrawFateProgress()
        {
            if (!AchievementManager.TryGetAchievement(AchievementID, out var achievement))
                return;
            
            var fateProgress = achievement.Current;
            
            ImGui.SetWindowFontScale(0.8f);
            var text = fateProgress > 6 ? $"{fateProgress - 6}/60" : $"{fateProgress}/6";
            ImGui.SetCursorPosY(ImGui.GetContentRegionMax().Y - ImGui.CalcTextSize(text).Y);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (4f * GlobalFontScale));
            ImGui.Text(text);

            DrawFinalProgress(fateProgress);
        }

        private static void DrawFinalProgress(uint fateProgress)
        {
            var remainingProgress = 66 - fateProgress;
            var text = fateProgress == 66
                           ? LuminaGetter.GetRow<Addon>(3930)!.Value.Text.ExtractText()
                           : Lang.Get("BetterFateProgressUI-LeftFateAmount", remainingProgress);

            ImGui.SetWindowFontScale(0.95f);
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(text).X);
            ImGui.TextColored(ImGuiColors.ParsedGold, text);
        }

        private unsafe void HandleInteraction(TerritoryType zoneSheetRow)
        {
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                var agent = AgentMap.Instance();
                if (agent->AgentInterface.IsAgentActive() && agent->SelectedMapId == zoneSheetRow.Map.RowId)
                    agent->AgentInterface.Hide();
                else
                {
                    agent->MapTitleString = *Utf8String.FromString(LuminaGetter.GetRow<Addon>(3933)!.Value.Text.ExtractText());
                    agent->OpenMapByMapId(zoneSheetRow.Map.RowId);
                }
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                if (AetheryteID != 0)
                    Telepo.Instance()->Teleport(AetheryteID, 0);
            }
        }

        public override string ToString() => $"ZoneFateProgressInfo_Version{Version + 5}.0_{ZoneID}_{Counter}";
    }
}

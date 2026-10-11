using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic.Interface;

// TODO：之后重写成新模块，现在并不需要自己维护一个 ImGui 界面了
public class BetterFateProgressUI : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BetterFateProgressUITitle"),
        Description = Lang.Get("BetterFateProgressUIDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private readonly CancellationTokenSource cancelSource = new();

    private int     bicolorGemAmount;
    private Vector2 bicolorGemComponentSize;

    private bool isWindowUnlock;

    protected override void Init()
    {
        if (GameState.IsLoggedIn)
            ObtainAllFateProgress();

        Overlay                 ??= new(this);
        Overlay.Flags           &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags           |=  ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Overlay.SizeConstraints =   new() { MinimumSize = ChildSize };
        Overlay.WindowName      =   $"{LuminaWrapper.GetAddonText(3933)}###BetterFateProgressUI";

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "FateProgress", OnAddon);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

        cancelSource.Cancel();
        cancelSource.Dispose();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("BetterFateProgressUI-UnlockWindow"), ref isWindowUnlock))
        {
            if (isWindowUnlock)
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
        if (!Throttler.Shared.Throttle("BetterFateProgress-Refresh", 10_000)) return;

        ObtainAllFateProgress();
        bicolorGemAmount = InventoryManager.Instance()->GetInventoryItemCount(26807);
    }

    protected override void OverlayOnOpen() => ObtainAllFateProgress();

    protected override void OverlayUI()
    {
        using var fontPush = FontManager.Instance().UIFont120.Push();
        DrawBicolorGemComponent();
        DrawFateProgressTabs();
    }

    private void DrawBicolorGemComponent()
    {
        var originalPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosX(ImGui.GetWindowSize().X - bicolorGemComponentSize.X - (ImGui.GetStyle().ItemSpacing.X * 2));

        using (ImRaii.Group())
        {
            ImGui.Image(ImageHelper.GetGameIcon(LuminaGetter.GetRowOrDefault<Item>(26807).Icon).Handle, new(ImGui.GetFrameHeight()));

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{bicolorGemAmount}/{BicolorGemCap}");
        }

        bicolorGemComponentSize = ImGui.GetItemRectSize();
        ImGui.SetCursorPos(originalPos);
    }

    private static void DrawFateProgressTabs()
    {
        using var group = ImRaii.Group();
        using var bar   = ImRaii.TabBar("FateProgressTab");
        if (!bar) return;

        foreach (var version in VersionToZoneInfos.Keys)
            DrawFateProgressTabItem(version);
    }

    private static void DrawFateProgressTabItem
    (
        uint version
    )
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

    private static unsafe void ObtainAllFateProgress()
    {
        for (byte i = 0; i < FateAchievements.VersionCount; i++)
            UIState.Instance()->Achievement.RequestFateProgressTab(i);
    }

    private unsafe void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        addon->Close(true);
        Overlay.IsOpen ^= true;
    }

    private class ZoneFateProgressInfo
    (
        uint version,
        uint counter,
        uint achievementID,
        uint zoneID,
        uint aetheryteID
    )
    {
        private IDalamudTextureWrap? backgroundTexture;
        public  uint                 Version       { get; init; } = version;
        public  uint                 Counter       { get; init; } = counter;
        public  uint                 AchievementID { get; init; } = achievementID;
        public  uint                 ZoneID        { get; init; } = zoneID;
        public  uint                 AetheryteID   { get; init; } = aetheryteID;

        public void SetBackgroundTexture
        (
            IDalamudTextureWrap texture
        ) =>
            backgroundTexture = texture;

        public void Draw()
        {
            if (!LuminaGetter.TryGetRow<TerritoryType>(ZoneID, out var zoneRow)) return;

            using (var child = ImRaii.Child
                   (
                       $"{ToString()}",
                       ChildSize,
                       true,
                       ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                   ))
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

        private static void DrawZoneName
        (
            string name
        )
        {
            ImGui.SetWindowFontScale(1.05f);

            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (8f * GlobalUIScale));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (4f * GlobalUIScale));
            ImGui.TextUnformatted(name);

            ImGui.SetWindowFontScale(1f);
        }

        private void DrawFateProgress()
        {
            if (!AchievementManager.Instance().TryGetAchievement(AchievementID, out var achievement))
                return;

            var fateProgress = achievement.Current;

            ImGui.SetWindowFontScale(0.8f);
            var text = fateProgress > 6 ?
                           $"{fateProgress - 6}/60" :
                           $"{fateProgress}/6";
            ImGui.SetCursorPosY(ImGui.GetContentRegionMax().Y - ImGui.CalcTextSize(text).Y);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX()         + (4f * GlobalUIScale));
            ImGui.TextUnformatted(text);

            DrawFinalProgress(fateProgress);
        }

        private static void DrawFinalProgress
        (
            uint fateProgress
        )
        {
            var remainingProgress = 66 - fateProgress;
            var text = fateProgress == 66 ?
                           LuminaWrapper.GetAddonText(3930) :
                           Lang.Get("BetterFateProgressUI-LeftFateAmount", remainingProgress);

            ImGui.SetWindowFontScale(0.95f);
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(text).X);
            ImGui.TextColored(ImGuiColors.ParsedGold, text);
        }

        private unsafe void HandleInteraction
        (
            TerritoryType zoneSheetRow
        )
        {
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                AgentMap.Instance()->SetMapAndOpen(zoneSheetRow.Map.RowId);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                if (AetheryteID != 0)
                    Telepo.Instance()->Teleport(AetheryteID, 0);
            }
        }

        public override string ToString() => $"ZoneFateProgressInfo_Version{Version + 5}.0_{ZoneID}_{Counter}";
    }

    #region 常量

    private static FrozenDictionary<uint, List<ZoneFateProgressInfo>> VersionToZoneInfos
    {
        get
        {
            if (field != null) return field;

            const string ULD_PATH = "ui/uld/FateProgress.uld";

            var dic = new Dictionary<uint, List<ZoneFateProgressInfo>>();

            var counter        = 0;
            var currentVersion = 0U;

            foreach (var (achievementID, zoneID) in FateAchievements.AchievementToZone)
            {
                if (!LuminaGetter.TryGetRow<TerritoryType>(zoneID, out var zoneRow)) continue;

                var version = achievementID >= 3559 ? 2U : achievementID >= 3022 ? 1U : 0U;

                if (currentVersion != version)
                {
                    counter        = 0;
                    currentVersion = version;
                }

                var aetheryteID = zoneRow.Aetheryte.RowId;
                var zoneInfo    = new ZoneFateProgressInfo(version, (uint)counter, achievementID, zoneID, aetheryteID);

                dic.TryAdd(version, []);
                dic[version].Add(zoneInfo);

                counter++;
            }

            foreach (var (version, zoneInfos) in dic)
            {
                var texturePath = $"ui/uld/FlyingPermission{version + 3}_hr1.tex";

                for (var i = 0; i < zoneInfos.Count; i++)
                {
                    var texture = IDalamudPluginInterface.Instance().UiBuilder.LoadUld(ULD_PATH).LoadTexturePart(texturePath, i);
                    zoneInfos[i].SetBackgroundTexture(texture);
                }
            }

            return field = dic.ToFrozenDictionary();
        }
    }

    private static readonly Vector2 ChildSize = ScaledVector2(450f, 150f);

    private static readonly uint BicolorGemCap = LuminaGetter.GetRowOrDefault<Item>(26807).StackSize;

    #endregion
}

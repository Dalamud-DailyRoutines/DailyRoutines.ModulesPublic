using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyBonusFate : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyBonusFateTitle"),
        Description = Lang.Get("AutoNotifyBonusFateDescription"),
        Category    = ModuleCategory.Notice,
        Author      = ["Due"]
    };

    private Config config = null!;

    private readonly HashSet<ushort> notifiedFates = [];

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        ExecuteCommandManager.Instance().Unreg(OnPostExecuteCommand);

        notifiedFates.Clear();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendTTS"), ref config.SendTTS))
            config.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("OpenMap"), ref config.AutoOpenMap))
            config.Save(this);
    }

    private void OnZoneChanged(uint u)
    {
        ExecuteCommandManager.Instance().Unreg(OnPostExecuteCommand);
        TaskHelper.Abort();
        notifiedFates.Clear();

        if (!ValidTerritories.Contains(GameState.TerritoryType)) return;

        ExecuteCommandManager.Instance().RegPost(OnPostExecuteCommand);
    }

    private void OnPostExecuteCommand(ExecuteCommandFlag command, uint param1, uint param2, uint param3, uint param4)
    {
        if (command != ExecuteCommandFlag.FateLoad) return;

        TaskHelper.Abort();
        TaskHelper.DelayNext(200);
        TaskHelper.Enqueue(UpdateAndNotify);
    }

    private void UpdateAndNotify()
    {
        if (!ValidTerritories.Contains(GameState.TerritoryType) || GameState.Map == 0) return;

        var fateTable = DService.Instance().Fate;

        if (fateTable.Length == 0)
        {
            if (notifiedFates.Count > 0) notifiedFates.Clear();
            return;
        }

        var currentBonusFates = new HashSet<ushort>();

        foreach (var fate in fateTable)
        {
            if (!fate.HasBonus) continue;

            currentBonusFates.Add(fate.FateId);

            if (notifiedFates.Add(fate.FateId))
                NotifyFate(fate);
        }

        if (notifiedFates.Count > 0)
            notifiedFates.IntersectWith(currentBonusFates);
    }

    private unsafe void NotifyFate(IFate fate)
    {
        var mapPos = PositionHelper.WorldToMap(fate.Position.ToVector2(), GameState.MapData);

        var chatMessage = Lang.GetSe
        (
            "AutoNotifyBonusFate-Chat",
            fate.Name.ToString(),
            fate.Progress,
            SeString.CreateMapLink(GameState.TerritoryType, GameState.Map, mapPos.X, mapPos.Y)
        );
        var notificationMessage = Lang.Get("AutoNotifyBonusFate-Notification", fate.Name.ToString(), fate.Progress);

        if (config.SendChat)
            NotifyHelper.Instance().Chat(chatMessage);
        if (config.SendNotification)
            NotifyHelper.Instance().NotificationInfo(notificationMessage);
        if (config.SendTTS)
            NotifyHelper.Speak(notificationMessage);

        if (config.AutoOpenMap)
        {
            var instance = AgentMap.Instance();
            if (instance == null) return;

            var currentZoneMapID = instance->CurrentMapId;
            instance->SelectedMapId = currentZoneMapID;

            if (!instance->IsAgentActive())
                instance->Show();

            instance->SetFlagMapMarker(GameState.TerritoryType, currentZoneMapID, fate.Position);
            instance->OpenMap(currentZoneMapID, GameState.TerritoryType, fate.Name.ToString());
        }
    }

    private class Config : ModuleConfig
    {
        public bool AutoOpenMap = true;
        public bool SendChat;
        public bool SendNotification = true;
        public bool SendTTS          = true;
    }
    
    #region 常量
    
    private static readonly FrozenSet<uint> ValidTerritories =
        LuminaGetter.Get<TerritoryType>()
                    .Where(x => x.TerritoryIntendedUse.RowId == 1)
                    .Where(x => x.ExVersion.Value.RowId      >= 2)
                    .Select(x => x.RowId)
                    .ToFrozenSet();
    
    #endregion
}

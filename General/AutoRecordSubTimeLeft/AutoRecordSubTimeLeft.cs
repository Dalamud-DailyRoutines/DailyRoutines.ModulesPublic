using System.Runtime.CompilerServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using OmenTools.Dalamud;
using OmenTools.ImGuiOm.Widgets;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public partial class AutoRecordSubTimeLeft : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动记录剩余游戏时间",
        Description = "登录时, 自动记录保存当前账号剩余的游戏时间, 并显示在服务器信息栏",
        Category    = ModuleCategory.General,
        Author      = ["Due"]
    };

    private static readonly CompSig                          AgentLobbyOnLoginSig = new("E8 ?? ?? ?? ?? 41 C6 45 ?? ?? E9 ?? ?? ?? ?? 83 FB 03");
    private unsafe delegate nint                             AgentLobbyOnLoginDelegate(AgentLobby* agent);
    private                 Hook<AgentLobbyOnLoginDelegate>? AgentLobbyOnLoginHook;

    private Config           config = null!;
    private IDtrBarEntry?    entry;
    private PlaytimeTracker? tracker;
    
    private DatePicker? startDatePicker;
    private DatePicker? endDatePicker;
    private DateTime    queryStartDate;
    private DateTime    queryEndDate;

    public override ModulePermission Permission { get; } = new() { CNOnly = true, CNDefaultEnabled = true };

    protected override unsafe void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new();

        EnsureQueryState();

        var legacyPath = Path.Join(ConfigDirectoryPath, "PlatimeData.log");
        var storePath  = Path.Join(ConfigDirectoryPath, "PlaytimeData.v2.json");

        tracker = new PlaytimeTracker(storePath, legacyPath);
        tracker.Start();

        entry         ??= DService.Instance().DTRBar.Get("DailyRoutines-GameTimeLeft");
        entry.OnClick =   OnDTREntryClick;

        UpdateEntryAndTimeInfo();

        AgentLobbyOnLoginHook ??= AgentLobbyOnLoginSig.GetHook<AgentLobbyOnLoginDelegate>(AgentLobbyOnLoginDetour);
        AgentLobbyOnLoginHook.Enable();

        DService.Instance().ClientState.Login  += OnLogin;
        DService.Instance().ClientState.Logout += OnLogout;

        FrameworkManager.Instance().Reg(OnUpdate, 5_000);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "CharaSelect",        OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "CharaSelect",        OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_CharaSelectRemain", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "_CharaSelectRemain", OnAddon);
    }

    protected override void ConfigUI()
    {
        EnsureQueryState();

        DrawSubscriptionInfo(LocalPlayerState.ContentID);

        ImGui.NewLine();

        DrawPlaytimeStatistics();
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        FrameworkManager.Instance().Unreg(OnUpdate);

        DService.Instance().ClientState.Login  -= OnLogin;
        DService.Instance().ClientState.Logout -= OnLogout;

        AgentLobbyOnLoginHook?.Disable();

        entry?.Remove();
        entry = null;

        tracker?.Dispose();
        tracker = null;
    }

    private void OnLogin()
    {
        tracker?.OnLogin();
        TaskHelper.Enqueue
        (() =>
            {
                var contentID = LocalPlayerState.ContentID;
                if (contentID == 0) return false;

                UpdateEntryAndTimeInfo(contentID);
                return true;
            }
        );
    }

    private void OnLogout(int code, int type)
    {
        tracker?.OnLogout();
        TaskHelper?.Abort();
    }

    private void OnUpdate(IFramework _) => 
        UpdateEntryAndTimeInfo();

    private unsafe void OnAddon(AddonEvent type, AddonArgs _)
    {
        if (CharaSelect == null) return;

        if (type == AddonEvent.PostDraw && !Throttler.Shared.Throttle("AutoRecordSubTimeLeft-OnAddonDraw"))
            return;

        var agent = AgentLobby.Instance();
        if (agent == null || agent->WorldIndex == -1) return;

        var info = agent->LobbyData.LobbyUIClient.SubscriptionInfo;
        if (info == null) return;

        var contentID = agent->HoveredCharacterContentId;
        if (contentID == 0) return;

        TryUpdateSubscriptionInfo(contentID, *info, out var leftMonth, out var leftTime);
        UpdateCharacterSelectRemain(leftMonth, leftTime);
        UpdateEntryAndTimeInfo(contentID);
    }

    private unsafe nint AgentLobbyOnLoginDetour(AgentLobby* agent)
    {
        var ret = AgentLobbyOnLoginHook!.Original(agent);
        UpdateSubscriptionFromAgent(agent);
        return ret;
    }

    private unsafe void UpdateSubscriptionFromAgent(AgentLobby* agent)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue
        (
            () =>
            {
                try
                {
                    if (agent->WorldIndex == -1) return false;

                    var info = agent->LobbyData.LobbyUIClient.SubscriptionInfo;
                    if (info == null) return false;

                    var contentID = agent->HoveredCharacterContentId;
                    if (contentID == 0) return false;

                    TryUpdateSubscriptionInfo(contentID, *info, out _, out _);
                    UpdateEntryAndTimeInfo(contentID);
                }
                catch (Exception ex)
                {
                    DLog.Warning("更新游戏点月卡订阅信息失败", ex);
                    NotifyHelper.Instance().NotificationWarning(ex.Message, "更新游戏点月卡订阅信息失败");
                }

                return true;
            },
            "更新订阅信息"
        );
    }

    private bool TryUpdateSubscriptionInfo(ulong contentID, in LobbySubscriptionInfo info, out TimeSpan leftMonth, out TimeSpan leftTime)
    {
        var timeInfo = GetLeftTimeSecond(info);
        leftMonth = NormalizeSubscriptionTime(timeInfo.MonthTime);
        leftTime  = NormalizeSubscriptionTime(timeInfo.PointTime);

        if (config.Infos.TryGetValue(contentID, out var current) &&
            current.LeftMonth == leftMonth                             &&
            current.LeftTime  == leftTime                              &&
            current.Record    != DateTime.MinValue) return false;

        config.Infos[contentID] = new(StandardTimeManager.Instance().Now, leftMonth, leftTime);
        config.Save(this);
        return true;
    }

    private static unsafe (int MonthTime, int PointTime) GetLeftTimeSecond(in LobbySubscriptionInfo info)
    {
        var bytes = new ReadOnlySpan<byte>(Unsafe.AsPointer(ref Unsafe.AsRef(in info)), sizeof(LobbySubscriptionInfo));
        return (ReadLittleEndian24(bytes, 16), ReadLittleEndian24(bytes, 24));
    }

    private static int ReadLittleEndian24(ReadOnlySpan<byte> bytes, int offset) =>
        bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;

    private void UpdateEntryAndTimeInfo(ulong contentID = 0)
    {
        if (entry == null || tracker == null || !GameState.IsLoggedIn) return;

        if (contentID == 0)
            contentID = LocalPlayerState.ContentID;

        if (contentID == 0                                           ||
            DService.Instance().Condition[ConditionFlag.InCombat]    ||
            !config.Infos.TryGetValue(contentID, out var info) ||
            info.Record == DateTime.MinValue                         ||
            info.LeftMonth == TimeSpan.MinValue && info.LeftTime == TimeSpan.MinValue)
        {
            entry.Shown = false;
            return;
        }

        var isMonth    = info.LeftMonth != TimeSpan.MinValue;
        var expireTime = info.Record + (isMonth ? info.LeftMonth : info.LeftTime);
        var now        = StandardTimeManager.Instance().Now;
        var stats      = tracker.Snapshot;

        using var builder = new RentedSeStringBuilder();
        builder.Builder
               .PushColorType(25)
               .Append($"[{(isMonth ? "月卡" : "点卡")}] ")
               .PopColorType()
               .Append($"{expireTime:MM/dd HH:mm}");
        entry.Text = builder.Builder.ToReadOnlySeString().ToDalamudString();

        using var tooltipBuilder = new RentedSeStringBuilder();
        tooltipBuilder.Builder
                      .PushColorType(28)
                      .Append("[过期时间]")
                      .PopColorType()
                      .AppendNewLine()
                      .Append($"{expireTime:yyyy/MM/dd HH:mm:ss}")
                      .AppendNewLine()
                      .PushColorType(28)
                      .Append("[剩余时长]")
                      .PopColorType()
                      .AppendNewLine()
                      .Append(FormatTimeSpan(expireTime - now))
                      .AppendNewLine()
                      .AppendNewLine()
                      .PushColorType(28)
                      .Append("[本日游玩时长]")
                      .PopColorType()
                      .AppendNewLine()
                      .Append(FormatTimeSpan(stats.Today))
                      .AppendNewLine()
                      .PushColorType(28)
                      .Append("[昨日游玩时长]")
                      .PopColorType()
                      .AppendNewLine()
                      .Append(FormatTimeSpan(stats.Yesterday))
                      .AppendNewLine()
                      .PushColorType(28)
                      .Append("[近 7 天游玩时长]")
                      .PopColorType()
                      .AppendNewLine()
                      .Append(FormatTimeSpan(stats.Last7Days))
                      .AppendNewLine()
                      .AppendNewLine()
                      .Append("(左键: ")
                      .PushColorType(34)
                      .Append("模块配置界面")
                      .PopColorType()
                      .Append(")")
                      .AppendNewLine()
                      .Append("(右键: ")
                      .PushColorType(34)
                      .Append("时长充值页面")
                      .PopColorType()
                      .Append(")");
        entry.Tooltip = tooltipBuilder.Builder.ToReadOnlySeString().ToDalamudString();
        entry.Shown   = true;
    }

    private static void OnDTREntryClick(DtrInteractionEvent eventData)
    {
        switch (eventData.ClickType)
        {
            case MouseClickType.Left:
                ChatManager.Instance().SendMessage($"/pdr search {nameof(AutoRecordSubTimeLeft)}");
                break;
            case MouseClickType.Right:
                Util.OpenLink("https://pay.sdo.com/item/GWPAY-100001900");
                break;
        }
    }
}

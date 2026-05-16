
using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSendMoney : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoSendMoneyTitle"),
        Description = Lang.Get("AutoSendMoneyDescription"),
        Category    = ModuleCategory.General,
        Author      = ["status102"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private bool IsRunning => runtime != null;
    
    private Config config = null!;

    private int[] moneyButtons = [];

    private readonly List<Member>           members  = [];
    private readonly Dictionary<uint, long> editPlan = [];

    private float  nameLength = -1;
    private double planAll;
    private long   currentChange;

    private SendMoneyRuntime? runtime;
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        ValidateConfigChanges();
        TaskHelper ??= new() { TimeoutMS = 5_000 };
    }

    protected override void Uninit() =>
        Stop();
    
    #region UI

    protected override void ConfigUI()
    {
        if (nameLength < 0)
            nameLength = ImGui.CalcTextSize(Lang.Get("All")).X;

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Settings")}");

        using (ImRaii.PushIndent())
        using (ImRaii.ItemWidth(100f * GlobalUIScale))
        {
            ImGui.InputInt($"{Lang.Get("AutoSendMoney-Step", 1)}##Step1Input", ref config.Step1, flags: ImGuiInputTextFlags.CharsDecimal);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                ValidateConfigChanges();
                config.Save(this);
            }

            ImGui.InputInt($"{Lang.Get("AutoSendMoney-Step", 2)}##Step2Input", ref config.Step2, flags: ImGuiInputTextFlags.CharsDecimal);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                ValidateConfigChanges();
                config.Save(this);
            }

            ImGui.InputInt($"{Lang.Get("AutoSendMoney-DelayLowerLimit")}##DelayLowerLimitInput", ref config.Delay1, flags: ImGuiInputTextFlags.CharsDecimal);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                ValidateConfigChanges();
                config.Save(this);
            }

            ImGui.InputInt($"{Lang.Get("AutoSendMoney-DelayUpperLimit")}##DelayUpperLimitInput", ref config.Delay2, flags: ImGuiInputTextFlags.CharsDecimal);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                ValidateConfigChanges();
                config.Save(this);
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Control")}");

        using (ImRaii.PushIndent())
        {
            using (ImRaii.Disabled(IsRunning))
            {
                if (ImGui.Button($"{FontAwesomeIcon.FlagCheckered.ToIconString()} {Lang.Get("Start")}"))
                    Start();
            }

            ImGui.SameLine();
            if (ImGui.Button($"{FontAwesomeIcon.Stop.ToIconString()} {Lang.Get("Stop")}"))
                Stop();
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoSendMoney-ListToTrade")}");

        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(Lang.Get("AutoSendMoney-AddPartyList")))
                AddPartyMembers();

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("AutoSendMoney-AddTarget")))
                AddCurrentTarget();

            using (ImRaii.PushId("All"))
                DrawGlobalPlan();

            foreach (var p in members)
            {
                using (ImRaii.PushId(p.EntityID.ToString()))
                    DrawMemberPlan(p);
            }
        }
    }

    private void DrawGlobalPlan()
    {
        using var group   = ImRaii.Group();
        var       hasPlan = editPlan.Count > 0;

        if (ImGui.Checkbox("##AllHasPlan", ref hasPlan))
        {
            if (hasPlan)
            {
                foreach (var p in members)
                {
                    if (editPlan.ContainsKey(p.EntityID)) continue;
                    editPlan.Add(p.EntityID, (long)(planAll * 10000));
                }
            }
            else
                editPlan.Clear();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(Lang.Get("All"));

        using var disabled = ImRaii.Disabled(IsRunning);

        ImGui.SameLine(nameLength + 60);

        ImGui.SetNextItemWidth(80f * GlobalUIScale);
        ImGui.InputDouble($"{Lang.Get("Wan")}##AllMoney", ref planAll, 0, 0, "%.1lf", ImGuiInputTextFlags.CharsDecimal);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var keys = editPlan.Keys.ToArray();
            foreach (var key in keys)
                editPlan[key] = (long)(planAll * 10000);
        }

        currentChange = 0;

        foreach (var num in moneyButtons)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(15f * GlobalUIScale);
            var display = $"{(num < 0 ? string.Empty : '+')}{num}";
            if (ImGui.Button($"{display}##All"))
                currentChange = num * 1_0000;
        }

        if (currentChange != 0)
        {
            planAll += currentChange / 10000.0;

            foreach (var p in members)
            {
                if (!editPlan.TryAdd(p.EntityID, currentChange))
                    editPlan[p.EntityID] += currentChange;
            }
        }

        ImGui.SameLine();

        if (ImGui.Button($"{Lang.Get("Reset")}###ResetAll"))
        {
            planAll = 0;

            var keys = editPlan.Keys.ToArray();
            foreach (var key in keys)
                editPlan[key] = 0;
        }
    }

    private void DrawMemberPlan(Member p)
    {
        using var group   = ImRaii.Group();
        var       hasPlan = editPlan.ContainsKey(p.EntityID);

        using (ImRaii.Disabled(IsRunning))
        {
            if (ImGui.Checkbox($"##{p.FullName}-CheckBox", ref hasPlan))
            {
                if (hasPlan)
                    editPlan.Add(p.EntityID, (long)(planAll * 10000));
                else
                    editPlan.Remove(p.EntityID);
            }
        }

        if (p.GroupIndex >= 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"{(char)('A' + p.GroupIndex)}-");
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(p.FullName);

        ImGui.SameLine(nameLength + 60);
        if (!hasPlan)
            return;

        if (IsRunning)
            ImGui.TextUnformatted(Lang.Get("AutoSendMoney-Count", runtime?.GetRemaining(p.EntityID) ?? 0));
        else
        {
            ImGui.SetNextItemWidth(80f * GlobalUIScale);
            var value = editPlan.TryGetValue(p.EntityID, out var valueToken) ? valueToken / 10000.0 : 0;
            ImGui.InputDouble($"{Lang.Get("Wan")}##{p.EntityID}-Money", ref value, 0, 0, "%.1lf", ImGuiInputTextFlags.CharsDecimal);
            if (ImGui.IsItemDeactivatedAfterEdit())
                editPlan[p.EntityID] = (long)(value * 10000);

            currentChange = 0;

            foreach (var num in moneyButtons)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(15f * GlobalUIScale);
                var display = $"{(num < 0 ? string.Empty : '+')}{num}";
                if (ImGui.Button($"{display}##Single_{p.EntityID}"))
                    currentChange = num * 1_0000;
            }

            if (currentChange != 0)
                editPlan[p.EntityID] = (long)(value * 10000) + currentChange;

            ImGui.SameLine();
            if (ImGui.Button($"{Lang.Get("Reset")}###ResetSingle-{p.EntityID}"))
                editPlan[p.EntityID] = 0;
        }
    }

    public void AddPartyMembers()
    {
        members.Clear();
        var cwProxy = InfoProxyCrossRealm.Instance();

        if (cwProxy->IsCrossRealm)
        {
            var myGroup = InfoProxyCrossRealm.GetMemberByEntityId((uint)Control.GetLocalPlayer()->GetGameObjectId())->GroupIndex;
            AddCrossRealmGroupMembers(cwProxy->CrossRealmGroups[myGroup], myGroup);

            for (var i = 0; i < cwProxy->CrossRealmGroups.Length; i++)
            {
                if (i == myGroup)
                    continue;

                AddCrossRealmGroupMembers(cwProxy->CrossRealmGroups[i], i);
            }
        }
        else
        {
            var pAgentHUD = AgentHUD.Instance();

            for (var i = 0; i < pAgentHUD->PartyMemberCount; ++i)
            {
                var charData        = pAgentHUD->PartyMembers[i];
                var partyMemberName = SeString.Parse(charData.Name.Value).TextValue;

                AddMember(charData.EntityId, partyMemberName, charData.Object->HomeWorld);
            }
        }

        var removedKeys = editPlan.Keys.Where(k => members.All(m => m.EntityID != k)).ToArray();
        foreach (var key in removedKeys)
            editPlan.Remove(key);

        foreach (var item in members)
            editPlan.TryAdd(item.EntityID, 0);

        nameLength = members.Select(p => ImGui.CalcTextSize(p.FullName).X)
                            .Append(ImGui.CalcTextSize(Lang.Get("All")).X)
                            .Max();
    }

    private void AddCrossRealmGroupMembers(CrossRealmGroup crossRealmGroup, int groupIndex)
    {
        for (var i = 0; i < crossRealmGroup.GroupMemberCount; i++)
        {
            var groupMember = crossRealmGroup.GroupMembers[i];
            AddMember(groupMember.EntityId, SeString.Parse(groupMember.Name).TextValue, (ushort)groupMember.HomeWorld, groupIndex);
        }
    }

    private void AddMember(uint entityID, string fullName, ushort worldID, int groupIndex = -1)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return;
        if (!Sheets.Worlds.TryGetValue(worldID, out var world))
            return;

        members.Add(new() { EntityID = entityID, FirstName = fullName, World = world.Name.ToString(), GroupIndex = groupIndex });
    }

    private void AddCurrentTarget()
    {
        var target = TargetSystem.Instance()->GetTargetObject();

        if (target is not null &&
            DService.Instance().ObjectTable.SearchByEntityID(target->EntityId) is ICharacter { ObjectKind: ObjectKind.Pc } player)
        {
            if (members.Any(p => p.EntityID == player.EntityID))
                return;

            members.Add(new(player));
            editPlan.TryAdd(player.EntityID, 0);
            nameLength = members.Select(p => ImGui.CalcTextSize(p.FullName).X)
                                .Append(ImGui.CalcTextSize(Lang.Get("All")).X)
                                .Max();
        }
    }

    #endregion

    #region 交易流程

    private void Start()
    {
        if (runtime != null) return;
        ValidateConfigChanges();
        runtime = new SendMoneyRuntime(this);
    }

    private void Stop()
    {
        runtime?.Dispose();
        runtime = null;
    }

    #endregion

    #region 工具

    private static bool IsWithinTradeDistance(Vector3 pos2)
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer)
            return false;

        var delta      = localPlayer.Position - pos2;
        var distanceSq = delta.X * delta.X    + delta.Z * delta.Z;
        return distanceSq < 16;
    }

    private int GetRandomDelayMS()
    {
        var min = Math.Max(0, config.Delay1);
        var max = Math.Max(0, config.Delay2);
        if (max <= min) return min;
        return Random.Shared.Next(min, max);
    }

    private void ValidateConfigChanges()
    {
        config.Step1 = Math.Abs(config.Step1);
        config.Step2 = Math.Abs(config.Step2);

        if (config.Step1 == 0) config.Step1 = 50;
        if (config.Step2 == 0) config.Step2 = 100;
        if (config.Step2 < config.Step1)
            (config.Step1, config.Step2) = (config.Step2, config.Step1);

        config.Delay1 = Math.Max(0, config.Delay1);
        config.Delay2 = Math.Max(0, config.Delay2);
        if (config.Delay2 < config.Delay1)
            (config.Delay1, config.Delay2) = (config.Delay2, config.Delay1);
        if (config.Delay2 == config.Delay1)
            config.Delay2 = config.Delay1 + 1;

        moneyButtons =
        [
            -config.Step2,
            -config.Step1,
            config.Step1,
            config.Step2
        ];
    }

    #endregion

    private class Config : ModuleConfig
    {
        public int Delay1 = 200;
        public int Delay2 = 500;
        public int Step1  = 50;
        public int Step2  = 100;
    }

    private class Member
    {
        public uint   EntityID;
        public string FirstName  = null!;
        public int    GroupIndex = -1;
        public string World      = null!;

        public Member() { }

        public Member(ICharacter gameObject)
        {
            EntityID  = gameObject.EntityID;
            FirstName = gameObject.Name.ToString();

            var worldID = gameObject.ToBCStruct()->HomeWorld;
            World = LuminaWrapper.GetWorldName(worldID) ?? "???";
        }

        public string FullName =>
            $"{FirstName}@{World}";
    }

    private readonly record struct PreCheckState
    (
        bool SelfConfirmed,
        bool OtherConfirmed
    );

    private sealed class SendMoneyRuntime : IDisposable
    {
        private static readonly CompSig TradeRequestSig = new("48 89 6C 24 ?? 56 57 41 56 48 83 EC ?? 48 8B E9 44 8B F2 48 8D 0D");
        private delegate nint                        TradeRequestDelegate(InventoryManager* manager, uint entityID);
        private          Hook<TradeRequestDelegate>? TradeRequestHook;
        
        private static readonly CompSig TradeStatusUpdateSig = new
        (
            "E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 4C 8B C2 8B D1 48 8D 0D ?? ?? ?? ?? E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 48 8D 0D"
        );
        private delegate nint                             TradeStatusUpdateDelegate(InventoryManager* manager, nint entityID, nint a3);
        private          Hook<TradeStatusUpdateDelegate>? TradeStatusUpdateHook;
        
        private readonly AutoSendMoney          owner;
        private readonly HashSet<uint>          pendingTradeRequests = [];
        private readonly Dictionary<uint, long> tradePlan;
        private          PreCheckState          checkState;
        private          uint                   currentMoney;
        private          bool                   isDisposed;

        private bool isTrading;
        private uint lastTradeEntityID;
        
        public SendMoneyRuntime(AutoSendMoney owner)
        {
            this.owner = owner;
            tradePlan  = [.. owner.editPlan.Where(i => i.Value > 0)];

            TradeRequestHook = TradeRequestSig.GetHook<TradeRequestDelegate>(OnTradeRequest);
            TradeRequestHook.Enable();

            TradeStatusUpdateHook = TradeStatusUpdateSig.GetHook<TradeStatusUpdateDelegate>(OnTradeStatusUpdate);
            TradeStatusUpdateHook.Enable();

            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Trade", OnTradeAddonSetup);
            FrameworkManager.Instance().Reg(OnFrameworkTick, 1_000);

            LogMessageManager.Instance().RegPost(OnLogMessage);

            TryQueueNextTrade();
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            FrameworkManager.Instance().Unreg(OnFrameworkTick);
            DService.Instance().AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Trade", OnTradeAddonSetup);

            LogMessageManager.Instance().Unreg(OnLogMessage);

            TradeRequestHook?.Dispose();
            TradeRequestHook = null;

            TradeStatusUpdateHook?.Dispose();
            TradeStatusUpdateHook = null;

            owner.TaskHelper?.Abort();
        }

        public long GetRemaining(uint entityID) =>
            tradePlan.GetValueOrDefault(entityID, 0);

        private void OnLogMessage(uint logMessageID, LogMessageQueueItem item)
        {
            if (!TradeFinishLogMessages.Contains(logMessageID)) return;
            if (Trade == null) return;

            Trade->Close(true);
            CompleteTrade();
        }

        private void OnTradeAddonSetup(AddonEvent type, AddonArgs args)
        {
            owner.TaskHelper?.Abort();
            pendingTradeRequests.Clear();

            if (!tradePlan.TryGetValue(lastTradeEntityID, out var value))
            {
                owner.TaskHelper?.DelayNext(owner.GetRandomDelayMS());
                owner.TaskHelper?.Enqueue(CancelTradeAddon, nameof(CancelTradeAddon));
            }
            else
            {
                owner.TaskHelper?.DelayNext(owner.GetRandomDelayMS());
                owner.TaskHelper?.Enqueue(() => SetTradeGil((uint)Math.Min(value, MAXIMUM_GIL_PER_TRADE)), nameof(SetTradeGil));
                owner.TaskHelper?.DelayNext(owner.GetRandomDelayMS());
                owner.TaskHelper?.Enqueue(ConfirmPreCheck, nameof(ConfirmPreCheck));
            }
        }

        private nint OnTradeRequest(InventoryManager* manager, uint entityID)
        {
            if (TradeRequestHook == null) return 0;
            var ret = TradeRequestHook.Original(manager, entityID);

            if (ret == 0)
                BeginTrade(entityID);
            else
                pendingTradeRequests.Remove(entityID);

            return ret;
        }

        private nint OnTradeStatusUpdate(InventoryManager* manager, nint entityID, nint a3)
        {
            var eventType = Marshal.ReadByte(a3 + 4);

            switch (eventType)
            {
                case 1:
                    BeginTrade((uint)Marshal.ReadInt32(a3 + 40));
                    break;
                case 16:
                    var updateType = Marshal.ReadByte(a3 + 5);

                    switch (updateType)
                    {
                        case 3:
                            UpdatePreCheck((uint)Marshal.ReadInt32(a3 + 40), false);
                            break;
                        case 4:
                        case 5:
                            UpdatePreCheck((uint)Marshal.ReadInt32(a3 + 40), true);
                            break;
                    }

                    break;
                case 5:
                    ConfirmFinal();
                    break;
                case 7:
                    CancelTrade();
                    break;
            }

            return TradeStatusUpdateHook == null ? 0 : TradeStatusUpdateHook.Original(manager, entityID, a3);
        }

        private void BeginTrade(uint entityID)
        {
            currentMoney      = 0;
            checkState        = default;
            isTrading         = true;
            lastTradeEntityID = entityID;
            pendingTradeRequests.Remove(entityID);
        }

        private void UpdatePreCheck(uint objectID, bool confirm)
        {
            if (objectID == LocalPlayerState.EntityID)
                checkState = checkState with { SelfConfirmed = confirm };
            else if (objectID == lastTradeEntityID)
                checkState = checkState with { OtherConfirmed = confirm };

            if (!tradePlan.TryGetValue(lastTradeEntityID, out var value))
            {
                owner.TaskHelper?.DelayNext(owner.GetRandomDelayMS());
                owner.TaskHelper?.Enqueue(CancelTradeAddon, nameof(CancelTradeAddon));
                return;
            }

            if (currentMoney <= value && checkState is { SelfConfirmed: false, OtherConfirmed: true })
            {
                owner.TaskHelper?.DelayNext(owner.GetRandomDelayMS());
                owner.TaskHelper?.Enqueue(ConfirmPreCheck, nameof(ConfirmPreCheck));
            }
        }

        private void ConfirmFinal()
        {
            if (tradePlan.TryGetValue(lastTradeEntityID, out var value) && currentMoney <= value)
            {
                owner.TaskHelper?.DelayNext(owner.GetRandomDelayMS());
                owner.TaskHelper?.Enqueue(() => FinalCheckTradeAddon(), nameof(FinalCheckTradeAddon));
            }
        }

        private void CancelTrade()
        {
            isTrading = false;
            pendingTradeRequests.Clear();
            checkState = default;

            owner.TaskHelper?.Abort();
        }

        private void CompleteTrade()
        {
            isTrading = false;
            pendingTradeRequests.Clear();
            checkState = default;

            if (!tradePlan.ContainsKey(lastTradeEntityID))
                DLog.Warning(Lang.Get("AutoSendMoney-NoPlan"));
            else
            {
                tradePlan[lastTradeEntityID] -= currentMoney;

                if (tradePlan[lastTradeEntityID] <= 0)
                {
                    tradePlan.Remove(lastTradeEntityID);
                    owner.editPlan.Remove(lastTradeEntityID);
                }
            }

            if (tradePlan.Count == 0)
                StopSelf();
        }

        private void IssueTradeRequest(uint entityID, GameObject* gameObjectAddress)
        {
            TargetSystem.Instance()->Target = gameObjectAddress;
            InventoryManager.Instance()->SendTradeRequest(entityID);
        }

        private bool SetTradeGil(uint money)
        {
            if (!Trade->IsAddonAndNodesReady()) return false;

            InventoryManager.Instance()->SetTradeGilAmount(money);
            currentMoney = money;
            return true;
        }

        private bool ConfirmPreCheck()
        {
            if (!Trade->IsAddonAndNodesReady()) return false;
            if (checkState.SelfConfirmed) return true;

            Trade->GetComponentButtonById(33)->Click();
            checkState = checkState with { SelfConfirmed = true };
            return true;
        }

        private void OnFrameworkTick(IFramework framework) =>
            TryQueueNextTrade();

        private void TryQueueNextTrade()
        {
            if (tradePlan.Count == 0)
            {
                StopSelf();
                return;
            }

            var taskHelper = owner.TaskHelper;
            if (taskHelper == null) return;

            if (isTrading || taskHelper.IsBusy)
                return;

            if (TrySelectTarget(out var entityID, out var targetAddress))
            {
                if (!pendingTradeRequests.Add(entityID))
                    return;

                taskHelper.DelayNext(owner.GetRandomDelayMS(), $"{nameof(AutoSendMoney)}-IssueTradeRequest");
                taskHelper.Enqueue(() => IssueTradeRequest(entityID, (GameObject*)targetAddress), $"{nameof(IssueTradeRequest)}({entityID})");
            }
        }

        private void StopSelf()
        {
            owner.runtime = null;
            Dispose();
        }

        private bool TrySelectTarget(out uint entityID, out nint address)
        {
            entityID = 0;
            address  = 0;

            if (lastTradeEntityID != 0 && tradePlan.ContainsKey(lastTradeEntityID) && !pendingTradeRequests.Contains(lastTradeEntityID))
            {
                var target = DService.Instance().ObjectTable.SearchByEntityID(lastTradeEntityID);

                if (target != null && IsWithinTradeDistance(target.Position))
                {
                    entityID = lastTradeEntityID;
                    address  = target.Address;
                    return true;
                }
            }

            foreach (var id in tradePlan.Keys)
            {
                if (pendingTradeRequests.Contains(id)) continue;

                var target = DService.Instance().ObjectTable.SearchByEntityID(id);
                if (target == null) continue;

                if (!IsWithinTradeDistance(target.Position)) continue;

                entityID = id;
                address  = target.Address;
                return true;
            }

            return false;
        }

        private static void CancelTradeAddon()
        {
            if (Trade == null) return;
            Trade->Callback(1, 0);
        }

        private static void FinalCheckTradeAddon(bool confirm = true)
        {
            if (SelectYesno == null) return;
            SelectYesno->Callback(confirm ? 0 : 1);
        }
        
        #region 常量

        private static readonly FrozenSet<uint>        TradeFinishLogMessages = [10920, 10921, 10922, 10923];

        #endregion
    }
    
    #region 常量

    private const uint MAXIMUM_GIL_PER_TRADE = 1_000_000;

    #endregion
}

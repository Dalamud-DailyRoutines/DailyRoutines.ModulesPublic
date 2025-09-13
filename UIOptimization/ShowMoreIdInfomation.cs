using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using ActionKind = FFXIVClientStructs.FFXIV.Client.UI.Agent.ActionKind;
using RowStatus = Lumina.Excel.Sheets.Status;

namespace DailyRoutines.ModulesPublic;

public unsafe class ShowMoreIdInfomation : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("ShowMoreIdInfomationTitle"),
        Description = GetLoc("ShowMoreIdInfomationDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Middo"]
    };

    private static readonly CompSig GenerateItemTooltipSig = new("48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC ?? 48 8B 42 ?? 4C 8B EA");
    private delegate void* GenerateItemTooltipDelegate(AtkUnitBase* addonItemDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData);
    private static Hook<GenerateItemTooltipDelegate>? GenerateItemTooltipHook;

    private static readonly CompSig ActionTooltipSig = new("48 89 5C 24 ?? 55 56 57 41 56 41 57 48 83 EC ?? 48 8B 9A");
    private delegate nint ActionTooltipDelegate(AtkUnitBase* a1, void* a2, ulong a3);
    private static Hook<ActionTooltipDelegate>? ActionTooltipHook;

    private static readonly CompSig ActionHoveredSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 83 EC ?? 45 8B F1 41 8B D8");
    private delegate void ActionHoveredDelegate(AgentActionDetail* agent,ActionKind actionKind, uint actionId, int flag, byte isLovmActionDetail);
    private static Hook<ActionHoveredDelegate>? ActionHoveredHook;

    private static readonly CompSig TooltipShowSig = new("66 44 89 44 24 ?? 55 53 41 54");
    private delegate void TooltipShowDelegate(AtkTooltipManager* atkTooltipManager, AtkTooltipManager.AtkTooltipType type, ushort parentId, AtkResNode* targetNode, AtkTooltipManager.AtkTooltipArgs* tooltipArgs, long unkDelegate, byte unk7, byte unk8);
    private static Hook<TooltipShowDelegate>? TooltipShowHook;

    private static Config ModuleConfig = null!;

    private static uint HoveredActionid = 0;

    private static IDtrBarEntry? MapIdEntry;

    protected override void Init() 
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        MapIdEntry ??= DService.DtrBar.Get("ShowMoreIdInfomation-MapId");
        
        GenerateItemTooltipHook ??= GenerateItemTooltipSig.GetHook<GenerateItemTooltipDelegate>(OnGenerateItemTooltipDetour);
        GenerateItemTooltipHook.Enable();

        ActionTooltipHook ??= ActionTooltipSig.GetHook<ActionTooltipDelegate>(OnActionTooltipDetour);
        ActionTooltipHook.Enable();

        ActionHoveredHook ??= ActionHoveredSig.GetHook<ActionHoveredDelegate>(OnActionHoveredDetour);
        ActionHoveredHook.Enable();

        TooltipShowHook ??= TooltipShowSig.GetHook<TooltipShowDelegate>(OnTooltipShowDetour);
        TooltipShowHook.Enable();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh,       "ActionDetail", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh,         "ItemDetail", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_TargetInfoMainTarget", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,              "_NaviMap", OnAddon);
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Group())
        {
            if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ShowItemId"), ref ModuleConfig.ShowItemId))
                SaveConfig(ModuleConfig);
            ImGui.SameLine();
            if (ModuleConfig.ShowItemId)
            {
                if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ItemIdUseHexId"), ref ModuleConfig.ItemIdUseHexId))
                    SaveConfig(ModuleConfig);
                if (ModuleConfig.ItemIdUseHexId)
                {
                    ImGui.SameLine();
                    if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ItemIdUseBothHexAndDecimal"), ref ModuleConfig.ItemIdUseBothHexAndDecimal))
                        SaveConfig(ModuleConfig);
                }
            }
        }

        using (ImRaii.Group())
        {
            if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ShowActionId"), ref ModuleConfig.ShowActionId))
                SaveConfig(ModuleConfig);
            if (ModuleConfig.ShowActionId)
            {
                ImGui.SameLine();
                if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ActionIdUseHex"), ref ModuleConfig.ActionIdUseHex))
                    SaveConfig(ModuleConfig);
                ImGui.SameLine();
                if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ShowResolvedActionId"), ref ModuleConfig.ShowResolvedActionId))
                    SaveConfig(ModuleConfig);               
            }

            ImRaii.PushIndent(2);
            if (ModuleConfig.ActionIdUseHex)
            {
                if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ActionIdUseBothHexAndDecimal"), ref ModuleConfig.ActionIdUseBothHexAndDecimal))
                {
                    ModuleConfig.ShowOriginalActionId = false;
                    SaveConfig(ModuleConfig);
                }
            }
            if (ModuleConfig.ShowResolvedActionId && ModuleConfig.ActionIdUseHex) 
                ImGui.SameLine();             
            if (ModuleConfig.ShowResolvedActionId)
            {
                if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ShowOriginalActionId"), ref ModuleConfig.ShowOriginalActionId))
                {
                    ModuleConfig.ActionIdUseBothHexAndDecimal = false;
                    SaveConfig(ModuleConfig);
                }  
            }
        }

        using (ImRaii.Group())
        {
            if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ShowTargetId"), ref ModuleConfig.ShowTargetId))
                SaveConfig(ModuleConfig);
            ImGui.SameLine();
            if (ModuleConfig.ShowTargetId)
            {
                if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ShowBattleNpcTargetId"), ref ModuleConfig.ShowBattleNpcTargetId))
                    SaveConfig(ModuleConfig);
                ImGui.SameLine();
                if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ShowEventNpcTargetId"), ref ModuleConfig.ShowEventNpcTargetId))
                    SaveConfig(ModuleConfig);
                ImGui.SameLine();
                if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ShowCompanionTargetId"), ref ModuleConfig.ShowCompanionTargetId))
                    SaveConfig(ModuleConfig);
                ImGui.SameLine();
                if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ShowOthersTargetId"), ref ModuleConfig.ShowOthersTargetId))
                    SaveConfig(ModuleConfig);
            }
        }

        using (ImRaii.Group())
        {
            if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ShowBuffId"), ref ModuleConfig.ShowBuffId))
                SaveConfig(ModuleConfig);
            ImGui.SameLine();
            if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ShowWeatherId"), ref ModuleConfig.ShowWeatherId))
                SaveConfig(ModuleConfig);
            ImGui.SameLine();
            if (ImGui.Checkbox(GetLoc("ShowMoreIdInfomation-ShowMapId"), ref ModuleConfig.ShowMapId))
                SaveConfig(ModuleConfig);
        }
    }
    
    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (args.AddonName)
        {
            case "ActionDetail":
                if (ActionDetail== null) return;

                var actionTextNode = ActionDetail->GetTextNodeById(35);
                if (actionTextNode == null) return;

                actionTextNode->TextFlags |= (byte)TextFlags.MultiLine;
                break;
            case "ItemDetail":
                if (ItemDetail== null) return;

                var itemTextnode = ItemDetail->GetTextNodeById(6);
                if (itemTextnode == null) return;

                itemTextnode->TextFlags |= (byte)TextFlags.MultiLine;
                break;
            case "_TargetInfoMainTarget":
                if (TargetInfoMainTarget == null) return;
                
                var targetNameNode = TargetInfoMainTarget->GetNodeById(10)->GetAsAtkTextNode();
                var target = DService.Targets.Target;
                if (targetNameNode == null || target == null)  return;

                var targetid = target.DataId;
                if (!ModuleConfig.ShowTargetId || targetid == 0) return;

                var show = target.ObjectKind switch
                {
                    ObjectKind.BattleNpc   => ModuleConfig.ShowBattleNpcTargetId,
                    ObjectKind.EventNpc    => ModuleConfig.ShowEventNpcTargetId,
                    ObjectKind.Companion   => ModuleConfig.ShowCompanionTargetId,
                    _                      => ModuleConfig.ShowOthersTargetId,
                };
                if (!show) return;

                var name = targetNameNode->NodeText.ExtractText();
                if (name != null && !name.Contains($"[{targetid}]")) 
                    targetNameNode->NodeText.SetString($"{name}  [{targetid}]");

                break;
            case "_NaviMap":
                MapIdEntry.Shown = ModuleConfig.ShowMapId;

                var mapId = DService.ClientState.MapId;
                if (mapId == 0) return;

                MapIdEntry.Text = $"{GetLoc("ShowMoreIdInfomation-CurrentMapIdIs")}{mapId}";
                break;
        }
    }

    public void OnTooltipShowDetour(AtkTooltipManager* atkTooltipManager, AtkTooltipManager.AtkTooltipType type, ushort parentId, AtkResNode* targetNode, AtkTooltipManager.AtkTooltipArgs* tooltipArgs, long unkDelegate, byte unk7, byte unk8)
    {
        if (ModuleConfig.ShowBuffId) 
            TooltipBuffIdAdd(targetNode, tooltipArgs);
        if (ModuleConfig.ShowWeatherId)
            TooltipWeatherAdd(parentId, targetNode, tooltipArgs);

        TooltipShowHook?.Original(atkTooltipManager, type, parentId, targetNode, tooltipArgs, unkDelegate, unk7, unk8);
    }

    public void* OnGenerateItemTooltipDetour(AtkUnitBase* addonItemDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData) 
    {
        if (!ModuleConfig.ShowItemId) return GenerateItemTooltipHook.Original(addonItemDetail, numberArrayData, stringArrayData); 

        var seStr = MemoryHelper.ReadSeStringNullTerminated((nint)stringArrayData->StringArray[2]); // 此处与下方set函数中的 2 均为ItemUiCategory在tooltip中的索引
        if (seStr == null) return GenerateItemTooltipHook.Original(addonItemDetail, numberArrayData, stringArrayData);
        if (seStr.TextValue.EndsWith(']')) return GenerateItemTooltipHook.Original(addonItemDetail, numberArrayData, stringArrayData);

        var id = AgentItemDetail.Instance()->ItemId;
        if (id < 2000000) 
            id %= 500000;

        seStr.Payloads.Add(new UIForegroundPayload(3));
        seStr.Payloads.Add(new TextPayload("   ["));
        if (ModuleConfig.ItemIdUseHexId == false || ModuleConfig.ItemIdUseBothHexAndDecimal) 
            seStr.Payloads.Add(new TextPayload($"{id}"));

        if (ModuleConfig.ItemIdUseHexId) 
        {
            if (ModuleConfig.ItemIdUseBothHexAndDecimal) 
                seStr.Payloads.Add(new TextPayload(" - "));
            seStr.Payloads.Add(new TextPayload($"0x{id:X}"));
        }

        seStr.Payloads.Add(new TextPayload("]"));
        seStr.Payloads.Add(new UIForegroundPayload(0));

        stringArrayData->SetValue(2, seStr.EncodeWithNullTerminator(), false);

        return GenerateItemTooltipHook.Original(addonItemDetail, numberArrayData, stringArrayData);
    }

    private void OnActionHoveredDetour(AgentActionDetail* agent, ActionKind actionKind, uint actionId, int flag, byte IsLovmActionDetail) 
    {
        HoveredActionid = actionId;

        ActionHoveredHook?.Original(agent, actionKind, actionId, flag, IsLovmActionDetail);
    }

    private nint OnActionTooltipDetour(AtkUnitBase* addon, void* a2, ulong a3) 
    {
        if(!ModuleConfig.ShowActionId) return ActionTooltipHook.Original(addon, a2, a3);

        var categoryText = addon->GetTextNodeById(6);
        if (categoryText == null) return ActionTooltipHook.Original(addon, a2, a3);

        var seStr = MemoryHelper.ReadSeStringNullTerminated((nint)categoryText->NodeText.StringPtr.Value);
        if (seStr.Payloads.Count > 1) return ActionTooltipHook.Original(addon, a2, a3);

        var id = ModuleConfig.ShowResolvedActionId ? ActionManager.Instance()->GetAdjustedActionId(HoveredActionid) : HoveredActionid;
        if (seStr.Payloads.Count >= 1) 
            {
            if (ModuleConfig is { ShowResolvedActionId:true, ShowOriginalActionId:true, ActionIdUseBothHexAndDecimal:false} && id != HoveredActionid)
                seStr.Payloads.Add(new NewLinePayload());
            else
                seStr.Payloads.Add(new TextPayload("   "));
        }

        seStr.Payloads.Add(new UIForegroundPayload(3));
        seStr.Payloads.Add(new TextPayload("["));

        if (ModuleConfig is { ShowResolvedActionId: true, ShowOriginalActionId: true, ActionIdUseBothHexAndDecimal: false } && id != HoveredActionid)
        {
            if(ModuleConfig.ActionIdUseHex)
                seStr.Payloads.Add(new TextPayload($"0x{HoveredActionid:X}→"));
            else
                seStr.Payloads.Add(new TextPayload($"{HoveredActionid}→"));
        }

        if (!ModuleConfig.ActionIdUseHex || ModuleConfig.ActionIdUseBothHexAndDecimal)
                seStr.Payloads.Add(new TextPayload($"{id}"));

        if (ModuleConfig.ActionIdUseHex) 
        {
            if (ModuleConfig.ActionIdUseBothHexAndDecimal) 
                seStr.Payloads.Add(new TextPayload(" - "));
            seStr.Payloads.Add(new TextPayload($"0x{id:X}"));
        }

        seStr.Payloads.Add(new TextPayload("]"));
        seStr.Payloads.Add(new UIForegroundPayload(0));
        categoryText->SetText(seStr.EncodeWithNullTerminator());

        return ActionTooltipHook.Original(addon, a2, a3);
    }    
    
    private void TooltipBuffIdAdd(AtkResNode* targetNode, AtkTooltipManager.AtkTooltipArgs* tooltipArgs)
    {
        Dictionary<uint, uint> IconStatusIDMap = [];

        var localPlayer = DService.ObjectTable.LocalPlayer;
        if (localPlayer == null || targetNode == null) return;

        var imageNode = targetNode->GetAsAtkImageNode();
        if (imageNode == null) return;

        var iconId = imageNode->PartsList->Parts[imageNode->PartId].UldAsset->AtkTexture.Resource->IconId;
        if (iconId < 210000 || iconId > 230000) return;

        var rawStr = (string)tooltipArgs->Text;
        if (rawStr == null) return;

        var currentTarget = DService.Targets.Target;
        if (currentTarget != null && currentTarget != localPlayer)
            AddStatusesToMap(currentTarget.ToBCStruct()->StatusManager, ref IconStatusIDMap);

        var focusTarget = DService.Targets.FocusTarget;
        if (focusTarget != null)
            AddStatusesToMap(focusTarget.ToBCStruct()->StatusManager, ref IconStatusIDMap);

        var partyList = AgentHUD.Instance()->PartyMembers;
        foreach (var member in partyList.ToArray().Where(m => m.Index != 0))
        {
            if (member.Object != null)
                AddStatusesToMap(member.Object->StatusManager, ref IconStatusIDMap);
        }

        AddStatusesToMap(localPlayer.ToBCStruct()->StatusManager, ref IconStatusIDMap);

        if (!IconStatusIDMap.TryGetValue(iconId, out var statuId)) return;

        if (rawStr.Contains($"[{statuId}]") || statuId == 0) return;

        SeString RegexedStr = Regex.Replace(rawStr, @"^(.*?)(?=\(|（|\n|$)", "$1" + $"  [{statuId}]"); // 正则表达式含义为匹配第一个左括号或换行符

        SetTooltipCStringPointer(ref tooltipArgs->Text, RegexedStr);
    }

    private void TooltipWeatherAdd(uint parentId, AtkResNode* targetNode, AtkTooltipManager.AtkTooltipArgs* tooltipArgs)
    {
        if (targetNode == null || NaviMap == null || parentId != NaviMap->Id) return;

        var compNode = targetNode->ParentNode->GetAsAtkComponentNode();
        if (compNode == null) return;

        var imageNode = compNode->Component->UldManager.SearchNodeById(3)->GetAsAtkImageNode();
        if (imageNode == null) return;

        var iconId = imageNode->PartsList->Parts[imageNode->PartId].UldAsset->AtkTexture.Resource->IconId;
        var weatherId = WeatherManager.Instance()->WeatherId;

        if (LuminaGetter.TryGetRow<Weather>(weatherId, out var weather))
        {
            if (weather.Icon != iconId) return;
            SeString processedStr = $"{tooltipArgs->Text} [{weatherId}]";

            SetTooltipCStringPointer(ref tooltipArgs->Text, processedStr);
        }
    }

    private static unsafe void AddStatusesToMap(StatusManager statusesManager,ref Dictionary<uint, uint> map)
    {
        foreach (var statuse in statusesManager.Status)
        {
            if (statuse.StatusId == 0) continue;
            if (!LuminaGetter.TryGetRow<RowStatus>(statuse.StatusId, out var status))
                continue;

            map.TryAdd(status.Icon, status.RowId);

            for (var i = 1; i <= statuse.Param; i++)
                map.TryAdd((uint)(status.Icon + i), status.RowId);
        }
    }

    protected static void SetTooltipCStringPointer(ref CStringPointer cStringPointer, SeString seString)
    {
        var bytes = seString.EncodeWithNullTerminator();
        var ptr = (byte*)Marshal.AllocHGlobal(bytes.Length);

        for (var i = 0; i < bytes.Length; i++)
            ptr[i] = bytes[i];

        cStringPointer = ptr;
    }

    protected override void Uninit()
    {
        MapIdEntry.Remove();
        MapIdEntry = null;

        DService.AddonLifecycle.UnregisterListener(OnAddon);

        GenerateItemTooltipHook.Disable();
        ActionTooltipHook.Disable();
        ActionHoveredHook.Disable();
        TooltipShowHook.Disable();
    }

    public class Config : ModuleConfiguration
    {
        public bool ShowItemId                   =  true;
        public bool ItemIdUseHexId               = false;
        public bool ItemIdUseBothHexAndDecimal   = false;

        public bool ShowActionId                 =  true;
        public bool ActionIdUseHex               = false;
        public bool ActionIdUseBothHexAndDecimal = false;
        public bool ShowResolvedActionId         = false;
        public bool ShowOriginalActionId         = false;

        public bool ShowBuffId                   = false;
        public bool ShowWeatherId                = false;
        public bool ShowMapId                    = false;
        public bool ShowTargetId                 = false;

        public bool ShowBattleNpcTargetId        = false;
        public bool ShowCompanionTargetId        = false;
        public bool ShowEventNpcTargetId         = false;
        public bool ShowOthersTargetId           = false;
    }
}

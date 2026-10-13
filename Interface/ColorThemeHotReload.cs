using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Iced.Intel;
using InteropGenerator.Runtime;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Uld;
using OmenTools.Dalamud;
using OmenTools.Info.Lumina.ULD;
using Decoder = Iced.Intel.Decoder;
using AtkUIColorHolder = FFXIVClientStructs.Expansion.FFXIV.Component.GUI.AtkUIColorHolder;
using ExpansionTextNode = FFXIVClientStructs.Expansion.FFXIV.Component.GUI.AtkTextNode;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class ColorThemeHotReload : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ColorThemeHotReloadTitle"),
        Description = Lang.Get("ColorThemeHotReloadDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Hook<AtkUIColorHolder.Delegates.LoadColors>? LoadColorsHook;
    private Hook<AtkUnitBase.Delegates.LoadUldByName>?         LoadUldByNameHook;

    private readonly Dictionary<nint, string>              addonPaths      = [];
    private readonly Dictionary<nint, string[]>            loaderPaths     = [];
    private readonly Dictionary<string, ColorThemeULDData> resources       = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, uint>                loadedColors    = [];
    private readonly HashSet<nint>                         visitedManagers = [];

    private byte loadedTheme;

    protected override void Init()
    {
        var holder = AtkStage.Instance()->AtkUIColorHolder;
        loadedTheme = holder->ActiveColorThemeType;
        CaptureColors((AtkUIColorHolder*)holder);

        LoadColorsHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AtkUIColorHolder.MemberFunctionPointers),
            nameof(AtkUIColorHolder.MemberFunctionPointers.LoadColors),
            (AtkUIColorHolder.Delegates.LoadColors)LoadColorsDetour
        );
        LoadColorsHook.Enable();

        LoadUldByNameHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AtkUnitBase.MemberFunctionPointers),
            nameof(AtkUnitBase.MemberFunctionPointers.LoadUldByName),
            (AtkUnitBase.Delegates.LoadUldByName)LoadUldByNameDetour
        );
        LoadUldByNameHook.Enable();

        IGameConfig.Instance().SystemChanged += OnConfigChanged;
        ColorHelper.TextStyleChanged         += OnKTKTextStyleChanged;

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, OnAddon);

        var units = &AtkStage.Instance()->RaptureAtkUnitManager->AllLoadedUnitsList;

        for (var i = 0; i < units->Count; i++)
        {
            var addon = units->Entries[i].Value;
            if (addon == null || addon->UldManager.LoadedState != AtkLoadState.Loaded)
                continue;

            try
            {
                BindResource(addon);
            }
            catch (Exception ex)
            {
                DLog.Error($"[ColorThemeHotReload] 无法绑定 {addon->NameString} 的界面资源。", ex);
            }
        }
    }

    protected override void Uninit()
    {
        IGameConfig.Instance().SystemChanged -= OnConfigChanged;
        ColorHelper.TextStyleChanged         -= OnKTKTextStyleChanged;
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

        LoadColorsHook?.Disable();
        LoadUldByNameHook?.Disable();

        addonPaths.Clear();
        loaderPaths.Clear();
        resources.Clear();
        loadedColors.Clear();
        visitedManagers.Clear();
    }

    private static void OnConfigChanged
    (
        object?           sender,
        ConfigChangeEvent e
    )
    {
        if (e.Name != nameof(SystemConfigOption.ColorThemeType))
            return;

        var theme  = IGameConfig.Instance().System.GetUInt(nameof(SystemConfigOption.ColorThemeType));
        var holder = (AtkUIColorHolder*)AtkStage.Instance()->AtkUIColorHolder;
        if (holder->ActiveColorThemeType != theme)
            holder->SetActiveColorThemeType((byte)theme);
    }

    private bool LoadUldByNameDetour
    (
        AtkUnitBase*   addon,
        CStringPointer name,
        byte           keepHandle,
        uint           resourceCategory
    )
    {
        var result = LoadUldByNameHook.Original(addon, name, keepHandle, resourceCategory);
        if (result)
            addonPaths[(nint)addon] = addon->UldManager.UldResourceHandle->FileName.ToString();

        return result;
    }

    private void LoadColorsDetour
    (
        AtkUIColorHolder* holder
    )
    {
        if (holder != AtkStage.Instance()->AtkUIColorHolder)
        {
            LoadColorsHook.Original(holder);
            return;
        }

        CaptureColors(holder);
        LoadColorsHook.Original(holder);

        try
        {
            visitedManagers.Clear();
            var units = &AtkStage.Instance()->RaptureAtkUnitManager->AllLoadedUnitsList;

            for (var i = 0; i < units->Count; i++)
            {
                var addon = units->Entries[i].Value;
                if (addon == null || addon->UldManager.LoadedState != AtkLoadState.Loaded)
                    continue;

                try
                {
                    if (!addonPaths.TryGetValue((nint)addon, out var path))
                        path = BindResource(addon);

                    if (path != null && resources.TryGetValue(path, out var data))
                        RefreshManager(&addon->UldManager, data, holder);
                }
                catch (Exception ex)
                {
                    DLog.Error($"[ColorThemeHotReload] 无法绑定或刷新 {addon->NameString} 的界面资源。", ex);
                }
            }
        }
        finally
        {
            ColorHelper.RefreshColorTheme();
            loadedTheme = holder->ActiveColorThemeType;
            visitedManagers.Clear();
        }
    }

    private static void OnKTKTextStyleChanged
    (
        TextNode node
    )
    {
        if (node.Node == null || !node.Node->TextFlags.IsSet(TextFlags.FontCache))
            return;

        ((ExpansionTextNode*)node.Node)->RebuildFontCache();
    }

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        var address = (nint)args.Addon;

        if (type == AddonEvent.PreFinalize)
            addonPaths.Remove(address);
        else
        {
            var addon = (AtkUnitBase*)address;

            try
            {
                var path   = BindResource(addon);
                var holder = AtkStage.Instance()->AtkUIColorHolder;

                if (path != null && holder->SetupState == 2)
                {
                    visitedManagers.Clear();
                    RefreshManager(&addon->UldManager, resources[path], (AtkUIColorHolder*)holder);
                    visitedManagers.Clear();
                }
            }
            catch (Exception ex)
            {
                DLog.Error($"[ColorThemeHotReload] 无法刷新 {addon->NameString} 的界面资源。", ex);
            }
        }
    }

    private string? BindResource
    (
        AtkUnitBase* addon
    )
    {
        var manager = &addon->UldManager;
        if (!manager->ResourceFlags.IsSet(AtkUldManagerResourceFlag.AssetsHaveThemeSupport))
            return null;

        if (addonPaths.TryGetValue((nint)addon, out var recordedPath))
        {
            LoadResource(recordedPath);
            return recordedPath;
        }

        if (manager->UldResourceHandle != null)
        {
            var path = manager->UldResourceHandle->FileName.ToString();
            LoadResource(path);
            addonPaths[(nint)addon] = path;
            return path;
        }

        var loader = (nint)addon->VirtualTable->LoadUldResourceHandle;

        if (!loaderPaths.TryGetValue(loader, out var paths))
        {
            paths = ReadLoaderPaths(loader);
            loaderPaths.Add(loader, paths);
        }

        string? matchedPath = null;

        foreach (var path in paths)
        {
            var data = LoadResource(path);
            if (data == null || !data.Matches(manager))
                continue;

            if (matchedPath != null)
                throw new InvalidOperationException($"无法唯一确定 {addon->NameString} 的 ULD 资源。");

            matchedPath = path;
        }

        matchedPath = matchedPath ?? throw new InvalidOperationException($"无法取得 {addon->NameString} 的 ULD 资源。");

        addonPaths[(nint)addon] = matchedPath;
        return matchedPath;
    }

    private ColorThemeULDData? LoadResource
    (
        string path
    )
    {
        if (resources.TryGetValue(path, out var data))
            return data;

        var file = IDataManager.Instance().GetFile<UldFile>(path);
        if (file == null)
            return null;

        data = new ColorThemeULDData(file);
        resources.Add(path, data);
        return data;
    }

    private static string[] ReadLoaderPaths
    (
        nint loader
    )
    {
        var scanner           = ISigScanner.Instance();
        var moduleBase        = scanner.Module.BaseAddress;
        var codeStart         = moduleBase + (nint)scanner.TextSectionOffset;
        var codeEnd           = codeStart  + scanner.TextSectionSize;
        var dataStart         = moduleBase + (nint)scanner.RDataSectionOffset;
        var dataEnd           = dataStart  + scanner.RDataSectionSize;
        var pending           = new Queue<nint>();
        var visited           = new HashSet<nint>();
        var paths             = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var instructionBudget = 2048;
        pending.Enqueue(loader);

        while (pending.TryDequeue(out var address))
        {
            while (address >= codeStart && address < codeEnd && visited.Add(address))
            {
                if (--instructionBudget < 0)
                    throw new InvalidOperationException("ULD 加载方法超出指令读取范围。");

                var bytes = new ReadOnlySpan<byte>
                (
                    (void*)(scanner.SearchBase + address - moduleBase),
                    (int)Math.Min(15, codeEnd - address)
                ).ToArray();
                var decoder = Decoder.Create(64, bytes);
                decoder.IP = (ulong)address;
                decoder.Decode(out var instruction);
                if (instruction.IsInvalid)
                    break;

                if (instruction is { Mnemonic: Mnemonic.Lea, IsIPRelativeMemoryOperand: true })
                {
                    var nameAddress = (nint)instruction.IPRelativeMemoryAddress;

                    if (nameAddress >= dataStart && nameAddress < dataEnd)
                    {
                        var nameBytes = new ReadOnlySpan<byte>
                        (
                            (void*)(scanner.SearchBase + nameAddress - moduleBase),
                            (int)Math.Min(64, dataEnd - nameAddress)
                        );
                        var length = nameBytes.IndexOf((byte)0);
                        if (length > 0 &&
                            nameBytes[..length].IndexOfAnyExcept(UldNameCharacters) <
                            0)
                            paths.Add($"ui/uld/{Encoding.UTF8.GetString(nameBytes[..length])}.uld");
                    }
                }

                if (instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)
                {
                    var target = (nint)instruction.NearBranchTarget;
                    if (target != (nint)AtkUnitBase.MemberFunctionPointers.LoadUldByName)
                        pending.Enqueue(target);
                }

                if (instruction.FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch or
                    FlowControl.IndirectBranch or FlowControl.Exception)
                    break;

                address = (nint)instruction.NextIP;
            }
        }

        return [.. paths];
    }

    private void CaptureColors
    (
        AtkUIColorHolder* holder
    )
    {
        loadedColors.Clear();

        foreach (var color in holder->UIColors)
        {
            loadedColors[color.RowId] = loadedTheme == 0 ?
                                            color.Color :
                                            color.ThemedColor;
        }
    }

    private void RefreshManager
    (
        AtkUldManager*    manager,
        ColorThemeULDData data,
        AtkUIColorHolder* holder
    )
    {
        if (!visitedManagers.Add((nint)manager) || manager->Objects == null)
            return;

        var useTheme    = manager->ResourceFlags.IsSet(AtkUldManagerResourceFlag.AssetsHaveThemeSupport);
        var definitions = data.WidgetNodes;
        if (manager->BaseType == AtkUldManagerBaseType.Component &&
            !data.ComponentNodes.TryGetValue(manager->Objects->Id, out definitions))
            return;

        if (manager->BaseType == AtkUldManagerBaseType.Widget && useTheme)
        {
            RefreshTextures(manager, data, holder->ActiveColorThemeType);
            RefreshTimelineColors(manager->TimelineManager, data, holder, useTheme);
        }

        for (var i = 0; i < manager->Objects->NodeCount; i++)
            RefreshNode(manager->Objects->NodeList[i], definitions, data, holder, useTheme);

        for (var entry = manager->DuplicateObjectsList.End; entry != null; entry = entry->Previous)
        {
            var duplicates = entry->Value.Value;
            for (var i = 0; i < duplicates->NodeCount; i++)
                RefreshNode((AtkResNode*)&duplicates->NodeList[i], definitions, data, holder, useTheme);
        }
    }

    private void RefreshNode
    (
        AtkResNode*                              node,
        FrozenDictionary<uint, UldRoot.NodeData> definitions,
        ColorThemeULDData                        data,
        AtkUIColorHolder*                        holder,
        bool                                     useTheme
    )
    {
        if (node == null)
            return;

        if ((ushort)node->Type >= 1000)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component == null || component->UldManager.Objects == null)
                return;

            RefreshManager(&component->UldManager, data, holder);
            var info = (AtkUldComponentInfo*)component->UldManager.Objects;

            if (info->ComponentType == ComponentType.NumericInput)
            {
                var componentUsesTheme = component->UldManager.ResourceFlags.IsSet(AtkUldManagerResourceFlag.AssetsHaveThemeSupport);
                RefreshNumericInput((AtkComponentNumericInput*)component, data, holder, componentUsesTheme);
            }
        }
        else if (node->Type == NodeType.Text                                        &&
                 definitions.TryGetValue(node->GetBaseNodeId(), out var definition) &&
                 definition.Node is NodeData.TextNode textData)
            RefreshTextNode((AtkTextNode*)node, textData, data, holder, useTheme);
    }

    private static void RefreshTextures
    (
        AtkUldManager*    manager,
        ColorThemeULDData data,
        byte              theme
    )
    {
        for (var i = 0; i < manager->AssetCount; i++)
        {
            var asset = &manager->Assets[i];
            if (!data.Assets.TryGetValue(asset->Id, out var definition) ||
                definition.IconId              != 0                     ||
                definition.ThemeSupportBitmask == 0                     ||
                asset->AtkTexture.TextureType  != TextureType.Resource)
                continue;

            var path = new string(definition.Path).TrimEnd('\0');
            if (path.Length == 0)
                continue;

            var separator = path.LastIndexOf('/');
            if (separator >= 0        &&
                theme is > 0 and <= 7 &&
                (definition.ThemeSupportBitmask & (1 << (theme - 1))) != 0)
                path = path.Insert(separator + 1, $"img{theme:D2}/");

            var resource = asset->AtkTexture.Resource;
            if (resource == null || resource->TexFileResourceHandle == null)
                continue;

            var scale       = resource->Version;
            var currentPath = resource->TexFileResourceHandle->FileName.ToString();
            var targetPath = scale == 1 ?
                                 path :
                                 path.Insert(path.LastIndexOf('.'), "_hr1");
            if (currentPath != targetPath)
                asset->AtkTexture.LoadTexture(path, scale);
        }
    }

    private void RefreshTextNode
    (
        AtkTextNode*      node,
        NodeData.TextNode data,
        ColorThemeULDData resourceData,
        AtkUIColorHolder* holder,
        bool              useTheme
    )
    {
        if (data.TextFlags2.IsSet(NodeData.TextFlags2.IsUIColor))
        {
            var expected = holder->GetColor(false, data.Color);
            if (useTheme && loadedColors.TryGetValue(data.Color, out var previous))
                expected = previous;

            if ((node->TextColor.RGBA & RGB_MASK) == (expected & RGB_MASK))
            {
                node->TextColor.RGBA = (holder->GetColor(useTheme, data.Color) & RGB_MASK) |
                                       (node->TextColor.RGBA                   & ALPHA_MASK);
            }
        }

        if (data.TextFlags2.IsSet(NodeData.TextFlags2.IsUIEdgeColor))
        {
            var expected = holder->GetColor(false, data.EdgeColor);
            if (useTheme && loadedColors.TryGetValue(data.EdgeColor, out var previous))
                expected = previous;

            if ((node->EdgeColor.RGBA & RGB_MASK) == (expected & RGB_MASK))
            {
                node->EdgeColor.RGBA = (holder->GetColor(useTheme, data.EdgeColor) & RGB_MASK) |
                                       (node->EdgeColor.RGBA                       & ALPHA_MASK);
            }
        }

        if (data.TextFlags.IsSet(NodeData.TextFlags.Emboss))
        {
            if (useTheme && holder->ActiveColorThemeType != 0)
                node->TextFlags &= ~TextFlags.Emboss;
            else
                node->TextFlags |= TextFlags.Emboss;
        }

        var timeline = node->Timeline;

        if (timeline           != null &&
            timeline->Resource != null &&
            resourceData.ColorTimelineFrames.ContainsKey(timeline->Resource->Id))
        {
            var mask = timeline->Mask;
            timeline->Mask |= AtkTimelineMask.VendorSpecific1 | AtkTimelineMask.VendorSpecific2;
            AtkTimelineKeyValue output;
            if (timeline->GetInterpolatedValue((int)UldRoot.KeyUsage.TextColor, AtkTimelineKeyGroupType.RGB, &output))
                node->TextColor.RGBA = (output.RGB.RGBA & RGB_MASK) | (node->TextColor.RGBA & ALPHA_MASK);

            if (timeline->GetInterpolatedValue((int)UldRoot.KeyUsage.EdgeColor, AtkTimelineKeyGroupType.RGB, &output))
                node->EdgeColor.RGBA = (output.RGB.RGBA & RGB_MASK) | (node->EdgeColor.RGBA & ALPHA_MASK);

            timeline->Mask = mask;
        }

        node->IsDirty = true;
        if (node->TextFlags.IsSet(TextFlags.FontCache))
            ((ExpansionTextNode*)node)->RebuildFontCache();
    }

    private void RefreshTimelineColors
    (
        AtkTimelineManager* manager,
        ColorThemeULDData   data,
        AtkUIColorHolder*   holder,
        bool                useTheme
    )
    {
        if (manager == null)
            return;

        for (var resourceIndex = 0; resourceIndex < manager->ResourceCount; resourceIndex++)
        {
            var resource = &manager->Resources[resourceIndex];
            if (!data.ColorTimelineFrames.TryGetValue(resource->Id, out var frames))
                continue;

            foreach (var definition in frames)
            {
                if (definition.AnimationIndex >= resource->AnimationCount)
                    continue;

                ref var group = ref resource->Animations[definition.AnimationIndex].KeyGroups[definition.GroupIndex];
                if (group.Type != AtkTimelineKeyGroupType.RGB || definition.FrameIndex >= group.KeyFrameCount)
                    continue;

                var frame = &group.KeyFrames[definition.FrameIndex];
                if (frame->FrameIdx != definition.FrameID)
                    continue;

                var expected = holder->GetColor(false, definition.ColorID);
                if (useTheme && loadedColors.TryGetValue(definition.ColorID, out var previous))
                    expected = previous;

                if ((frame->Value.RGB.RGBA & RGB_MASK) == (expected & RGB_MASK))
                {
                    frame->Value.RGB.RGBA = (holder->GetColor(useTheme, definition.ColorID) & RGB_MASK) |
                                            (frame->Value.RGB.RGBA                          & ALPHA_MASK);
                }
            }
        }
    }

    private void RefreshNumericInput
    (
        AtkComponentNumericInput* input,
        ColorThemeULDData         data,
        AtkUIColorHolder*         holder,
        bool                      useTheme
    )
    {
        var focusColor = holder->GetColor(useTheme, 67);
        var textNode   = input->AtkTextNode;

        if (textNode != null)
        {
            if (input->IsActive && (textNode->TextColor.RGBA & RGB_MASK) == (input->FocusColor.RGBA & RGB_MASK))
            {
                textNode->TextColor.RGBA = (focusColor & RGB_MASK) | (textNode->TextColor.RGBA & ALPHA_MASK);
                textNode->IsDirty        = true;
                if (textNode->TextFlags.IsSet(TextFlags.FontCache))
                    ((ExpansionTextNode*)textNode)->RebuildFontCache();
            }

            if (data.ComponentNodes.TryGetValue(input->UldManager.Objects->Id, out var definitions) &&
                definitions.TryGetValue(textNode->GetBaseNodeId(), out var definition)              &&
                definition.Node is NodeData.TextNode textData                                       &&
                textData.TextFlags2.IsSet(NodeData.TextFlags2.IsUIColor))
            {
                var expected = holder->GetColor(false, textData.Color);
                if (useTheme && loadedColors.TryGetValue(textData.Color, out var previous))
                    expected = previous;

                if ((input->DefaultTextColor.RGBA & RGB_MASK) == (expected & RGB_MASK))
                {
                    input->DefaultTextColor.RGBA = (holder->GetColor(useTheme, textData.Color) & RGB_MASK) |
                                                   (input->DefaultTextColor.RGBA               & ALPHA_MASK);
                }
            }
        }

        input->FocusColor.RGBA               = focusColor;
        input->InputDataBase.FocusColor.RGBA = focusColor;
        input->Data.FocusColor.RGBA          = focusColor;
        if (input->UldManager.ComponentData != null)
            ((AtkUldComponentDataNumericInput*)input->UldManager.ComponentData)->FocusColor.RGBA = focusColor;
    }

    #region 常量

    private const uint RGB_MASK   = 0x00FFFFFF;
    private const uint ALPHA_MASK = 0xFF000000;

    private static readonly SearchValues<byte> UldNameCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_"u8);

    #endregion
}

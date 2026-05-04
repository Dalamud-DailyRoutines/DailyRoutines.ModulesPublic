using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class DyeColorPreview : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "染剂颜色预览",
        Description = "鼠标移动至通用染剂、追加染剂1、追加染剂2时，会显示其可染颜色。",
        Category    = ModuleCategory.UIOptimization,
        Author      = ["ErxCharlotte"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private DyeInfo? currentDye;
    private bool     hideNativeTooltip;

    protected override void Init()
    {
        DService.Instance().GameGUI.HoveredItemChanged += OnHoveredItemChanged;
        DService.Instance().UIBuilder.Draw             += DrawOverlay;

        foreach (var addonName in TooltipAddonNames)
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw, addonName, OnTooltipPreDraw);
    }

    protected override void Uninit()
    {
        DService.Instance().GameGUI.HoveredItemChanged -= OnHoveredItemChanged;
        DService.Instance().UIBuilder.Draw             -= DrawOverlay;

        foreach (var addonName in TooltipAddonNames)
            DService.Instance().AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, addonName, OnTooltipPreDraw);

        currentDye        = null;
        hideNativeTooltip = false;
    }

    private void OnHoveredItemChanged(object? sender, ulong rawItemID)
    {
        currentDye        = null;
        hideNativeTooltip = false;

        if (!Dyes.TryGetValue(NormalizeItemID(rawItemID), out var dye)) return;

        currentDye        = dye;
        hideNativeTooltip = true;
    }

    private unsafe void OnTooltipPreDraw(AddonEvent type, AddonArgs args)
    {
        if (!hideNativeTooltip) return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        addon->IsVisible = false;
    }

    private void DrawOverlay()
    {
        if (currentDye == null) return;

        var count   = currentDye.StainIDs.Length;
        var compact = count > 40;

        var perRow     = compact ? 8 : 5;
        var cellWidth  = compact ? 56f : 82f;
        var cellHeight = compact ? 54f : 58f;
        var colorSize  = compact ? 34f : 42f;
        var rows       = (int)MathF.Ceiling(count / (float)perRow);
        var size       = new Vector2(perRow * cellWidth + 46f, (compact ? 104f : 84f) + rows * cellHeight + (compact ? 8f : 2f));
        var mouse      = ImGui.GetMousePos();
        var display    = ImGui.GetIO().DisplaySize;
        var pos        = mouse + new Vector2(36f, 24f);

        if (pos.X + size.X > display.X)
            pos.X = mouse.X - size.X - 36f;

        if (pos.Y + size.Y > display.Y)
            pos.Y = display.Y - size.Y - 20f;

        pos.X = MathF.Max(pos.X, 20f);
        pos.Y = MathF.Max(pos.Y, 20f);

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.95f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4f, 3f));
        ImGui.PushStyleColor(ImGuiCol.Border, currentDye.HighlightColor);

        if (ImGui.Begin("##DyeColorPreviewOverlay", WindowFlags))
        {
            ImGui.SetWindowFontScale(1.15f);
            ImGui.TextColored(currentDye.HighlightColor, $"★ {currentDye.Name}");
            ImGui.Text($"可用颜色：{count}");
            ImGui.Separator();
            DrawPalette(currentDye.StainIDs, perRow, cellWidth, cellHeight, colorSize);
            ImGui.SetWindowFontScale(1f);
        }

        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
    }

    private static void DrawPalette(uint[] stainIDs, int perRow, float cellWidth, float cellHeight, float colorSize)
    {
        var stainSheet = DService.Instance().Data.GetExcelSheet<Stain>();

        for (var i = 0; i < stainIDs.Length; i++)
        {
            var stainID = stainIDs[i];
            var stain   = stainSheet.GetRowOrDefault(stainID);
            if (stain == null) continue;

            var name  = stain.Value.Name.ExtractText();
            var color = stain.Value.Color;

            ImGui.PushID((int)stainID);
            DrawStainCell(stainID, name, color, cellWidth, cellHeight, colorSize);
            ImGui.PopID();

            if ((i + 1) % perRow != 0)
                ImGui.SameLine();
        }
    }

    private static void DrawStainCell(uint stainID, string name, uint color, float cellWidth, float cellHeight, float colorSize)
    {
        var start = ImGui.GetCursorScreenPos();

        ImGui.BeginGroup();

        var x = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(x + (cellWidth - colorSize) / 2f);
        ImGui.ColorButton("##Color", ToVector4(color), ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop, new Vector2(colorSize, colorSize));

        if (ImGui.IsItemHovered())
            DrawStainTooltip(stainID, name, color);

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 2f);

        var shortName = name.Replace("染剂", string.Empty);
        var textSize  = ImGui.CalcTextSize(shortName);

        ImGui.SetCursorPosX(x + MathF.Max(0f, (cellWidth - textSize.X) / 2f));
        ImGui.Text(shortName);
        ImGui.Dummy(new Vector2(cellWidth, MathF.Max(0f, cellHeight - colorSize - textSize.Y - 4f)));
        ImGui.EndGroup();

        if (!ImGui.IsItemHovered()) return;

        ImGui.GetWindowDrawList().AddRect
        (
            start,
            start + new Vector2(cellWidth, cellHeight),
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.75f)),
            6f,
            ImDrawFlags.None,
            1.5f
        );
    }

    private static void DrawStainTooltip(uint id, string name, uint color)
    {
        ImGui.BeginTooltip();
        ImGui.Text($"Stain ID: {id}");
        ImGui.Text($"名称: {name}");
        ImGui.Text($"HEX: #{color:X6}");
        ImGui.Text($"RGB: {GetR(color)}, {GetG(color)}, {GetB(color)}");
        ImGui.ColorButton("##TooltipColor", ToVector4(color), ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop, new Vector2(80f, 32f));
        ImGui.EndTooltip();
    }

    private static uint NormalizeItemID(ulong rawItemID)
    {
        if (rawItemID == 0) return 0;
        if (rawItemID > 1_000_000) rawItemID -= 1_000_000;
        return (uint)rawItemID;
    }

    private static Vector4 ToVector4(uint rgb) => new(GetR(rgb) / 255f, GetG(rgb) / 255f, GetB(rgb) / 255f, 1f);

    private static int GetR(uint rgb) => (int)((rgb >> 16) & 0xFF);
    private static int GetG(uint rgb) => (int)((rgb >> 8) & 0xFF);
    private static int GetB(uint rgb) => (int)(rgb & 0xFF);

    private sealed class DyeInfo
    {
        public required string  Name           { get; init; }
        public required Vector4 HighlightColor { get; init; }
        public required uint[]  StainIDs       { get; init; }
    }

    #region 常量

    private static readonly string[] TooltipAddonNames =
    [
        "ItemDetail",
        "ItemDetailCompare",
        "ItemDetailContext"
    ];

    private static readonly Dictionary<uint, DyeInfo> Dyes = new()
    {
        [52254] = new()
        {
            Name           = "通用染剂",
            HighlightColor = new(1f, 0.95f, 0.65f, 1f),
            StainIDs       = Enumerable.Range(1, 85).Select(x => (uint)x).ToArray()
        },
        [52255] = new()
        {
            Name           = "追加染剂1",
            HighlightColor = new(1f, 0.25f, 0.25f, 1f),
            StainIDs       = [86, 87, 88, 89, 90, 91, 92, 93, 94]
        },
        [52256] = new()
        {
            Name           = "追加染剂2",
            HighlightColor = new(0.25f, 0.45f, 1f, 1f),
            StainIDs       = [95, 96, 97, 98, 99, 100, 121, 122, 123, 124, 125]
        }
    };

    private const ImGuiWindowFlags WindowFlags = ImGuiWindowFlags.NoTitleBar        |
                                                ImGuiWindowFlags.NoResize          |
                                                ImGuiWindowFlags.NoSavedSettings   |
                                                ImGuiWindowFlags.NoFocusOnAppearing |
                                                ImGuiWindowFlags.NoNav             |
                                                ImGuiWindowFlags.NoMove            |
                                                ImGuiWindowFlags.NoScrollbar;

    #endregion
}

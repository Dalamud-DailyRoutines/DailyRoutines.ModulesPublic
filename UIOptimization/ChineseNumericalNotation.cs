using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class ChineseNumericalNotation : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ChineseNumericalNotationTitle"),
        Description = Lang.Get("ChineseNumericalNotationDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { CNDefaultEnabled = true, TCDefaultEnabled = true };

    private static readonly CompSig                     FormatNumberSig = new("E8 ?? ?? ?? ?? 44 3B F7");
    private delegate        Utf8String*                 FormatNumberDelegate(Utf8String* outNumberString, int number, int baseNumber, int mode, void* seperator);
    private                 Hook<FormatNumberDelegate>? FormatNumberHook;

    private static readonly CompSig                                AtkCounterNodeSetNumberSig = new("40 53 48 83 EC ?? 48 8B C2 48 8B D9 48 85 C0");
    private delegate        void                                   AtkCounterNodeSetNumberDelegate(AtkCounterNode* node, CStringPointer number);
    private                 Hook<AtkCounterNodeSetNumberDelegate>? AtkCounterNodeSetNumberHook;
    
    // 千分位转万分位
    private readonly MemoryPatch AtkTextNodeSetNumberCommaPatch = new
    (
        "B8 ?? ?? ?? ?? F7 E1 D1 EA 8D 04 52 2B C8 83 F9 ?? 75 ?? 41 0F B6 D0 48 8D 8F",
        [
            // mov eax, 0AAAAAAABh
            0x83, 0xE1, 0x03, // and ecx, 3
            0x90, 0x90,       // nop, nop
            // all nop
            0x90, 0x90, 0x90, 0x90, 0x90,
            0x90, 0x90, 0x90, 0x90
        ]
    );

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        AtkTextNodeSetNumberCommaPatch.Enable();

        AtkCounterNodeSetNumberHook ??= AtkCounterNodeSetNumberSig.GetHook<AtkCounterNodeSetNumberDelegate>(AtkCounterNodeSetNumberDetour);
        AtkCounterNodeSetNumberHook.Enable();

        FormatNumberHook ??= FormatNumberSig.GetHook<FormatNumberDelegate>(FormatNumberDetour);
        FormatNumberHook.Enable();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("ChineseNumericalNotation-NoChineseUnit"), ref config.NoChineseUnit))
            config.Save(this);

        if (!config.NoChineseUnit)
        {
            if (ImGui.Checkbox(Lang.Get("Dye"), ref config.ColoringUnit))
                config.Save(this);

            if (config.ColoringUnit)
            {
                using (ImRaii.Group())
                {
                    if (!LuminaGetter.TryGetRow<UIColor>(config.ColorMinus, out var minusColorRow))
                    {
                        config.ColorMinus = 17;
                        config.Save(this);
                        return;
                    }

                    ImGui.ColorButton("###ColorButtonMinus", minusColorRow.ToVector4());

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(200f * GlobalUIScale);
                    if (ImGui.InputUShort(Lang.Get("ChineseNumericalNotation-ColorMinus"), ref config.ColorMinus, 1, 1))
                        config.Save(this);
                }

                ImGui.SameLine();
                ImGui.TextDisabled("|");

                ImGui.SameLine();

                using (ImRaii.Group())
                {
                    if (!LuminaGetter.TryGetRow<UIColor>(config.ColorUnit, out var unitColorRow))
                    {
                        config.ColorUnit = 17;
                        config.Save(this);
                        return;
                    }

                    ImGui.ColorButton("###ColorButtonUnit", unitColorRow.ToVector4());

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(200f * GlobalUIScale);
                    if (ImGui.InputUShort(Lang.Get("ChineseNumericalNotation-ColorUnit"), ref config.ColorUnit, 1, 1))
                        config.Save(this);
                }

                var sheet = LuminaGetter.Get<UIColor>();

                using (var node = ImRaii.TreeNode(Lang.Get("ChineseNumericalNotation-ColorTable")))
                {
                    if (node)
                    {
                        using var table = ImRaii.Table("###ColorTable", 6);
                        if (!table) return;

                        var counter = 0;

                        foreach (var row in sheet)
                        {
                            if (row.RowId == 0) continue;
                            if (row.Dark  == 0) continue;

                            if (counter % 5 == 0)
                                ImGui.TableNextRow();
                            ImGui.TableNextColumn();

                            counter++;

                            using (ImRaii.Group())
                            {
                                ImGui.ColorButton($"###ColorButtonTable{row.RowId}", row.ToVector4());

                                ImGui.SameLine();
                                ImGui.TextUnformatted($"{row.RowId}");
                            }
                        }
                    }
                }
            }
        }
    }

    protected override void Uninit() =>
        AtkTextNodeSetNumberCommaPatch.Dispose();

    private Utf8String* FormatNumberDetour(Utf8String* outNumberString, int number, int baseNumber, int mode, void* seperator)
    {
        var ret = FormatNumberHook.Original(outNumberString, number, baseNumber, mode, seperator);

        if (baseNumber % 10 == 0)
        {
            switch (mode)
            {
                // 千分位分隔
                case 1:
                {
                    var minusColor = config.ColoringUnit ? config.ColorMinus : (ushort?)null;
                    var unitColor  = config.ColoringUnit ? config.ColorUnit : (ushort?)null;

                    var formatted = !config.NoChineseUnit
                                        ? number.ToChineseSeString(minusColor, unitColor)
                                        : number.ToMyriadString();

                    outNumberString->SetString(formatted.ToDalamudString().EncodeWithNullTerminator());
                    return outNumberString;
                }
                case 2 or 3 or 4 or 5:
                    break;
                // 纯数字
                default:
                {
                    var formatted = number.ToMyriadString();

                    outNumberString->SetString(new SeString(new TextPayload(formatted)).EncodeWithNullTerminator());
                    return outNumberString;
                }
            }
        }

        return ret;
    }

    private void AtkCounterNodeSetNumberDetour(AtkCounterNode* node, CStringPointer number)
    {
        if (!config.NoChineseUnit           &&
            number.HasValue                       &&
            number.ExtractText() is var textValue &&
            textValue.IsAnyChinese())
        {
            node->SetText(textValue.FromChineseString<int>().ToMyriadString());
            node->UpdateWidth();
            return;
        }

        AtkCounterNodeSetNumberHook.Original(node, number);
    }

    private class Config : ModuleConfig
    {
        public bool   ColoringUnit;
        public ushort ColorMinus = 17;
        public ushort ColorUnit  = 25;
        public bool   NoChineseUnit;
    }
}

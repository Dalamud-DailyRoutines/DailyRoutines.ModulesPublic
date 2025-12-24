using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class ChineseNumericalNotation : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("ChineseNumericalNotationTitle"),
        Description = GetLoc("ChineseNumericalNotationDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { CNDefaultEnabled = true, TCDefaultEnabled = true };

    // 千分位转万分位
    private static readonly MemoryPatch AtkTextNodeSetNumberCommaPatch = new(
        "B8 ?? ?? ?? ?? F7 E1 D1 EA 8D 04 52 2B C8 83 F9 ?? 75 ?? 41 0F B6 D0 48 8D 8F",
        [
            // mov eax, 0AAAAAAABh
            0x83, 0xE1, 0x03, // and ecx, 3
            0x90, 0x90,       // nop, nop
            // all nop
            0x90, 0x90, 0x90, 0x90, 0x90,
            0x90, 0x90, 0x90, 0x90
        ]);
    
    private static readonly CompSig FormatNumberSig = new("E8 ?? ?? ?? ?? 44 3B F7");
    private delegate Utf8String* FormatNumberDelegate(ref Utf8String* outNumberString, int number, int baseNumber, int mode, char* seperator);
    private static Hook<FormatNumberDelegate>? FormatNumberHook;

    private static readonly CompSig AtkCounterNodeSetNumberSig =
        new("40 53 48 83 EC ?? 48 8B C2 48 8B D9 48 85 C0");
    private delegate void AtkCounterNodeSetNumberDelegate(AtkCounterNode* node, byte* number);
    private static Hook<AtkCounterNodeSetNumberDelegate>? AtkCounterNodeSetNumberHook;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        AtkTextNodeSetNumberCommaPatch.Enable();

        AtkCounterNodeSetNumberHook ??= AtkCounterNodeSetNumberSig.GetHook<AtkCounterNodeSetNumberDelegate>(AtkCounterNodeSetNumberDetour);
        AtkCounterNodeSetNumberHook.Enable();
        
        FormatNumberHook ??= FormatNumberSig.GetHook<FormatNumberDelegate>(FormatNumberDetour);
        FormatNumberHook.Enable();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("ChineseNumericalNotation-NoChineseUnit"), ref ModuleConfig.NoChineseUnit))
            SaveConfig(ModuleConfig);

        if (!ModuleConfig.NoChineseUnit)
        {
            if (ImGui.Checkbox(GetLoc("Dye"), ref ModuleConfig.ColoringUnit))
                SaveConfig(ModuleConfig);

            if (ModuleConfig.ColoringUnit)
            {
                using (ImRaii.Group())
                {
                    if (!LuminaGetter.TryGetRow<UIColor>(ModuleConfig.ColorMinus, out var minusColorRow))
                    {
                        ModuleConfig.ColorMinus = 17;
                        ModuleConfig.Save(this);
                        return;
                    }

                    ImGui.ColorButton("###ColorButtonMinus", UIColorToVector4Color(minusColorRow.Dark));

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(200f * GlobalFontScale);
                    if (ImGui.InputUInt(GetLoc("ChineseNumericalNotation-ColorMinus"), ref ModuleConfig.ColorMinus, 1, 1))
                        SaveConfig(ModuleConfig);
                }
                
                ImGui.SameLine();
                ImGui.TextDisabled("|");
                
                ImGui.SameLine();
                using (ImRaii.Group())
                {
                    if (!LuminaGetter.TryGetRow<UIColor>(ModuleConfig.ColorUnit, out var unitColorRow))
                    {
                        ModuleConfig.ColorUnit = 17;
                        ModuleConfig.Save(this);
                        return;
                    }

                    ImGui.ColorButton("###ColorButtonUnit", UIColorToVector4Color(unitColorRow.Dark));

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(200f * GlobalFontScale);
                    if (ImGui.InputUInt(GetLoc("ChineseNumericalNotation-ColorUnit"), ref ModuleConfig.ColorUnit, 1, 1))
                        SaveConfig(ModuleConfig);
                }

                var sheet = LuminaGetter.Get<UIColor>();
                using (var node = ImRaii.TreeNode(GetLoc("ChineseNumericalNotation-ColorTable")))
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
                                ImGui.ColorButton($"###ColorButtonTable{row.RowId}", UIColorToVector4Color(row.Dark));
                                        
                                ImGui.SameLine();
                                ImGui.Text($"{row.RowId}");
                            }
                        }
                    }
                }
            }
        }
    }

    protected override void Uninit() => 
        AtkTextNodeSetNumberCommaPatch.Dispose();

    private static Utf8String* FormatNumberDetour(ref Utf8String* outNumberString, int number, int baseNumber, int mode, char* seperator)
    {
        var ret = FormatNumberHook.Original(ref outNumberString, number, baseNumber, mode, seperator);
        
        if (baseNumber % 10 == 0)
        {
            switch (mode)
            {
                // 千分位分隔
                case 1:
                    var minusColor = ModuleConfig.ColoringUnit ? (int)ModuleConfig.ColorMinus : -1;
                    var unitColor = ModuleConfig.ColoringUnit ? (int)ModuleConfig.ColorUnit : -1;

                    var formatted = !ModuleConfig.NoChineseUnit
                                        ? FormatUtf8NumberByChineseNotation(number, Lang.CurrentLanguage, minusColor,
                                                                        unitColor)
                                        : FormatUtf8NumberByTenThousand(number);
                    outNumberString = formatted;
                    return outNumberString;
                case 2 or 3 or 4 or 5:
                    break;
                // 纯数字
                default:
                    var formattedByTenThousand = FormatUtf8NumberByTenThousand(number);
                    outNumberString = formattedByTenThousand;
                    return outNumberString;
            }
        }

        return ret;
    }

    private static void AtkCounterNodeSetNumberDetour(AtkCounterNode* node, byte* number)
    {
        if (!ModuleConfig.NoChineseUnit && number != null && SeString.Parse(number).TextValue.Any(IsChineseCharacter))
        {
            node->NodeText = *FormatUtf8NumberByTenThousand(ParseFormattedChineseNumber(SeString.Parse(number).TextValue));
            node->UpdateWidth();
            return;
        }
        
        AtkCounterNodeSetNumberHook.Original(node, number);
    }

    private class Config : ModuleConfiguration
    {
        public bool NoChineseUnit;
        public bool ColoringUnit;
        public uint ColorUnit  = 25;
        public uint ColorMinus = 17;
    }
}

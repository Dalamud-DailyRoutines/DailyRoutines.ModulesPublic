using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSharpenInterfaceText : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoSharpenInterfaceTextTitle"),
        Description = GetLoc("AutoSharpenInterfaceTextDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    private static readonly CompSig                          AtkTextNodeSetTextSig = new("48 85 C9 0F 84 ?? ?? ?? ?? 4C 8B DC 53 56");
    private delegate        void                             AtkTextNodeSetTextDelegate(AtkTextNode* node, CStringPointer text);
    private static          Hook<AtkTextNodeSetTextDelegate> AtkTextNodeSetTextHook;

    protected override void Init()
    {
        AtkTextNodeSetTextHook ??= AtkTextNodeSetTextSig.GetHook<AtkTextNodeSetTextDelegate>(AtkTextNodeSetTextDetour);
        AtkTextNodeSetTextHook.Enable();
    }

    private static void AtkTextNodeSetTextDetour(AtkTextNode* node, CStringPointer text)
    {
        AtkTextNodeSetTextHook.Original(node, text);

        if (node == null || !text.HasValue) return;
        
        // NamePlate
        if ((byte)node->TextFlags == 152 && node->AlignmentFontType == 7)
            return;
        
        var flag = node->TextFlags;
        if (flag.HasFlag((TextFlags)(1 << 12)))
        {
            flag           &= ~(TextFlags)(1 << 12);
            node->TextFlags =  flag;
        }
    }
}

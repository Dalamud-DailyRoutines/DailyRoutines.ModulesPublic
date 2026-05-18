using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using InteropGenerator.Runtime;
using OmenTools.Interop.Game.Models;
using TinyPinyin;

namespace DailyRoutines.ModulesPublic;

public class BetterNameSort : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "更好的游戏内名称排序",
        Description = "将多个游戏系统 (情感动作、无人岛、成就等) 中的项目名称排序方式, 由按 Unicode 码点排序改为按汉语拼音排序, 使界面显示更直观",
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true, TCOnly = true, TCDefaultEnabled = true, CNDefaultEnabled = true };

    private static readonly CompSig CompareStringByCodePointSig = new("48 89 5C 24 ?? 55 56 57 48 83 EC ?? 33 C0 48 8D 35");
    private delegate        int     CompareStringByCodePointDelegate(CStringPointer strA, CStringPointer strB, bool useAsciiCaseMap, bool foldKana);
    private                 Hook<CompareStringByCodePointDelegate> CompareStringByCodePointHook;

    protected override void Init()
    {
        CompareStringByCodePointHook = CompareStringByCodePointSig.GetHook<CompareStringByCodePointDelegate>(CompareStringByCodePointDetour);
        CompareStringByCodePointHook.Enable();
    }

    /// <remarks>-1 - strA 的码点小于 strB; 0 - 两个字符串完全相同; 1 - strA 大于 strB</remarks>
    private int CompareStringByCodePointDetour(CStringPointer strA, CStringPointer strB, bool useAsciiCaseMap, bool foldKana)
    {
        var orig = CompareStringByCodePointHook.Original(strA, strB, useAsciiCaseMap, foldKana);
        if (orig == 0)
            return orig;

        var strContentA = strA.ToString();
        var strContentB = strB.ToString();
        if (!strContentA.IsAnyChinese() && !strContentB.IsAnyChinese())
            return orig;

        var pinyinA = PinyinHelper.GetPinyin(strContentA, string.Empty);
        var pinyinB = PinyinHelper.GetPinyin(strContentB, string.Empty);

        var cmp = string.CompareOrdinal(pinyinA, pinyinB);
        return cmp != 0 ? MathF.Sign(cmp) : orig;
    }
}

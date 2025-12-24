using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using DailyRoutines.Abstracts;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Component.Shell;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using TinyPinyin;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoAntiCensorship : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动反屏蔽词",
        Description = "发送消息/编辑招募描述时, 自动在屏蔽词内部加点, 或是将其转成拼音以防止屏蔽\n接收消息时, 自动阻止屏蔽词系统工作, 显示消息原文",
        Category    = ModuleCategories.System,
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true, CNDefaultEnabled = true };

    private static readonly CompSig GetFilteredUtf8StringSig =
        new("48 89 74 24 ?? 57 48 83 EC ?? 48 83 79 ?? ?? 48 8B FA 48 8B F1 0F 84 ?? ?? ?? ?? 48 89 5C 24");
    private delegate void                           GetFilteredUtf8StringDelegate(nint vulgarInstance, Utf8String* str);
    private static   GetFilteredUtf8StringDelegate? GetFilteredUtf8String;

    private static readonly CompSig VulgarInstanceOffsetBaseSig = new("48 8B 81 ?? ?? ?? ?? 48 85 C0 74 ?? 48 8B D3");
    private static          nint    VulgarInstanceOffset;
    
    private static readonly CompSig PartyFinderOriginalMessageOffsetBaseSig = new("48 8D 99 ?? ?? ?? ?? 48 8B F9 48 8B CB E8 ?? ?? ?? ?? 48 8B 0D");
    private static          nint    PartyFinderOriginalMessageOffset;

    private static readonly CompSig                 Utf8StringCopySig = new("E8 ?? ?? ?? ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 85 ED 74");
    private delegate        nint                    Utf8StringCopyDelegate(Utf8String* target, Utf8String* source);
    private static          Utf8StringCopyDelegate? Utf8StringCopy;

    private static readonly CompSig LocalMessageDisplaySig = new("40 53 48 83 EC ?? 48 8D 99 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 48 8B 0D");
    private delegate        nint LocalMessageDisplayDelegate(nint a1, Utf8String* source);
    private static          Hook<LocalMessageDisplayDelegate>? LocalMessageDisplayHook;

    private static readonly CompSig PartyFinderMessageDisplaySig =
        new("48 89 5C 24 ?? 57 48 83 EC ?? 48 8D 99 ?? ?? ?? ?? 48 8B F9 48 8B CB E8");
    private delegate nint PartyFinderMessageDisplayDelegate(nint a1, Utf8String* source);
    private static Hook<PartyFinderMessageDisplayDelegate>? PartyFinderMessageDisplayHook;

    private static readonly CompSig LookingForGroupConditionReceiveEventSig = new("E8 ?? ?? ?? ?? 0F B6 F8 E9 ?? ?? ?? ?? 45 8B C2 48 8B D6 48 8B CB E8 ?? ?? ?? ?? 0F B6 F8 E9 ?? ?? ?? ?? 45 8B C2 48 8B D6 48 8B CB E8 ?? ?? ?? ?? 0F B6 F8 E9 ?? ?? ?? ?? 48 8B CE");
    private delegate byte LookingForGroupConditionReceiveEventDelegate(nint a1, AtkValue* a2);
    private static Hook<LookingForGroupConditionReceiveEventDelegate>? LookingForGroupConditionReceiveEventHook;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig ??= LoadConfig<Config>() ?? new();
        
        // mov rax, [rcx + XXXX], 因为是四字节所以用 uint
        if (VulgarInstanceOffset == nint.Zero)
            VulgarInstanceOffset = VulgarInstanceOffsetBaseSig.GetStatic();
        Debug($"[{nameof(AutoAntiCensorship)}] 屏蔽词系统偏移量: {VulgarInstanceOffset}");

        // lea rbx, [rcx+XXXX], 因为是四字节所以用 uint
        if (PartyFinderOriginalMessageOffset == nint.Zero)
            PartyFinderOriginalMessageOffset = PartyFinderOriginalMessageOffsetBaseSig.GetStatic();
        Debug($"[{nameof(AutoAntiCensorship)}] 招募信息原始字符串偏移量: {PartyFinderOriginalMessageOffset}");
        
        GetFilteredUtf8String ??= GetFilteredUtf8StringSig.GetDelegate<GetFilteredUtf8StringDelegate>();
        Utf8StringCopy        ??= Utf8StringCopySig.GetDelegate<Utf8StringCopyDelegate>();
        
        LocalMessageDisplayHook ??= LocalMessageDisplaySig.GetHook<LocalMessageDisplayDelegate>(LocalMessageDisplayDetour);
        LocalMessageDisplayHook.Enable();

        ChatManager.RegPreExecuteCommandInner(OnPreExecuteCommandInner);
        
        PartyFinderMessageDisplayHook ??= PartyFinderMessageDisplaySig.GetHook<PartyFinderMessageDisplayDelegate>(PartyFinderMessageDisplayDetour);
        PartyFinderMessageDisplayHook.Enable();

        LookingForGroupConditionReceiveEventHook ??=
            LookingForGroupConditionReceiveEventSig.GetHook<LookingForGroupConditionReceiveEventDelegate>(LookingForGroupConditionReceiveEventDetour);
        LookingForGroupConditionReceiveEventHook.Enable();
    }

    protected override void Uninit() => 
        ChatManager.Unreg(OnPreExecuteCommandInner);

    protected override void ConfigUI()
    {
        DrawDisplayCensoredText();
        
        ImGui.NewLine();

        DrawHandleCensoredText();
    }

    private void DrawDisplayCensoredText()
    {
        if (ImGui.Checkbox("显示屏蔽文本", ref ModuleConfig.DisplayCensoredText))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker("接收到含屏蔽词的文本时, 自动将其还原为原始文本, 并高亮显示其中包含的屏蔽文本");
        
        if (!ModuleConfig.DisplayCensoredText) return;
        
        using var indent = ImRaii.PushIndent();
            
        ImGui.SetNextItemWidth(150f * GlobalFontScale);
        ImGui.InputInt("高亮颜色###HighlightColorInput", ref ModuleConfig.HighlightColor, 1, 1);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
        ImGuiOm.TooltipHover("设置为 -1 以禁用高亮");
        
        if (ModuleConfig.HighlightColor >= 0)
        {
            if(!LuminaGetter.TryGetRow<UIColor>((uint)ModuleConfig.HighlightColor, out var unitColorRow))
            {
                ModuleConfig.HighlightColor = 17;
                ModuleConfig.Save(this);
                return;
            }
            
            ImGui.SameLine();
            ImGui.ColorButton("###HighlightColorPreview", UIColorToVector4Color(unitColorRow.Dark));
        }

        ImGui.SameLine(0, 8f * GlobalFontScale);
        if (ImGui.Button($"{FontAwesomeIcon.Palette.ToIconChar()} 参考颜色表"))
            ChatManager.SendCommand("/xldata uicolor");
    }
    
    private void DrawHandleCensoredText()
    {
        if (ImGui.Checkbox("处理屏蔽文本", ref ModuleConfig.HandleCensoredText))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker("在发送聊天消息 / 编辑招募留言时, 自动检测其中可能包含的屏蔽文本, 并将其自动处理为未被屏蔽的文本");
        
        if (!ModuleConfig.HandleCensoredText) return;
        
        using var indent = ImRaii.PushIndent();
        
        var seperator = ModuleConfig.Seperator.ToString();
        ImGui.SetNextItemWidth(150f * GlobalFontScale);
        if (ImGui.InputText($"分隔符###SeperatorInput", ref seperator, 4))
        {
            seperator = seperator.Trim();
                
            // 我觉得真有人会输入 * 号来看看会发生什么
            if (string.IsNullOrWhiteSpace(seperator) || seperator == "*")
                seperator = ".";
                
            ModuleConfig.Seperator = seperator[0];
            ModuleConfig.Save(this);
        }
        
        ImGui.NewLine();
        
        ImGui.AlignTextToFramePadding();
        ImGui.Text("自定义替换规则");
        
        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, "添加"))
        {
            ModuleConfig.CustomReplacements[string.Empty] = string.Empty;
            ModuleConfig.Save(this);
        }
        
        ImGui.Spacing();
        
        if (ModuleConfig.CustomReplacements.Count > 0)
        {
            using var table = ImRaii.Table("###CustomReplacementsTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
            if (table)
            {
                ImGui.TableSetupColumn("原始文本", ImGuiTableColumnFlags.WidthStretch, 0.3f);
                ImGui.TableSetupColumn("替换文本", ImGuiTableColumnFlags.WidthStretch, 0.3f);
                ImGui.TableSetupColumn("状态",   ImGuiTableColumnFlags.WidthStretch, 0.2f);
                ImGui.TableSetupColumn("操作",   ImGuiTableColumnFlags.WidthFixed,   80f * GlobalFontScale);
                
                ImGui.TableHeadersRow();

                var counter = 0;
                foreach (var (originalWord, replacement) in ModuleConfig.CustomReplacements.ToList())
                {
                    using var id = ImRaii.PushId(counter);
                    counter++;
                    
                    ImGui.TableNextRow();
                    
                    ImGui.TableNextColumn();
                    var originalWordInput = originalWord;
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputText("###Original", ref originalWordInput, 256);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        if (string.IsNullOrWhiteSpace(originalWord))
                            ModuleConfig.CustomReplacements.Remove(originalWord);
                        ModuleConfig.CustomReplacements[originalWordInput] = replacement;
                        ModuleConfig.Save(this);
                    }
                    
                    ImGui.TableNextColumn();
                    var replacementInput = replacement;
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputText("###Replacement", ref replacementInput, 256);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        ModuleConfig.CustomReplacements[originalWord] = replacementInput;
                        ModuleConfig.Save(this);
                    }

                    ImGui.TableNextColumn();
                    switch (string.IsNullOrWhiteSpace(replacement))
                    {
                        case false when ValidateCustomReplacement(replacement):
                            ImGui.TextColored(KnownColor.GreenYellow.ToVector4(), "有效");
                            break;
                        case false:
                            ImGui.TextColored(KnownColor.Red.ToVector4(), "无效");
                            ImGuiOm.TooltipHover($"替换词包含屏蔽内容:\n{replacement}: {GetFilteredString(replacement)}");
                            break;
                        default:
                            ImGui.TextColored(KnownColor.Gray.ToVector4(), "无");
                            break;
                    }
                    
                    ImGui.TableNextColumn();
                    if (ImGuiOm.ButtonIcon($"Delete_{originalWord.GetHashCode()}", FontAwesomeIcon.TrashAlt, "删除"))
                    {
                        ModuleConfig.CustomReplacements.Remove(originalWord);
                        ModuleConfig.Save(this);
                    }
                }
            }
        }
    }
    
    // 消息发送
    private static void OnPreExecuteCommandInner(ref bool isPrevented, ref ReadOnlySeString message)
    {
        if (!ModuleConfig.HandleCensoredText) return;
        
        var seString = message.ToDalamudString();
        // 信息为空或者为指令
        if (string.IsNullOrWhiteSpace(seString.TextValue) || seString.TextValue.StartsWith('/'))
            return;
        
        var builder = new SeStringBuilder();
        foreach (var payload in seString.Payloads)
        {
            // 不处理非文本
            if (payload is not TextPayload textPayload)
            {
                builder.Add(payload);
                continue;
            }
            
            BypassCensorshipByTextPayload(ref textPayload);
            builder.Add(textPayload);
        }

        message = new ReadOnlySeString(builder.Encode());
    }
    
    // 编辑招募
    private static byte LookingForGroupConditionReceiveEventDetour(nint a1, AtkValue* values)
    {
        if (!ModuleConfig.HandleCensoredText) 
            return InvokeOriginal();
        
        try
        {
            if (values == null || values->Int != 15)
                return InvokeOriginal();

            var managedString = values[1].String;
            if (managedString.Value == null)
                return InvokeOriginal();

            var origText = SeString.Parse(managedString.Value);
            if (origText == null || string.IsNullOrWhiteSpace(origText.TextValue))
                return InvokeOriginal();

            var builderHandled = new SeStringBuilder();
            foreach (var payload in origText.Payloads)
            {
                // 不处理非文本
                if (payload is not TextPayload textPayload)
                {
                    builderHandled.Add(payload);
                    continue;
                }

                BypassCensorshipByTextPayload(ref textPayload);
                builderHandled.Add(textPayload);
            }

            var handledText = builderHandled.Build();

            if (handledText.TextValue == origText.TextValue)
                return InvokeOriginal();
            
            var builderHighlight = new SeStringBuilder();
            foreach (var payload in origText.Payloads)
            {
                // 不处理非文本
                if (payload is not TextPayload textPayload)
                {
                    builderHighlight.Add(payload);
                    continue;
                }

                builderHighlight.Append(HighlightCensorship(textPayload.Text));
            }

            var highlightedText = builderHighlight.Build();

            values[1].SetManagedString(handledText.EncodeWithNullTerminator());

            var textInputComponent = (AtkComponentTextInput*)LookingForGroupCondition->GetComponentByNodeId(22);
            if (textInputComponent != null)
                textInputComponent->SetText(handledText.EncodeWithNullTerminator());
            
            Chat(new SeStringBuilder().Append("已对招募留言进行反屏蔽处理:\n").Append(highlightedText).Append("\n↓\n").Append(handledText).Build());
        }
        catch
        {
            // ignored
        }
        
        return InvokeOriginal();
        
        byte InvokeOriginal() => LookingForGroupConditionReceiveEventHook.Original(a1, values);
    }

    // 聊天信息显示
    private static nint LocalMessageDisplayDetour(nint a1, Utf8String* source)
    {
        if (!ModuleConfig.DisplayCensoredText) 
            return LocalMessageDisplayHook.Original(a1, source);
        
        var seString = SeString.Parse(source->AsSpan());
        var builder  = new SeStringBuilder();
        foreach (var payload in seString.Payloads)
        {
            // 不处理非文本
            if (payload is not TextPayload textPayload)
            {
                builder.Add(payload);
                continue;
            }
            
            var result = HighlightCensorship(textPayload.Text);
            builder.Append(result);
        }

        source->SetString(new ReadOnlySeStringSpan(builder.Build().Encode()));
        return Utf8StringCopy((Utf8String*)(a1 + 1096), source);
    }

    // 招募信息显示
    private static nint PartyFinderMessageDisplayDetour(nint a1, Utf8String* source)
    {
        if (!ModuleConfig.DisplayCensoredText) 
            return PartyFinderMessageDisplayHook.Original(a1, source);
        
        var seString = SeString.Parse(source->AsSpan());
        var builder  = new SeStringBuilder();
        foreach (var payload in seString.Payloads)
        {
            // 不处理非文本
            if (payload is not TextPayload textPayload)
            {
                builder.Add(payload);
                continue;
            }
            
            var result = HighlightCensorship(textPayload.Text);
            builder.Append(result);
        }
        
        source->SetString(builder.Build().EncodeWithNullTerminator());
        return Utf8StringCopy((Utf8String*)(a1 + PartyFinderOriginalMessageOffset), source);
    }
    
    private static void BypassCensorshipByTextPayload(ref TextPayload payload)
    {
        // 非国服或只有星号
        if (DService.ClientState.ClientLanguage != (ClientLanguage)4 ||
            string.IsNullOrWhiteSpace(payload.Text?.Replace('*', ' ').Trim() ?? string.Empty)) 
            return;

        var bypassed = BypassCensorship(payload.Text);
        if (bypassed != payload.Text)
            payload = new TextPayload(bypassed);
    }

    public static string BypassCensorship(string originalText)
    {
        if (string.IsNullOrEmpty(originalText)) return originalText;

        var result   = ApplyCustomReplacements(originalText);
        var filtered = GetFilteredString(result);

        // 记录已处理过的文本, 防止无限循环
        var processedTexts = new HashSet<string>();
        while (filtered != result && !processedTexts.Contains(result))
        {
            processedTexts.Add(result);
            var newResult = new StringBuilder();

            // 跳过 <> 标签内容
            var insideTag = false;

            for (var i = 0; i < result.Length; i++)
            {
                // 检查是否进入或离开标签
                if (result[i] == '<')
                    insideTag = true;

                if (insideTag)
                {
                    newResult.Append(result[i]);
                    if (result[i] == '>') 
                        insideTag = false;

                    continue;
                }

                // 处理非标签内容
                if (i < filtered.Length && filtered[i] == '*' && result[i] != '*')
                {
                    // 找出连续被屏蔽的部分
                    var startPos = i;
                    while (i + 1 < filtered.Length && filtered[i + 1] == '*' && result[i + 1] != '*') 
                        i++;

                    // 截取被屏蔽的词
                    var censoredWord = result.Substring(startPos, i - startPos + 1);
                    if (censoredWord.Length == 1 && IsChineseCharacter(censoredWord[0]))
                        newResult.Append(PinyinHelper.GetPinyin(censoredWord).ToLowerInvariant());
                    else if (IsChineseString(censoredWord))
                    {
                        // 汉字词组加分隔符
                        for (var j = 0; j < censoredWord.Length; j++)
                        {
                            newResult.Append(censoredWord[j]);
                            if (j < censoredWord.Length - 1) 
                                newResult.Append(ModuleConfig.Seperator);
                        }
                    }
                    else
                    {
                        // 其他内容加分隔符
                        for (var j = 0; j < censoredWord.Length; j++)
                        {
                            newResult.Append(censoredWord[j]);
                            if (j < censoredWord.Length - 1) 
                                newResult.Append(ModuleConfig.Seperator);
                        }
                    }
                }
                else
                    newResult.Append(result[i]);
            }

            result   = newResult.ToString();
            filtered = GetFilteredString(result);
        }

        return result;
    }
    
    public static SeString HighlightCensorship(string originalText)
    {
        if (ModuleConfig.HighlightColor < 0) 
            return originalText;
        if (string.IsNullOrEmpty(originalText)) 
            return originalText;

        var filtered = GetFilteredString(originalText);
        
        // 如果没有被屏蔽的内容，直接返回原文
        if (filtered == originalText) return originalText;
        
        var result = new SeStringBuilder();
        
        var insideTag = false;
        var insideCensored = false;
        
        for (var i = 0; i < originalText.Length; i++)
        {
            // 检查是否进入或离开标签
            if (originalText[i] == '<')
                insideTag = true;
            
            if (insideTag)
            {
                result.Append(originalText[i].ToString());
                if (originalText[i] == '>') 
                    insideTag = false;
                continue;
            }
            
            // 处理非标签内容
            if (i < filtered.Length && filtered[i] == '*' && originalText[i] != '*')
            {
                // 屏蔽词开始, 添加染色
                if (!insideCensored)
                {
                    result.Add(new UIForegroundPayload((ushort)ModuleConfig.HighlightColor));
                    insideCensored = true;
                }
            }
            else
            {
                // 屏蔽词结束, 结束染色
                if (insideCensored)
                {
                    result.Add(UIForegroundPayload.UIForegroundOff);
                    insideCensored = false;
                }
            }

            result.Append(originalText[i].ToString());
        }
        
        // 字符串结束了仍然在屏蔽词里, 结束染色
        if (insideCensored)
            result.Add(UIForegroundPayload.UIForegroundOff);
        
        return result.Build();
    }
    
    private static string GetFilteredString(string str)
    {
        var utf8String = Utf8String.FromString(str);
        GetFilteredUtf8String(Marshal.ReadIntPtr((nint)Framework.Instance() + VulgarInstanceOffset), utf8String);
        var result = utf8String->ExtractText();

        utf8String->Dtor(true);
        return result;
    }

    private static string ApplyCustomReplacements(string text)
    {
        if (string.IsNullOrEmpty(text) || ModuleConfig.CustomReplacements.Count == 0) 
            return text;

        var result = text;
        
        var sortedReplacements = ModuleConfig.CustomReplacements
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
            .Where(kvp => ValidateCustomReplacement(kvp.Value)) // 只使用有效的替换词
            .OrderByDescending(kvp => kvp.Key.Length);

        foreach (var (originalWord, replacement) in sortedReplacements)
        {
            if (result.Contains(originalWord))
                result = result.Replace(originalWord, replacement);
        }

        return result;
    }
    
    private static bool ValidateCustomReplacement(string replacement)
    {
        if (string.IsNullOrWhiteSpace(replacement)) return false;
        
        var filtered = GetFilteredString(replacement);
        return filtered == replacement;
    }

    private class Config : ModuleConfiguration
    {
        public bool DisplayCensoredText = true;
        public bool HandleCensoredText  = true;
        
        public char Seperator = '.';
        public int HighlightColor   = 17;
        public Dictionary<string, string> CustomReplacements = new();
    }
}

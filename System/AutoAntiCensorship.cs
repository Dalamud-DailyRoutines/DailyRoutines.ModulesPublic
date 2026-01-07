using System;
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

    private static readonly CompSig TextInputReceiveEventSig =
        new("4C 8B DC 55 53 57 41 54 41 57 49 8D AB ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 8B 9D");
    private delegate void TextInputReceiveDelegate(AtkComponentTextInput* textInput, AtkEventType eventType, int eventParam, AtkEvent* atkEvent, AtkEventData* atkEventData);
    private static Hook<TextInputReceiveDelegate>? TextInputReceiveEventHook;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        if (DService.PI.IsPluginEnabled("StarlightBreaker"))
        {
            NotificationError("检测到不兼容的插件 StarlightBreaker, 自动反屏蔽词模块已禁用");
            throw new Exception("检测到不兼容的插件 StarlightBreaker");
        }

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

        TextInputReceiveEventHook ??= TextInputReceiveEventSig.GetHook<TextInputReceiveDelegate>(TextInputReceiveEventDetour);
        TextInputReceiveEventHook.Enable();

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
            ImGui.ColorButton("###HighlightColorPreview",  unitColorRow.ToVector4());
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
        ImGui.TextUnformatted("自定义替换规则");
        
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
    
    // 聊天消息编辑
    private static void TextInputReceiveEventDetour(
        AtkComponentTextInput* textInput,
        AtkEventType           eventType,
        int                    eventParam,
        AtkEvent*              atkEvent,
        AtkEventData*          atkEventData)
    {
        TextInputReceiveEventHook.Original(textInput, eventType, eventParam, atkEvent, atkEventData);
        
        if (!ModuleConfig.HandleCensoredText) return;

        if (eventType == AtkEventType.FocusStop && textInput != null)
        {
            var addon = textInput->OwnerAddon;
            if (addon == null)
                addon = textInput->ContainingAddon2;

            if (addon == null)
                addon = RaptureAtkUnitManager.Instance()->GetAddonByNode((AtkResNode*)textInput->OwnerNode);
            
            if (addon->NameString != "ChatLog") return;
            
            var origText = SeString.Parse(textInput->EvaluatedString);
            if (origText == null)
                return;

            var handleText = new SeString(origText.Payloads);
            BypassCensorship(ref handleText);
            
            if (handleText.TextValue == origText.TextValue)
                return;
            
            var highlightText = new SeString(origText.Payloads);
            HighlightCensorship(ref highlightText);
            
            Chat(new SeStringBuilder().AddUiForeground(28)
                                      .AddText("[自动反屏蔽词]\n")
                                      .AddUiForegroundOff()
                                      .Append(highlightText)
                                      .AddText("\n   ↓   \n")
                                      .Append(handleText)
                                      .Build());
            
            textInput->SetText(handleText.EncodeWithNullTerminator());
        }
    }
    
    // 消息发送
    private static void OnPreExecuteCommandInner(ref bool isPrevented, ref ReadOnlySeString message)
    {
        if (!ModuleConfig.HandleCensoredText) return;
        
        var seString = message.ToDalamudString();
        // 信息为空或者为指令
        if (string.IsNullOrWhiteSpace(seString.TextValue))
            return;

        BypassCensorship(ref seString);
        message = new ReadOnlySeString(seString.Encode());
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

            var handleText = new SeString(origText.Payloads);
            BypassCensorship(ref handleText);

            if (handleText.TextValue == origText.TextValue)
                return InvokeOriginal();
            
            var highlightText = new SeString(origText.Payloads);
            HighlightCensorship(ref highlightText);

            values[1].SetManagedString(handleText.EncodeWithNullTerminator());

            var textInputComponent = (AtkComponentTextInput*)LookingForGroupCondition->GetComponentByNodeId(22);
            if (textInputComponent != null)
                textInputComponent->SetText(handleText.EncodeWithNullTerminator());

            Chat(new SeStringBuilder().AddUiForeground(32)
                                      .AddText("[自动反屏蔽词]\n")
                                      .AddUiForegroundOff()
                                      .Append(highlightText)
                                      .AddText("\n   ↓   \n")
                                      .Append(handleText)
                                      .Build());
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
        HighlightCensorship(ref seString);
        
        source->SetString(seString.EncodeWithNullTerminator());
        return Utf8StringCopy((Utf8String*)(a1 + 1096), source);
    }

    // 招募信息显示
    private static nint PartyFinderMessageDisplayDetour(nint a1, Utf8String* source)
    {
        if (!ModuleConfig.DisplayCensoredText) 
            return PartyFinderMessageDisplayHook.Original(a1, source);
        
        var seString = SeString.Parse(source->AsSpan());
        HighlightCensorship(ref seString);
        
        source->SetString(seString.EncodeWithNullTerminator());
        return Utf8StringCopy((Utf8String*)(a1 + PartyFinderOriginalMessageOffset), source);
    }

    private static void BypassCensorship(ref SeString text)
    {
        if (string.IsNullOrWhiteSpace(text.TextValue) ||
            text.TextValue.StartsWith('/'))
            return;

        var builder = new SeStringBuilder();
        foreach (var payload in text.Payloads)
        {
            // 不处理非文本
            if (payload is not TextPayload textPayload)
            {
                builder.Add(payload);
                continue;
            }
            
            BypassCensorship(ref textPayload);
            builder.Add(textPayload);
        }

        var handledText = builder.Build();
        text = new SeString(handledText.Payloads);
    }
    
    private static void BypassCensorship(ref TextPayload payload)
    {
        // 非国服或只有星号
        if (!GameState.IsCN ||
            string.IsNullOrEmpty(payload.Text?.Replace('*', ' ').Trim() ?? string.Empty)) 
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

            var resultRunes   = result.EnumerateRunes().ToList();
            var filteredRunes = filtered.EnumerateRunes().ToList();

            var i = 0;
            var j = 0;
            var insideTag = false;

            while (i < resultRunes.Count)
            {
                var resultRune = resultRunes[i];

                if (resultRune.Value == '<')
                    insideTag = true;

                if (insideTag)
                {
                    newResult.Append(resultRune.ToString());

                    if (j < filteredRunes.Count && filteredRunes[j] == resultRune)
                        j++;

                    if (resultRune.Value == '>')
                        insideTag = false;

                    i++;
                    continue;
                }

                Rune? filteredRune = j < filteredRunes.Count ? filteredRunes[j] : null;

                if (filteredRune.HasValue && filteredRune.Value == resultRune)
                {
                    newResult.Append(resultRune.ToString());
                    i++;
                    j++;
                }
                else
                {
                    if (filteredRune is { Value: '*' })
                    {
                        var nextClearFilteredIndex = j;
                        while (nextClearFilteredIndex < filteredRunes.Count && filteredRunes[nextClearFilteredIndex].Value == '*')
                            nextClearFilteredIndex++;

                        if (nextClearFilteredIndex >= filteredRunes.Count)
                        {
                            var count        = resultRunes.Count - i;
                            var censoredWord = GetStringFromRunes(resultRunes, i, count);
                            ProcessCensoredWord(newResult, censoredWord);
                            i = resultRunes.Count;
                            j = filteredRunes.Count;
                        }
                        else
                        {
                            var anchorRune = filteredRunes[nextClearFilteredIndex];

                            var nextClearResultIndex = i;
                            var found                = false;
                            while (nextClearResultIndex < resultRunes.Count)
                            {
                                if (resultRunes[nextClearResultIndex] == anchorRune)
                                {
                                    found = true;
                                    break;
                                }

                                nextClearResultIndex++;
                            }

                            if (found)
                            {
                                var count        = nextClearResultIndex - i;
                                var censoredWord = GetStringFromRunes(resultRunes, i, count);
                                ProcessCensoredWord(newResult, censoredWord);

                                i = nextClearResultIndex;
                                j = nextClearFilteredIndex;
                            }
                            else
                            {
                                newResult.Append(resultRune.ToString());
                                i++;
                                j++;
                            }
                        }
                    }
                    else
                    {
                        newResult.Append(resultRune.ToString());
                        i++;
                        if (j < filteredRunes.Count) 
                            j++;
                    }
                }
            }

            result   = newResult.ToString();
            filtered = GetFilteredString(result);
        }

        return result;
    }

    private static string GetStringFromRunes(List<Rune> runes, int start, int count)
    {
        var sb = new StringBuilder();
        for (var k = 0; k < count; k++)
            sb.Append(runes[start + k].ToString());
        return sb.ToString();
    }

    private static void ProcessCensoredWord(StringBuilder builder, string censoredWord)
    {
        var censoredRunes = censoredWord.EnumerateRunes().ToList();

        if (censoredRunes.Count == 1 && string.IsChineseRune(censoredRunes[0]))
            builder.Append(PinyinHelper.GetPinyin(censoredWord).ToLowerInvariant());
        else if (censoredWord.IsChinese())
        {
            // 汉字词组加分隔符
            for (var j = 0; j < censoredRunes.Count; j++)
            {
                builder.Append(censoredRunes[j].ToString());
                if (j < censoredRunes.Count - 1)
                    builder.Append(ModuleConfig.Seperator);
            }
        }
        else
        {
            // 其他内容加分隔符
            for (var j = 0; j < censoredRunes.Count; j++)
            {
                builder.Append(censoredRunes[j].ToString());
                if (j < censoredRunes.Count - 1)
                    builder.Append(ModuleConfig.Seperator);
            }
        }
    }
    
    private static void HighlightCensorship(ref SeString text)
    {
        if (string.IsNullOrWhiteSpace(text.TextValue) ||
            text.TextValue.StartsWith('/'))
            return;

        var builder = new SeStringBuilder();
        foreach (var payload in text.Payloads)
        {
            // 不处理非文本
            if (payload is not TextPayload textPayload)
            {
                builder.Add(payload);
                continue;
            }

            builder.Append(HighlightCensorship(textPayload.Text));
        }

        var handledText = builder.Build();
        text = new SeString(handledText.Payloads);
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
        var result = utf8String->ToString();

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

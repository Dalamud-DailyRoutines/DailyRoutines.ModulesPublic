using System;
using System.Collections;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.Player;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Game;

// 以下using为本地模块加载测试时使用
using OmenTools;
using OmenTools.Infos;
using OmenTools.Managers;
using OmenTools.Helpers;

/***********************************************************************************
 *  参考模块：
 *  - AutoGathererRoleActions（自动采集职业职能技能）
 *  - AutoDisplayIDInfomation（自动显示ID信息）
 *  - AutoDisplayMitigation（自动显示减伤信息）
 *  目前可实现针对100级治疗职业的数据量化
 *  保留了70~100级的计算接口，计划后续实现该等级范围内数据量化。
 *  目前存在以下技术瓶颈：
 *  1. 等级同步时需读取同步后武器性能
 *  2. 不同等级下，因职业特性导致技能数据变更（85级职业特性：治疗技能效果提高）
 *  3. 目前技能数据、额外提示均存放于模块脚本内，待参考“自动显示减伤信息”模块研究大量数据存放与读取方式
***********************************************************************************/

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoQuantifyHealerSkillData : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动量化治疗技能数据",
        Description = "将治疗技能的恢复力、持续恢复力等数据结合自身属性量化为具体数值，数值为最低期望。",
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Usami"]
    };

    // private static Config ModuleConfig = null!;
    private static TooltipModification? ActionModification;
    
    private static readonly HashSet<uint> ValidJobs   = [24, 28, 33, 40];     // 白魔法师、学者、占星术士、贤者
    private static readonly HashSet<uint> ValidLevels = [100];
    
    protected override void Init()
    {
        GameTooltipManager.Instance().RegGenerateActionTooltipModifier(ModifyActionTooltip);
    }

    protected override void Uninit()
    {
        GameTooltipManager.Instance().Unreg(generateActionModifiers: ModifyActionTooltip);
        GameTooltipManager.Instance().RemoveActionDetail(ActionModification);
    }
    
    private static void ModifyActionTooltip(AtkUnitBase* addonActionDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
    {
        if (ActionModification != null)
        {
            GameTooltipManager.Instance().RemoveActionDetail(ActionModification);
            ActionModification = null;
        }

        if (!ValidLevels.Contains(LocalPlayerState.CurrentLevel)) return;
        if (!ValidJobs.Contains(LocalPlayerState.ClassJob))       return;

        var hoveredID = AgentActionDetail.Instance()->ActionId;
        if (!ValidSkillsDictionary.TryGetValue(hoveredID, out var skillDict)) return;
        List<Payload> newDescription = GetNewDescriptionPayloads(hoveredID, skillDict);
        
        ActionModification = GameTooltipManager.Instance().AddActionDetail
        (
            hoveredID,
            TooltipActionType.Description,
            new SeString(newDescription),
            TooltipModifyMode.Overwrite
        );
    }
    
    public class Config : ModuleConfiguration
    {
    }

    #region Description
    private static List<Payload> GetNewDescriptionPayloads(uint hoveredID, (string, bool isDamageSkill, uint[] ValueUnits) skillDict)
    {
        var baseSkillValue = skillDict.ValueUnits;
        
        if (baseSkillValue == null)
            return null;
        
        var newDescription = skillDict.isDamageSkill ? GetNewDescriptionPayloadsDamage(baseSkillValue) : GetNewDescriptionPayloadsHealing(baseSkillValue);
        AddExtraHint(hoveredID, ref newDescription);
        return newDescription;
    }

    private static List<Payload> GetNewDescriptionPayloadsDamage(uint[]? baseSkillValue)
    {
        var payloads = new List<Payload>();
        var damageParam = ProcessOriginNumber(true);
        // TODO
        return null;
    }

    private static void WritePayloads(ref List<Payload> payloads, Colors textColor, string text, Colors prefixColor = Colors.Normal, string prefix = "", bool newLine = true)
    {
        if (prefix != "")
        {
            payloads.Add(new UIForegroundPayload((ushort)prefixColor));
            payloads.Add(new TextPayload($"{prefix}："));
        }
        payloads.Add(new UIForegroundPayload((ushort)textColor));
        payloads.Add(new TextPayload($"{text}"));
        if (newLine)
            payloads.Add(new NewLinePayload());
    }
    
    private static List<Payload> GetNewDescriptionPayloadsHealing(uint[]? bsv)
    {
        var payloads      = new List<Payload>();
        var healingParam  = ProcessOriginNumber(false);
        
        var healingVal    = Math.Floor(bsv[(int)ValueUnitType.HealingVal] / 100d * healingParam.normal);
        var healOverTime  = Math.Floor(bsv[(int)ValueUnitType.HealOverTime] / 100d * healingParam.normal);
        var times         = bsv[(int)ValueUnitType.Count];
        var totalHealVal  = healingVal + (healOverTime * times);
        
        var shieldVal     = Math.Floor(bsv[(int)ValueUnitType.Shield] / 100d * healingParam.normal);
        var mitPercentVal = bsv[(int)ValueUnitType.Mitigation];
        var mitTypeStr    = 
            bsv[(int)ValueUnitType.MitigationType] == (uint)MitigationType.Physical ? "物理" :
            bsv[(int)ValueUnitType.MitigationType] == (uint)MitigationType.Magic ? "魔法" : "";
        
        var healBuff      = bsv[(int)ValueUnitType.HealBuff];
        var duration      = bsv[(int)ValueUnitType.Duration];
        
        for (var i = 0; i < ((ICollection)bsv).Count; i++)
        {
            if (bsv[i] == 0) continue;
            switch (i)
            {
                case (int)ValueUnitType.HealingVal:
                    WritePayloads(ref payloads, Colors.Normal, $"{healingVal}", Colors.Healing, "恢复力");
                    break;
                
                case (int)ValueUnitType.HealOverTime:
                    WritePayloads(ref payloads, Colors.Normal, $"{healOverTime}", Colors.Healing, "持续恢复力", times == 0);
                    break;
                
                case (int)ValueUnitType.Count:
                    
                    WritePayloads(ref payloads, Colors.Normal, $" * {times} 跳");
                    WritePayloads(ref payloads, Colors.Normal, $"{totalHealVal}", Colors.Healing, "总恢复力");
                    break;
                
                case (int)ValueUnitType.Shield:
                    
                    WritePayloads(ref payloads, Colors.Normal, $"{shieldVal}", Colors.Shield, "盾值");
                    break;
                
                case (int)ValueUnitType.Mitigation:
                    
                    WritePayloads(ref payloads, Colors.Normal, $"{mitPercentVal}% {mitTypeStr}", Colors.Shield, "减伤");
                    break;
                
                case (int)ValueUnitType.HealBuff:
                    
                    WritePayloads(ref payloads, Colors.Normal, $"{healBuff}%", Colors.Buff, "治疗量增益");
                    break;
                
                case (int)ValueUnitType.Duration:
                    
                    WritePayloads(ref payloads, Colors.Normal, $"{duration} 秒", Colors.Duration, "持续时间");
                    break;
            }
        }
        return payloads;
    }
    
    #endregion
    
    #region Calculator
    private static (double normal, double crit) ProcessOriginNumber(bool isDamageSkill)
    {
        var potency = Math.Floor(100 + ((isDamageSkill ? GetAttackModifier() : GetHealModifier()) * (GetAttackPotency() - GetLevelModifier().main) / GetLevelModifier().main)) / 100d;
        var baseMultiplier = Math.Floor(100 * potency * GetWeaponDamage());
        var detParam = GetDetParam();
        var tenParam = GetTenParam();
        var potencyWithAttribute = Math.Floor(baseMultiplier * (1 + detParam) * (1 + tenParam));
        
        var traitModifer = isDamageSkill || IsCaster() ? GetTraitModifier() : 1;
        var normal = Math.Floor(potencyWithAttribute * traitModifer);
        var crit = Math.Floor(normal * GetCritParam());
        
        return (normal, crit);
    }
    #endregion

    #region Modifier
    public static double GetTraitModifier() 
    {
        if (LocalPlayerState.ClassJob is 36) return 1.5;    // 青魔法师
        if (IsRanger()) return 1.2;
        if (IsCaster()) return 1.3;
        return 1;
    }
    
    public static double GetJobModifier()
    {
        var jobID = LocalPlayerState.ClassJob;
        return jobID switch
        {
            19 or 29 or 37 => 100,
            20 or 30 or 41 => 110,
            21 or 26 or 32 => 105,
            34             => 112,
            _              => 115,
        };
    }
    
    public static double GetAttackModifier()
    {
        var level = LocalPlayerState.CurrentLevel;
        if (IsTank())
        {
            return level switch {
                <= 80 => level + 35,
                <= 90 => ((level - 80) * 4.1) + 115,
                _     => ((level - 90) * 3.4) + 156
            };
        }
        else
        {
            return level switch {
                <= 70 => ((level - 50) * 2.5) + 75,
                <= 80 => ((level - 70) * 4) + 125,
                <= 90 => ((level - 80) * 3) + 165,
                _     => ((level - 90) * 4.2) + 195
            };
        }
    }

    private static (int main, int sub, int div) GetLevelModifier()
    {
        var level = LocalPlayerState.CurrentLevel;
        return level switch
        {
            70  => (292, 364, 900),
            80  => (340, 380, 1300),
            90  => (390, 400, 1900),
            100 => (440, 420, 2780),
            _   => (0, 0, 0)
        };
    }
    
    public static double GetHealModifier()
    {
        var level = LocalPlayerState.CurrentLevel;
        return level switch
        {
            < 80 => 120,
            _    => ((level - 80) * 2.5) + 120.8
        };
    }
    
    #endregion

    #region JobIdentifier

    private static bool IsCaster()
    {
        // 白魔、黑魔、召唤、学者、占星、赤魔、青魔、贤者、画家
        HashSet<uint> casterJobID = [24, 25, 27, 28, 33, 35, 36, 40, 42];
        return casterJobID.Contains(LocalPlayerState.ClassJob);
    }
    
    private static bool IsRanger()
    {
        // 机工、舞者、诗人
        HashSet<uint> rangerJobID = [23, 31, 38];
        return rangerJobID.Contains(LocalPlayerState.ClassJob);
    }
    
    private static bool IsTank()
    {
        // 战士、骑士、黑骑、绝枪
        HashSet<uint> tankJobID = [21, 19, 32, 37];
        return tankJobID.Contains(LocalPlayerState.ClassJob);
    }

    #endregion

    #region AttributeParam

    private static double GetAttackPotency() =>
        DService.Instance().PlayerState.GetAttribute(IsCaster() ? PlayerAttribute.AttackMagicPotency : PlayerAttribute.AttackPower);

    private static double GetWeaponDamage()
    {
        var inventoryExcelData = (ushort*)((nint)InventoryManager.Instance() + 9360);
        var weaponBaseDamage = inventoryExcelData[IsCaster() ? 21 : 20] + inventoryExcelData[33]; // 武器性能 + HQ加成
        return Math.Floor(weaponBaseDamage + (GetLevelModifier().main * GetJobModifier() / 1000d)) / 100d;
    }

    private static double GetDetParam()
    {
        var det = DService.Instance().PlayerState.GetAttribute(PlayerAttribute.Determination);
        var detParam = Math.Floor(140d * (det - GetLevelModifier().main) / GetLevelModifier().div) / 1000d;
        return detParam;
    }
    
    private static double GetTenParam()
    {
        var ten = DService.Instance().PlayerState.GetAttribute(PlayerAttribute.Tenacity);
        var tenParam = Math.Floor(112d * (ten - GetLevelModifier().sub) / GetLevelModifier().div) / 1000d;
        return tenParam;
    }
    
    private static double GetCritParam()
    {
        var crit = DService.Instance().PlayerState.GetAttribute(PlayerAttribute.CriticalHit);
        var critParam = Math.Floor((200d * (crit - GetLevelModifier().sub) / GetLevelModifier().div) + 1400) / 1000d;
        return critParam;
    }

    #endregion

    #region 技能字典

    private static Dictionary<uint, (string SkillName, bool isDamageSkill, uint[] ValueUnits)> ValidSkillsDictionary = new()
    {
        // 恢复力，持续恢复力，跳，盾值，减伤，类型（0全部、1魔法、2物理），治疗量增益，持续时间
        #region 技能字典 白魔法师
        { 124,   ("医治", false, [400, 0, 0, 0, 0, 0, 0, 0]) },
        { 135,   ("救疗", false, [800, 0, 0, 0, 0, 0, 0, 0]) },
        { 137,   ("再生", false, [0, 250, 6, 0, 0, 0, 0, 18]) },
        { 131,   ("愈疗", false, [600, 0, 0, 0, 0, 0, 0, 0]) },
        { 16531, ("安慰之心", false, [800, 0, 0, 0, 0, 0, 0, 0]) },
        { 16534, ("狂喜之心", false, [400, 0, 0, 0, 0, 0, 0, 0]) },
        { 37010, ("医养", false, [250, 175, 5, 0, 0, 0, 0, 15]) },
        { 3569,  ("庇护所", false, [0, 100, 9, 0, 0, 0, 10, 24]) },
        { 3571,  ("法令", false, [400, 0, 0, 0, 0, 0, 0, 0]) },
        { 3570,  ("神名", false, [700, 0, 0, 0, 0, 0, 0, 0]) },
        { 7432,  ("神祝祷", false, [0, 0, 0, 500, 0, 0, 0, 15]) },
        { 7433,  ("全大赦", false, [200, 0, 0, 0, 10, 0, 0, 10]) },
        { 16536, ("节制", false, [0, 0, 0, 0, 10, 0, 20, 20]) },
        { 25862, ("礼仪之铃", false, [0, 400, 5, 0, 0, 0, 0, 20]) },
        { 37011, ("神爱抚", false, [0, 200, 5, 400, 0, 0, 0, 10]) },
        #endregion
        
        #region 技能字典 占星术士
        { 3600, ("阳星", false, [400, 0, 0, 0, 0, 0, 0, 0]) },
        { 3610, ("福星", false, [800, 0, 0, 0, 0, 0, 0, 0]) },
        { 3595, ("吉星相位", false, [250, 250, 5, 0, 0, 0, 0, 0]) },
        { 37030, ("阳星合相", false, [250, 175, 5, 0, 0, 0, 0, 15]) },
        { 3614, ("先天禀赋", false, [400, 0, 0, 0, 0, 0, 0, 0]) },
        { 3613, ("命运之轮", false, [0, 100, 5, 0, 10, 0, 0, 10]) },
        { 16553, ("天星冲日", false, [200, 100, 5, 0, 10, 0, 0, 15]) },
        { 7439, ("地星", false, [720, 0, 0, 0, 0, 0, 0, 10]) },
        { 16556, ("天星交错", false, [200, 0, 0, 400, 0, 0, 0, 30]) },
        { 16557, ("天宫图", false, [200, 0, 0, 0, 0, 0, 0, 10]) },
        { 16559, ("中间学派", false, [0, 0, 0, 0, 0, 0, 20, 30]) },
        { 25873, ("擢升", false, [500, 0, 0, 0, 10, 0, 0, 8]) },
        { 37031, ("太阳星座", false, [0, 0, 0, 0, 10, 0, 0, 15]) },
        { 37024, ("放浪神之箭", false, [0, 0, 0, 0, 0, 0, 10, 15]) },
        { 37025, ("建筑神之塔", false, [0, 0, 0, 400, 0, 0, 0, 15]) },
        { 37027, ("世界树之干", false, [0, 0, 0, 0, 10, 0, 0, 15]) },
        { 37028, ("河流神之瓶", false, [0, 200, 5, 0, 0, 0, 0, 15]) },
        { 7445, ("王冠之贵妇", false, [400, 0, 0, 0, 0, 0, 0, 0]) },
        { 25874, ("大宇宙", false, [200, 0, 0, 0, 0, 0, 0, 0]) },
        { 25875, ("小宇宙", false, [200, 0, 0, 0, 0, 0, 0, 0]) },
        #endregion
        
        #region 技能字典 学者
        { 190,   ("医术", false, [450, 0, 0, 0, 0, 0, 0, 0]) },
        { 17215, ("朝日召唤", false, [0, 180, 0, 0, 0, 0, 0, 0]) },
        { 16545, ("炽天召唤", false, [0, 180, 0, 180, 0, 0, 0, 30]) },
        { 185,   ("鼓舞激励之策", false, [300, 0, 0, 540, 0, 0, 0, 30]) },
        { 37013, ("意气轩昂之策", false, [200, 0, 0, 360, 0, 0, 0, 30]) },
        { 16537, ("仙光的低语", false, [0, 80, 7, 0, 0, 0, 0, 21]) },
        { 16538, ("异想的幻光", false, [0, 0, 0, 0, 5, 1, 10, 20]) },
        { 189,   ("生命活性法", false, [600, 0, 0, 0, 0, 0, 10, 0]) },
        { 188,   ("野战治疗阵", false, [0, 100, 6, 0, 10, 0, 0, 15]) },
        { 3583,  ("不屈不挠之策", false, [400, 0, 0, 0, 0, 0, 0, 0]) },
        { 7434,  ("深谋远虑之策", false, [800, 0, 0, 0, 0, 0, 0, 45]) },
        { 3587,  ("转化", false, [0, 0, 0, 0, 0, 0, 20, 30]) },
        { 7437,  ("以太契约", false, [0, 300, 0, 0, 0, 0, 0, 0]) },
        { 16543, ("异想的祥光", false, [320, 0, 0, 0, 0, 0, 0, 0]) },
        { 16546, ("慰藉", false, [250, 0, 0, 250, 0, 0, 0, 30]) },
        { 25868, ("疾风怒涛之计", false, [0, 0, 0, 0, 10, 0, 0, 20]) },
        { 37014, ("炽天附体", false, [0, 100, 7, 0, 0, 0, 0, 20]) },
        { 37015, ("显灵之章", false, [360, 0, 0, 648, 0, 0, 0, 30]) },
        { 37016, ("降临之章", false, [240, 0, 0, 432, 0, 0, 0, 30]) },
        #endregion
        
        #region 技能字典 贤者
        { 24286, ("预后", false, [300, 0, 0, 0, 0, 0, 0, 0]) },
        { 37034, ("均衡预后II", false, [100, 0, 0, 360, 0, 0, 0, 30]) },
        { 24284, ("诊断", false, [450, 0, 0, 0, 0, 0, 0, 0]) },
        { 24291, ("均衡诊断", false, [300, 0, 0, 540, 0, 0, 0, 30]) },
        { 24318, ("魂灵风息", false, [600, 0, 0, 0, 0, 0, 0, 0]) },
        { 24285, ("心关", false, [0, 170, 0, 0, 0, 0, 0, 0]) },
        { 24296, ("灵橡清汁", false, [600, 0, 0, 0, 0, 0, 0, 0]) },
        { 24298, ("坚角清汁", false, [0, 100, 5, 0, 10, 0, 0, 15]) },
        { 24299, ("寄生清汁", false, [400, 0, 0, 0, 0, 0, 0, 0]) },
        { 24301, ("消化", false, [350, 0, 0, 0, 0, 0, 0, 0]) },
        { 24302, ("自生II", false, [0, 130, 5, 0, 0, 0, 10, 15]) },
        { 24303, ("白牛清汁", false, [700, 0, 0, 0, 10, 0, 0, 15]) },
        { 24305, ("输血", false, [0, 0, 0, 300, 0, 0, 0, 15]) },
        { 24311, ("泛输血", false, [0, 0, 0, 200, 0, 0, 0, 15]) },
        { 24310, ("整体论", false, [300, 0, 0, 300, 10, 0, 0, 20]) },
        { 24317, ("混合", false, [0, 0, 0, 0, 0, 0, 20, 10]) },
        { 24300, ("活化", false, [0, 0, 0, 0, 0, 0, 50, 30]) },
        { 37035, ("智慧之爱", false, [0, 150, 0, 0, 0, 0, 20, 20]) },
        #endregion
    };

    #endregion

    #region ExtraHint
    private static void AddExtraHint(uint hoveredID, ref List<Payload> payloads)
    {
        var healingParam = ProcessOriginNumber(false);
        var damageParam = ProcessOriginNumber(true);
        
        switch (hoveredID)
        {
            #region ExtraHint 白魔法师

            case 3571:  // 法令
                
                var damage3571 = Math.Floor(400d / 100 * damageParam.normal);
                WritePayloads(ref payloads, Colors.Normal, $"{damage3571}", Colors.Damage, "威力");
                break;
            
            case 3569:  // 庇护所
                
                WritePayloads(ref payloads, Colors.Hint, "治疗量增益Buff存在滞留效应，效果往往额外持续3秒。");
                break;
            
            case 7433:  // 全大赦
                
                WritePayloads(ref payloads, Colors.Hint, "使用医治、愈疗、医济/医养、狂喜之心触发额外的恢复效果。");
                break;
            
            case 16536:  // 节制
                
                if (LocalPlayerState.CurrentLevel == 100)
                    WritePayloads(ref payloads, Colors.Hint, "使用后变为“神安抚”。");
                break;
            
            case 25862:  // 礼仪之铃
                
                var heal25862 = Math.Floor(200d / 100 * healingParam.normal);
                WritePayloads(ref payloads, Colors.Normal, $"{heal25862}", Colors.Healing, "持续时间结束单层治疗量");
                WritePayloads(ref payloads, Colors.Hint, "白魔法师主体每受到一次伤害，触发一次治疗。");
                break;
            
            case 37011:  // 神爱抚
                
                WritePayloads(ref payloads, Colors.Hint, "持续恢复效果在盾值消失后触发。");
                break;
            
            #endregion
            
            #region ExtraHint 学者
            
            case 185:  // 鼓舞激励之策
                
                var heal185 = Math.Floor(300d / 100 * healingParam.crit);
                var shield185 = Math.Floor(540d / 100 * healingParam.crit);
                WritePayloads(ref payloads, Colors.Normal, $"{heal185}", Colors.Healing, "暴击恢复力");
                WritePayloads(ref payloads, Colors.Normal, $"{shield185}", Colors.Shield, "暴击盾值(鼓舞/激励)");
                WritePayloads(ref payloads, Colors.Hint, $"展开战术仅展开“鼓舞”盾值。");
                break;
            
            case 37013:  // 意气轩昂之策
                
                var shield37013 = Math.Floor(360d / 100 * healingParam.crit);
                var heal37013 = Math.Floor(200d / 100 * healingParam.crit);
                WritePayloads(ref payloads, Colors.Normal, $"{heal37013}", Colors.Healing, "暴击恢复力");
                WritePayloads(ref payloads, Colors.Normal, $"{shield37013}", Colors.Shield, "暴击盾值(鼓舞)");
                break;
            
            case 3583:  // 不屈不挠之策
                
                var heal3583 = Math.Floor(400d / 100 * healingParam.crit);
                WritePayloads(ref payloads, Colors.Normal, $"{heal3583}", Colors.Healing, "暴击恢复力");
                break;
            
            case 3587:  // 转化
                
                WritePayloads(ref payloads, Colors.Hint, $"无法使用仙女技。");
                WritePayloads(ref payloads, Colors.Hint, $"仅增益治疗魔法，不包含能力。");
                break;
            
            case 16538:  // 异想的幻光
                
                WritePayloads(ref payloads, Colors.Hint, $"仅增益治疗魔法，不包含能力。");
                break;
            
            case 25868:  // 疾风怒涛之计
                
                WritePayloads(ref payloads, Colors.Normal, $"10 秒", Colors.Duration, "疾跑效果持续时间");
                break;
            
            case 16545:  // 炽天召唤
                
                WritePayloads(ref payloads, Colors.Normal, $"22 秒（含出现动画）", Colors.Duration, "炽天使持续时间");
                break;
            
            case 188:  // 野战治疗阵
                
                WritePayloads(ref payloads, Colors.Hint, $"减伤Buff存在滞留效应，效果往往额外持续3秒。");
                break;
            
            case 37015 or 37016:  // 显灵之章 降临之章
                
                WritePayloads(ref payloads, Colors.Hint, $"无法响应“秘策”效果。");
                break;
            
            #endregion

            #region ExtraHint 占星术士

            case 3614:  // 先天禀赋
                
                var heal3614 = Math.Floor(900d / 100 * healingParam.normal);
                WritePayloads(ref payloads, Colors.Normal, $"{heal3614}", Colors.Healing, "目标HP 30%以下时恢复力");
                break;
            
            case 3613:  // 命运之轮
                
                WritePayloads(ref payloads, Colors.Hint, $"持续展开命运之轮将刷新持续恢复时间，最多展开18秒。");
                WritePayloads(ref payloads, Colors.Hint, $"减伤持续时间不变。");
                break;
            
            case 7439:  // 地星
                
                var damage7439Min = Math.Floor(205d / 100 * damageParam.normal);
                var damage7439Max = Math.Floor(310d / 100 * damageParam.normal);
                var heal7439Min = Math.Floor(540d / 100 * healingParam.normal);
                WritePayloads(ref payloads, Colors.Normal, $"{damage7439Max}", Colors.Damage, "巨星主宰威力");
                WritePayloads(ref payloads, Colors.Normal, $"{heal7439Min}", Colors.Healing, "小地星恢复力");
                WritePayloads(ref payloads, Colors.Normal, $"{damage7439Min}", Colors.Damage, "小地星威力");
                WritePayloads(ref payloads, Colors.Hint, $"小地星10秒后变为巨星，巨星10秒后自动爆炸。");
                break;

            case 16557:  // 天宫图
                
                var heal16557 = Math.Floor(400d / 100 * healingParam.normal);
                WritePayloads(ref payloads, Colors.Normal, $"{heal16557}", Colors.Healing, "阳星天宫图恢复力");
                WritePayloads(ref payloads, Colors.Normal, $"30 秒", Colors.Duration, "阳星天宫图持续时间");
                WritePayloads(ref payloads, Colors.Hint, $"释放阳星相位/阳星合相后变为阳星天宫图。");
                WritePayloads(ref payloads, Colors.Hint, $"持续时间结束自动触发恢复效果。");
                break;
            
            case 25873:  // 擢升
                
                WritePayloads(ref payloads, Colors.Hint, $"持续时间结束自动触发恢复效果。");
                break;
            
            case 25875:  // 小宇宙
                
                WritePayloads(ref payloads, Colors.Normal, $"积蓄所受伤害的 50%", Colors.Healing, "额外恢复力");
                WritePayloads(ref payloads, Colors.Hint, $"持续时间结束自动触发恢复效果。");
                break;
            
            case 25874:  // 大宇宙
                
                var damage25874 = Math.Floor(270d / 100 * damageParam.normal);
                WritePayloads(ref payloads, Colors.Normal, $"{damage25874}", Colors.Damage, "威力");
                WritePayloads(ref payloads, Colors.Normal, $"40%", Colors.Damage, "额外目标衰减");
                WritePayloads(ref payloads, Colors.Normal, $"积蓄所受伤害的 50%", Colors.Healing, "额外恢复力");
                WritePayloads(ref payloads, Colors.Hint, $"持续时间结束自动触发恢复效果。");
                break;
            
            case 37030:  // 阳星合相
                
                var shield37030 = Math.Floor(312.5d / 100 * healingParam.crit);
                WritePayloads(ref payloads, Colors.Normal, $"{shield37030}", Colors.Shield, "中间学派额外盾值");
                WritePayloads(ref payloads, Colors.Normal, $"30 秒", Colors.Duration, "盾值持续时间");
                break;
            
            case 3595:  // 吉星相位
                
                var shield3595 = Math.Floor(625d / 100 * healingParam.crit);
                WritePayloads(ref payloads, Colors.Normal, $"{shield3595}", Colors.Shield, "吉星相位额外盾值");
                WritePayloads(ref payloads, Colors.Normal, $"30 秒", Colors.Duration, "盾值持续时间");
                break;
            
            #endregion

            #region ExtraHint 贤者

            case 37034:  // 均衡预后II
                
                var shield37034 = Math.Floor(360d / 100 * 1.5 * healingParam.normal);
                var heal37034 = Math.Floor(100d / 100 * 1.5 * healingParam.normal);
                WritePayloads(ref payloads, Colors.Normal, $"{heal37034}", Colors.Healing, "活化状态下恢复力");
                WritePayloads(ref payloads, Colors.Normal, $"{shield37034}", Colors.Shield, "活化状态下盾值");
                WritePayloads(ref payloads, Colors.Hint, $"无视数值覆盖学者“鼓舞”盾值。");
                break;
            
            case 24291:  // 均衡诊断
                
                var heal24291 = Math.Floor(300d / 100 * 1.5 * healingParam.normal);
                var shield24291 = Math.Floor(540d / 100 * 1.5 * healingParam.normal);
                var heal24291Crit = Math.Floor(300d / 100 * healingParam.crit);
                var shield24291Crit = Math.Floor(540d / 100 * healingParam.crit);
                WritePayloads(ref payloads, Colors.Normal, $"{heal24291}", Colors.Healing, "活化状态下恢复力");
                WritePayloads(ref payloads, Colors.Normal, $"{shield24291}", Colors.Shield, "活化状态下盾值");
                WritePayloads(ref payloads, Colors.Normal, $"{heal24291Crit}", Colors.Healing, "暴击恢复力");
                WritePayloads(ref payloads, Colors.Normal, $"{shield24291Crit}", Colors.Shield, "暴击盾值(均衡/齐衡)");
                WritePayloads(ref payloads, Colors.Hint, $"无视数值覆盖学者“鼓舞”盾值。");
                break;
            
            case 24285:  // 心关
                
                var heal24285 = Math.Floor(170d / 100 * 1.7 * healingParam.normal);
                WritePayloads(ref payloads, Colors.Normal, $"{heal24285}", Colors.Healing, "拯救状态下持续恢复力");
                break;
            
            case 24318:  // 魂灵风息
                
                var heal24318 = Math.Floor(600d / 100 * 1.5 * healingParam.normal);
                var damage24318 = Math.Floor(380d / 100 * damageParam.normal);
                WritePayloads(ref payloads, Colors.Normal, $"{heal24318}", Colors.Healing, "活化状态下恢复力");
                WritePayloads(ref payloads, Colors.Normal, $"{damage24318}", Colors.Damage, "威力");
                WritePayloads(ref payloads, Colors.Normal, $"40%", Colors.Damage, "额外目标衰减");
                break;
            
            case 24300:  // 活化
                WritePayloads(ref payloads, Colors.Hint, $"仅作用一次治疗魔法。");
                WritePayloads(ref payloads, Colors.Hint, $"仅增益治疗魔法，不包含能力。");
                break;
            
            case 24301:  // 消化
                
                var heal24301 = Math.Floor(450d / 100 * healingParam.normal);
                WritePayloads(ref payloads, Colors.Hint, $"上述为移除“均衡预后”(群盾)恢复力。");
                WritePayloads(ref payloads, Colors.Normal, $"{heal24301}", Colors.Healing, "移除“均衡诊断”(单盾)恢复力");
                break;
            
            case 24303:  // 白牛清汁
                
                WritePayloads(ref payloads, Colors.Hint, $"无法与尖角清汁(罩子)减伤效果共存。");
                break;
            
            case 24305 or 24311:  // 输血、泛输血
                
                var shield24305 = Math.Floor((hoveredID == 24305 ? 1500d : 1000d) / 100 * healingParam.normal);
                var heal24305 = Math.Floor((hoveredID == 24305 ? 150d : 100d) / 100 * healingParam.normal);
                WritePayloads(ref payloads, Colors.Hint, $"盾值因伤害消失后，重新附加一层，上限五层。");
                WritePayloads(ref payloads, Colors.Normal, $"{shield24305}", Colors.Shield, "总盾值");
                WritePayloads(ref payloads, Colors.Normal, $"{heal24305}", Colors.Healing, "持续时间结束单层治疗量");
                break;
            
            case 24310:  // 整体论
                
                WritePayloads(ref payloads, Colors.Normal, $"30 秒", Colors.Duration, "盾值持续时间");
                break;

            case 37035:  // 智慧之爱
                
                WritePayloads(ref payloads, Colors.Hint, $"使用魔法技能触发恢复效果。");
                WritePayloads(ref payloads, Colors.Hint, $"仅增益治疗魔法，不包含能力。");
                break;
            
            #endregion
            
            default:
                break;
        }
    }
    
    #endregion
    
    #region 数组属性

    private enum ValueUnitType : int
    {
        HealingVal     = 0,
        HealOverTime   = 1,
        Count          = 2,
        Shield         = 3,
        Mitigation     = 4,
        MitigationType = 5,
        HealBuff       = 6,
        Duration       = 7,
    }
    
    public enum MitigationType: uint
    {
        All      = 0,
        Magic    = 1,
        Physical = 2
    }

    private enum Colors: ushort
    {
        Healing  = 57,
        Shield   = 45,
        Damage   = 16,
        Buff     = 24,
        Duration = 32,
        Normal   = 0,
        Hint     = 3,
    }
    
    #endregion
}

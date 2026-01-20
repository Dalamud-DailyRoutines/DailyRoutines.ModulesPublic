using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Game.Player;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Game;
using Newtonsoft.Json;

// 以下using为本地模块加载测试时使用
using OmenTools;
using OmenTools.Infos;
using OmenTools.Managers;
using OmenTools.Helpers;
using static OmenTools.Helpers.ThrottlerHelper;
using static DailyRoutines.Helpers.NotifyHelper;

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
***********************************************************************************/

namespace DailyRoutines.ModulesPublic;

public class AutoQuantifyHealerSkillData : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动量化治疗技能数据",
        Description = "将治疗技能的恢复力、持续恢复力等数据结合自身属性量化为具体数值，数值为最低期望。",
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Usami"]
    };
    
    private static CancellationTokenSource? RemoteFetchCancelSource;

    private static TooltipModification? ActionModification;
    private static PlayerStrat Player;
    
    private static readonly HashSet<uint> ValidJobs   = [24, 28, 33, 40];     // 白魔法师、学者、占星术士、贤者
    private static readonly HashSet<uint> ValidLevels = [100];
    
    protected override unsafe void Init()
    {
        DService.Instance().ClientState.ClassJobChanged       += OnClassJobChanged;
        DService.Instance().ClientState.LevelChanged          += OnLevelChanged;
        DService.Instance().GameInventory.InventoryChanged    += OnInventoryChanged;
        
        TaskHelper ??= new() { TimeoutMS = 1_000 };
        
        RemoteFetchCancelSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        _                       = RemoteRepoManager.FetchSkillDataAsync(RemoteFetchCancelSource.Token);
        _                       = RemoteRepoManager.FetchExtraHintAsync(RemoteFetchCancelSource.Token);
        
        GameTooltipManager.Instance().RegGenerateActionTooltipModifier(ModifyActionTooltip);
        UpdatePlayerInfo();
    }

    protected override unsafe void Uninit()
    {
        DService.Instance().ClientState.ClassJobChanged       -= OnClassJobChanged;
        DService.Instance().ClientState.LevelChanged          -= OnLevelChanged;
        DService.Instance().GameInventory.InventoryChanged    -= OnInventoryChanged;
        
        RemoteFetchCancelSource?.Cancel();
        RemoteFetchCancelSource?.Dispose();
        RemoteFetchCancelSource = null;
        
        GameTooltipManager.Instance().Unreg(generateActionModifiers: ModifyActionTooltip);
        GameTooltipManager.Instance().RemoveActionDetail(ActionModification);
    }
    
    private void OnClassJobChanged(uint classJobID) =>
        UpdatePlayerInfo();

    private void OnLevelChanged(uint classJobId, uint level) =>
        UpdatePlayerInfo();
    
    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events) =>
        UpdatePlayerInfo();
    
    private unsafe void UpdatePlayerInfo()
    {
        if (!Throttler.Throttle("AutoQuantifyHealerSkillData-OnUpdatePlayerStrat")) return;
       
        TaskHelper.DelayNext(500, "等待 500 毫秒");
        // DService.Instance().Log.Debug($"触发等待，即将更新玩家数据");
        TaskHelper.Enqueue(() =>
        {
            // DService.Instance().Log.Debug($"更新玩家数据");
            Player.Level = LocalPlayerState.CurrentLevel;
            Player.ClassJob = LocalPlayerState.ClassJob;
            
            if (!ValidLevels.Contains(Player.Level))   return;
            if (!ValidJobs.Contains(Player.ClassJob))  return;
            
            bool isCaster = LocalPlayerState.ClassJobData.ClassJobCategory.RowId == 31;
            var attackValue = DService.Instance().PlayerState.GetAttribute(isCaster ? PlayerAttribute.AttackMagicPotency: PlayerAttribute.AttackPower);
            
            var levelModifier = Player.Level switch
            {
                70  => (main: 292u, sub: 364u, div: 900u),
                80  => (main: 340u, sub: 380u, div: 1300u),
                90  => (main: 390u, sub: 400u, div: 1900u),
                100 => (main: 440u, sub: 420u, div: 2780u),
                _   => (main: 0u,   sub: 0u,   div: 0u)
            };
            
            var traitModifier = (LocalPlayerState.ClassJob, LocalPlayerState.ClassJobData) switch
            {
                (36, _)                                      => 1.5f, // BlueMage
                (_, { Role: 3, ClassJobCategory.RowId: 30 }) => 1.2f, // Ranger
                (_, {          ClassJobCategory.RowId: 31 }) => 1.3f, // Caster
                _                                            => 1.0f,
            };
            
            var jobModifier = LocalPlayerState.ClassJob switch
            {
                19 or 29 or 37 => 100,
                20 or 30 or 41 => 110,
                21 or 26 or 32 => 105,
                34             => 112,
                _              => 115,
            };
            
            var attackModifier = Player.Level switch
            {
                70  => LocalPlayerState.ClassJobData.Role == 1 ? 105u : 125u,
                80  => LocalPlayerState.ClassJobData.Role == 1 ? 115u : 165u,
                90  => LocalPlayerState.ClassJobData.Role == 1 ? 156u : 195u,
                100 => LocalPlayerState.ClassJobData.Role == 1 ? 190u : 237u,
                _   => 0u
            };
            
            var healModifier = Player.Level switch
            {
                70  => 120.0f,
                80  => 120.8f,
                90  => 145.8f,
                100 => 170.8f,
                _   => 0.0f
            };
            
            var inventoryExcelData = (ushort*)((nint)InventoryManager.Instance() + 9360);
            var weaponBaseDamage   = inventoryExcelData[isCaster ? 21 : 20] + inventoryExcelData[33];
            float weaponDamage     = (int)(weaponBaseDamage + (levelModifier.main * jobModifier / 1000f)) / 100f;
            
            float detParam  = (int)(140 * 
                                   (DService.Instance().PlayerState.GetAttribute(PlayerAttribute.Determination) - levelModifier.main) / 
                                   levelModifier.div) / 1000f;
            float tenParam  = (int)(112 * 
                                   (DService.Instance().PlayerState.GetAttribute(PlayerAttribute.Tenacity) - levelModifier.sub) / 
                                   levelModifier.div) / 1000f;
            float critParam = (int)((200 * 
                                     (DService.Instance().PlayerState.GetAttribute(PlayerAttribute.CriticalHit) - levelModifier.sub) / 
                                     levelModifier.div) + 1400) / 1000f;
            
            (int normal, int crit) Calculate(float modifier, float traitMod)
            {
                float potency              = (int)(100 + (modifier * (attackValue - levelModifier.main) / levelModifier.main)) / 100f;
                int   multiplier           = (int)(100 * potency * weaponDamage);
                uint  potencyWithAttribute = (uint)(multiplier * (1 + detParam) * (1 + tenParam));
                int   normal               = (int)(potencyWithAttribute * traitMod);
                int   crit                 = (int)(normal * critParam);
                
                return (normal, crit);
            }
            
            Player.HealParam = Calculate(healModifier, isCaster ? traitModifier : 1.0f);
            Player.DamageParam = Calculate(attackModifier, traitModifier);
        });
    }
    
    private static unsafe void ModifyActionTooltip(AtkUnitBase* addonActionDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
    {
        if (ActionModification != null)
        {
            GameTooltipManager.Instance().RemoveActionDetail(ActionModification);
            ActionModification = null;
        }

        if (!ValidLevels.Contains(Player.Level))   return;
        if (!ValidJobs.Contains(Player.ClassJob))  return;

        var hoveredID = AgentActionDetail.Instance()->ActionId;
        
        var skillDataDict = RemoteRepoManager.GetSkillDataInfo();
        if (skillDataDict.Count == 0)
        {
            DService.Instance().Log.Debug($"技能数据字典为空");
            return;
        }
        var skillEntry = skillDataDict
                         .Where(x => x.Value.ActionID == hoveredID && x.Value.Level >= Player.Level)
                         .OrderByDescending(x => x.Value.Level)
                         .FirstOrDefault();
        var skillData  = skillEntry.Value;
        var extraHints = RemoteRepoManager.GetExtraHintInfo()
                                          .Where(x => x.Value.ActionID == hoveredID && x.Value.Level >= Player.Level)
                                          .OrderBy(x => x.Value.Order)
                                          .Select(x => x.Value);
        var newDescription = GetNewDescriptionPayloads(skillData, extraHints);
        
        ActionModification = GameTooltipManager.Instance().AddActionDetail
        (
            hoveredID,
            TooltipActionType.Description,
            newDescription.Build(),
            TooltipModifyMode.Overwrite
        );
    }
    
    private static class RemoteRepoManager
    {
        private const string URI = "https://raw.githubusercontent.com/lianying1997/TempRepoScript/refs/heads/main";
        
        private static FrozenDictionary<uint, SkillData> SkillDataInfo = FrozenDictionary<uint, SkillData>.Empty;
        private static FrozenDictionary<uint, ExtraHint> ExtraHintInfo = FrozenDictionary<uint, ExtraHint>.Empty;
        public static FrozenDictionary<uint, SkillData> GetSkillDataInfo() =>
            !Throttler.Throttle("AutoQuantifyHealerSkillData-GetSkillDataInfo") ? SkillDataInfo : Volatile.Read(ref SkillDataInfo);

        public static FrozenDictionary<uint, ExtraHint> GetExtraHintInfo() =>
            !Throttler.Throttle("AutoQuantifyHealerSkillData-GetExtraHintInfo") ? ExtraHintInfo : Volatile.Read(ref ExtraHintInfo);

        private static async Task FetchDataAsync<TJson, TData>(
            CancellationToken ct,
            string targetFile,
            string errorMessagePrefix,
            Func<TJson, uint> getID,
            Func<TJson, TData> createData,
            Action<Dictionary<uint, TData>> setResult)
            where TJson : class
        {
            try
            {
                var url = $"{URI}/{targetFile}.json";
                var json = await HTTPClientHelper.Get().GetStringAsync(url, ct).ConfigureAwait(false);
                var resp = JsonConvert.DeserializeObject<TJson[]>(json);
                if (resp == null)
                {
                    DService.Instance().Log.Error($"{targetFile}解析失败");
                    return;
                }
                var builder = new Dictionary<uint, TData>(resp.Length);
                foreach (var item in resp)
                    builder[getID(item)] = createData(item);
                setResult(builder);
            }

            catch (OperationCanceledException)
            {
                DService.Instance().Log.Error($"[AutoQuantifyHealerSkillData] OperationCanceledException");
            }

            catch (Exception ex)
            {
                DService.Instance().Log.Error($"[AutoQuantifyHealerSkillData] {errorMessagePrefix}解析失败: {ex}");
            }
        }

        public static async Task FetchSkillDataAsync(CancellationToken ct)
        {
            await FetchDataAsync<SkillDataJson, SkillData>(
                ct,
                "skillData",
                "技能文件",
                item => item.ID,
                item => new SkillData(
                    item.SkillName, item.Category, item.ActionID, item.Level,
                    item.IsDamageSkill, item.MainVal, item.ContVal, item.Times,
                    item.MinorVal, item.MitPercent, item.MitType, item.BuffPercent, item.Duration),
                builder => Volatile.Write(ref SkillDataInfo, builder.ToFrozenDictionary())
            );
        }
        
        public static async Task FetchExtraHintAsync(CancellationToken ct)
        {
            await FetchDataAsync<ExtraHintJson, ExtraHint>(
                ct,
                "extraHint",
                "额外提示文件",
                item => item.ID,
                item => new ExtraHint(
                    item.SkillName, item.Category, item.ActionID, item.Level,
                    item.TemplateType, item.Value, item.ColorType, item.Description,
                    item.Order),
                builder => Volatile.Write(ref ExtraHintInfo, builder.ToFrozenDictionary())
            );
        }
    }

    #region Description
    private static SeStringBuilder GetNewDescriptionPayloads(SkillData skillData, IEnumerable<ExtraHint> extraHint)
    {
        var newDescription = skillData.IsDamageSkill ?
                                 GetNewDescriptionPayloadsDamage(skillData) :
                                 GetNewDescriptionPayloadsHealing(skillData);
        AddExtraHint(extraHint, ref newDescription);
        return newDescription;
    }
    
    private static SeStringBuilder GetNewDescriptionPayloadsDamage(SkillData skd)
    {
        var payloads = new SeStringBuilder();
        var damageParam = Player.DamageParam;
        // TODO
        return null;
    }

    private static void WritePayloads(ref SeStringBuilder payloads, Colors textColor, string text,
                                      Colors prefixColor = Colors.Normal, string prefix = "", bool newLine = true)
    {
        if (prefix != "")
        {
            payloads.AddUiForeground((ushort)prefixColor);
            payloads.AddText($"{prefix}：");
            payloads.AddUiForegroundOff();
        }
        payloads.AddUiForeground((ushort)textColor);
        payloads.AddText($"{text}");
        if (newLine)
            payloads.Add(NewLinePayload.Payload);
    }
    
    
    private static SeStringBuilder GetNewDescriptionPayloadsHealing(SkillData skd)
    {
        var payloads      = new SeStringBuilder();
        var healingParam  = Player.HealParam;
        
        var healingVal    = Math.Floor(skd.MainVal / 100d * healingParam.normal);
        var healOverTime  = Math.Floor(skd.ContVal / 100d * healingParam.normal);
        var times         = skd.Times;
        var totalHealVal  = healingVal + (healOverTime * times);
        
        var shieldVal     = Math.Floor(skd.MinorVal / 100d * healingParam.normal);
        var mitPercentVal = skd.MitPercent;
        var mitTypeStr    = skd.MitType switch
            {
                (uint)MitigationType.Physical => "物理",
                (uint)MitigationType.Magic => "魔法",
                _ => ""
            };
        
        var healBuff      = skd.BuffPercent;
        var duration      = skd.Duration;
        
        if (healingVal!= 0)
            WritePayloads(ref payloads, Colors.Normal, $"{healingVal}", Colors.Healing, "恢复力");
        if (healOverTime != 0)
            WritePayloads(ref payloads, Colors.Normal, $"{healOverTime}", Colors.Healing, "持续恢复力", times == 0);
        if (times != 0)
        {
            WritePayloads(ref payloads, Colors.Normal, $" * {times} 跳");
            WritePayloads(ref payloads, Colors.Normal, $"{totalHealVal}", Colors.Healing, "总恢复力");
        }
        if (shieldVal != 0)
            WritePayloads(ref payloads, Colors.Normal, $"{shieldVal}", Colors.Shield, "盾值");
        if (mitPercentVal != 0)
            WritePayloads(ref payloads, Colors.Normal, $"{mitPercentVal}% {mitTypeStr}", Colors.Shield, "减伤");
        if (healBuff != 0)
            WritePayloads(ref payloads, Colors.Normal, $"{healBuff}%", Colors.Buff, "治疗量增益");
        if (duration != 0)
            WritePayloads(ref payloads, Colors.Normal, $"{duration} 秒", Colors.Duration, "持续时间");
            
        return payloads;
    }
    
    #endregion

    #region ExtraHint
    
    private static void AddExtraHint(IEnumerable<ExtraHint> extraHints, ref SeStringBuilder payloads)
    {
        foreach (var hint in extraHints)
        {
            var value = CalculateValue();
            var hasPrefixAndValue = hint.ColorType != "hint";
            var prefixColor = hint.ColorType switch
            {
                "damage"   => Colors.Damage,
                "healing"  => Colors.Healing,
                "duration" => Colors.Duration,
                "shield"   => Colors.Shield,
                "buff"     => Colors.Buff,
                "hint"     => Colors.Hint,
                _          => Colors.Normal
            };
            payloads.AddUiForeground((ushort)prefixColor);
            payloads.AddText($"{hint.Description}{(hasPrefixAndValue ? "：" : "")}");
            payloads.AddUiForegroundOff();
            
            if (!string.IsNullOrEmpty(value) && value != "0")
            {
                payloads.AddUiForeground((ushort)Colors.Normal);
                payloads.AddText(value);
                payloads.AddUiForegroundOff();
            }
            payloads.Add(new NewLinePayload());
            
            string CalculateValue()
            {
                if (!float.TryParse(hint.Value, out var val))
                    return hint.Value;

                var calculated = hint.TemplateType switch
                {
                    "damage_norm_calc" => Player.DamageParam.normal * val / 100,
                    "damage_crit_calc" => Player.DamageParam.crit   * val / 100,
                    "heal_norm_calc"   => Player.HealParam.normal   * val / 100,
                    "heal_crit_calc"   => Player.HealParam.crit     * val / 100,
                    _ => 0
                };
                return calculated > 0 ? ((uint)calculated).ToString() : "0";
            }
        }
    }
    
    #endregion
    
    #region 数组属性
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
    private struct PlayerStrat
    {
        public uint Level;
        public uint ClassJob;
        
        public (int normal, int crit) HealParam;
        public (int normal, int crit) DamageParam;
    }
    
    private sealed class SkillDataJson
    {
        [JsonProperty("ID")]            public uint     ID            { get; private set; }
        [JsonProperty("SkillName")]     public string   SkillName     { get; private set; }
        [JsonProperty("Category")]      public string   Category      { get; private set; }
        [JsonProperty("ActionID")]      public uint     ActionID      { get; private set; }
        [JsonProperty("Level")]         public uint     Level         { get; private set; }
        [JsonProperty("IsDamageSkill")] public bool     IsDamageSkill { get; private set; }
        [JsonProperty("MainVal")]       public uint     MainVal       { get; private set; }
        [JsonProperty("ContVal")]       public uint     ContVal       { get; private set; }
        [JsonProperty("Times")]         public uint     Times         { get; private set; }
        [JsonProperty("MinorVal")]      public uint     MinorVal      { get; private set; }
        [JsonProperty("MitPercent")]    public uint     MitPercent    { get; private set; }
        [JsonProperty("MitType")]       public uint     MitType       { get; private set; }
        [JsonProperty("BuffPercent")]   public uint     BuffPercent   { get; private set; }
        [JsonProperty("Duration")]      public uint     Duration      { get; private set; }
    }
    
    private readonly struct SkillData
    (
        string   skillName    ,
        string   category     ,
        uint     actionID     ,
        uint     level        ,
        bool     isDamageSkill,
        uint     mainVal      ,
        uint     contVal      ,
        uint     times        ,
        uint     minorVal     ,
        uint     mitPercent   ,
        uint     mitType      ,
        uint     buffPercent  ,
        uint     duration     
    )
    {
        public string   SkillName     { get; } = skillName     ;
        public string   Category      { get; } = category      ;
        public uint     ActionID      { get; } = actionID      ;
        public uint     Level         { get; } = level         ;
        public bool     IsDamageSkill { get; } = isDamageSkill ;
        public uint     MainVal       { get; } = mainVal       ;
        public uint     ContVal       { get; } = contVal       ;
        public uint     Times         { get; } = times         ;
        public uint     MinorVal      { get; } = minorVal      ;
        public uint     MitPercent    { get; } = mitPercent    ;
        public uint     MitType       { get; } = mitType       ;
        public uint     BuffPercent   { get; } = buffPercent   ;
        public uint     Duration      { get; } = duration      ;
    }
    
    private sealed class ExtraHintJson
    {
        [JsonProperty("ID")]            public uint     ID              { get; private set; }
        [JsonProperty("SkillName")]     public string   SkillName       { get; private set; }
        [JsonProperty("Category")]      public string   Category        { get; private set; }
        [JsonProperty("ActionID")]      public uint     ActionID        { get; private set; }
        [JsonProperty("Level")]         public uint     Level           { get; private set; }
        [JsonProperty("TemplateType")]  public string   TemplateType    { get; private set; }
        [JsonProperty("Value")]         public string   Value           { get; private set; }
        [JsonProperty("ColorType")]     public string   ColorType       { get; private set; }
        [JsonProperty("Description")]   public string   Description     { get; private set; }
        [JsonProperty("Order")]         public uint     Order           { get; private set; }
    }
    
    private readonly struct ExtraHint
    (
        string   skillName    ,
        string   category     ,
        uint     actionID     ,
        uint     level    ,
        string   templateType ,
        string   value        ,
        string   colorType    ,
        string   description  ,
        uint     order        
    )
    {
        public string   SkillName    { get; } = skillName    ;
        public string   Category     { get; } = category     ;
        public uint     ActionID     { get; } = actionID     ;
        public uint     Level        { get; } = level    ;
        public string   TemplateType { get; } = templateType ;
        public string   Value        { get; } = value        ;
        public string   ColorType    { get; } = colorType    ;
        public string   Description  { get; } = description  ;
        public uint     Order        { get; } = order        ;
    }
    
    #endregion
}

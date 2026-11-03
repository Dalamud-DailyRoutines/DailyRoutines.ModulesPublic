using System.Collections.Frozen;

namespace DailyRoutines.ModulesPublic.AutoSupportKeyboundBrawlerPinyin;

public unsafe partial class AutoSupportKeyboundBrawlerPinyin
{
    private readonly Dictionary<string, string[][]> cangjieReadings = new(StringComparer.Ordinal);

    private bool MatchesCangjieReading(string word, string input)
    {
        if (!cangjieReadings.TryGetValue(word, out var characters))
            return false;

        Span<bool> positions     = new bool[input.Length + 1];
        Span<bool> nextPositions = new bool[input.Length + 1];
        positions[0] = true;

        foreach (var codes in characters)
        {
            nextPositions.Clear();

            for (var offset = 0; offset < input.Length; offset++)
            {
                if (!positions[offset])
                    continue;

                foreach (var code in codes)
                {
                    if (input.AsSpan(offset).StartsWith(code, StringComparison.Ordinal))
                        nextPositions[offset + code.Length] = true;
                }
            }

            var previousPositions = positions;
            positions     = nextPositions;
            nextPositions = previousPositions;
        }

        return positions[input.Length];
    }

    #region 常量

    private const string CANGJIE_RADICALS = "日月金木水火土竹戈十大中一弓人心手口尸廿山女田難卜重";

    private static readonly FrozenDictionary<char, string[]> CANGJIE_READINGS =
        """
            一 m
            丁 mn
            七 ju jv xjv
            万 ms
            三 mmm
            上 ym
            下 my
            不 mf
            与 ysm
            丑 ng
            专 qni
            世 pt
            丘 om
            业 tc xtc
            丝 vvm
            两 moob
            严 mtch
            丧 gcv
            个 ol xol
            中 l
            串 ll
            临 lloa
            丸 kni knk
            为 iksi
            丽 mbib mmbib
            久 no
            么 hi
            义 ik xik
            之 ino
            乎 hfd
            乐 hvd
            乔 hkll
            乘 hdlp
            习 sim
            乡 vvh
            书 ids
            乱 hru
            了 nn
            争 nsd
            事 jlln
            于 md
            云 mmi
            五 mdm
            亚 mtc
            亡 yv
            交 yck
            享 yrnd
            亮 yrbn yrbu
            亲 ytd
            人 o
            亿 on xon
            什 oj
            仇 okn
            今 oin
            从 oo
            他 opd
            付 odi
            仙 ou
            代 oip
            令 oini omsl xomsl
            以 vio
            仪 oik xoik
            们 olis
            价 ooll
            任 ohg omg xomg
            伊 osk
            伍 omdm
            伏 oik
            伐 oi
            休 od
            优 oiku
            伙 of
            会 ommi
            伟 oqs
            传 oqni
            伤 ooks
            伦 oop
            伯 oha
            似 ovio
            伽 oksr
            但 oam
            位 oyt
            住 oyg
            体 odm
            何 omnr
            作 ohs
            你 onf
            佩 ohnb
            使 ojlk
            侬 ohbv
            俏 ofb
            保 ord
            信 oymr
            修 olhh oloh
            俯 oioi
            值 ojbm
            假 orye
            做 ojrk
            停 oyrn
            偶 owlb
            傀 ohi
            催 ouog
            像 onao
            儡 owww
            兀 mu
            充 yihu
            先 hghu
            光 fmu
            克 jrhu
            党 fbrhu
            入 oh
            全 omg
            八 ho
            公 ci
            兰 tmm
            共 tc
            关 cmk tk
            兴 fmc
            兵 omc
            其 tmmc
            具 bmmc bumc
            兹 tvii
            兽 cwmr
            内 ob xob
            再 mgb
            军 bkq
            农 hbv
            冠 bmui
            冥 bayc
            冰 ime
            冲 iml
            冻 imkd
            准 imog
            凌 imgce
            几 hn
            凡 hni
            凤 hne
            凯 uuhn
            凰 hnhag
            凶 uk
            出 uu
            击 qu
            刀 sh
            刃 shh shi sk xxxsk
            分 csh
            切 psh
            刑 mtln
            划 iln
            列 mnln
            则 boln
            判 fqln
            利 hdln
            别 rsln xrsln
            到 mgln
            制 hbln
            刺 dbln
            刻 yoln
            刽 oiln
            剂 ylln
            前 tbln
            剑 omln
            剧 srln
            剿 vdln
            力 ks
            功 mks
            加 ksr
            劣 fhks
            动 miks
            助 bmks
            努 veks
            励 msks
            劲 nmks
            劳 tbks
            势 qiks qkks
            勃 jdks
            勇 nbks nwks
            勋 roks
            勒 tjks
            匕 uh um
            化 op
            匙 aop
            匠 shml
            匪 slmy
            区 sk
            医 sok
            匿 stkr
            十 j
            升 ht
            华 opj
            单 cwj
            卖 jnyk
            南 jbtj
            博 jibi jiwi
            卡 ymy
            印 hpsl
            即 aisl
            却 gisl
            卷 fqsu
            历 mks xmks
            压 mgi xmgi
            厌 mik
            原 mhaf
            厦 mmue
            去 gi
            又 nk
            叉 ei xei
            友 ke
            双 ee
            反 he me xme
            发 ivke
            叔 yfe
            取 sje
            受 bbe
            变 yce
            叛 fqhe fqme
            口 r
            古 jr
            句 pr
            只 rc
            叫 rvl
            召 shr
            叭 rc xrc
            可 mnr
            台 ir
            史 lk
            叶 rj
            号 rmvs
            司 smr
            吃 ron
            各 her
            合 omr
            吉 gr
            同 bmr
            名 nir
            后 hmr
            向 hbr
            吗 rnvm
            吞 mkr
            吧 rau
            启 hsr isr xhsr
            吸 rnhe
            吻 rphh
            吼 rndu
            告 hgr
            员 rbo
            呢 rsp
            周 bgr bqr
            呵 rmnr
            呼 rhfd
            命 omrl
            咆 rpru rpsu
            和 hdr
            咏 rine
            咪 rfd
            咬 ryck
            咻 rod
            哀 yrhv
            哈 romr
            响 rhbr
            哔 rppj
            哥 mrmnr mrnr xmrmn
            哦 rhqi
            哮 rjkd
            唤 rnbk xrnbk
            唱 raa
            商 ycbr
            啊 rnlr
            啸 rlll
            啾 rhdf
            喂 rwmv
            喇 rdln
            喙 rvno
            喜 grtr
            嘉 grtr xgrtr
            嘛 ridd rijc
            嘶 rtcl
            嘻 rgrr
            器 rrikr rrkrr
            噬 rhmo
            嚼 rbwi
            囊 jbrrv
            回 wr
            因 wk
            团 wdh
            园 wmmu
            困 wd xwd
            围 wqs
            圆 wrbo
            圈 wfqu
            圣 eg
            在 klg
            地 gpd
            场 gnsh
            坏 gmf
            坐 oog
            坑 gyhn
            坚 leg
            坠 nog xnog
            坦 gam
            型 mng
            垒 iiig
            垫 qig qkg
            埃 giok
            埋 gwg
            城 gihs
            堂 fbrg
            堆 gog
            堡 odg
            塔 gtor
            填 gjbc gjuc
            境 gytu
            墓 takg
            墙 ggcw
            增 gcwa
            墨 wgfg
            士 jm
            声 gah
            壳 gbhn gbmn
            壹 gbmt
            处 heo hey xhey
            备 hew
            复 oahe
            夏 muhe
            外 niy
            多 nini
            夜 yonk
            大 k
            天 mk
            太 ki
            夫 qo
            失 hqo
            头 yk xyk
            夺 kdi
            奇 kmnr
            奋 kw
            奏 qkhk qkmk
            奔 kjt
            奖 lnk
            套 ksmi
            奥 hbk xhbk
            女 v
            好 vnd
            如 vr
            妈 vnvm
            妖 vhk
            妨 vyhs
            妮 vsp
            姆 vwyi
            始 vir
            姜 tgv
            姿 iov mov
            威 ihmv
            娘 viav
            娜 vsql
            婪 ddv
            子 nd
            字 jnd
            存 klnd
            季 hdnd
            孤 ndhvo
            学 fbnd
            它 jp
            宇 jmd
            守 jdi
            安 jv
            完 jmmu
            宗 jmmf
            官 jrlr
            宙 jlw
            定 jmyo
            宝 jmgi
            实 jyk
            审 jlwl
            客 jher
            宣 jmam
            宫 jrr
            家 jmso
            容 jcor
            宾 jomc
            密 jphu
            寐 jvmd
            对 edi
            寻 smdi
            导 rudi
            射 hhdi
            将 lmbdi lmnii
            尊 cmwi twdi
            小 nc
            少 fh
            尔 nf
            尖 fk
            尘 fg xfg
            尤 iku
            就 yfiku
            尼 sp
            尽 soy
            尾 shqu
            居 sjr
            屈 suu
            屋 smig
            屎 sfd
            展 stv
            属 shlb
            山 u
            岔 cshu
            岩 umr
            岸 ummj
            峡 ukt
            崎 ukmr
            崩 ubb
            工 mlm
            巨 ss
            巫 moo
            差 thm tqm
            己 su
            已 su xsu
            巴 au
            巾 lb
            市 ylb
            布 klb
            师 ilmlb llmb
            希 kklb
            帕 lbha
            帝 yblb
            带 tjblb
            席 itlb
            帮 qllb
            常 fbrlb
            帽 lbabu
            幅 lbmrw
            幕 takb
            干 mj
            平 mfj
            年 oq
            并 tt xtt
            幸 gtj
            幻 vis
            幼 viks
            幽 uvii
            广 yh
            庆 ik xxik
            序 inin
            库 ikq
            应 ifm
            底 ihpm ihvi
            店 iyr
            府 iodi
            废 iive
            度 ite
            座 ioog
            开 mt
            异 rut sut
            式 ipm
            弗 lln
            弟 cnlh
            弹 ncwj
            强 nrli
            归 ilsm llsm
            当 fsm
            彗 qjsm
            形 mjmjh mkmjh mthhh
            影 afhhh
            彻 hopsh
            径 hoeg honom
            待 hogdi
            很 hoav
            徒 hogyo
            得 hoami
            御 hooml hoovl
            循 hohju
            微 hounk houuk
            德 hojwp
            徽 houfk
            心 p
            必 ph
            忆 pn
            忍 shp sip skp xskp
            志 gp
            忘 yvp
            快 pdk
            怀 pmf
            态 kip
            怒 vep
            急 nmp nsp
            性 phqm
            怨 nup
            怪 peg
            怯 pgi
            总 crp
            恐 mkp mnp xmnp xxmkp
            恒 pmam
            恢 pkf pmf xpmf
            恨 pav
            恩 wkp
            恫 pbmr
            恶 mcp
            悔 powj powy
            悲 lyp
            情 pqmb
            惑 imp
            惠 jip
            惩 hmp
            想 dup
            愈 ombp
            意 ytap
            愚 wbp
            感 irp
            愤 pjto
            愿 mfp
            慧 qjsmp
            憎 pcwa
            懊 phbk
            戏 ei
            成 ihs
            我 hqi
            戒 it
            或 irm
            战 yri
            戮 shi xshi
            戴 jiwtc
            房 hsyhs isyhs msys
            手 q
            才 dh
            扎 qu xqu
            打 qmn
            扔 qnhs
            托 qhp
            扛 qm
            扩 qi xqi
            扫 qsm
            扭 qng
            扰 qiku
            找 qi
            承 nnqo
            技 qje
            把 qau
            投 qhne
            抗 qyhn
            抚 qmku
            护 qhs qis xqhs
            报 qsle
            披 qdhe
            抱 qpru qpsu
            抵 qhpm qhvi
            拉 qyt
            拔 qike qikk
            招 qshr
            拜 hqmqj
            拟 qvio
            拥 qbq
            择 qeq
            拯 qnem
            拳 fqq
            拾 qomr
            指 qpa
            挑 qlmo
            挥 qbkq
            振 qmmv
            捉 qryo
            捌 qrsn
            捏 qag
            捕 qijb
            换 qnbk xqnbk
            捷 qjlo
            掉 qyaj
            掌 fbrq
            探 qbcd
            接 qytv
            推 qog
            掴 qwmg
            掷 qtkl xqtkl
            揍 qqkk
            提 qamo
            插 qhjx qmjx
            握 qsmg
            揭 qapv
            援 qbme
            搏 qibi qili
            搜 qhxe
            搞 qyrb
            搭 qtor
            摆 qwli
            摇 qbou xqbou
            摩 icq idq
            撕 qtcl
            撞 qytg
            撸 qnwa xqnwa
            擅 qywm
            擦 qjbf
            支 je
            收 vlok
            攻 mok
            放 ysok
            政 mmok
            故 jrok
            敌 hrok xhrok
            救 ieok
            教 jdok
            散 tbok
            数 fvok
            文 yk
            斑 mgykg
            斗 yj
            斩 kqhml
            断 vdhml
            斯 tchml
            新 ydhml
            方 yhs
            旅 ysohv
            旋 ysono
            族 ysook
            无 mku
            既 aimlu aimvu
            日 a
            时 adi
            昂 ahvl
            昆 app
            明 ab
            昏 hpa
            星 ahqm
            是 amyo
            晚 anau ashu
            普 tca tma
            晴 aqmb
            晶 aaa
            智 ora
            暗 ayta
            暴 atce
            曼 awle
            曾 cwa cwfa
            替 qoa
            最 asje
            月 b
            有 kb
            朋 bb
            服 bsle
            望 ybhg ybmg
            期 tcb
            木 d
            未 jd
            末 dj
            札 du
            术 id xid
            朵 hnd
            机 dhn
            杀 kd xkd
            权 de
            杉 dhhh
            材 ddh
            村 ddi
            杖 djk
            杜 dg
            束 dl
            条 hed
            来 dt
            杰 df
            松 dci
            板 dhe dme xdhe
            极 dnhe
            林 dd
            果 wd
            枝 dje
            枪 dosu
            枭 pysd
            架 krd
            柒 epd xepd
            柜 dss
            查 dam
            柯 dmnr
            柱 dyg
            树 dedi
            校 dyck
            样 dtq
            根 dav
            格 dher
            档 dfsm
            梅 dowj dowy
            梣 duon
            梦 ddni
            械 dit
            棍 dapp
            棒 dqkq
            棘 dbdb
            森 ddd
            棱 dgce
            楔 dqhk
            榭 dhhi
            榴 dhhw
            槭 dihf
            模 dtak
            橡 dnao
            次 imno mmno
            欢 eno
            欧 skno
            欲 crno
            歇 avno
            歉 hcno tcno xhcno xtcno
            歌 mrno
            止 ylm
            正 mylm
            此 ymp
            步 ylmh
            武 mpylm
            歧 ymje
            歪 mfmym
            死 mnp
            殊 mnhjd
            残 mnij mniq
            殒 mnrbo
            殿 schne
            毁 hmhne
            母 wyi
            毒 qmwj qmwjh qmwyi xqmwj
            比 pp
            毕 ppj
            毛 hqu
            氏 hvp
            民 rvp
            气 omn xomn
            水 e
            永 ine
            求 ije
            汇 es
            汉 ee xee
            沃 ehk
            沉 ebhn ebhu
            沌 epu
            沙 efh
            没 ehne
            油 elw
            治 eir
            沾 eyr
            泉 hae
            法 egi
            波 edhe
            泰 qke
            泽 eeq
            洁 egr
            洛 eher
            洞 ebmr
            活 ehjr
            流 eyiu eyiv
            测 ebon
            浮 ebnd
            海 eowj eowy
            涅 eag
            消 efb
            涌 enib
            液 eyok
            深 ebcd
            混 eapp
            渊 elfl
            渣 edam edbm
            温 eabt
            游 eysd
            源 emhf
            溢 ecmt etct
            溪 ebvk
            滑 ebbb
            满 etmb
            滨 ejoc
            滩 eeog
            漂 emwf
            漆 edoe
            演 ejmc
            漠 etak
            潜 eqoa
            激 ehsk
            灌 etrg
            火 f
            灭 mf xmf
            灵 smf
            灾 jf
            炎 ff
            炮 fpru fpsu
            炸 fhs
            点 yrf xyrf
            烂 ftmm
            烈 mnf
            烙 fher
            烧 fjpu
            热 qif qkf
            焦 ogf
            焰 fhsx fnhx fshx
            然 bkf
            照 arf
            燃 fbkf
            燕 tlpf
            爆 fate
            爪 hlo
            爱 bbke
            爸 ckau
            片 llml llmn
            牌 llhhj lnhhj
            牙 mvdh
            牛 hq
            牧 hqok
            物 hqphh
            特 hqgdi
            犬 ik
            狂 khmg
            狄 khf
            狐 khhvo
            狠 khav
            独 khlmi
            狮 khllb
            狱 khivk
            狲 khndf xkhnd
            狼 khiav
            猎 khta
            猛 khndt
            猢 khjrb
            猪 khjka
            猫 khtw
            猬 khwb
            獠 khkcf
            王 mg
            玛 mgnvm
            环 mgmf
            现 mgbhu
            珍 mgohh
            珠 mghjd
            班 mghlg mgilg
            球 mgije
            理 mgwg
            琪 mgtmc
            琳 mgdd
            琴 mgoin
            瑙 mgvvw
            瑞 mgumb
            瑟 mgph
            瑶 mgbmu mgbou xmgbo
            瓜 hvio
            瓦 mvni
            瓮 cimn
            甘 tm
            甜 hrtm mrtm
            生 hqm
            用 bq
            由 lw
            甲 wl
            电 lwu
            男 wks
            画 mluw muw
            界 wcll woll
            略 wher
            疗 knn
            疾 kok
            痒 ktq
            癫 kjco
            白 ha
            百 ma
            的 hapi hapm
            皇 hamg
            皮 dhe
            盆 csht
            盖 tgbt
            盗 iobt
            盘 hybt
            盛 isbt
            目 bu
            直 jbmm jbuv
            相 dbu
            盾 hjbu
            看 hqbu
            真 jbmc jumc
            眠 burvp
            眼 buav
            着 thbu tqbu xthbu
            睛 buqmb
            督 yebu
            瞄 butw
            瞬 bubbq
            矛 ninh
            知 okr
            石 mr
            矿 mri
            砂 mrfh
            破 mrdhe
            砾 mrhvd
            硬 mrmlk
            碍 mrami
            碎 mryoj
            碟 mrptd
            磁 mrtvi
            磨 icmr idmr
            示 mmf
            礼 ifu mfu
            祈 ifhml mfhml
            祖 ifbm mfbm
            祝 ifrhu mfrhu
            神 iflwl mflwl
            祠 ifsmr mfsmr
            祭 bommf
            祷 ifqki mfqki
            祸 ifrob
            禄 ifnme mfnme
            福 ifmrw mfmrw
            离 yukb
            秀 hdnhs
            秋 hdf
            种 hdl
            科 hdyj
            秘 hdph
            积 hdrc
            称 hdnf hdof
            移 hdnin
            稀 hdkkb
            程 hdrhg hdrmg
            究 jckn
            空 jcm
            穿 jcmvh
            突 jcik jck
            窃 jcpsh
            窍 jcmms
            窟 jcsuu
            立 yt
            站 ytyr
            竞 ytrhu
            竟 ytahu
            章 ytaj
            端 ytumb
            笑 hhk
            笛 hlw
            符 hodi
            第 hnlh
            等 hgdi
            算 hbut
            管 hjrr
            箭 htbn
            箱 hdbu
            篷 hyhj
            米 fd
            粉 fdcsh
            粗 fdbm
            精 fdqmb
            糊 fdjrb
            索 jbvif
            紧 levif
            紫 ypvif
            红 vmm
            约 vmpi
            级 vmnhe
            纳 vmob
            纵 vmoo
            纹 vmyk
            线 vmij
            终 vmhey
            经 vmnom
            结 vmgr
            给 vmomr
            绝 vmnau
            继 vmvfd
            续 vmjnk
            绯 vmlmy
            维 vmog
            绽 vmjmo
            绿 vmnme
            缎 vmhje
            缪 vmsmh
            罗 wlni
            罪 wllmy
            置 wljbm wljbv
            美 tgk
            羽 smsim
            翁 cism
            翅 jesmm
            翔 tqsmm
            翼 smwtc
            耀 fusmg
            老 jkp
            考 jkmvs jkys
            者 jka jkia
            而 mbll
            耐 mbdi
            耳 sj
            耶 sjnl
            耻 sjylm
            聊 sjhhl
            联 sjtk
            聘 sjlws
            肆 silq
            肉 obo
            肌 bhn
            肩 hsb isb msb
            肮 byhn
            肯 ymb
            胁 bksc
            胆 bam
            背 lpb
            胎 bir
            胖 bfq
            能 ibpp
            脆 bnmu
            脊 fcb
            脏 big
            脑 byuk
            脚 bgil
            脱 bcru xbcru
            腕 bjnu
            腥 bahm
            腰 bmwv
            腹 boae
            腿 byav
            膛 bfbg
            臂 sjb
            自 hbu
            臭 huik huk
            致 mghe mgok
            舒 ornin
            舞 otniq
            航 hyyhn
            艇 hynkg
            良 iav
            色 nau
            艾 tk xtk
            节 tsl
            芙 tqo
            花 top
            芳 tyhs
            苍 tosu
            苔 tir
            英 tlbk
            范 tesu
            草 taj
            荣 tbd xtbd
            药 tvmi
            莉 thdn
            莎 tefh
            莫 tak
            莱 tdt
            获 tkhk
            菇 tvjr
            菲 tlmy
            萨 tnlh
            落 tehr
            葛 tapv
            蒂 tybb
            蓄 tyvw
            蓝 tlit
            蕾 tmbw
            薪 tydl
            藏 tims
            蘑 ticr tidr
            虎 yphn yphu
            虚 yptc
            虫 lmi
            蚣 lici
            蛇 lijp
            蛙 ligg
            蛛 lihjd
            蛮 yclmi
            蛾 lihqi
            蜂 lihej
            蜈 lirmk lirvk
            蜗 lirob
            蜘 liokr
            蜜 jphi
            蜥 lidhl
            蜴 liaph
            蝎 liapv
            蝙 lihsb liisb limsb
            蝠 limrw
            螺 liwvf
            蟆 litak
            蟹 nqlmi
            蠕 limbb
            蠢 qkali
            血 hbt
            衅 htfq
            行 hommn
            街 hoggn
            衡 honkn
            补 ly
            表 qmv
            被 ldhe
            袭 ipyhv
            裁 jiyhv
            裂 mnyhv
            装 lgyhv
            裔 yvbcr
            裘 ieyhv
            褐 lapp lapv xlapp
            西 mcw
            要 mwv
            见 bhu
            觉 fbbhu
            角 nbg nbq
            解 nbshq
            触 nblmi
            警 tkymr
            计 ivj
            认 ivo
            讨 ivdi
            让 ivym
            记 ivsu
            许 ivoj
            诛 ivhjd
            诡 ivnmu
            该 ivyvo
            说 ivcru
            请 ivqmb
            诸 ivjka
            诺 ivtkr
            谁 ivog
            谊 ivjbm
            谐 ivppa
            谢 ivhhi
            谷 cor
            豆 mrt
            豚 bmso
            象 napo
            豪 yrbo
            贝 bo
            贡 mbo xmbo
            贤 lebo
            账 bopo
            质 hjbo
            贪 oino xoino
            贫 csho
            贼 boij boik
            贾 mwbo
            赎 bojnk
            赐 boaph
            赛 jtco
            赞 hubo
            赤 glnc
            赭 gcjka
            走 gyo
            起 goru gosu
            趁 goohh
            超 goshr
            越 goiv
            趣 gosje
            足 ryo
            跃 rmhk
            跑 rmpru rmpsu
            距 rmss
            跟 rmav
            路 rmher
            跳 rmlmo
            踢 rmaph
            蹈 rmbhx
            蹦 rmubb
            身 hxh
            车 kq
            轨 kqkn
            转 kqqni
            轮 kqop
            软 kqno
            轰 kqee
            辈 lykq
            输 kqomn
            辟 srytj
            辣 yjdl
            辰 mmmv
            达 yk xxyk
            迅 ynj ynk
            过 ydi
            迈 yms
            迎 yhvl
            运 ymmi
            返 yhe yme
            还 ymf
            这 yyk
            进 ytt
            远 ymmu
            违 yqs
            连 ykq
            迦 yksr
            迪 ylw
            迫 yha
            迷 yfd
            迹 yylc
            追 yhrr
            退 yav
            送 ycmk ytk
            逃 ylmo
            逅 yhmr
            逆 ytu
            选 yhgu
            透 yhds
            逐 ymso
            逗 ymrt
            通 ynib
            速 ydl
            造 yhgr yhqr
            逢 yhej
            遇 ywlb
            道 ythu
            遗 ylmo xylmo
            遵 ycmi ytwi
            避 ysrj
            邂 ynbq
            那 sqnl
            邪 mhnl
            部 yrnl
            都 janl
            配 mwsu
            酒 emcw
            醇 mwyrd
            里 wg
            重 hjwg
            野 wgnin
            量 amwg
            金 c
            针 opj xopj
            钉 opmn
            钛 opki
            钟 opl
            钢 opbk
            钥 opb
            钩 oppi xoppi
            钱 opij
            钳 optm
            钻 opyr
            铁 ophqo
            铆 ophhl
            铜 opbmr
            铲 opyth
            银 opav
            链 opykq
            锁 opfbo
            错 opta
            锤 ophjm
            镇 opjbc
            镖 opmwf
            镜 opytu
            镯 opwli
            镰 opitc
            长 po
            门 lis
            闪 lso
            问 lsr
            间 lsa
            闹 lsylb
            闻 lssj
            阔 lsehr
            队 nlo
            防 nlyhs
            阳 nla
            阴 nlb
            阵 nlkq
            阻 nlbm
            阿 nlmnr
            陆 nlqu
            陌 nlma
            降 nlheq
            限 nlav
            除 nlomd
            陨 nlrbo
            陷 nlhsx nlnhx
            隆 nlhem
            隐 nlnsp
            隔 nlmrb
            难 eog xeog
            雄 kiog
            雅 mhog
            集 ogd
            雪 mbsm
            雳 mbmks
            零 mboii mboml
            雷 mbw
            雾 mbhes
            需 mbmbl
            霍 mbog
            露 mbrmr
            霸 mbtjb
            霹 mbsrj
            青 qmb
            静 qbnsd
            非 lmyyy
            面 mwyl
            革 tlj
            靴 tjop
            音 yta
            顶 mnmbo
            项 mmbo
            领 oimbo
            颈 nmmbo
            颗 wdmbo
            题 aombo
            额 jrmbo
            风 hnk
            飓 hnbmc xhnbm
            飞 no xxno
            食 oiav
            饰 nvolb
            馆 nvjrr
            首 thbu
            香 hda
            马 nvsm
            驯 nmlll
            驰 nmpd
            驱 nmsk
            骋 nmlws
            验 nmomm
            骑 nmkmr
            骨 bbb
            骰 bbhne
            高 yrbr
            鬼 hi xhi
            魂 mihi
            魅 hijd
            魇 miki
            魏 hvhi
            魔 ichi idhi
            鱼 nwm
            鲁 nwma
            鲜 nmtq
            鳄 nmrrs
            鳖 fknwm
            鸟 pysm
            鸡 epym
            鸣 rpym
            鹫 yupym
            鹰 igpym
            麦 qke qme xxqke
            黄 tlwc twc
            黑 wgf
            默 wfik
            鼓 gtje
            鼠 hxvyv
            齿 ymuo
            龙 ikp
            龟 nwu nwv
            """.Split('\n', StringSplitOptions.RemoveEmptyEntries)
               .Select(x => x.Split(' ', StringSplitOptions.RemoveEmptyEntries))
               .ToFrozenDictionary(x => x[0][0], x => x[1..]);

    #endregion
}

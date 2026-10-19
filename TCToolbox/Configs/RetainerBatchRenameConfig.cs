using System.Collections.Generic;

namespace TCToolbox;

/// <summary>候選名字的狀態。</summary>
/// <remarks>
/// 🔴 <b>零值必須是有效狀態。</b>反序列化時 JSON 缺少這個鍵就會落在零值上，
/// 而「未用」正是那種情況的正確答案——把零值放在「已被占用」之類的狀態上，
/// 會讓舊設定檔升上來之後整份名單靜默變成不可用。
/// </remarks>
public enum RetainerNameCandidateState
{
    /// <summary>還沒用過，可以指派。<b>這是預設狀態，所以必須是零值。</b></summary>
    Available = 0,

    /// <summary>已經用在某位僱員身上。</summary>
    Used = 1,

    /// <summary>伺服器（或使用者）回報這個名字已經被占用。</summary>
    Taken = 2,

    /// <summary>預檢或伺服器判定這個名字不能用。</summary>
    Rejected = 3,
}

/// <summary>一個候選名字的持久化狀態。</summary>
/// <remarks>
/// 🔴 <b>這份狀態是全域的，不分角色。</b>僱員名字是<b>全世界唯一</b>的，所以
/// 「這個名字已經被占用」對每個角色都成立；同一份名單要能跨角色輪流消耗
/// （使用者的僱員分散在多個角色上，每個角色上限 10 位）。
/// 換角色<b>絕對不能</b>重置這些狀態——那會讓流程一再去試已知不能用的名字。
/// </remarks>
public sealed class RetainerNameCandidateStatus
{
    public RetainerNameCandidateState State { get; set; } = RetainerNameCandidateState.Available;

    /// <summary>用在哪一位僱員身上（<c>RetainerId</c> 全域唯一）。</summary>
    public ulong UsedByRetainerId { get; set; }

    /// <summary>當時那位僱員屬於哪個角色（僅供視窗顯示用）。</summary>
    public ulong UsedByContentId { get; set; }

    /// <summary>當時的角色名（僅供視窗顯示用；角色改名的話這裡會是舊的，無妨）。</summary>
    public string UsedByCharacterName { get; set; } = string.Empty;

    /// <summary>當時那位僱員叫什麼（僅供視窗顯示用）。</summary>
    public string UsedByRetainerName { get; set; } = string.Empty;

    /// <summary>人看的說明（為什麼被剔除／被誰用掉）。</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>批次僱員改名。</summary>
public sealed class RetainerBatchRenameConfig
{
    /// <summary>
    /// 自動操作 CharaMake 改名畫面（保留容貌、只改名字），而不是停在那裡等你手動改。
    /// </summary>
    /// <remarks>
    /// 🔴 這條路徑的每一步都<b>比對話文字</b>才動作，對不上就<b>不動、逾時跳過</b>（fail-closed）——
    /// 尤其「要使用已儲存的角色形象嗎？」那道閘門若沒先通過，絕不確認任何「設定成目前的樣子」，
    /// 以免把外觀改成空白（不可逆）。
    /// ⚠️ 關掉＝只自動卸裝／穿裝，改名畫面停在那裡等你手動操作（期間照舊錄製）。
    /// </remarks>
    public bool AutoCharaMakeRename = true;

    /// <summary>
    /// 使用者手動改名的期間，把畫面資訊寫進記錄。
    /// </summary>
    /// <remarks>
    /// ⚠️ 錄製只在流程停在「等你改名」的那一段才生效，
    /// 其餘時間 hook 是停用的（<c>AtkUnitBase::FireCallback</c> 是全遊戲的熱路徑）。
    /// 想錄整條流程請用視窗上的「開始錄製（我自己操作）」。
    /// </remarks>
    public bool RecordDuringManualRename = true;

    /// <summary>
    /// 偵測到僱員名稱已經改變就自動往下走，不必按「改名完成」。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：名字變了是「使用者已經改完」最直接的證據。
    /// 關掉的話一律等按鈕——按鈕本身<b>永遠都在</b>，這格只影響要不要另外自動偵測。
    /// </remarks>
    public bool AutoDetectRenameDone = true;

    /// <summary>
    /// 卸裝之後等這麼久（毫秒）才驗證裝備真的離開僱員身上。
    /// </summary>
    /// <remarks>
    /// ⚠️ 設太短的後果是「卸裝其實沒生效卻繼續往下跑」，那會讓改名整個失敗；
    /// 設太長只是慢一點。不確定就往大的調。
    /// </remarks>
    public int UndressVerifyDelayMs = 10_000;

    /// <summary>
    /// 同一位僱員最多連續試幾個候選名字。
    /// </summary>
    /// <remarks>
    /// 📌 預設 5。名字被占用時會自動換下一個候選；這是<b>失控保險絲</b>——
    /// 若偵測「被占用」的訊號誤判成常態，沒有這道上限會一路把整份名單燒光。
    /// </remarks>
    public int MaxCandidateAttemptsPerRetainer = 5;

    /// <summary>
    /// 改名之前，先把該僱員<b>已經完成</b>的探險成果收回來（<b>不重新派遣</b>）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這一格只影響「探險<b>已經完成</b>」的僱員。<b>探險還在跑</b>的僱員照舊擋下來
    /// （沒有任何辦法讓探險提早結束），前置檢查會顯示剩餘時間。
    /// 🔴 <b>與 AutoRetainer 互斥</b>：AutoRetainer 若正在跑自己的收派循環，
    /// 會把剛收回的僱員立刻重新派遣。所以「這一輪真的會去收探險」時，
    /// 前置檢查會另外要求 AutoRetainer 不在忙碌狀態。
    /// </remarks>
    public bool CollectCompletedVentureBeforeRename = true;

    /// <summary>
    /// 候選名單原文（一行一個名字）。
    /// </summary>
    /// <remarks>
    /// 📌 預設空字串＝<b>沒有名單，流程不會開始</b>（前置閘門會擋）。
    /// 使用者可以直接貼、從設定資料夾的 <c>retainer_names.txt</c> 載入、
    /// 或按「載入內建名單」把組件內附的 100 個候選加進來。
    /// </remarks>
    public string NamePoolText = string.Empty;

    /// <summary>
    /// 每個候選名字的狀態。<b>鍵是名字本身</b>，不是行號。
    /// </summary>
    /// <remarks>
    /// 🔑 用名字當鍵，使用者重新排序或增刪名單時，已知被占用的名字不會因為位置變了就被重試。
    /// 🔴 這份是<b>全域</b>的（見 <see cref="RetainerNameCandidateStatus"/>）：僱員名全世界唯一，
    /// 所以跨角色共用同一份消耗紀錄。
    /// </remarks>
    public Dictionary<string, RetainerNameCandidateStatus> CandidateStatus { get; set; } = [];
}

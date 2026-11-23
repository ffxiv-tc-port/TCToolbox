using System;
using System.Collections.Generic;

namespace TCToolbox;

/// <summary>「角色總覽」模組的設定與紀錄。</summary>
public sealed class CharacterOverviewConfig
{
    /// <summary>分隊歸隊、訓練結束時在聊天欄提醒一行；預設關。</summary>
    public bool RemindSquadron { get; set; }

    /// <summary>提醒時另外請塔塔露誇獎念一句；預設關。</summary>
    public bool RemindWithTataru { get; set; }

    /// <summary>角色 ContentId → 最近一次在登入中讀到的分隊狀態。</summary>
    /// <remarks>初始式刻意留空：Dalamud 對集合欄位是併入，放了東西使用者就刪不掉。</remarks>
    public Dictionary<ulong, SquadronSnapshot> Squadron { get; set; } = [];
}

/// <summary>一個角色的分隊任務與訓練（讀自遊戲的玩家狀態；時間是 Unix 秒）。</summary>
public sealed class SquadronSnapshot
{
    /// <summary>「名稱＠伺服器」，只給畫面顯示用。</summary>
    public string Character { get; set; } = string.Empty;

    /// <summary>進行中的分隊任務（GcArmyExpedition 列號）；0＝沒有。</summary>
    public ushort MissionId { get; set; }

    public uint MissionEnd { get; set; }

    /// <summary>進行中的訓練（GcArmyTraining 列號）；0＝沒有。</summary>
    public ushort TrainingId { get; set; }

    public uint TrainingEnd { get; set; }

    /// <summary>讀到時已完成分隊解鎖任務；<see langword="null"/>＝問不到。</summary>
    public bool? Unlocked { get; set; }

    public DateTime ReadUtc { get; set; }
}

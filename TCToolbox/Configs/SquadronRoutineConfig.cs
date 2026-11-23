using System;
using System.Collections.Generic;

namespace TCToolbox;

/// <summary>「國防聯軍分隊」模組的設定與紀錄。</summary>
public sealed class SquadronRoutineConfig
{
    /// <summary>角色 ContentId → 最近一次看到的派遣確認框解析結果。</summary>
    /// <remarks>初始式刻意留空：Dalamud 對集合欄位是併入，放了東西使用者就刪不掉。</remarks>
    public Dictionary<ulong, SquadronReturnRecord> Returns { get; set; } = [];

    /// <summary>顯示「替我選今天的任務並派遣」按鈕；預設關。</summary>
    public bool AutoDeployEnabled { get; set; }

    /// <summary>顯示「本角色一鍵：傳送→進軍營→派遣→綜合訓練」按鈕；預設關。</summary>
    public bool OneKeyEnabled { get; set; }

    /// <summary>一鍵流程走到訓練清單就停手，訓練由使用者自己選（不替你按綜合訓練）。</summary>
    public bool OneKeyStopAtTrainingList { get; set; }
}

/// <summary>一個角色的預定歸隊時間（唯讀解析派遣確認框得來）。</summary>
public sealed class SquadronReturnRecord
{
    /// <summary>「名稱＠伺服器」，只給畫面顯示用。</summary>
    public string Character { get; set; } = string.Empty;

    public string Mission { get; set; } = string.Empty;

    public DateTime ReturnUtc { get; set; }

    /// <summary>看到確認框的時刻（UTC）。</summary>
    public DateTime SeenUtc { get; set; }

    /// <summary>之後有看到「開始了…」系統訊息＝真的派出去了；<see langword="false"/>＝只看到確認框（可能按了否）。</summary>
    public bool Confirmed { get; set; }
}

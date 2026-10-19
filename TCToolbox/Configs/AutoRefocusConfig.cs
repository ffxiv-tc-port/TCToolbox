namespace TCToolbox;

public sealed class AutoRefocusConfig
{
    /// <summary>只在副本內生效。</summary>
    public bool OnlyInDuty = true;

    /// <summary>恢復焦點時寫一筆 Information 記錄（已節流）。</summary>
    public bool NotifyOnRestore;
}

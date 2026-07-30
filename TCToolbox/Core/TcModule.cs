using System;

namespace TCToolbox.Core;

/// <summary>功能模組基底：獨立開關、獨立生命週期，預設全部關閉。</summary>
public abstract class TcModule
{
    /// <summary>存檔用內部識別名（勿改，改了使用者的開關設定會遺失）。</summary>
    public abstract string InternalName { get; }

    /// <summary>顯示名稱。</summary>
    public abstract string DisplayName { get; }

    /// <summary>功能描述。</summary>
    public abstract string Description { get; }

    public bool IsEnabled { get; private set; }

    /// <summary>模組是否有自己的設定／操作 UI。</summary>
    public virtual bool HasConfigUI => false;

    public void Enable()
    {
        if (IsEnabled) return;
        try
        {
            OnEnable();
            IsEnabled = true;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 啟用失敗");
        }
    }

    public void Disable()
    {
        if (!IsEnabled) return;
        try
        {
            OnDisable();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 停用時發生例外");
        }
        finally
        {
            IsEnabled = false;
        }
    }

    protected abstract void OnEnable();

    protected abstract void OnDisable();

    /// <summary>模組設定 UI（在主視窗中展開繪製）。</summary>
    public virtual void DrawConfig()
    {
    }
}

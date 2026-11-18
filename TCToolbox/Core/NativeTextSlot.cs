using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;

namespace TCToolbox.Core;

/// <summary>一幀要顯示的原生文字；<see cref="Text"/> 空字串＝隱藏。<see cref="Position"/> 是 addon 內座標（未乘縮放）。</summary>
internal readonly record struct NativeTextState(string Text, Vector2 Position, byte FontSize);

/// <summary>
/// 在一個常駐 HUD addon 的 RootNode 底下掛一個 KamiToolKit 文字節點（遊戲字型＋外框，跟著 addon 縮放）。
/// </summary>
/// <remarks>
/// 契約：建構、<see cref="Dispose"/> 與所有回呼都在框架執行緒。掛／拆的時機見 <see cref="NativeTextLifecycle"/>；
/// 只存 addon 位址做身分比對，不跨幀解參任何原生指標。任何例外都讓本格進入 <see cref="Faulted"/>，由擁有者收掉。
/// </remarks>
internal sealed unsafe class NativeTextSlot : IDisposable
{
    private readonly string addonName;
    private readonly Func<NativeTextState> state;
    private readonly AddonController<AtkUnitBase> controller;

    private TextNode? node;
    private nint attached;
    private string? shownText;
    private byte shownFontSize;
    private Vector2 shownPosition;
    private bool shownVisible;

    public NativeTextSlot(string addonName, Func<NativeTextState> state)
    {
        this.addonName = addonName;
        this.state = state;
        controller = new AddonController<AtkUnitBase>(addonName);
        controller.OnUpdate += OnUpdate;
        controller.OnDetach += OnDetach;
        controller.Enable();
    }

    public bool Faulted { get; private set; }

    /// <summary>節點此刻是否掛在 <paramref name="addon"/> 上（只比位址）。</summary>
    public bool IsAttachedTo(nint addon) => attached != 0 && attached == addon;

    private void OnUpdate(AtkUnitBase* addon)
    {
        try
        {
            var ready = addon != null && addon->RootNode != null && addon->UldManager.LoadedState == AtkLoadState.Loaded;
            switch (NativeTextLifecycle.OnUpdate(Faulted, attached, (nint)addon, ready))
            {
                case NativeUpdateAction.Attach:
                    Attach(addon);
                    if (attached != 0) Apply();
                    break;
                case NativeUpdateAction.Apply:
                    Apply();
                    break;
                case NativeUpdateAction.Mismatch:
                    Abandon("節點所在的介面沒收到關閉事件就換了一個");
                    break;
            }
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
    }

    private void OnDetach(AtkUnitBase* addon)
    {
        if (NativeTextLifecycle.OnFinalize(attached, (nint)addon) == NativeDetachAction.Detach)
            DetachCurrent();
    }

    private void Attach(AtkUnitBase* addon)
    {
        var native = KamiNative.Controller ?? throw new InvalidOperationException("NativeController 不存在");
        var created = new TextNode
        {
            NodeFlags = NodeFlags.Enabled,
            FontType = FontType.Axis,
            TextFlags = TextFlags.Edge | TextFlags.AutoAdjustNodeSize,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = new Vector4(1f, 1f, 1f, 1f),
            TextOutlineColor = new Vector4(0f, 0f, 0f, 1f),
        };

        // AttachNode 在框架執行緒上就地執行、例外被吞進丟棄的 Task；以「真的接到 RootNode 底下」判成敗。
        native.AttachNode(created, addon->RootNode, NodePosition.AsLastChild);
        if (((AtkResNode*)created)->ParentNode != addon->RootNode)
        {
            created.Dispose();
            throw new InvalidOperationException($"{addonName} 掛載原生文字節點失敗");
        }

        node = created;
        attached = (nint)addon;
        shownText = null;
        shownFontSize = 0;
        shownPosition = new Vector2(float.NaN);
        shownVisible = false;
    }

    private void Apply()
    {
        var current = state();
        var target = node!;

        if (current.Text.Length == 0)
        {
            SetVisible(target, false);
            return;
        }

        if (current.FontSize != shownFontSize)
        {
            target.FontSize = current.FontSize;
            shownFontSize = current.FontSize;
            shownText = null; // 換字級後要重設文字，節點大小才會重算
        }

        if (!string.Equals(current.Text, shownText, StringComparison.Ordinal))
            target.String = current.Text;
        shownText = current.Text;

        if (current.Position != shownPosition)
        {
            target.Position = current.Position;
            shownPosition = current.Position;
        }

        SetVisible(target, true);
    }

    private void SetVisible(TextNode target, bool visible)
    {
        if (shownVisible == visible) return;
        target.IsVisible = visible;
        shownVisible = visible;
    }

    private void DetachCurrent()
    {
        var current = node;
        node = null;
        attached = 0;
        if (current == null) return;

        // 不在框架執行緒時 DetachNode 會排到之後才做，那時 addon 可能已釋放；寧可放著不碰。
        var native = KamiNative.Controller;
        if (native != null && Svc.Framework.IsInFrameworkUpdateThread)
            native.DetachNode(current, current.Dispose);
        else
            Abandon("拆除時不在框架執行緒");
    }

    private void Abandon(string reason)
    {
        Faulted = true;
        node = null;
        attached = 0;
        KamiNative.MarkAbandoned();
        Svc.Log.Warning($"[TCToolbox] {addonName} 原生文字停用：{reason}；本次遊戲改回疊圖顯示");
    }

    private void Fault(Exception ex)
    {
        if (Faulted) return;
        Faulted = true;
        Svc.Log.Warning(ex, $"[TCToolbox] {addonName} 原生文字發生例外，本次遊戲改回疊圖顯示");
    }

    public void Dispose()
    {
        var current = (nint)UiHelper.GetAddon(addonName);
        switch (NativeTextLifecycle.OnStop(attached, current))
        {
            case NativeDetachAction.Detach:
                DetachCurrent();
                break;
            case NativeDetachAction.Abandon:
                Abandon("停用時介面已不在");
                break;
        }

        controller.OnUpdate -= OnUpdate;
        controller.OnDetach -= OnDetach;
        controller.Dispose();
    }
}

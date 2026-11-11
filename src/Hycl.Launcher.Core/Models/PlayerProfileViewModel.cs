using CommunityToolkit.Mvvm.ComponentModel;

namespace Hycl.Launcher.Core.Models;

/// <summary>
/// 玩家档案（对应网页版 profiles 数据）。SkinKey 是皮肤贴图的逻辑名，
/// UI 层负责映射到 Assets/skins 下的实际资源；SkinPath 非空时为下载到本地的真实皮肤。
/// </summary>
public partial class PlayerProfileViewModel : ObservableObject
{
    public PlayerProfileViewModel(string id, string name, string accountType, string skinKey, bool isMicrosoft = false, bool isActive = false, string accountUuid = "", string skinPath = "", bool isOffline = false, bool isSlim = false, string capePath = "")
    {
        Id = id;
        Name = name;
        AccountType = accountType;
        SkinKey = skinKey;
        IsMicrosoft = isMicrosoft;
        IsActive = isActive;
        AccountUuid = accountUuid;
        SkinPath = skinPath;
        IsOffline = isOffline;
        IsSlim = isSlim;
        CapePath = capePath;
    }

    public string Id { get; }

    [ObservableProperty]
    private string _name;

    public string AccountType { get; }

    public string SkinKey { get; }

    /// <summary>本地皮肤文件路径；非空时 UI 优先用它渲染真实皮肤。</summary>
    public string SkinPath { get; }

    /// <summary>本地披风贴图路径；空字符串表示当前账号没有披风（UI 必须据此清除旧披风）。</summary>
    public string CapePath { get; }

    public bool IsMicrosoft { get; }

    /// <summary>离线档案（可重命名）。</summary>
    public bool IsOffline { get; }

    /// <summary>Alex(slim) 皮肤模型（手臂 3 宽）；仅正版/皮肤站档案可能有值。</summary>
    public bool IsSlim { get; }

    /// <summary>对应账号的 UUID（档案删除等操作按此定位）。</summary>
    public string AccountUuid { get; }

    [ObservableProperty]
    private bool _isActive;

    /// <summary>批量管理模式下的勾选状态（档案侧栏右键 → 多选批量删除）。</summary>
    [ObservableProperty]
    private bool _isMultiSelected;
}

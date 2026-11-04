using CommunityToolkit.Mvvm.ComponentModel;

namespace Hycl.Launcher.Core.Models;

/// <summary>
/// 玩家档案（对应网页版 profiles 数据）。SkinKey 是皮肤贴图的逻辑名，
/// UI 层负责映射到 Assets/skins 下的实际资源；SkinPath 非空时为下载到本地的真实皮肤。
/// </summary>
public partial class PlayerProfileViewModel : ObservableObject
{
    public PlayerProfileViewModel(string id, string name, string accountType, string skinKey, bool isMicrosoft = false, bool isActive = false, string accountUuid = "", string skinPath = "")
    {
        Id = id;
        Name = name;
        AccountType = accountType;
        SkinKey = skinKey;
        IsMicrosoft = isMicrosoft;
        IsActive = isActive;
        AccountUuid = accountUuid;
        SkinPath = skinPath;
    }

    public string Id { get; }

    [ObservableProperty]
    private string _name;

    public string AccountType { get; }

    public string SkinKey { get; }

    /// <summary>本地皮肤文件路径；非空时 UI 优先用它渲染真实皮肤。</summary>
    public string SkinPath { get; }

    public bool IsMicrosoft { get; }

    /// <summary>对应账号的 UUID（档案删除等操作按此定位）。</summary>
    public string AccountUuid { get; }

    [ObservableProperty]
    private bool _isActive;
}

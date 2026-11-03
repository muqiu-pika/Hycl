using CommunityToolkit.Mvvm.ComponentModel;

namespace Hycl.Launcher.Core.Models;

/// <summary>
/// 玩家档案（对应网页版 profiles 数据）。SkinKey 是皮肤贴图的逻辑名，
/// UI 层负责映射到 Assets/skins 下的实际资源。
/// </summary>
public partial class PlayerProfileViewModel : ObservableObject
{
    public PlayerProfileViewModel(string id, string name, string accountType, string skinKey, bool isMicrosoft = false, bool isActive = false)
    {
        Id = id;
        Name = name;
        AccountType = accountType;
        SkinKey = skinKey;
        IsMicrosoft = isMicrosoft;
        IsActive = isActive;
    }

    public string Id { get; }

    [ObservableProperty]
    private string _name;

    public string AccountType { get; }

    public string SkinKey { get; }

    public bool IsMicrosoft { get; }

    [ObservableProperty]
    private bool _isActive;
}

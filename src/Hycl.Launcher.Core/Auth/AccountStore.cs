using System.Text.Json;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Auth;

/// <summary>账号列表持久化（accounts.json）。</summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly LauncherLogger _logger;

    public AccountStore(string filePath, LauncherLogger logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public List<PlayerAccount> Accounts { get; private set; } = new();

    public int SelectedIndex { get; private set; }

    /// <summary>
    /// 当前选中账号；没有账号（初始状态/全部删除）时返回 null，
    /// 不再自动创建默认离线档案——是否登录由玩家自己决定。
    /// </summary>
    public PlayerAccount? SelectedAccount
    {
        get
        {
            if (Accounts.Count == 0)
            {
                return null;
            }

            SelectedIndex = Math.Clamp(SelectedIndex, 0, Accounts.Count - 1);
            return Accounts[SelectedIndex];
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            Accounts = new List<PlayerAccount>();
            SelectedIndex = 0;
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize<AccountPayload>(json, SerializerOptions);
            Accounts = payload?.Accounts ?? new List<PlayerAccount>();
            SelectedIndex = payload?.SelectedIndex ?? 0;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.Error("读取账号失败，已重置为空账号列表", ex);
            Accounts = new List<PlayerAccount>();
        }

        SelectedIndex = Math.Clamp(SelectedIndex, 0, Math.Max(0, Accounts.Count - 1));
        foreach (var account in Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Uuid) || account.Uuid == "0")
            {
                account.Uuid = OfflineUuid.Create(account.Name);
            }
        }
    }

    public void AddOffline(string playerName)
    {
        var account = new PlayerAccount
        {
            Name = playerName,
            Uuid = OfflineUuid.Create(playerName),
            Type = "offline"
        };
        Accounts.Add(account);
        SelectedIndex = Accounts.Count - 1;
    }

    /// <summary>
    /// 新增或更新微软正版账号：同 UUID 已存在则原位更新令牌，否则追加并选中。
    /// </summary>
    public void AddMicrosoft(MicrosoftAuthResult result)
    {
        var existing = Accounts.FirstOrDefault(x =>
            x.Type == "msa" && x.Uuid.Equals(result.Uuid, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ApplyMicrosoftAuth(existing, result);
            SelectedIndex = Accounts.IndexOf(existing);
            return;
        }

        var account = new PlayerAccount
        {
            Name = result.UserName,
            Uuid = result.Uuid,
            Type = "msa",
            AccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken,
            Xuid = result.Xuid,
            ExpiresAt = result.ExpiresAt
        };
        Accounts.Add(account);
        SelectedIndex = Accounts.Count - 1;
    }

    /// <summary>把最新令牌写回账号对象（刷新与重登录共用）。</summary>
    public static void ApplyMicrosoftAuth(PlayerAccount account, MicrosoftAuthResult result)
    {
        account.Name = result.UserName;
        account.Uuid = result.Uuid;
        account.Type = "msa";
        account.AccessToken = result.AccessToken;
        account.RefreshToken = result.RefreshToken;
        account.Xuid = result.Xuid;
        account.ExpiresAt = result.ExpiresAt;
    }

    /// <summary>
    /// 新增或更新皮肤站账号：同 皮肤站+UUID 已存在则原位更新令牌，否则追加并选中。
    /// </summary>
    public void AddAuthlib(AuthlibAuthResult result)
    {
        var existing = Accounts.FirstOrDefault(x =>
            x.Type == "authlib"
            && x.Server.Equals(result.ApiRoot, StringComparison.OrdinalIgnoreCase)
            && x.Uuid.Equals(result.Uuid, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ApplyAuthlibAuth(existing, result);
            SelectedIndex = Accounts.IndexOf(existing);
            return;
        }

        var account = new PlayerAccount
        {
            Name = result.UserName,
            Uuid = result.Uuid,
            Type = "authlib",
            AccessToken = result.AccessToken,
            ClientToken = result.ClientToken,
            LoginName = result.LoginName,
            Server = result.ApiRoot,
            ServerName = result.ServerName
        };
        Accounts.Add(account);
        SelectedIndex = Accounts.Count - 1;
    }

    /// <summary>把皮肤站最新令牌写回账号对象。</summary>
    public static void ApplyAuthlibAuth(PlayerAccount account, AuthlibAuthResult result)
    {
        account.Name = result.UserName;
        account.Uuid = result.Uuid;
        account.Type = "authlib";
        account.AccessToken = result.AccessToken;
        account.ClientToken = result.ClientToken;
        account.LoginName = result.LoginName;
        account.Server = result.ApiRoot;
        account.ServerName = result.ServerName;
    }

    public void Select(int index)
    {
        if (index >= 0 && index < Accounts.Count)
        {
            SelectedIndex = index;
        }
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= Accounts.Count)
        {
            return;
        }

        Accounts.RemoveAt(index);
        SelectedIndex = Math.Clamp(SelectedIndex, 0, Math.Max(0, Accounts.Count - 1));
    }

    /// <summary>按账号 UUID 删除档案；返回是否删除成功。</summary>
    public bool RemoveById(string uuid)
    {
        var index = Accounts.FindIndex(x => x.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        RemoveAt(index);
        return true;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? ".");
            var payload = new AccountPayload
            {
                Accounts = Accounts,
                SelectedIndex = SelectedIndex
            };
            var json = JsonSerializer.Serialize(payload, SerializerOptions);
            var tempFile = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempFile, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempFile, _filePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("保存账号失败", ex);
        }
    }

    private sealed class AccountPayload
    {
        public List<PlayerAccount> Accounts { get; set; } = new();

        public int SelectedIndex { get; set; }
    }
}

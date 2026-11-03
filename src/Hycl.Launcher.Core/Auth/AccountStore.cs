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

    public PlayerAccount SelectedAccount
    {
        get
        {
            if (Accounts.Count == 0)
            {
                AddOffline("Player");
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
            AddOffline("Player");
            await SaveAsync(cancellationToken).ConfigureAwait(false);
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
            _logger.Error("读取账号失败，已使用默认离线账号", ex);
            Accounts = new List<PlayerAccount>();
        }

        if (Accounts.Count == 0)
        {
            AddOffline("Player");
        }

        SelectedIndex = Math.Clamp(SelectedIndex, 0, Accounts.Count - 1);
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

    public void Select(int index)
    {
        if (index >= 0 && index < Accounts.Count)
        {
            SelectedIndex = index;
        }
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= Accounts.Count || Accounts.Count <= 1)
        {
            return;
        }

        Accounts.RemoveAt(index);
        SelectedIndex = Math.Clamp(SelectedIndex, 0, Accounts.Count - 1);
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

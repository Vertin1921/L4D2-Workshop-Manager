using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using L4D2ModManager.App.Infrastructure;
using L4D2ModManager.App.Services;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Mods;
using L4D2ModManager.Core.Services.Voice;

namespace L4D2ModManager.App.ViewModels;

/// <summary>一名生还者的卡片。</summary>
public sealed class VoiceCharacterCard : ObservableObject
{
    private string _statusText = "尚未检测";
    private string _directoryText = string.Empty;
    private string _fileCountText = string.Empty;
    private bool _isModified;
    private bool _directoryExists;
    private string? _avatarPath;

    public VoiceCharacterCard(VoiceCharacterInfo info)
    {
        Info = info;
        Accent = new SolidColorBrush(info.IsL4D1
            ? Color.FromRgb(0xE0, 0x8A, 0x3C)   // 一代：暖橙
            : Color.FromRgb(0xC8, 0x2A, 0x2A)); // 二代：血红
        Accent.Freeze();
    }

    public VoiceCharacterInfo Info { get; }

    /// <summary>角色头像（自定义图片，放在数据目录 Avatars\&lt;代号&gt;.png）。</summary>
    public string? AvatarPath
    {
        get => _avatarPath;
        private set
        {
            if (!Set(ref _avatarPath, value)) return;
            Raise(nameof(HasAvatar), nameof(AvatarImage));
        }
    }

    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarPath) && File.Exists(AvatarPath);

    /// <summary>头像图片（加载失败时退回首字母圆牌）。</summary>
    public ImageSource? AvatarImage
    {
        get
        {
            if (!HasAvatar) return null;

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 96;
                image.UriSource = new Uri(AvatarPath!, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }
    }

    public Brush Accent { get; }

    public string EnglishName => Info.EnglishName;

    public string ChineseName => Info.ChineseName;

    public string Initial => Info.Initial;

    public string Codename => Info.Codename;

    public string GenerationText => Info.GenerationText;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string DirectoryText
    {
        get => _directoryText;
        private set => Set(ref _directoryText, value);
    }

    public string FileCountText
    {
        get => _fileCountText;
        private set => Set(ref _fileCountText, value);
    }

    public bool IsModified
    {
        get => _isModified;
        private set => Set(ref _isModified, value);
    }

    public bool DirectoryExists
    {
        get => _directoryExists;
        private set => Set(ref _directoryExists, value);
    }

    public void Apply(VoiceCharacterStatus status, string? avatarPath)
    {
        AvatarPath = avatarPath;
        StatusText = status.StatusText;
        DirectoryText = status.DirectoryText;
        FileCountText = status.DirectoryExists
            ? $"{status.CurrentFileCount} 个语音文件 · {status.SizeText}"
            : "—";
        IsModified = status.IsModified;
        DirectoryExists = status.DirectoryExists;
    }
}

/// <summary>
/// 求生之路 2 语音管理器（L4D2 Voice Manager）。
/// 安全原则：没有成功的 ZIP 备份，就绝对不替换原版语音。
/// </summary>
public sealed class VoiceViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ModLibraryService _library;
    private readonly VoiceManager _manager = new();

    private string _gameRoot = string.Empty;
    private string _gamePathStatus = "尚未检测游戏路径";
    private bool _gamePathValid;
    private string _statusText = "把语音 Mod（文件夹或 .vpk）拖到下面，或点某个角色的「安装」。";
    private string _logText = string.Empty;
    private VoiceBackupInfo? _selectedBackup;
    private bool _isBusy;
    private string _avatarHintText = "人物头像：使用角色首字母占位，可点「下载人物头像」自动获取，或把自己的图片放进 Avatars 目录";
    private bool _avatarAutoTried;

    public VoiceViewModel(AppServices services)
    {
        _services = services;
        _library = services.Library;

        Cards = VoiceCharacters.All.Select(c => new VoiceCharacterCard(c)).ToList();
        L4D1Cards = Cards.Where(c => c.Info.IsL4D1).ToList();
        L4D2Cards = Cards.Where(c => !c.Info.IsL4D1).ToList();

        InstallCommand = new AsyncRelayCommand(parameter => InstallAsync(parameter as VoiceCharacterCard), _ => !IsBusy, ex => Report("安装失败", ex));
        BackupCommand = new AsyncRelayCommand(parameter => BackupAsync(parameter as VoiceCharacterCard), _ => !IsBusy, ex => Report("备份失败", ex));
        RestoreLatestCommand = new AsyncRelayCommand(parameter => RestoreLatestAsync(parameter as VoiceCharacterCard), _ => !IsBusy, ex => Report("恢复失败", ex));
        UninstallCommand = new AsyncRelayCommand(parameter => UninstallAsync(parameter as VoiceCharacterCard), _ => !IsBusy, ex => Report("删除失败", ex));
        OpenVoiceFolderCommand = new RelayCommand(parameter => OpenVoiceFolder(parameter as VoiceCharacterCard));
        OpenDlcFolderCommand = new RelayCommand(parameter => OpenDlcFolder(parameter as VoiceCharacterCard));

        BrowseExeCommand = new RelayCommand(_ => BrowseExe());
        AutoDetectCommand = new RelayCommand(_ => DetectGamePath(force: true));
        RefreshCommand = new RelayCommand(_ => Refresh());
        OpenGameFolderCommand = new RelayCommand(_ => OpenFolder(_gameRoot));
        OpenBackupsFolderCommand = new RelayCommand(_ => OpenFolder(_manager.BackupsDirectory));
        OpenDataFolderCommand = new RelayCommand(_ => OpenFolder(_manager.DataDirectory));
        OpenLogsFolderCommand = new RelayCommand(_ => OpenFolder(_manager.LogsDirectory));
        OpenAvatarsFolderCommand = new RelayCommand(_ => OpenAvatarsFolder());
        DownloadAvatarsCommand = new AsyncRelayCommand(DownloadAvatarsAsync, () => !IsBusy, ex => Report("下载头像失败", ex));
        ChooseModFileCommand = new AsyncRelayCommand(ChooseModFileAsync, () => !IsBusy, ex => Report("选择语音 Mod 失败", ex));
        ChooseModFolderCommand = new AsyncRelayCommand(ChooseModFolderAsync, () => !IsBusy, ex => Report("选择语音 Mod 失败", ex));
        CacheHelpCommand = new RelayCommand(_ => ShowCacheHelp());
        CopyCacheCommand = new RelayCommand(_ => CopyToClipboard(VoiceManager.RebuildCacheCommand, "已复制：snd_rebuildaudiocache"));
        CopyQuitCommand = new RelayCommand(_ => CopyToClipboard(VoiceManager.QuitCommand, "已复制：quit"));

        RestoreBackupCommand = new AsyncRelayCommand(RestoreSelectedAsync, () => !IsBusy && SelectedBackup != null, ex => Report("恢复失败", ex));
        DeleteBackupCommand = new RelayCommand(DeleteSelectedBackup, _ => SelectedBackup != null);
        VerifyBackupCommand = new RelayCommand(VerifySelectedBackup, _ => SelectedBackup != null);

        Log.MessageLogged += OnMessageLogged;

        DetectGamePath(force: false);
        Refresh();
        TryAutoDownloadAvatars();
    }

    // ------------------------------------------------------------------ 绑定

    public IReadOnlyList<VoiceCharacterCard> Cards { get; }

    public IReadOnlyList<VoiceCharacterCard> L4D1Cards { get; }

    public IReadOnlyList<VoiceCharacterCard> L4D2Cards { get; }

    public ObservableCollection<VoiceBackupInfo> Backups { get; } = new();

    public ObservableCollection<string> LogLines { get; } = new();

    public AsyncRelayCommand InstallCommand { get; }

    public AsyncRelayCommand BackupCommand { get; }

    public AsyncRelayCommand RestoreLatestCommand { get; }

    public AsyncRelayCommand UninstallCommand { get; }

    public RelayCommand OpenVoiceFolderCommand { get; }

    public RelayCommand OpenDlcFolderCommand { get; }

    public RelayCommand BrowseExeCommand { get; }

    public RelayCommand AutoDetectCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand OpenGameFolderCommand { get; }

    public RelayCommand OpenBackupsFolderCommand { get; }

    public RelayCommand OpenDataFolderCommand { get; }

    public RelayCommand OpenLogsFolderCommand { get; }

    public RelayCommand OpenAvatarsFolderCommand { get; }

    /// <summary>从社区维基下载八名角色的头像（缺失的才下载）。</summary>
    public AsyncRelayCommand DownloadAvatarsCommand { get; }

    /// <summary>选择语音 Mod 文件（.vpk）。</summary>
    public AsyncRelayCommand ChooseModFileCommand { get; }

    /// <summary>选择语音 Mod 文件夹。</summary>
    public AsyncRelayCommand ChooseModFolderCommand { get; }

    /// <summary>当前是否以管理员身份运行（此时 Windows 会阻止从资源管理器拖放）。</summary>
    public bool IsElevated { get; } = L4D2ModManager.Core.Services.Deployment.AppDeployment.IsElevated;

    public string DragHintText => IsElevated
        ? "⚠ 当前以管理员身份运行，Windows 会阻止从资源管理器拖放文件（系统安全限制）。请用下面的「选择 Mod 文件 / 选择 Mod 文件夹」按钮。"
        : "把语音 Mod（文件夹或 .vpk）拖到本页面任意位置即可自动识别角色；也可以点下面的按钮选择。";

    /// <summary>头像状态说明。</summary>
    public string AvatarHintText
    {
        get => _avatarHintText;
        private set => Set(ref _avatarHintText, value);
    }

    public RelayCommand CacheHelpCommand { get; }

    public RelayCommand CopyCacheCommand { get; }

    public RelayCommand CopyQuitCommand { get; }

    public AsyncRelayCommand RestoreBackupCommand { get; }

    public RelayCommand DeleteBackupCommand { get; }

    public RelayCommand VerifyBackupCommand { get; }

    /// <summary>游戏根目录。</summary>
    public string GameRoot
    {
        get => _gameRoot;
        private set
        {
            if (!Set(ref _gameRoot, value)) return;
            Raise(nameof(GameRootDisplay));
        }
    }

    public string GameRootDisplay => string.IsNullOrWhiteSpace(GameRoot) ? "（未检测到，请手动选择 left4dead2.exe）" : GameRoot;

    public string GamePathStatus
    {
        get => _gamePathStatus;
        private set => Set(ref _gamePathStatus, value);
    }

    public bool GamePathValid
    {
        get => _gamePathValid;
        private set => Set(ref _gamePathValid, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string LogText
    {
        get => _logText;
        private set => Set(ref _logText, value);
    }

    /// <summary>声音缓存提示（替换完成后必须显示）。</summary>
    public string CacheNoticeText =>
        "语音文件已经替换完成。\r\n\r\n" +
        "为了避免游戏继续使用旧的声音缓存，请启动 L4D2 后打开开发者控制台（按 ~），输入：\r\n" +
        $"    {VoiceManager.RebuildCacheCommand}\r\n" +
        "等待声音缓存重建完成，然后输入：\r\n" +
        $"    {VoiceManager.QuitCommand}\r\n" +
        "退出游戏，再重新启动 L4D2。";

    public string MultiplayerNoticeText =>
        "语音 Mod 属于客户端本地声音资源：你和好友使用不同的本地语音 Mod 时，不一定会影响游戏逻辑，" +
        "但双方不会自动获得对方的本地语音文件；如果 Mod 涉及服务器限制或被服务器禁止，可能无法正常使用。本程序不会修改任何服务器文件。";

    public VoiceBackupInfo? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (!Set(ref _selectedBackup, value)) return;
            RestoreBackupCommand.RaiseCanExecuteChanged();
            DeleteBackupCommand.RaiseCanExecuteChanged();
            VerifyBackupCommand.RaiseCanExecuteChanged();
            Raise(nameof(SelectedBackupDetail));
        }
    }

    public string SelectedBackupDetail => SelectedBackup == null
        ? "请选择一个备份"
        : SelectedBackup.DetailText + "\r\n" + SelectedBackup.ValidText;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            InstallCommand.RaiseCanExecuteChanged();
            BackupCommand.RaiseCanExecuteChanged();
            RestoreLatestCommand.RaiseCanExecuteChanged();
            UninstallCommand.RaiseCanExecuteChanged();
            RestoreBackupCommand.RaiseCanExecuteChanged();
        }
    }

    // ------------------------------------------------------------------ 路径

    private void DetectGamePath(bool force)
    {
        if (force) StatusText = "正在自动检测 L4D2 安装目录…";

        // ① 用户手动指定过的路径
        var configured = _library.Config.L4D2GamePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var validated = VoiceManager.ValidateGameRoot(configured, out var error);
            if (validated != null)
            {
                SetGameRoot(validated, "✓ 使用已保存的游戏路径");
                return;
            }

            Log.Warn($"[语音] 已保存的游戏路径无效（{error}），改为自动检测");
        }

        // ② 自动检测
        var detected = VoiceManager.FindGameRoot(_library.SteamPaths);
        if (!string.IsNullOrWhiteSpace(detected))
        {
            SetGameRoot(detected, "✓ 已自动检测到 L4D2");
            return;
        }

        GameRoot = string.Empty;
        GamePathValid = false;
        GamePathStatus = "✗ 未找到有效的 Left 4 Dead 2 安装目录。请点「浏览 left4dead2.exe」手动选择。";
        Log.Warn("[语音] 未自动检测到 L4D2 安装目录");
    }

    private void SetGameRoot(string root, string status)
    {
        GameRoot = root;
        GamePathValid = true;

        var hasExe = File.Exists(Path.Combine(root, "left4dead2.exe"));
        var hasSound = Directory.Exists(Path.Combine(root, "left4dead2", "sound"));
        var dlcCount = VoiceCharacters.GameFolders.Count(f => Directory.Exists(Path.Combine(root, f)));

        GamePathStatus = $"{status}\r\n✓ left4dead2.exe　{(hasExe ? "已找到" : "缺失")}\r\n" +
                         $"✓ left4dead2\\sound　{(hasSound ? "正常" : "缺失")}\r\n" +
                         $"✓ 游戏目录　{dlcCount} 个（left4dead2" +
                         (dlcCount > 1 ? " + DLC" : string.Empty) + "）";
        Log.Info($"[语音] 游戏路径：{root}");
    }

    private void BrowseExe()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "请选择 Left 4 Dead 2 的 left4dead2.exe",
            Filter = "Left 4 Dead 2 (left4dead2.exe)|left4dead2.exe|所有程序 (*.exe)|*.exe",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        var root = VoiceManager.ValidateGameRoot(dialog.FileName, out var error);
        if (root == null)
        {
            GamePathValid = false;
            GamePathStatus = "✗ " + (error ?? "未找到有效的 Left 4 Dead 2 安装目录。");
            _services.Dialogs.Error(error ?? "未找到有效的 Left 4 Dead 2 安装目录。", "游戏路径");
            return;
        }

        _library.Config.L4D2GamePath = root;
        _library.SaveConfig();
        SetGameRoot(root, "✓ 已使用手动选择的路径");
        Refresh();
    }

    // ------------------------------------------------------------------ 刷新

    public void Refresh()
    {
        if (string.IsNullOrWhiteSpace(GameRoot) || !GamePathValid)
        {
            foreach (var card in Cards)
            {
                card.Apply(new VoiceCharacterStatus { Info = card.Info }, _manager.FindAvatar(card.Info));
            }
        }
        else
        {
            foreach (var card in Cards)
            {
                card.Apply(_manager.GetStatus(GameRoot, card.Info), _manager.FindAvatar(card.Info));
            }
        }

        Backups.Clear();
        foreach (var backup in _manager.ListBackups()) Backups.Add(backup);

        Raise(nameof(SelectedBackupDetail));

        if (Backups.Count > 0 && SelectedBackup == null) SelectedBackup = Backups[0];
    }

    // ------------------------------------------------------------------ 操作

    /// <summary>拖放入口：文件夹 / .vpk / 单个语音文件。</summary>
    public async Task AcceptDropAsync(string path, VoiceCharacterCard? card)
    {
        if (string.IsNullOrWhiteSpace(GameRoot) || !GamePathValid)
        {
            _services.Dialogs.Error("请先指定 Left 4 Dead 2 的游戏路径。", "游戏路径");
            return;
        }

        var source = _manager.ScanSource(path);
        if (!source.Success)
        {
            _services.Dialogs.Error(source.Error ?? "无法读取该语音 Mod。", "语音 Mod");
            return;
        }

        var character = card?.Info ?? source.Character;
        if (character == null)
        {
            // 规格要求：识别不出来时弹出提示并让用户手动选
            _services.Dialogs.Error("无法自动识别该语音 Mod 对应的角色，请手动选择。\r\n\r\n" +
                                    "可以先在下面的角色卡片上点「安装」，再选择这个 Mod。", "无法识别角色");
            return;
        }

        await InstallInternalAsync(source, character, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
    }

    private async Task InstallAsync(VoiceCharacterCard? card)
    {
        if (card == null) return;

        if (string.IsNullOrWhiteSpace(GameRoot) || !GamePathValid)
        {
            _services.Dialogs.Error("请先指定 Left 4 Dead 2 的游戏路径。", "游戏路径");
            return;
        }

        // 让用户选择：语音 Mod 文件夹 或 VPK 文件
        var choose = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"选择 {card.EnglishName} 的语音 Mod（可直接把 .vpk 文件名填进来）",
            Filter = "语音 Mod (*.vpk)|*.vpk|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (choose.ShowDialog() != true) return;

        var source = _manager.ScanSource(choose.FileName);
        if (!source.Success)
        {
            _services.Dialogs.Error(source.Error ?? "无法读取该语音 Mod。", "语音 Mod");
            return;
        }

        await InstallInternalAsync(source, card.Info, Path.GetFileName(choose.FileName));
    }

    private async Task InstallInternalAsync(VoiceSourceInfo source, VoiceCharacterInfo character, string modName)
    {
        var targets = string.IsNullOrWhiteSpace(GameRoot)
            ? new List<string>()
            : VoiceManager.FindVoiceDirectories(GameRoot, character);

        // VPK 走 addons（不碰游戏内语音文件）；散装文件夹才需要备份后覆盖语音目录
        var addonsDirectory = string.IsNullOrWhiteSpace(GameRoot)
            ? string.Empty
            : Path.Combine(GameRoot, "left4dead2", "addons");

        var confirmText = source.FromVpk
            ? $"检测到：\r\n" +
              $"角色：{character.DisplayName}\r\n" +
              $"语音文件数量：{source.TotalVoiceFiles}（该 VPK 内含）\r\n" +
              $"来源：VPK {Path.GetFileName(source.SourcePath)}\r\n" +
              $"目标：{addonsDirectory}\\{Path.GetFileName(source.SourcePath)}\r\n\r\n" +
              "安装方式：作为 addon 放进 addons 目录，由游戏加载。\r\n" +
              "**不会覆盖或修改游戏内的语音文件**；删除时只需移除这个 VPK。\r\n" +
              (File.Exists(Path.Combine(addonsDirectory, Path.GetFileName(source.SourcePath)))
                  ? "（addons 里已有同名文件，会先备份它再覆盖）"
                  : string.Empty)
            : $"检测到：\r\n" +
              $"角色：{character.DisplayName}\r\n" +
              $"文件数量：{source.TotalVoiceFiles}\r\n" +
              $"来源：文件夹 {Path.GetFileName(source.SourcePath)}\r\n" +
              $"目标目录：\r\n" +
              (targets.Count == 0
                  ? "    （未找到语音目录，将无法安装）"
                  : string.Join("\r\n", targets.Select(t => "    " + Path.GetRelativePath(GameRoot, t)))) +
              "\r\n\r\n安装方式：先把这些目录里的原版语音打包成 ZIP 备份，验证通过后再用你的文件覆盖。\r\n" +
              $"备份文件名：{character.EnglishName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip";

        if (!source.FromVpk && targets.Count == 0)
        {
            _services.Dialogs.Error($"没有找到 {character.VoiceRelativeDirectory} 目录，无法安装。\r\n" +
                                    "请确认游戏文件完整。", "语音目录不存在");
            return;
        }

        if (source.FromVpk && !Directory.Exists(addonsDirectory))
        {
            _services.Dialogs.Error($"找不到 addons 目录：{addonsDirectory}\r\n请确认游戏文件完整。", "addons 目录不存在");
            return;
        }

        if (!_services.Dialogs.Confirm(confirmText, "确认安装语音 Mod", null, "备份并安装")) return;

        IsBusy = true;
        StatusText = "正在安装…";
        try
        {
            var progress = new Progress<string>(message => StatusText = message);
            var result = await Task.Run(() => _manager.Install(GameRoot, source, character, modName, progress)).ConfigureAwait(true);

            StatusText = result.Message;
            Refresh();

            if (result.Success)
            {
                _services.Dialogs.Info(result.Message + "\r\n\r\n" + CacheNoticeText, "安装完成", string.Join("\r\n", result.Log));
            }
            else
            {
                _services.Dialogs.Error(result.Message, "安装失败", string.Join("\r\n", result.Log));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BackupAsync(VoiceCharacterCard? card)
    {
        if (card == null) return;
        if (string.IsNullOrWhiteSpace(GameRoot) || !GamePathValid)
        {
            _services.Dialogs.Error("请先指定 Left 4 Dead 2 的游戏路径。", "游戏路径");
            return;
        }

        if (!_services.Dialogs.Confirm(
                $"将把 {card.EnglishName}（{card.Info.VoiceRelativeDirectory}）当前的全部语音文件打包成 ZIP 备份，不会修改任何游戏文件。",
                "备份原版语音", null, "开始备份"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(message => StatusText = message);
            var (result, backup, _) = await Task.Run(() =>
                _manager.CreateBackup(GameRoot, card.Info, label: null, pendingAddedFiles: null, progress)).ConfigureAwait(true);

            StatusText = result.Message;
            Refresh();

            if (result.Success && backup != null)
                _services.Dialogs.Info($"{result.Message}\r\n\r\n文件数：{backup.FileCount}\r\n大小：{backup.SizeText}", "备份成功", string.Join("\r\n", result.Log));
            else
                _services.Dialogs.Error(result.Message, "备份失败", string.Join("\r\n", result.Log));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreLatestAsync(VoiceCharacterCard? card)
    {
        if (card == null) return;

        var latest = _manager.ListBackups()
            .Where(b => string.Equals(b.CharacterCodename, card.Codename, StringComparison.OrdinalIgnoreCase) && !b.IsPreRestore)
            .OrderByDescending(b => b.CreatedLocal, StringComparer.Ordinal)
            .FirstOrDefault();

        if (latest == null)
        {
            _services.Dialogs.Info($"还没有 {card.EnglishName} 的备份。\r\n可以先点「备份」创建一份原版备份。", "没有备份");
            return;
        }

        SelectedBackup = latest;
        await RestoreBackupInternalAsync(latest);
    }

    private async Task RestoreSelectedAsync() => await RestoreBackupInternalAsync(SelectedBackup);

    private async Task RestoreBackupInternalAsync(VoiceBackupInfo? backup)
    {
        if (backup == null) return;
        if (string.IsNullOrWhiteSpace(GameRoot) || !GamePathValid)
        {
            _services.Dialogs.Error("请先指定 Left 4 Dead 2 的游戏路径。", "游戏路径");
            return;
        }

        if (!_services.Dialogs.Confirm(
                backup.DetailText + "\r\n\r\n恢复前会先把「当前状态」再备份一次，所以恢复到错误的版本也可以再恢复回来。\r\n\r\n确定恢复吗？",
                "确认恢复原版语音", null, "恢复"))
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在恢复…";
        try
        {
            var progress = new Progress<string>(message => StatusText = message);
            var result = await Task.Run(() => _manager.Restore(GameRoot, backup, progress)).ConfigureAwait(true);

            StatusText = result.Message;
            Refresh();

            if (result.Success)
                _services.Dialogs.Info(result.Message + "\r\n\r\n" + CacheNoticeText, "恢复完成", string.Join("\r\n", result.Log));
            else
                _services.Dialogs.Error(result.Message, "恢复失败", string.Join("\r\n", result.Log));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UninstallAsync(VoiceCharacterCard? card)
    {
        if (card == null) return;

        if (!_services.Dialogs.Confirm(
                $"删除 {card.EnglishName} 当前安装的语音 Mod：\r\n" +
                "1) 先把当前状态备份成 ZIP；\r\n" +
                "2) 再恢复安装时创建的原版备份；\r\n" +
                "3) 更新安装记录。\r\n\r\n确定删除吗？",
                "确认删除语音 Mod", null, "删除 Mod"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(message => StatusText = message);
            var result = await Task.Run(() => _manager.Uninstall(GameRoot, card.Info, progress)).ConfigureAwait(true);

            StatusText = result.Message;
            Refresh();

            if (result.Success) _services.Dialogs.Info(result.Message + "\r\n\r\n" + CacheNoticeText, "已删除", string.Join("\r\n", result.Log));
            else _services.Dialogs.Error(result.Message, "删除失败", string.Join("\r\n", result.Log));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void DeleteSelectedBackup(object? parameter)
    {
        var backup = parameter as VoiceBackupInfo ?? SelectedBackup;
        if (backup == null) return;

        if (!_services.Dialogs.Confirm($"确定删除这个备份吗？\r\n\r\n{backup.FileName}\r\n{backup.DetailText}",
                "删除备份", "删除后无法用这个 ZIP 恢复语音。", "删除"))
        {
            return;
        }

        var result = _manager.DeleteBackup(backup);
        StatusText = result.Message;
        SelectedBackup = null;
        Refresh();
    }

    private void VerifySelectedBackup(object? parameter)
    {
        var backup = parameter as VoiceBackupInfo ?? SelectedBackup;
        if (backup == null) return;

        var verify = _manager.VerifyBackup(backup.FilePath);
        if (verify.Success) _services.Dialogs.Info(backup.DetailText + "\r\n\r\n" + verify.Message, "ZIP 验证通过");
        else _services.Dialogs.Error(verify.Message, "ZIP 验证失败");
    }

    private void OpenVoiceFolder(VoiceCharacterCard? card)
    {
        if (card == null || string.IsNullOrWhiteSpace(GameRoot)) return;

        var directories = VoiceManager.FindVoiceDirectories(GameRoot, card.Info);
        if (directories.Count == 0)
        {
            _services.Dialogs.Info($"没有找到 {card.Info.VoiceRelativeDirectory} 目录。", "打开目录");
            return;
        }

        OpenFolder(directories[0]);
    }

    private void OpenDlcFolder(VoiceCharacterCard? card)
    {
        if (card == null || string.IsNullOrWhiteSpace(GameRoot)) return;

        var dlcDirectories = VoiceManager.FindVoiceDirectories(GameRoot, card.Info)
            .Where(d => !Path.GetRelativePath(GameRoot, d).StartsWith("left4dead2" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (dlcDirectories.Count == 0)
        {
            _services.Dialogs.Info("该角色在 DLC 目录里没有语音文件。", "打开 DLC 语音目录");
            return;
        }

        OpenFolder(dlcDirectories[0]);
    }

    private async Task ChooseModFileAsync()
    {
        if (!EnsureGameRoot()) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择语音 Mod（.vpk 优先，也可以选单个语音文件）",
            Filter = "语音 Mod (*.vpk)|*.vpk|语音文件 (*.wav;*.mp3;*.ogg)|*.wav;*.mp3;*.ogg|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;
        await AcceptDropAsync(dialog.FileName, null);
    }

    private async Task ChooseModFolderAsync()
    {
        if (!EnsureGameRoot()) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择语音 Mod 文件夹（例如解压出来的 sound\\player\\survivor\\voice\\…）" };
        if (dialog.ShowDialog() != true) return;
        await AcceptDropAsync(dialog.FolderName, null);
    }

    private bool EnsureGameRoot()
    {
        if (!string.IsNullOrWhiteSpace(GameRoot) && GamePathValid) return true;

        _services.Dialogs.Error("请先指定 Left 4 Dead 2 的游戏路径。", "游戏路径");
        return false;
    }

    /// <summary>首次进入且没有任何头像时，后台自动尝试获取一次（可在设置/配置里关掉）。</summary>
    private void TryAutoDownloadAvatars()
    {
        if (_avatarAutoTried) return;
        _avatarAutoTried = true;

        if (!_library.Config.AutoDownloadAvatars) return;
        if (Cards.Any(c => c.HasAvatar)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                Ui.InvokeAsync(() => AvatarHintText = "人物头像：正在后台自动获取…");
                var results = await AvatarDownloader.DownloadAllAsync(_manager).ConfigureAwait(false);
                var ok = results.Count(r => r.Success && !r.Message.Contains("跳过", StringComparison.Ordinal));

                Ui.InvokeAsync(() =>
                {
                    Refresh();
                    AvatarHintText = ok > 0
                        ? $"人物头像：已自动获取 {ok} 个角色头像"
                        : "人物头像：自动获取失败（网络不可达或图片源结构变化），可点「下载人物头像」重试，或把自己的图片放进 Avatars 目录";
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"自动下载头像失败：{ex.Message}");
                Ui.InvokeAsync(() => AvatarHintText = "人物头像：自动获取失败，可点「下载人物头像」重试");
            }
        });
    }

    private async Task DownloadAvatarsAsync()
    {
        if (!_services.Dialogs.Confirm(
                "将从同人维基（left4dead.fandom.com）抓取八名生还者的头像，保存到：\r\n" +
                $"    {_manager.AvatarDirectory}\r\n\r\n" +
                "图片仅缓存在本机用于界面显示；程序不内置任何官方美术。\r\n" +
                "你也可以把自己的图片命名为代号（如 mechanic.png）放进该目录覆盖。\r\n\r\n开始下载吗？",
                "下载人物头像", null, "开始下载"))
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在下载人物头像…";
        try
        {
            var progress = new Progress<string>(message => StatusText = message);
            var results = await Task.Run(() => AvatarDownloader.DownloadAllAsync(_manager, false, progress)).ConfigureAwait(true);

            var ok = results.Count(r => r.Success && !r.Message.Contains("跳过", StringComparison.Ordinal));
            var skipped = results.Count(r => r.Message.Contains("跳过", StringComparison.Ordinal));
            var failed = results.Count(r => !r.Success);

            Refresh();
            StatusText = $"头像下载完成：新增 {ok} 个，已有 {skipped} 个，失败 {failed} 个";

            var detail = string.Join("\r\n", results.Select(r => $"{(r.Success ? "✓" : "✗")} {r.CharacterName}：{r.Message}"));
            if (failed > 0) _services.Dialogs.Error(StatusText + "\r\n\r\n" + detail, "下载人物头像");
            else _services.Dialogs.Info(StatusText + "\r\n\r\n" + detail, "下载人物头像");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenAvatarsFolder()
    {
        Directory.CreateDirectory(_manager.AvatarDirectory);
        OpenFolder(_manager.AvatarDirectory);
    }

    private void ShowCacheHelp()
    {
        _services.Dialogs.Info(CacheNoticeText, "声音缓存说明", MultiplayerNoticeText);
    }

    private void CopyToClipboard(string text, string status)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusText = status;
        }
        catch (Exception ex)
        {
            _services.Dialogs.Error("复制失败：" + ex.Message, "复制命令");
        }
    }

    private void OnMessageLogged(string message)
    {
        // 只把语音相关日志显示在页面日志窗口里
        if (!message.Contains("[语音", StringComparison.Ordinal)) return;

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Ui.InvokeAsync(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > 500) LogLines.RemoveAt(0);
            LogText = string.Join(Environment.NewLine, LogLines);
        });
    }

    private void Report(string title, Exception exception) =>
        _services.Dialogs.Error(exception.Message, title, exception.ToString());

    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开目录失败：{ex.Message}");
        }
    }
}

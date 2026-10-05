using L4D2ModManager.App.Infrastructure;
using L4D2ModManager.App.Services;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Mods;
using L4D2ModManager.Core.Services.Voice;

namespace L4D2ModManager.App.ViewModels;

/// <summary>角色下拉项。</summary>
public sealed record VoiceCharacterOption(VoiceCharacterInfo Info, string Text);

/// <summary>
/// 人物语音替换：拖入语音文件夹 → 自动识别角色 → 批量替换到对应目录，
/// 并支持按角色 / 全部一键还原（替换前自动备份）。
/// </summary>
public sealed class VoiceViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ModLibraryService _library;
    private readonly VoiceReplacer _replacer = new();

    private string _sourceDirectory = string.Empty;
    private string? _detectedCharacterId;
    private VoiceCharacterOption _selectedCharacter;
    private string _statusText = "把一个语音文件夹拖到上面的方框里，程序会自动识别是哪个角色。";
    private string _backupText = "还没有任何备份。";
    private bool _hasBackup;
    private bool _isBusy;

    public VoiceViewModel(AppServices services)
    {
        _services = services;
        _library = services.Library;

        Characters = VoiceCharacters.All
            .Select(c => new VoiceCharacterOption(c, c.DisplayName))
            .ToList();
        _selectedCharacter = Characters[0];

        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder());
        ReplaceCommand = new AsyncRelayCommand(ReplaceAsync, () => !IsBusy && HasSource, ex => Report("替换失败", ex));
        RestoreCharacterCommand = new AsyncRelayCommand(
            () => RestoreAsync(SelectedCharacter.Info.Character), () => !IsBusy && HasBackup, ex => Report("还原失败", ex));
        RestoreAllCommand = new AsyncRelayCommand(
            () => RestoreAsync(null), () => !IsBusy && HasBackup, ex => Report("还原失败", ex));
        OpenBackupCommand = new RelayCommand(_ => OpenFolder(_replacer.BackupRoot));

        RefreshBackups();
    }

    // ------------------------------------------------------------------ 绑定

    public IReadOnlyList<VoiceCharacterOption> Characters { get; }

    public RelayCommand ChooseFolderCommand { get; }

    public AsyncRelayCommand ReplaceCommand { get; }

    public AsyncRelayCommand RestoreCharacterCommand { get; }

    public AsyncRelayCommand RestoreAllCommand { get; }

    public RelayCommand OpenBackupCommand { get; }

    /// <summary>要替换的语音文件夹（拖入或选择）。</summary>
    public string SourceDirectory
    {
        get => _sourceDirectory;
        set
        {
            var text = value ?? string.Empty;
            if (!Set(ref _sourceDirectory, text)) return;

            AutoDetectCharacter();
            Raise(nameof(HasSource), nameof(TargetText), nameof(DetectionText));
            ReplaceCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSource => !string.IsNullOrWhiteSpace(SourceDirectory) && Directory.Exists(SourceDirectory);

    public VoiceCharacterOption SelectedCharacter
    {
        get => _selectedCharacter;
        set
        {
            if (!Set(ref _selectedCharacter, value)) return;
            _detectedCharacterId = value.Info.Id;
            Raise(nameof(DetectionText), nameof(TargetText));
        }
    }

    /// <summary>识别结果说明。</summary>
    public string DetectionText
    {
        get
        {
            if (!HasSource) return "尚未选择语音文件夹";

            var detected = VoiceCharacters.FindById(_detectedCharacterId);
            if (detected == null) return "未能从文件夹名识别角色，请在下面手动选择";

            return $"识别到角色：{detected.DisplayName}" +
                   (detected.IsL4D1 ? "（1 代角色，会写入 3 个 DLC 目录）" : "（2 代角色）");
        }
    }

    /// <summary>将要写入的目录说明。</summary>
    public string TargetText
    {
        get
        {
            var gameRoot = VoiceReplacer.FindGameRoot(_library.SteamPaths);
            if (string.IsNullOrWhiteSpace(gameRoot)) return "未找到游戏目录（请在设置里检查 Steam 路径）";

            var targets = VoiceReplacer.BuildTargets(gameRoot, SelectedCharacter.Info);
            return "写入目录：" + string.Join("、", targets.Select(t => t.Folder));
        }
    }

    public string GameRootText
    {
        get
        {
            var gameRoot = VoiceReplacer.FindGameRoot(_library.SteamPaths);
            return string.IsNullOrWhiteSpace(gameRoot) ? "游戏目录：未找到" : $"游戏目录：{gameRoot}";
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string BackupText
    {
        get => _backupText;
        private set => Set(ref _backupText, value);
    }

    public bool HasBackup
    {
        get => _hasBackup;
        private set
        {
            if (!Set(ref _hasBackup, value)) return;
            RestoreCharacterCommand.RaiseCanExecuteChanged();
            RestoreAllCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            ReplaceCommand.RaiseCanExecuteChanged();
            RestoreCharacterCommand.RaiseCanExecuteChanged();
            RestoreAllCommand.RaiseCanExecuteChanged();
        }
    }

    // ------------------------------------------------------------------ 操作

    /// <summary>供拖放使用：传入文件或文件夹路径。</summary>
    public void SetSource(string path)
    {
        try
        {
            if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
        }
        catch
        {
            // 忽略
        }

        SourceDirectory = path;
    }

    /// <summary>依据文件夹名/内容自动识别角色（识别不出来就保留手动选择）。</summary>
    private void AutoDetectCharacter()
    {
        if (!HasSource) return;

        try
        {
            // ① 先看文件夹名（用户通常命名为 "佐伊语音包" 之类）
            var byName = VoiceCharacters.Detect(Path.GetFileName(SourceDirectory.TrimEnd(Path.DirectorySeparatorChar)));
            if (byName == null)
            {
                // ② 再看里面的路径/文件名
                var sample = Directory.EnumerateFiles(SourceDirectory, "*", SearchOption.AllDirectories).Take(30);
                foreach (var file in sample)
                {
                    var hit = VoiceCharacters.Detect(Path.GetRelativePath(SourceDirectory, file));
                    if (hit != null)
                    {
                        byName = hit;
                        break;
                    }
                }
            }

            if (byName != null)
            {
                _detectedCharacterId = byName.Id;
                _selectedCharacter = Characters.First(c => c.Info.Character == byName.Character);
                Raise(nameof(SelectedCharacter));
            }
            else
            {
                _detectedCharacterId = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"自动识别语音角色失败：{ex.Message}");
            _detectedCharacterId = null;
        }
    }

    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog() == true)
        {
            SetSource(dialog.FolderName);
        }
    }

    private async Task ReplaceAsync()
    {
        var gameRoot = VoiceReplacer.FindGameRoot(_library.SteamPaths);
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            _services.Dialogs.Error("没有找到 Left 4 Dead 2 的游戏目录。\r\n请在「设置」里确认 Steam 路径。", "语音替换");
            return;
        }

        var info = SelectedCharacter.Info;
        var targets = VoiceReplacer.BuildTargets(gameRoot, info);

        var message =
            $"角色：{info.DisplayName}\r\n" +
            $"源文件夹：{SourceDirectory}\r\n" +
            $"写入目录：{string.Join("、", targets.Select(t => t.Folder))}\r\n\r\n" +
            "覆盖前会把原有语音备份到程序的数据目录，之后可以按角色或全部一键还原。\r\n" +
            "（如果 Steam 装在 Program Files，需要以管理员身份运行本程序）";

        if (!_services.Dialogs.Confirm(message, "确认替换人物语音", null, "开始替换")) return;

        IsBusy = true;
        StatusText = "正在替换语音…";
        try
        {
            var progress = new Progress<string>(text => StatusText = text);
            var result = await Task.Run(() => _replacer.Replace(gameRoot, SourceDirectory, info, progress)).ConfigureAwait(true);

            StatusText = result.Summary;
            if (result.Success)
            {
                RefreshBackups();
                _services.Dialogs.Info(result.Summary, "语音替换完成");
            }
            else
            {
                _services.Dialogs.Error(result.Error ?? "替换失败。", "语音替换");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreAsync(VoiceCharacter? character)
    {
        var gameRoot = VoiceReplacer.FindGameRoot(_library.SteamPaths);
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            _services.Dialogs.Error("没有找到游戏目录，无法还原。", "语音还原");
            return;
        }

        var name = character.HasValue ? VoiceCharacters.Find(character.Value)?.DisplayName ?? "该角色" : "全部角色";
        if (!_services.Dialogs.Confirm(
                $"将还原{name}的语音：原有文件写回，替换时新增的文件会被删除。\r\n\r\n确定继续吗？",
                "确认还原语音", null, "开始还原"))
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在还原语音…";
        try
        {
            var result = await Task.Run(() => _replacer.Restore(character, gameRoot)).ConfigureAwait(true);
            StatusText = result.Summary;
            RefreshBackups();

            if (result.Success) _services.Dialogs.Info(result.Summary, "语音还原完成");
            else _services.Dialogs.Error(result.Error ?? "还原失败。", "语音还原");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshBackups()
    {
        var summary = _replacer.BackupSummary();
        HasBackup = summary.Count > 0;

        BackupText = summary.Count == 0
            ? "还没有任何备份。"
            : "已备份：" + string.Join("、", summary.Select(kv =>
            {
                var info = VoiceCharacters.FindById(kv.Key);
                return $"{info?.DisplayName ?? kv.Key}（{kv.Value} 个文件）";
            }));
    }

    private void Report(string title, Exception exception) =>
        _services.Dialogs.Error(exception.Message, title, exception.ToString());

    private static void OpenFolder(string path)
    {
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

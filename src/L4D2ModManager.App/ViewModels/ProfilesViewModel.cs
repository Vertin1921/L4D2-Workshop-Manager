using System.Collections.ObjectModel;
using L4D2ModManager.App.Infrastructure;
using L4D2ModManager.App.Services;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Mods;

namespace L4D2ModManager.App.ViewModels;

/// <summary>配置方案页面：内置方案（单人 / 联机 / 写实 / 枪械）+ 自定义方案。</summary>
public sealed class ProfilesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ModLibraryService _library;

    private ModProfile? _selected;
    private string _newProfileName = string.Empty;
    private string _newProfileDescription = string.Empty;
    private string _statusText = "选择一个方案后点击「应用方案」。";

    public ProfilesViewModel(AppServices services)
    {
        _services = services;
        _library = services.Library;

        RefreshCommand = new RelayCommand(_ => Refresh());
        ApplySelectedCommand = new RelayCommand(_ => Apply(Selected), _ => Selected != null);
        ApplyCommand = new RelayCommand(p => Apply(p as ModProfile ?? Selected));
        DeleteCommand = new RelayCommand(p => Delete(p as ModProfile ?? Selected));
        CaptureCommand = new RelayCommand(_ => Capture(), _ => !string.IsNullOrWhiteSpace(NewProfileName));
        ImportCurrentCommand = new RelayCommand(_ => Capture(fromCurrentState: true));
        EditOverridesCommand = new RelayCommand(p => ShowOverrides(p as ModProfile ?? Selected));

        _library.ModsChanged += (_, _) => Ui.InvokeAsync(RaiseLiveState);
    }

    public ObservableCollection<ModProfile> Profiles { get; } = new();

    public RelayCommand RefreshCommand { get; }

    public RelayCommand ApplySelectedCommand { get; }

    public RelayCommand ApplyCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand CaptureCommand { get; }

    public RelayCommand ImportCurrentCommand { get; }

    public RelayCommand EditOverridesCommand { get; }

    public ModProfile? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(SelectedDescription));
            ApplySelectedCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }

    public string SelectedDescription => Selected == null
        ? "未选择方案"
        : $"{Selected.Description}\r\n{Selected.PresetText} · {Selected.OverrideText}";

    public string NewProfileName
    {
        get => _newProfileName;
        set
        {
            if (!Set(ref _newProfileName, value)) return;
            CaptureCommand.RaiseCanExecuteChanged();
        }
    }

    public string NewProfileDescription
    {
        get => _newProfileDescription;
        set => Set(ref _newProfileDescription, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string LiveStateText =>
        $"当前：启用 {_library.Mods.Count(m => m.IsEnabled)} 个 · 禁用 {_library.Mods.Count(m => m.IsDisabled)} 个 · " +
        $"共 {_library.Mods.Count} 个 Mod";

    public void Refresh()
    {
        Profiles.Clear();
        foreach (var profile in _library.Config.Profiles) Profiles.Add(profile);

        if (Selected == null || !Profiles.Contains(Selected))
            Selected = Profiles.FirstOrDefault();

        RaiseLiveState();
    }

    private void RaiseLiveState() => Raise(nameof(LiveStateText), nameof(SelectedDescription));

    private void Apply(ModProfile? profile)
    {
        if (profile == null) return;

        if (_library.Mods.Count == 0)
        {
            _services.Dialogs.Info("当前列表为空，请先扫描 Mod 目录。", "无法应用方案");
            return;
        }

        var progress = new Progress<string>(message => StatusText = message);
        var result = _library.ApplyProfile(profile, progress);

        StatusText = $"[{profile.Name}] {result.Summary}";
        if (result.Errors.Count > 0)
            _services.Dialogs.Error(result.Summary, "应用方案时出现错误", string.Join("\r\n", result.Errors.Take(20)));

        RaiseLiveState();
    }

    private void Delete(ModProfile? profile)
    {
        if (profile == null) return;

        if (profile.IsBuiltIn)
        {
            _services.Dialogs.Info("内置方案不能删除，但可以在此基础上修改后另存为自定义方案。", "无法删除内置方案");
            return;
        }

        if (!_services.Dialogs.Confirm($"确定要删除方案「{profile.Name}」吗？", "删除方案")) return;

        if (_library.DeleteProfile(profile))
        {
            StatusText = $"已删除方案：{profile.Name}";
            Refresh();
        }
    }

    private void Capture(bool fromCurrentState = false)
    {
        var name = fromCurrentState ? $"方案 {DateTime.Now:MM-dd HH:mm}" : NewProfileName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _services.Dialogs.Info("请先填写方案名称。", "需要名称");
            return;
        }

        var description = string.IsNullOrWhiteSpace(NewProfileDescription)
            ? "保存当前 Mod 启停状态"
            : NewProfileDescription.Trim();

        var profile = _library.SaveCurrentAsProfile(name, description);
        NewProfileName = string.Empty;
        NewProfileDescription = string.Empty;
        Refresh();
        Selected = Profiles.FirstOrDefault(p => p.Id == profile.Id);
        StatusText = $"已保存方案：{name}（记录 {profile.Overrides.Count} 个 Mod 的状态）";
    }

    private void ShowOverrides(ModProfile? profile)
    {
        if (profile == null) return;

        var lines = profile.Overrides
            .OrderBy(pair => pair.Key)
            .Take(200)
            .Select(pair => $"{(pair.Value == ModState.Enabled ? "启用" : "禁用")}  {pair.Key}");

        _services.Dialogs.Info(
            $"方案：{profile.Name}\r\n{profile.PresetText}\r\n共记录 {profile.Overrides.Count} 个 Mod 的单独状态。",
            "方案详情",
            string.Join("\r\n", lines));
    }
}

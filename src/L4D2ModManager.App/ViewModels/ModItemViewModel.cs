using System.Windows.Media.Imaging;
using L4D2ModManager.App.Infrastructure;
using L4D2ModManager.Core.Models;

namespace L4D2ModManager.App.ViewModels;

/// <summary>列表中一张 Mod 卡片的视图模型。</summary>
public sealed class ModItemViewModel : ObservableObject
{
    private readonly ModsViewModel _owner;
    private readonly Services.AppServices _services;
    private BitmapImage? _thumbnail;
    private bool _isSelected;
    private bool _thumbnailRequested;

    public ModItemViewModel(ModItem model, ModsViewModel owner, Services.AppServices services)
    {
        Model = model;
        _owner = owner;
        _services = services;
    }

    public ModItem Model { get; }

    /// <summary>多选框状态。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value)) return;
            _owner.NotifySelectionChanged();
        }
    }

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (!Set(ref _thumbnail, value)) return;
            Raise(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail != null;

    /// <summary>卡片上按钮的文字（启用 / 禁用）。</summary>
    public string ToggleText => Model.IsEnabled ? "禁用" : "启用";

    public string DisplayName => string.IsNullOrWhiteSpace(Model.DisplayName) ? Model.FileName : Model.DisplayName;

    public string Author => string.IsNullOrWhiteSpace(Model.Author) ? "未知作者" : Model.Author;

    public string AuthorLine => "作者：" + Author;

    public string CategoryText => Model.CategoryText;

    public string SizeText => Model.SizeText;

    public string StateText => Model.StateText;

    public bool IsEnabled => Model.IsEnabled;

    public bool IsDisabled => Model.IsDisabled;

    public string AddedText => Model.AddedText;

    public string ModifiedText => Model.ModifiedText;

    public string FileName => Model.FileName;

    public string? WorkshopId => Model.WorkshopId;

    public bool HasWorkshopId => !string.IsNullOrWhiteSpace(Model.WorkshopId);

    public string WorkshopText => HasWorkshopId ? $"工坊 #{Model.WorkshopId}" : "本地 Mod";

    public bool HasConflict => Model.ConflictCount > 0;

    public string ConflictText => Model.ConflictCount > 0 ? $"⚠ 冲突 {Model.ConflictCount}" : string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(Model.Error);

    public string ErrorText => Model.Error ?? string.Empty;

    public string Description => Model.ShortDescription;

    public string PathText => Model.FilePath;

    public string DetailText =>
        $"路径：{Model.FilePath}\r\n" +
        $"大小：{Model.SizeText}\r\n" +
        $"创建：{Model.ModifiedText}\r\n" +
        $"加入：{Model.AddedText}\r\n" +
        $"状态：{Model.StateText}   分类：{Model.CategoryText}" +
        (HasWorkshopId ? $"\r\n创意工坊：https://steamcommunity.com/sharedfiles/filedetails/?id={Model.WorkshopId}" : string.Empty) +
        (Model.Tags.Length > 0 ? $"\r\n标签：{Model.Tags}" : string.Empty) +
        (!string.IsNullOrWhiteSpace(Model.Description) ? $"\r\n\r\n{Model.Description}" : string.Empty);

    public string ToolTipText => DetailText;

    /// <summary>异步加载缩略图（只请求一次）。</summary>
    public async Task EnsureThumbnailAsync()
    {
        if (_thumbnailRequested) return;
        _thumbnailRequested = true;

        var image = await _services.Thumbnails.LoadAsync(Model).ConfigureAwait(true);
        await Ui.InvokeAsync(() => Thumbnail = image).ConfigureAwait(true);
    }

    public void ResetThumbnail()
    {
        _thumbnailRequested = false;
        Thumbnail = null;
    }

    /// <summary>数据变化后刷新全部显示属性。</summary>
    public void Refresh() => Raise(
        nameof(DisplayName), nameof(Author), nameof(AuthorLine), nameof(CategoryText), nameof(SizeText),
        nameof(StateText), nameof(IsEnabled), nameof(IsDisabled), nameof(AddedText), nameof(ModifiedText),
        nameof(FileName), nameof(WorkshopId), nameof(HasWorkshopId), nameof(WorkshopText),
        nameof(HasConflict), nameof(ConflictText), nameof(HasError), nameof(ErrorText),
        nameof(Description), nameof(PathText), nameof(DetailText), nameof(ToolTipText), nameof(ToggleText));
}

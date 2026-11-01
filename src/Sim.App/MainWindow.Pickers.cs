using Avalonia.Platform.Storage;

namespace Sim.App;

/// <summary>파일·폴더 대화상자는 한 번에 하나만 — 대화상자가 떠 있는 동안 메인 창으로 포커스가 돌아오면(Alt+Tab 등)
/// 단축키가 두 번째 대화상자를 여는 일이 있었다(UI 점검 2026-10-11). 열려 있으면 새 요청은 빈 결과로 무시한다.</summary>
public partial class MainWindow
{
    private bool _picking;

    private async Task<IReadOnlyList<IStorageFile>> PickOpenAsync(FilePickerOpenOptions o)
    {
        if (_picking) return [];
        _picking = true;
        try { return await StorageProvider.OpenFilePickerAsync(o); }
        finally { _picking = false; }
    }

    private async Task<IStorageFile?> PickSaveAsync(FilePickerSaveOptions o)
    {
        if (_picking) return null;
        _picking = true;
        try { return await StorageProvider.SaveFilePickerAsync(o); }
        finally { _picking = false; }
    }

    private async Task<IReadOnlyList<IStorageFolder>> PickFolderAsync(FolderPickerOpenOptions o)
    {
        if (_picking) return [];
        _picking = true;
        try { return await StorageProvider.OpenFolderPickerAsync(o); }
        finally { _picking = false; }
    }
}

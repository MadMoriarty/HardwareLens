using Avalonia.Controls;
namespace HardwareLens.App.Pages;

public abstract class PageBase : UserControl
{
    protected readonly AppModel M;
    protected PageBase(AppModel model) { M = model; }
    /// <summary>Called when the page becomes visible.</summary>
    public virtual void OnShown() => Refresh();
    /// <summary>Called on every UI tick while visible (not while the display is paused, unless <see cref="RefreshWhenPaused"/>).</summary>
    public abstract void Refresh();
    public virtual bool RefreshWhenPaused => false;
    /// <summary>Called when inventory finished loading/refreshing.</summary>
    public virtual void OnInventoryChanged() { }
}

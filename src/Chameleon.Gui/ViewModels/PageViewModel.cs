using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Chameleon.Gui.ViewModels;

/// <summary>База для ViewModel страниц: заголовок и значок для sidebar.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    /// <summary>Название страницы (в sidebar и заголовке).</summary>
    public abstract string Title { get; }

    /// <summary>Векторный значок страницы (24×24). Эмодзи рисовались разными шрифтами и стилями.</summary>
    public abstract Geometry Icon { get; }

    /// <summary>true, если это текущая (активная) страница - для подсветки в sidebar.</summary>
    [ObservableProperty] private bool _isActive;

    partial void OnIsActiveChanged(bool value)
    {
        if (value) OnActivated();
    }

    /// <summary>Страница открыта - обновить показания, которые могли поменяться на других страницах.</summary>
    protected virtual void OnActivated()
    {
    }
}

/// <summary>Значки страниц (Material Design Icons, 24×24).</summary>
internal static class PageIcons
{
    public static readonly Geometry Power = Geometry.Parse(
        "M16.56,5.44L15.11,6.89C16.84,7.94 18,9.83 18,12A6,6 0 0,1 12,18A6,6 0 0,1 6,12C6,9.83 7.16,7.94 8.88,6.88L7.44,5.44C5.36,6.88 4,9.28 4,12A8,8 0 0,0 12,20A8,8 0 0,0 20,12C20,9.28 18.64,6.88 16.56,5.44M13,3H11V13H13");

    public static readonly Geometry Servers = Geometry.Parse(
        "M4 1h16a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V2a1 1 0 0 1 1-1zm0 13h16a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-6a1 1 0 0 1 1-1zM7 4.5a1 1 0 1 0 0 2 1 1 0 0 0 0-2zm0 13a1 1 0 1 0 0 2 1 1 0 0 0 0-2z");

    public static readonly Geometry Logs = Geometry.Parse(
        "M6,2A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2H6M6,4H13V9H18V20H6V4M8,12V14H16V12H8M8,16V18H13V16H8Z");

    public static readonly Geometry Routing = Geometry.Parse(
        "M14,4L16.29,6.29L13.41,9.17L14.83,10.59L17.71,7.71L20,10V4M10,4H4V10L6.29,7.71L11,12.41V20H13V11.59L7.71,6.29");

    public static readonly Geometry Settings = Geometry.Parse(
        "M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58a.49.49 0 0 0 .12-.61l-1.92-3.32a.488.488 0 0 0-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54a.484.484 0 0 0-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58a.49.49 0 0 0-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z");
}

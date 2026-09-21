using CommunityToolkit.Mvvm.ComponentModel;

namespace Chameleon.Gui.ViewModels;

/// <summary>База для ViewModel страниц: заголовок и ключ иконки для sidebar.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    /// <summary>Название страницы (в sidebar и заголовке).</summary>
    public abstract string Title { get; }

    /// <summary>Значок страницы (эмодзи-глиф, рендерится системным шрифтом).</summary>
    public abstract string Icon { get; }
}
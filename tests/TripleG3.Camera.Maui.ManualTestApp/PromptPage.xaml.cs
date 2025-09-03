using TripleG3.Skeye.ViewModels;

namespace TripleG3.Camera.Maui.ManualTestApp;

public partial class PromptPage : ContentPage
{
    private readonly PromptRequestViewModel _vm;
    public PromptPage()
    {
        InitializeComponent();
        _vm = ServiceHelper.GetRequiredService<PromptRequestViewModel>();
        BindingContext = _vm;
    }
}

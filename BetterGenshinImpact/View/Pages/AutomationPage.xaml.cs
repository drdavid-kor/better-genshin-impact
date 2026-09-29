using System.Windows.Controls;
using BetterGenshinImpact.ViewModel.Pages;

namespace BetterGenshinImpact.View.Pages;

public partial class AutomationPage : Page
{
    public AutomationPageViewModel ViewModel { get; }

    public AutomationPage(AutomationPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
}

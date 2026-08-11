using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using PersonalFitnessPlanner.App.Services;
using PersonalFitnessPlanner.App.ViewModels;

namespace PersonalFitnessPlanner.App;

public partial class MainWindow : Window
{
    private readonly AppRuntimeOptions _runtime;
    private bool _loaded;
    private bool _closingAfterSave;

    public MainWindow(MainViewModel viewModel, AppRuntimeOptions runtime)
    {
        InitializeComponent();
        DataContext = viewModel;
        _runtime = runtime;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await ((MainViewModel)DataContext).LoadAsync();
        if (_runtime.SmokeTest)
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var viewModel = (MainViewModel)DataContext;
            Application.Current.Shutdown(viewModel.InitializationSucceeded ? 0 : 1);
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        await ((MainViewModel)DataContext).Settings.LoginAsync(LoginPasswordBox.Password);
        LoginPasswordBox.Clear();
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 Personal Fitness Planner JSON",
            Filter = "JSON 文件 (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ((MainViewModel)DataContext).Settings.ImportAsync(dialog.FileName);
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        var viewModel = (MainViewModel)DataContext;
        if (_closingAfterSave || !viewModel.Settings.HasUnsavedChanges) return;
        e.Cancel = true;
        await viewModel.HandleClosingAsync();
        _closingAfterSave = true;
        Close();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var editingText = Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox;
        if (editingText) return;

        var viewModel = (MainViewModel)DataContext;
        var modifiers = Keyboard.Modifiers;
        if (!viewModel.PersonalDataAvailable) return;
        System.Windows.Input.ICommand? command = null;
        if (e.Key == Key.Space && modifiers == ModifierKeys.None) command = viewModel.Workout.ToggleTimerCommand;
        else if (e.Key == Key.Z && modifiers == ModifierKeys.Control) command = viewModel.Workout.UpdatePreviousSetCommand;
        else if (e.Key == Key.R && modifiers == ModifierKeys.Control) command = viewModel.Dashboard.MarkRestCommand;
        else if (e.Key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) command = viewModel.Dashboard.MarkCardioCommand;
        else if (e.Key == Key.E && modifiers == ModifierKeys.Control) command = viewModel.Workout.EndEarlyCommand;
        else if (e.Key == Key.Escape && modifiers == ModifierKeys.None)
        {
            viewModel.SelectedTabIndex = 0;
            e.Handled = true;
            return;
        }

        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}

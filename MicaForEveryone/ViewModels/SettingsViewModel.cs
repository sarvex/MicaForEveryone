﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using MicaForEveryone.Core.Interfaces;
using MicaForEveryone.Core.Models;
using MicaForEveryone.Core.Ui.Interfaces;
using MicaForEveryone.Core.Ui.Models;
using MicaForEveryone.Core.Ui.ViewModels;
using MicaForEveryone.Interfaces;
using MicaForEveryone.Views;
using MicaForEveryone.Win32;
using MicaForEveryone.Win32.PInvoke;
using IGeneralSettingsViewModel = MicaForEveryone.Interfaces.IGeneralSettingsViewModel;
using ISettingsViewModel = MicaForEveryone.Interfaces.ISettingsViewModel;

#nullable enable

namespace MicaForEveryone.ViewModels
{
    internal class SettingsViewModel : ObservableObject, ISettingsViewModel
    {
        private readonly ISettingsService _settingsService;
        private readonly ISettingsContainer _settingsContainer;
        private readonly GeneralPaneItem _generalPane;

        private SettingsWindow? _window;
        private IPaneItem? _selectedPane;
        private IRule? _newRule;

        public SettingsViewModel(ISettingsService settingsService, ISettingsContainer settingsContainer)
        {
            _settingsService = settingsService;
            _settingsService.RuleAdded += SettingsService_RuleAdded;
            _settingsService.RuleRemoved += SettingsService_RuleRemoved;
            _settingsService.RuleChanged += SettingsService_RuleChanged;
            _settingsService.ConfigFileReloaded += SettingsService_ConfigReloaded;

            _settingsContainer = settingsContainer;

            var vmGeneralPane = Program.CurrentApp.Container.GetService<IGeneralSettingsViewModel>();
            _generalPane = new GeneralPaneItem(vmGeneralPane);

            CloseCommand = new RelayCommand(DoClose);
            AddProcessRuleCommand = new RelayCommand(DoAddProcessRule);
            AddClassRuleCommand = new RelayCommand(DoAddClassRule);
            RemoveRuleAsyncCommand = new AsyncRelayCommand(DoRemoveRuleAsync, CanRemoveRule);

            if (Application.IsPackaged)
            {
                var version = Package.Current.Id.Version;
                Version = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            else
            {
                Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "<unknown>";
            }
        }

        ~SettingsViewModel()
        {
            _settingsService.RuleAdded -= SettingsService_RuleAdded;
            _settingsService.RuleRemoved -= SettingsService_RuleRemoved;
            _settingsService.RuleChanged -= SettingsService_RuleChanged;
            _settingsService.ConfigFileReloaded -= SettingsService_ConfigReloaded;
        }

        // properties

        public bool IsBackdropSupported => DesktopWindowManager.IsBackdropTypeSupported;
        public bool IsMicaSupported => DesktopWindowManager.IsUndocumentedMicaSupported;
        public bool IsImmersiveDarkModeSupported => DesktopWindowManager.IsImmersiveDarkModeSupported;
        public bool IsCornerPreferenceSupported => DesktopWindowManager.IsCornerPreferenceSupported;

        public string Version { get; }

        public IList<BackdropType> BackdropTypes { get; } = new List<BackdropType>();
        public IList<TitlebarColorMode> TitlebarColorModes { get; } = new List<TitlebarColorMode>();
        public IList<CornerPreference> CornerPreferences { get; } = new List<CornerPreference>();

        public IList<IPaneItem> PaneItems { get; set; } = new ObservableCollection<IPaneItem>();

        public IPaneItem? SelectedPane
        {
            get => _selectedPane;
            set
            {
                SetProperty(ref _selectedPane, value);
                RemoveRuleAsyncCommand.NotifyCanExecuteChanged();
            }
        }

        public ICommand CloseCommand { get; }
        public ICommand AddProcessRuleCommand { get; }
        public ICommand AddClassRuleCommand { get; }
        public IAsyncRelayCommand RemoveRuleAsyncCommand { get; }

        // public methods

        public void Initialize(SettingsWindow sender)
        {
            _window = sender;

            // restore saved WindowPlacement
            var serialized = _settingsContainer.GetValue("WindowPlacement") as byte[];
            if (serialized != null)
            {
                using var stream = new MemoryStream(serialized);
                var serializer = new BinaryFormatter();
                var placement = (WINDOWPLACEMENT)serializer.Deserialize(stream);
                _window!.SetWindowPlacement(placement);
            }

            _window.Destroy += OnClose;

            if (_generalPane.ViewModel is IGeneralSettingsViewModel vmGeneralPane)
                vmGeneralPane.Initialize(sender);

            if (BackdropTypes.Count <= 0)
            {
                BackdropTypes.Add(BackdropType.Default);
                if (IsMicaSupported)
                {
                    BackdropTypes.Add(BackdropType.None);
                    BackdropTypes.Add(BackdropType.Mica);
                }
                if (IsBackdropSupported)
                {
                    BackdropTypes.Add(BackdropType.Acrylic);
                    BackdropTypes.Add(BackdropType.Tabbed);
                }
            }

            if (TitlebarColorModes.Count <= 0)
            {
                TitlebarColorModes.Add(TitlebarColorMode.Default);
                if (IsImmersiveDarkModeSupported)
                {
                    TitlebarColorModes.Add(TitlebarColorMode.System);
                    TitlebarColorModes.Add(TitlebarColorMode.Light);
                    TitlebarColorModes.Add(TitlebarColorMode.Dark);
                }
            }

            if (CornerPreferences.Count <= 0)
            {
                CornerPreferences.Add(CornerPreference.Default);
                if (IsCornerPreferenceSupported)
                {
                    CornerPreferences.Add(CornerPreference.Square);
                    CornerPreferences.Add(CornerPreference.Rounded);
                    CornerPreferences.Add(CornerPreference.RoundedSmall);
                }
            }

            PopulatePanes();
        }

        // helper

        private void PopulatePanes()
        {
            PaneItems.Add(_generalPane);
            SelectedPane = _generalPane;

            foreach (var rule in _settingsService.Rules)
            {
                var item = rule.GetPaneItem(this, Program.CurrentApp.Container.GetRequiredService<IRuleSettingsViewModel>());
                item.ViewModel.ParentViewModel = this;
                PaneItems.Add(item);
            }
        }

        // event handlers

        private void SettingsService_RuleAdded(object? sender, RulesChangeEventArgs args)
        {
            Program.CurrentApp.Dispatcher.Enqueue(() =>
            {
                var pane = args.Rule.GetPaneItem(this, Program.CurrentApp.Container.GetRequiredService<IRuleSettingsViewModel>());
                var lastPane = SelectedPane;

                PaneItems.Add(pane!);
                if (args.Rule == _newRule)
                {
                    SelectedPane = pane;
                    _newRule = null;
                }
            });
        }

        private void SettingsService_RuleRemoved(object? sender, RulesChangeEventArgs args)
        {
            Program.CurrentApp.Dispatcher.Enqueue(() =>
            {
                var pane = args.Rule.GetPaneItem(this, Program.CurrentApp.Container.GetRequiredService<IRuleSettingsViewModel>());
                var lastPane = SelectedPane;

                PaneItems.Remove(pane!);
                if (args.Rule == _newRule)
                {
                    SelectedPane = pane;
                    _newRule = null;
                }
            });
        }

        private void SettingsService_RuleChanged(object? sender, RulesChangeEventArgs args)
        {
            Program.CurrentApp.Dispatcher.Enqueue(() =>
            {
                var pane = args.Rule.GetPaneItem(this, Program.CurrentApp.Container.GetRequiredService<IRuleSettingsViewModel>());
                var lastPane = SelectedPane;

                var index = PaneItems.IndexOf(pane!);
                PaneItems.Insert(index, pane!);
                PaneItems.RemoveAt(index + 1);
                if (lastPane?.Equals(pane) ?? false)
                    SelectedPane = pane;
            });
        }

        private void SettingsService_ConfigReloaded(object? sender, EventArgs e)
        {
            Program.CurrentApp.Dispatcher.Enqueue(() =>
            {
                var lastPane = SelectedPane;

                SelectedPane = null;
                PaneItems.Clear();
                PopulatePanes();

                // return to last pane if it's still there
                lastPane = PaneItems.FirstOrDefault(item => item.Equals(lastPane));
                if (lastPane != null)
                    SelectedPane = lastPane;
            });
        }

        private void OnClose(object? sender, WndProcEventArgs args)
        {
            // save WindowPlacement when closing window
            if (_window!.Handle == IntPtr.Zero) return;
            var placement = _window.GetWindowPlacement();
            var serializer = new BinaryFormatter();
            using var stream = new MemoryStream();
            serializer.Serialize(stream, placement);
            var bytes = stream.ToArray();
            _settingsContainer.SetValue("WindowPlacement", bytes);
        }

        // commands 

        private void DoClose()
        {
            _window?.Close();
        }

        private void DoAddProcessRule()
        {
            var dialogService = Program.CurrentApp.Container.GetService<IDialogService>();

            AddProcessRuleDialog dialog = new();
            dialog.Destroy += (sender, args) =>
            {
                dialog.Dispose();
            };
            dialog.ViewModel.Submit += async (sender, args) =>
            {
                _newRule = new ProcessRule(dialog.ViewModel.ProcessName);
                await _settingsService.AddRuleAsync(_newRule);
            };

            dialogService?.ShowDialog(_window, dialog);
        }

        private void DoAddClassRule()
        {
            var dialogService = Program.CurrentApp.Container.GetService<IDialogService>();

            AddClassRuleDialog dialog = new();
            dialog.Destroy += (sender, args) =>
            {
                dialog.Dispose();
            };
            dialog.ViewModel.Submit += async (sender, args) =>
            {
                _newRule = new ClassRule(dialog.ViewModel.ClassName);
                await _settingsService.AddRuleAsync(_newRule);
            };

            dialogService?.ShowDialog(_window, dialog);
        }

        private async Task DoRemoveRuleAsync()
        {
            if (SelectedPane is RulePaneItem rulePane &&
                    rulePane.ViewModel is IRuleSettingsViewModel viewModel &&
                    viewModel.Rule != null)
            {
                SelectedPane = _generalPane;
                await _settingsService.RemoveRuleAsync(viewModel.Rule);
            }
        }

        private bool CanRemoveRule() => SelectedPane != null &&
            SelectedPane.ItemType is not (PaneItemType.General or PaneItemType.Global);
    }
}

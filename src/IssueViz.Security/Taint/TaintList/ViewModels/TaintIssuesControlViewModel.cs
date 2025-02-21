﻿/*
 * SonarLint for Visual Studio
 * Copyright (C) 2016-2022 SonarSource SA
 * mailto:info AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using SonarLint.VisualStudio.Core.Helpers;
using SonarLint.VisualStudio.Core.Telemetry;
using SonarLint.VisualStudio.Infrastructure.VS;
using SonarLint.VisualStudio.Infrastructure.VS.DocumentEvents;
using SonarLint.VisualStudio.IssueVisualization.Editor;
using SonarLint.VisualStudio.IssueVisualization.Helpers;
using SonarLint.VisualStudio.IssueVisualization.Models;
using SonarLint.VisualStudio.IssueVisualization.Security.IssuesStore;
using SonarLint.VisualStudio.IssueVisualization.Security.Taint.Models;
using SonarLint.VisualStudio.IssueVisualization.Selection;

namespace SonarLint.VisualStudio.IssueVisualization.Security.Taint.TaintList.ViewModels
{
    internal interface ITaintIssuesControlViewModel : INotifyPropertyChanged, IDisposable
    {
        ICommand NavigateCommand { get; }

        ICommand ShowInBrowserCommand { get; }

        ICommand ShowVisualizationPaneCommand { get; }

        ICommand ShowDocumentationCommand { get; }

        ICollectionView IssuesView { get; }

        ITaintIssueViewModel SelectedIssue { get; set; }

        bool HasServerIssues { get; }

        string WindowCaption { get; }

        AnalysisInformation AnalysisInformation { get; }
    }

    /// <summary>
    /// View model that surfaces the data in the TaintStore
    /// </summary>
    /// <remarks>Monitors the taint store for changes and updates the view model accordingly.</remarks>
    internal sealed class TaintIssuesControlViewModel : ITaintIssuesControlViewModel
    {
        private readonly IActiveDocumentTracker activeDocumentTracker;
        private readonly IShowInBrowserService showInBrowserService;
        private readonly ITelemetryManager telemetryManager;
        private readonly IIssueSelectionService selectionService;
        private readonly ITaintStore store;
        private readonly IMenuCommandService menuCommandService;
        private readonly object Lock = new object();
        private string activeDocumentFilePath;
        private string windowCaption;
        private ITaintIssueViewModel selectedIssue;

        private readonly ObservableCollection<ITaintIssueViewModel> unfilteredIssues;

        public ICollectionView IssuesView { get; }

        public ICommand NavigateCommand { get; private set; }

        public ICommand ShowInBrowserCommand { get; private set; }

        public ICommand ShowVisualizationPaneCommand { get; private set; }

        public ICommand ShowDocumentationCommand { get; }

        public bool HasServerIssues => unfilteredIssues.Any();

        public string WindowCaption
        {
            get => windowCaption;
            set
            {
                if (windowCaption != value)
                {
                    windowCaption = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public ITaintIssueViewModel SelectedIssue
        {
            get => selectedIssue;
            set
            {
                if (selectedIssue != value)
                {
                    selectedIssue = value;
                    selectionService.SelectedIssue = selectedIssue?.TaintIssueViz;
                }
            }
        }

        public AnalysisInformation AnalysisInformation { get; private set; }

        public TaintIssuesControlViewModel(ITaintStore store,
            ILocationNavigator locationNavigator,
            IActiveDocumentTracker activeDocumentTracker,
            IActiveDocumentLocator activeDocumentLocator,
            IShowInBrowserService showInBrowserService,
            ITelemetryManager telemetryManager,
            IIssueSelectionService selectionService,
            ICommand navigateToDocumentationCommand,
            IMenuCommandService menuCommandService)
        {
            unfilteredIssues = new ObservableCollection<ITaintIssueViewModel>();
            AllowMultiThreadedAccessToIssuesCollection();

            this.menuCommandService = menuCommandService;
            activeDocumentFilePath = activeDocumentLocator.FindActiveDocument()?.FilePath;
            this.activeDocumentTracker = activeDocumentTracker;
            activeDocumentTracker.ActiveDocumentChanged += ActiveDocumentTracker_OnDocumentFocused;

            this.showInBrowserService = showInBrowserService;
            this.telemetryManager = telemetryManager;

            this.selectionService = selectionService;
            this.selectionService.SelectedIssueChanged += SelectionService_SelectionChanged;

            this.store = store;
            this.store.IssuesChanged += Store_IssuesChanged;

            UpdateIssues();

            IssuesView = new ListCollectionView(unfilteredIssues);

            ShowDocumentationCommand = navigateToDocumentationCommand;

            SetCommands(locationNavigator);
            ApplyViewFilter(ActiveDocumentFilter);
            SetDefaultSortOrder();
            UpdateCaption();
        }

        private void ActiveDocumentTracker_OnDocumentFocused(object sender, ActiveDocumentChangedEventArgs e)
        {
            activeDocumentFilePath = e.ActiveTextDocument?.FilePath;
            ApplyViewFilter(ActiveDocumentFilter);
            UpdateCaption();
        }

        private void ApplyViewFilter(Predicate<object> filter) =>
            IssuesView.Filter = filter;

        private bool ActiveDocumentFilter(object viewModel)
        {
            if (string.IsNullOrEmpty(activeDocumentFilePath))
            {
                return false;
            }

            var allFilePaths = ((ITaintIssueViewModel) viewModel).TaintIssueViz.GetAllLocations()
                .Select(x => x.CurrentFilePath)
                .Where(x => !string.IsNullOrEmpty(x));

            return allFilePaths.Any(x=> PathHelper.IsMatchingPath(x, activeDocumentFilePath));
        }

        private void SetDefaultSortOrder() =>
            IssuesView.SortDescriptions.Add(
                new SortDescription("TaintIssueViz.Issue.CreationTimestamp", ListSortDirection.Descending));

        /// <summary>
        /// Allow the observable collection <see cref="unfilteredIssues"/> to be modified from a non-UI thread.
        /// </summary>
        private void AllowMultiThreadedAccessToIssuesCollection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            BindingOperations.EnableCollectionSynchronization(unfilteredIssues, Lock);
        }

        private void SetCommands(ILocationNavigator locationNavigator)
        {
            NavigateCommand = new DelegateCommand(
                parameter =>
                {
                    telemetryManager.TaintIssueInvestigatedLocally();

                    var selected = (ITaintIssueViewModel)parameter;
                    locationNavigator.TryNavigate(selected.TaintIssueViz);
                },
                parameter => parameter is ITaintIssueViewModel);

            ShowInBrowserCommand = new DelegateCommand(
                parameter =>
                {
                    telemetryManager.TaintIssueInvestigatedRemotely();

                    var selected = (ITaintIssueViewModel)parameter;
                    var taintIssue = (ITaintIssue)selected.TaintIssueViz.Issue;
                    showInBrowserService.ShowIssue(taintIssue.IssueKey);
                },
                parameter => parameter is ITaintIssueViewModel);

            ShowVisualizationPaneCommand = new DelegateCommand(
                parameter =>
                {
                    telemetryManager.TaintIssueInvestigatedLocally();
                    var commandId = new CommandID(IssueVisualization.Commands.Constants.CommandSetGuid, IssueVisualization.Commands.Constants.ViewToolWindowCommandId);

                    menuCommandService.GlobalInvoke(commandId);

                }, parameter => parameter is ITaintIssueViewModel); 
            
            
        }

        private void UpdateIssues()
        {
            unfilteredIssues.Clear();

            foreach (var issueViz in store.GetAll())
            {
                unfilteredIssues.Add(new TaintIssueViewModel(issueViz));
            }

            AnalysisInformation = store.GetAnalysisInformation();

            NotifyPropertyChanged(nameof(HasServerIssues));
            NotifyPropertyChanged(nameof(AnalysisInformation));
        }

        private void UpdateCaption()
        {
            RunOnUIThread.Run(() =>
            {
                // We'll show the default caption if:
                // * there are no underlying issues, or
                // * there is not an active document.
                // Otherwise, we'll add a suffix showing the number of issues in the active document.
                string suffix = null;

                if (unfilteredIssues.Count != 0 && activeDocumentFilePath != null)
                {
                    suffix = $" ({GetFilteredIssuesCount()})";
                }

                WindowCaption = Resources.TaintToolWindowCaption + suffix;

                // Must run on the UI thread.
                int GetFilteredIssuesCount() => IssuesView.OfType<object>().Count();
            });
        }

        private void Store_IssuesChanged(object sender, IssuesChangedEventArgs e)
        {
            UpdateIssues();
            UpdateCaption();
        }

        private void SelectionService_SelectionChanged(object sender, EventArgs e)
        {
            selectedIssue = unfilteredIssues.FirstOrDefault(x => x.TaintIssueViz == selectionService.SelectedIssue);
            NotifyPropertyChanged(nameof(SelectedIssue));
        }

        public void Dispose()
        {
            selectionService.SelectedIssueChanged -= SelectionService_SelectionChanged;
            store.IssuesChanged -= Store_IssuesChanged;
            activeDocumentTracker.ActiveDocumentChanged -= ActiveDocumentTracker_OnDocumentFocused;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

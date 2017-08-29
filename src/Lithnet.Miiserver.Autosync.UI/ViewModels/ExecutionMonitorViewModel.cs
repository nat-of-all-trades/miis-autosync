﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.ServiceModel;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lithnet.Common.Presentation;
using PropertyChanged;

namespace Lithnet.Miiserver.AutoSync.UI.ViewModels
{
    public class ExecutionMonitorViewModel : ViewModelBase<string>, IEventCallBack
    {
        private EventClient client;

        public ExecutionMonitorViewModel(string maName)
            : base(maName)
        {
            this.Commands.Add("Start", new DelegateCommand(t => this.Start(), u => this.CanStart()));
            this.Commands.Add("Stop", new DelegateCommand(t => this.Stop(), u => this.CanStop()));
            this.ManagementAgentName = maName;
            this.DetailMessages = new ObservableCollection<string>();
            this.RunHistory = new ObservableCollection<RunProfileResultViewModel>();
            this.SubscribeToStateChanges();
        }

        public BitmapImage StatusIcon
        {
            get
            {
                switch (this.DisplayState)
                {
                    case "Disabled":
                        return App.GetImageResource("Stop.png");

                    case "Idle":
                        return App.GetImageResource("Clock1.png");

                    case "Paused":
                        return App.GetImageResource("Pause.png");

                    case "Processing":
                    case "Running":
                        return App.GetImageResource("Run.png");

                    case "Pausing":
                    case "Resuming":
                    case "Starting":
                    case "Waiting":
                    case "Stopping":
                        return App.GetImageResource("Hourglass.png");

                    case "Stopped":
                        return App.GetImageResource("Stop.png");

                    default:
                        return null;
                }
            }
        }

        public bool Disabled { get; private set; }

        public string DisplayName => this.ManagementAgentName ?? "Unknown MA";

        public string ManagementAgentName { get; set; }

        public string ExecutionQueue { get; private set; }

        public string Message { get; private set; }

        public string ExecutingRunProfile { get; private set; }

        [AlsoNotifyFor(nameof(LastRun), nameof(DisplayIcon))]
        public string LastRunProfileResult { get; private set; }

        [AlsoNotifyFor(nameof(LastRun), nameof(DisplayIcon))]
        public string LastRunProfileName { get; private set; }

        public string LastRun => this.LastRunProfileName == null ? null : $"{this.LastRunProfileName}: {this.LastRunProfileResult}";

        public string DisplayState { get; private set; }

        public ControlState ControlState { get; private set; }

        public ObservableCollection<string> DetailMessages { get; private set; }

        public ObservableCollection<RunProfileResultViewModel> RunHistory { get; private set; }


        public void MAStatusChanged(MAStatus status)
        {
            this.Message = status.Message;
            this.ExecutingRunProfile = status.ExecutingRunProfile;
            this.ExecutionQueue = status.ExecutionQueue;
            this.DisplayState = status.DisplayState;
            this.ControlState = status.ControlState;
            this.Disabled = this.ControlState == ControlState.Disabled;
            this.AddDetailMessage(status.Detail);
        }

        public new BitmapImage DisplayIcon => this.lastRunResult?.DisplayIcon;
        
        public void RunProfileExecutionComplete(string runProfileName, string result)
        {
            this.AddRunProfileHistory(runProfileName, result);
        }

        private string lastDetail;

        private void AddDetailMessage(string message)
        {
            if (this.lastDetail == message)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            this.lastDetail = message;

            Application.Current.Dispatcher.Invoke(() =>
            {

                lock (this.DetailMessages)
                {
                    while (this.DetailMessages.Count >= 100)
                    {
                        this.DetailMessages.RemoveAt(this.DetailMessages.Count - 1);
                    }

                    this.DetailMessages.Insert(0, $"{DateTime.Now}: {message}");
                }
            });
        }

        private RunProfileResultViewModel lastRunResult;

        private void AddRunProfileHistory(string runProfileName, string runProfileResult)
        {
            if (string.IsNullOrWhiteSpace(runProfileName))
            {
                return;
            }

            RunProfileResultViewModel t = new RunProfileResultViewModel
            {
                RunProfileName = runProfileName,
                Result = runProfileResult
            };

            this.lastRunResult = t;
            this.LastRunProfileName = t.RunProfileName;
            this.LastRunProfileResult = t.Result;

            Application.Current.Dispatcher.Invoke(() =>
            {
                lock (this.RunHistory)
                {
                    while (this.RunHistory.Count >= 100)
                    {
                        this.RunHistory.RemoveAt(this.RunHistory.Count - 1);
                    }

                    this.RunHistory.Insert(0, t);
                }
            });
        }


        private void Stop()
        {
            try
            {
                ConfigClient c = new ConfigClient();
                c.InvokeThenClose(x => x.Stop(this.ManagementAgentName));
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
                MessageBox.Show($"Could not stop the management agent\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool CanStop()
        {
            return this.ControlState == ControlState.Running;
        }

        private void Start()
        {
            try
            {
                ConfigClient c = new ConfigClient();
                c.InvokeThenClose(x => x.Start(this.ManagementAgentName));
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
                MessageBox.Show($"Could not start the management agent\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool CanStart()
        {
            return this.ControlState == ControlState.Stopped;
        }

        private void SubscribeToStateChanges()
        {
            InstanceContext i = new InstanceContext(this);
            this.client = new EventClient(i);
            this.client.Register(this.ManagementAgentName);
            this.client.InnerChannel.Closed += this.InnerChannel_Closed;
            this.client.InnerChannel.Faulted += this.InnerChannel_Faulted;

            MAStatus status = this.client.GetFullUpdate(this.ManagementAgentName);
            if (status != null)
            {
                this.MAStatusChanged(status);
            }
        }
        private void InnerChannel_Faulted(object sender, EventArgs e)
        {
            Trace.WriteLine($"Closing faulted event channel for {this.ManagementAgentName}");
            this.client.Abort();
        }

        private void InnerChannel_Closed(object sender, EventArgs e)
        {
            Trace.WriteLine($"Closing event channel for {this.ManagementAgentName}");
            this.CleanupAndRestartClient();
        }

        private void CleanupAndRestartClient()
        {
            this.client.InnerChannel.Closed -= this.InnerChannel_Closed;
            this.client.InnerChannel.Faulted -= this.InnerChannel_Faulted;
            this.SubscribeToStateChanges();
        }

    }
}
﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using Lithnet.Miiserver.Client;
using Lithnet.Logging;
using System.Net.Mail;

namespace Lithnet.Miiserver.AutoSync
{
    using System.Text;

    public class MAExecutor
    {
        protected static object GlobalStaggeredExecutionLock;
        protected static ManualResetEvent GlobalExclusiveOperationLock;
        protected static object GlobalSynchronizationStepLock;
        protected static List<WaitHandle> AllMaLocalOperationLocks;

        public static event SyncCompleteEventHandler SyncComplete;
        public delegate void SyncCompleteEventHandler(object sender, SyncCompleteEventArgs e);
        private ManagementAgent ma;
        private BlockingCollection<ExecutionParameters> pendingActions;
        private ManualResetEvent localOperationLock;
        private System.Timers.Timer importCheckTimer;
        private System.Timers.Timer unmanagedChangesCheckTimer;
        private TimeSpan importInterval;

        private Dictionary<string, string> perProfileLastRunStatus;

        public MAConfigParameters Configuration { get; }

        public string ExecutingRunProfile { get; private set; }

        private List<IMAExecutionTrigger> ExecutionTriggers { get; }

        private MAController controller;

        private CancellationTokenSource token;

        private Task internalTask;

        static MAExecutor()
        {
            MAExecutor.GlobalSynchronizationStepLock = new object();
            MAExecutor.GlobalStaggeredExecutionLock = new object();
            MAExecutor.GlobalExclusiveOperationLock = new ManualResetEvent(true);
            MAExecutor.AllMaLocalOperationLocks = new List<WaitHandle>();
        }

        public MAExecutor(ManagementAgent ma, MAConfigParameters profiles)
        {
            this.ma = ma;
            this.pendingActions = new BlockingCollection<ExecutionParameters>();
            this.perProfileLastRunStatus = new Dictionary<string, string>();
            this.ExecutionTriggers = new List<IMAExecutionTrigger>();
            this.Configuration = profiles;
            this.token = new CancellationTokenSource();
            this.controller = new MAController(ma);
            this.localOperationLock = new ManualResetEvent(true);
            MAExecutor.AllMaLocalOperationLocks.Add(this.localOperationLock);
            MAExecutor.SyncComplete += this.MAExecutor_SyncComplete;
            this.SetupImportSchedule();
            this.SetupUnmanagedChangesCheckTimer();
        }

        private void SetupUnmanagedChangesCheckTimer()
        {
            this.unmanagedChangesCheckTimer = new System.Timers.Timer();
            this.unmanagedChangesCheckTimer.Elapsed += this.UnmanagedChangesCheckTimer_Elapsed;
            this.unmanagedChangesCheckTimer.AutoReset = true;
            this.unmanagedChangesCheckTimer.Interval = Global.RandomizeOffset(Settings.UnmanagedChangesCheckInterval.TotalMilliseconds);
            this.unmanagedChangesCheckTimer.Start();
        }

        private void UnmanagedChangesCheckTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            this.CheckAndQueueUnmanagedChanges();
        }

        private void SetupImportSchedule()
        {
            if (this.Configuration.AutoImportScheduling != AutoImportScheduling.Disabled)
            {
                if (this.Configuration.AutoImportScheduling == AutoImportScheduling.Enabled ||
                    (this.ma.ImportAttributeFlows.Select(t => t.ImportFlows).Count() >= this.ma.ExportAttributeFlows.Select(t => t.ExportFlows).Count()))
                {
                    this.importCheckTimer = new System.Timers.Timer();
                    this.importCheckTimer.Elapsed += this.ImportCheckTimer_Elapsed;
                    int importSeconds = this.Configuration.AutoImportIntervalMinutes > 0 ? this.Configuration.AutoImportIntervalMinutes * 60 : MAExecutionTriggerDiscovery.GetTriggerInterval(this.ma);
                    this.importInterval = new TimeSpan(0, 0, Global.RandomizeOffset(importSeconds));
                    this.importCheckTimer.Interval = this.importInterval.TotalMilliseconds;
                    this.importCheckTimer.AutoReset = true;
                    Logger.WriteLine("{0}: Starting import interval timer. Imports will be queued if they have not been run for {1}", this.ma.Name, this.importInterval);
                    this.importCheckTimer.Start();
                }
                else
                {
                    Logger.WriteLine("{0}: Import schedule not enabled", this.ma.Name);
                }
            }
            else
            {
                Logger.WriteLine("{0}: Import schedule disabled", this.ma.Name);
            }
        }

        private void ImportCheckTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            this.AddPendingActionIfNotQueued(new ExecutionParameters(this.Configuration.ScheduledImportRunProfileName), "Import timer");
        }

        private void ResetImportTimerOnImport()
        {
            if (this.importCheckTimer != null)
            {
                Logger.WriteLine("{0}: Resetting import timer for {1}", this.ma.Name, this.importInterval);
                this.importCheckTimer.Stop();
                this.importCheckTimer.Start();
            }
        }

        public void AttachTrigger(params IMAExecutionTrigger[] triggers)
        {
            if (triggers == null)
            {
                throw new ArgumentNullException(nameof(triggers));
            }

            foreach (IMAExecutionTrigger trigger in triggers)
            {
                this.ExecutionTriggers.Add(trigger);
            }
        }

        private void StartTriggers()
        {
            foreach (IMAExecutionTrigger t in this.ExecutionTriggers)
            {
                try
                {
                    Logger.WriteLine("{0}: Registering execution trigger '{1}'", this.ma.Name, t.Name);
                    t.TriggerExecution += this.notifier_TriggerExecution;
                    t.Start();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("{0}: Could not start execution trigger {1}", this.ma.Name, t.Name);
                    Logger.WriteException(ex);
                }
            }
        }

        private void QueueFollowupActions(RunDetails d)
        {
            this.QueueFollowUpActionsExport(d);
            this.QueueFollowUpActionsImport(d);
            this.QueueFollowUpActionsSync(d);
        }

        private void QueueFollowUpActionsExport(RunDetails d)
        {
            if (this.CanConfirmExport())
            {
                if (MAExecutor.HasUnconfirmedExports(d))
                {
                    this.AddPendingActionIfNotQueued(new ExecutionParameters(this.Configuration.ConfirmingImportRunProfileName), d.RunProfileName);
                }
            }
        }

        private void QueueFollowUpActionsImport(RunDetails d)
        {
            if (MAExecutor.HasStagedImports(d))
            {
                this.AddPendingActionIfNotQueued(new ExecutionParameters(this.Configuration.DeltaSyncRunProfileName), d.RunProfileName);
            }
        }

        private void QueueFollowUpActionsSync(RunDetails d)
        {
            SyncCompleteEventHandler registeredHandlers = MAExecutor.SyncComplete;

            if (registeredHandlers == null)
            {
                return;
            }

            foreach (StepDetails s in d.StepDetails)
            {
                if (!s.StepDefinition.IsSyncStep)
                {
                    continue;
                }

                foreach (OutboundFlowCounters item in s.OutboundFlowCounters)
                {
                    if (!item.HasChanges)
                    {
                        continue;
                    }

                    SyncCompleteEventArgs args = new SyncCompleteEventArgs
                    {
                        SendingMAName = this.ma.Name,
                        TargetMA = item.MAID
                    };

                    registeredHandlers(this, args);
                }
            }
        }

        private void Execute(ExecutionParameters e)
        {
            try
            {
                if (this.token.IsCancellationRequested)
                {
                    return;
                }

                // Wait here if any exclusive operations are pending or in progress
                MAExecutor.GlobalExclusiveOperationLock.WaitOne();

                if (this.token.IsCancellationRequested)
                {
                    return;
                }

                if (!this.controller.ShouldExecute(e.RunProfileName))
                {
                    Logger.WriteLine("{0}: Controller indicated that run profile {1} should not be executed", this.ma.Name, e.RunProfileName);
                    return;
                }

                this.WaitOnUnmanagedRun();

                if (this.token.IsCancellationRequested)
                {
                    return;
                }

                if (e.Exclusive)
                {
                    Logger.WriteLine("{0}: Entering exclusive mode for {1}", this.ma.Name, e.RunProfileName);

                    // Signal all executors to wait before running their next job
                    MAExecutor.GlobalExclusiveOperationLock.Reset();

                    if (this.token.IsCancellationRequested)
                    {
                        return;
                    }

                    // Wait for all  MAs to finish their current job
                    Logger.WriteLine("{0}: Waiting for running tasks to complete", this.ma.Name);
                    WaitHandle.WaitAll(MAExecutor.AllMaLocalOperationLocks.ToArray());

                    if (this.token.IsCancellationRequested)
                    {
                        return;
                    }
                }

                // If another operation in this executor is already running, then wait for it to finish
                this.localOperationLock.WaitOne();

                if (this.token.IsCancellationRequested)
                {
                    return;
                }

                // Signal the local lock that an event is running
                this.localOperationLock.Reset();

                if (this.token.IsCancellationRequested)
                {
                    return;
                }
                // Grab the staggered execution lock, and hold for x seconds
                // This ensures that no MA can start within x seconds of another MA
                // to avoid deadlock conditions
                lock (MAExecutor.GlobalStaggeredExecutionLock)
                {
                    if (this.token.IsCancellationRequested)
                    {
                        return;
                    }

                    Thread.Sleep(Settings.ExecutionStaggerInterval);
                }

                if (this.token.IsCancellationRequested)
                {
                    return;
                }

                if (this.ma.RunProfiles[e.RunProfileName].RunSteps.Any(t => t.IsImportStep))
                {
                    this.ResetImportTimerOnImport();
                }

                if (this.token.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    Logger.WriteLine("{0}: Executing {1}", this.ma.Name, e.RunProfileName);
                    string result = this.ma.ExecuteRunProfile(e.RunProfileName, this.token.Token);

                    if (this.token.IsCancellationRequested)
                    {
                        return;
                    }

                    Logger.WriteLine("{0}: {1} returned {2}", this.ma.Name, e.RunProfileName, result);
                    Thread.Sleep(new TimeSpan(0, 0, 3));
                }
                catch (MAExecutionException ex)
                {
                    Logger.WriteLine("{0}: {1} returned {2}", this.ma.Name, e.RunProfileName, ex.Result);
                }

                if (this.token.IsCancellationRequested)
                {
                    return;
                }

                using (RunDetails r = this.ma.GetLastRun())
                {
                    this.PerformPostRunActions(r);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (System.Management.Automation.RuntimeException ex)
            {
                if (this.token.IsCancellationRequested)
                {
                    return;
                }

                UnexpectedChangeException changeException = ex.InnerException as UnexpectedChangeException;

                if (changeException != null)
                {
                    this.ProcessUnexpectedChangeException(changeException);
                }
                else
                {
                    Logger.WriteLine("{0}: Executor encountered an error executing run profile {1}", this.ma.Name, this.ExecutingRunProfile);
                    Logger.WriteException(ex);
                }
            }
            catch (UnexpectedChangeException ex)
            {
                if (this.token.IsCancellationRequested)
                {
                    return;
                }

                this.ProcessUnexpectedChangeException(ex);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("{0}: Executor encountered an error executing run profile {1}", this.ma.Name, this.ExecutingRunProfile);
                Logger.WriteException(ex);
            }
            finally
            {
                // Reset the local lock so the next operation can run
                this.localOperationLock.Set();

                if (e.Exclusive)
                {
                    // Reset the global lock so pending operations can run
                    MAExecutor.GlobalExclusiveOperationLock.Set();
                }
            }
        }

        private void WaitOnUnmanagedRun()
        {
            if (!this.ma.IsIdle())
            {
                this.localOperationLock.Reset();

                try
                {
                    Logger.WriteLine("{0}: Waiting on unmanaged run {1} to finish", this.ma.Name, this.ma.ExecutingRunProfileName);

                    if (this.ma.RunProfiles[this.ma.ExecutingRunProfileName].RunSteps.Any(t => t.IsSyncStep))
                    {
                        Logger.WriteLine("{0}: Getting exclusive sync lock for unmanaged run", this.ma.Name);

                        lock (MAExecutor.GlobalSynchronizationStepLock)
                        {
                            if (this.token.IsCancellationRequested)
                            {
                                return;
                            }

                            this.ma.Wait(this.token.Token);
                        }
                    }
                    else
                    {
                        this.ma.Wait(this.token.Token);
                    }

                    if (this.token.IsCancellationRequested)
                    {
                        return;
                    }

                    using (RunDetails ur = this.ma.GetLastRun())
                    {
                        this.PerformPostRunActions(ur);
                    }
                }
                finally
                {
                    this.localOperationLock.Set();
                }
            }
        }

        private void PerformPostRunActions(RunDetails r)
        {
            this.TrySendMail(r);
            this.controller.ExecutionComplete(r);
            this.QueueFollowupActions(r);
        }

        private void ProcessUnexpectedChangeException(UnexpectedChangeException ex)
        {
            if (ex.ShouldTerminateService)
            {
                Logger.WriteLine("{0}: Controller indicated that service should immediately stop. Run profile {1}", this.ma.Name, this.ExecutingRunProfile);
                if (AutoSyncService.ServiceInstance == null)
                {
                    Environment.Exit(1);
                }
                else
                {
                    AutoSyncService.ServiceInstance.Stop();
                }
            }
            else
            {
                Logger.WriteLine("{0}: Controller indicated that management agent executor should stop further processing on this MA. Run Profile {1}", this.ma.Name, this.ExecutingRunProfile);
                this.Stop();
            }
        }

        public Task Start()
        {
            if (this.Configuration.Disabled)
            {
                throw new Exception("Cannot start executor as it is disabled");
            }

            Logger.WriteSeparatorLine('-');

            Logger.WriteLine("{0}: Starting executor", this.ma.Name);

            Logger.WriteRaw($"{this}\n");

            Logger.WriteSeparatorLine('-');

            this.internalTask = new Task(() =>
            {
                try
                {
                    this.Init();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("{0}: The MAExecutor encountered a unrecoverable error", this.ma.Name);
                    Logger.WriteLine(ex.Message);
                    Logger.WriteLine(ex.StackTrace);
                }
            }, this.token.Token);

            this.internalTask.Start();

            return this.internalTask;
        }

        public void Stop()
        {
            Logger.WriteLine("{0}: Stopping MAExecutor", this.ma.Name);
            this.token?.Cancel();

            this.importCheckTimer?.Stop();

            foreach (IMAExecutionTrigger x in this.ExecutionTriggers)
            {
                x.Stop();
            }

            Logger.WriteLine("{0}: Stopped execution triggers", this.ma.Name);

            if (this.internalTask != null && !this.internalTask.IsCompleted)
            {
                Logger.WriteLine("{0}: Waiting for cancellation to complete", this.ma.Name);
                this.internalTask.Wait();
                Logger.WriteLine("{0}: Cancellation completed", this.ma.Name);
            }

            this.internalTask = null;
        }

        private void Init()
        {
            if (!this.ma.IsIdle())
            {
                try
                {
                    this.localOperationLock.Reset();
                    Logger.WriteLine("{0}: Waiting for current job to finish", this.ma.Name);
                    this.ma.Wait(this.token.Token);
                }
                finally
                {
                    this.localOperationLock.Set();
                }
            }

            this.token.Token.ThrowIfCancellationRequested();

            this.CheckAndQueueUnmanagedChanges();

            this.token.Token.ThrowIfCancellationRequested();

            this.StartTriggers();

            try
            {
                foreach (ExecutionParameters action in this.pendingActions.GetConsumingEnumerable(this.token.Token))
                {
                    this.token.Token.ThrowIfCancellationRequested();

                    try
                    {
                        this.ExecutingRunProfile = action.RunProfileName;

                        if (this.ma.RunProfiles[action.RunProfileName].RunSteps.Any(t => t.IsSyncStep))
                        {
                            lock (MAExecutor.GlobalSynchronizationStepLock)
                            {
                                this.Execute(action);
                            }
                        }
                        else
                        {
                            this.Execute(action);
                        }
                    }
                    finally
                    {
                        this.ExecutingRunProfile = null;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void CheckAndQueueUnmanagedChanges()
        {
            this.localOperationLock.WaitOne();

            try
            {
                // If another operation in this executor is already running, then wait for it to finish
                this.localOperationLock.WaitOne();

                // Signal the local lock that an event is running
                this.localOperationLock.Reset();

                if (this.ShouldExport())
                {
                    this.AddPendingActionIfNotQueued(new ExecutionParameters(this.Configuration.ExportRunProfileName), "Pending export check");
                }

                if (this.ShouldConfirmExport())
                {
                    this.AddPendingActionIfNotQueued(new ExecutionParameters(this.Configuration.ConfirmingImportRunProfileName), "Unconfirmed export check");
                }

                if (this.ma.HasPendingImports())
                {
                    this.AddPendingActionIfNotQueued(new ExecutionParameters(this.Configuration.DeltaSyncRunProfileName), "Staged import check");
                }
            }
            finally
            {
                // Reset the local lock so the next operation can run
                this.localOperationLock.Set();
            }
        }

        private void MAExecutor_SyncComplete(object sender, SyncCompleteEventArgs e)
        {
            if (e.TargetMA != this.ma.ID)
            {
                return;
            }

            if (!this.ShouldExport())
            {
                return;
            }

            ExecutionParameters p = new ExecutionParameters(this.Configuration.ExportRunProfileName);
            this.AddPendingActionIfNotQueued(p, "Synchronization on " + e.SendingMAName);
        }

        private void notifier_TriggerExecution(object sender, ExecutionTriggerEventArgs e)
        {
            IMAExecutionTrigger trigger = (IMAExecutionTrigger)sender;

            if (string.IsNullOrWhiteSpace(e.Parameters.RunProfileName))
            {
                if (e.Parameters.RunProfileType == MARunProfileType.None)
                {
                    Logger.WriteLine("{0}: Received empty run profile from trigger {1}", this.ma.Name, trigger.Name);
                    return;
                }
            }

            this.AddPendingActionIfNotQueued(e.Parameters, trigger.Name);
        }

        private void AddPendingActionIfNotQueued(ExecutionParameters p, string source)
        {
            if (string.IsNullOrWhiteSpace(p.RunProfileName))
            {
                if (p.RunProfileType == MARunProfileType.None)
                {
                    return;
                }

                p.RunProfileName = this.Configuration.GetRunProfileName(p.RunProfileType);
            }

            if (this.pendingActions.Contains(p))
            {
                return;
            }

            if (p.RunProfileName.Equals(this.ExecutingRunProfile, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            this.pendingActions.Add(p);
            Logger.WriteLine("{0}: Queuing {1} (triggered by: {2})", this.ma.Name, p.RunProfileName, source);
            Logger.WriteLine("{0}: Current queue {1}", this.ma.Name, string.Join(",", this.pendingActions.Select(t => t.RunProfileName)));
        }

        private static bool HasUnconfirmedExports(RunDetails d)
        {
            if (d.StepDetails == null)
            {
                return false;
            }

            foreach (StepDetails s in d.StepDetails)
            {
                if (s.StepDefinition.IsImportStep)
                {
                    // If an import is present, before an export step, a confirming import is not required
                    return false;
                }

                if (s.StepDefinition.Type == RunStepType.Export)
                {
                    // If we get here, an export step has been found that it more recent than any import step
                    // that may be in the run profile
                    return s.ExportCounters?.HasChanges ?? false;
                }
            }

            return false;
        }

        private static bool HasStagedImports(RunDetails d)
        {
            if (d.StepDetails == null)
            {
                return false;
            }

            foreach (StepDetails s in d.StepDetails)
            {
                if (s.StepDefinition.IsSyncStep)
                {
                    // If a sync is present, before an import step, a sync is not required
                    return false;
                }

                if (s.StepDefinition.IsImportStep)
                {
                    // If we get here, an import step has been found that it more recent than any sync step
                    // that may be in the run profile
                    return s.StagingCounters?.HasChanges ?? false;
                }
            }

            return false;
        }

        private static bool HasUnconfirmedExports(StepDetails s)
        {
            return s?.ExportCounters?.HasChanges ?? false;
        }

        private bool HasUnconfirmedExportsInLastRun()
        {
            return MAExecutor.HasUnconfirmedExports(this.ma.GetLastRun()?.StepDetails?.FirstOrDefault());
        }

        private bool ShouldExport()
        {
            return this.CanExport() && this.ma.HasPendingExports();
        }

        private bool CanExport()
        {
            return !string.IsNullOrWhiteSpace(this.Configuration.ExportRunProfileName);
        }

        private bool CanConfirmExport()
        {
            return !string.IsNullOrWhiteSpace(this.Configuration.ConfirmingImportRunProfileName);
        }

        private bool ShouldConfirmExport()
        {
            return this.CanConfirmExport() && this.HasUnconfirmedExportsInLastRun();
        }

        private void TrySendMail(RunDetails r)
        {
            try
            {
                this.SendMail(r);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("{0}: Send mail failed", this.ma.Name);
                Logger.WriteException(ex);
            }
        }

        private void SendMail(RunDetails r)
        {
            if (this.perProfileLastRunStatus.ContainsKey(r.RunProfileName))
            {
                if (this.perProfileLastRunStatus[r.RunProfileName] == r.LastStepStatus)
                {
                    if (Settings.MailSendOncePerStateChange)
                    {
                        // The last run returned the same return code. Do not send again.
                        return;
                    }
                }
                else
                {
                    this.perProfileLastRunStatus[r.RunProfileName] = r.LastStepStatus;
                }
            }
            else
            {
                this.perProfileLastRunStatus.Add(r.RunProfileName, r.LastStepStatus);
            }

            if (!MAExecutor.ShouldSendMail(r))
            {
                return;
            }

            MessageSender.SendMessage($"{r.MAName} {r.RunProfileName}: {r.LastStepStatus}", MessageBuilder.GetMessageBody(r));
        }

        private static bool ShouldSendMail(RunDetails r)
        {
            if (!MAExecutor.CanSendMail())
            {
                return false;
            }

            return Settings.MailIgnoreReturnCodes == null || !Settings.MailIgnoreReturnCodes.Contains(r.LastStepStatus, StringComparer.OrdinalIgnoreCase);
        }

        private static bool CanSendMail()
        {
            if (!Settings.MailEnabled)
            {
                return false;
            }

            if (!Settings.UseAppConfigMailSettings)
            {
                if (Settings.MailFrom == null || Settings.MailTo == null || Settings.MailServer == null)
                {
                    return false;
                }
            }
            else
            {
                if (Settings.MailTo == null)
                {
                    return false;
                }
            }

            return true;
        }

        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();

            builder.AppendLine("--- Configuration ---");
            builder.AppendLine(this.Configuration.ToString());

            if (this.importCheckTimer?.Interval > 0 || this.Configuration.AutoImportScheduling != AutoImportScheduling.Disabled)
            {
                builder.AppendLine("--- Schedules ---");

                if (this.importCheckTimer?.Interval > 0)
                {
                    builder.AppendLine($"Maximum allowed interval between imports: {new TimeSpan(0, 0, 0, 0, (int)this.importCheckTimer.Interval)}");
                }

                if (this.Configuration.AutoImportScheduling != AutoImportScheduling.Disabled && this.Configuration.AutoImportIntervalMinutes > 0)
                {
                    builder.AppendLine($"Scheduled import interval: {new TimeSpan(0, this.Configuration.AutoImportIntervalMinutes, 0)}");
                }
            }

            builder.AppendLine();

            builder.AppendLine("--- Triggers ---");

            foreach (IMAExecutionTrigger trigger in this.ExecutionTriggers)
            {
                Logger.WriteLine(trigger.ToString());
            }

            return builder.ToString();
        }
    }
}
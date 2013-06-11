﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.WindowsAzure.Diagnostics;
using Microsoft.WindowsAzure.ServiceRuntime;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;
using NuGetGallery.Operations.Infrastructure;

namespace NuGetGallery.Worker
{
    public class WorkerRole : RoleEntryPoint
    {
        private JobRunner _runner;

        public WorkerRole() : this(null) { }

        public WorkerRole(Settings settings)
        {
            // Configure NLog
            LoggingConfiguration config = new LoggingConfiguration();

            // Console Target
            var consoleTarget = new SnazzyConsoleTarget();
            config.AddTarget("console", consoleTarget);
            consoleTarget.Layout = "[${logger:shortName=true}] ${message}";

            // Get the logs resource
            string logDir = Path.Combine(Environment.CurrentDirectory, "Logs");

            try
            {
                if (RoleEnvironment.IsAvailable)
                {
                    LocalResource logsResource = RoleEnvironment.GetLocalResource("Logs");
                    logDir = logsResource.RootPath;
                }
            }
            catch (Exception)
            {
                // Just use basedir.
            }

            // File Target
            FileTarget jobLogTarget = new FileTarget()
            {
                FileName = Path.Combine(logDir, "Jobs", "${logger:shortName=true}.log.json"),
                ArchiveFileName = Path.Combine(logDir, "Jobs", "${logger:shortName=true}.${date:YYYY-MM-dd}.log")
            };
            ConfigureFileTarget(jobLogTarget);
            config.AddTarget("file", jobLogTarget);
            FileTarget hostTarget = new FileTarget()
            {
                FileName = Path.Combine(logDir, "Host", "Host.log.json"),
                ArchiveFileName = Path.Combine(logDir, "Host", "Host.${date:YYYY-MM-dd}.log")
            };
            ConfigureFileTarget(hostTarget);
            config.AddTarget("file", hostTarget);

            LoggingRule allMessagesToConsole = new LoggingRule("*", NLog.LogLevel.Trace, consoleTarget);
            config.LoggingRules.Add(allMessagesToConsole);

            LoggingRule hostToFile = new LoggingRule("JobRunner", NLog.LogLevel.Trace, hostTarget);
            config.LoggingRules.Add(hostToFile);

            LoggingRule roleToFile = new LoggingRule("WorkerRole", NLog.LogLevel.Trace, hostTarget);
            config.LoggingRules.Add(roleToFile);

            LoggingRule jobLogs = new LoggingRule("Job.*", NLog.LogLevel.Trace, jobLogTarget);
            config.LoggingRules.Add(jobLogs);

            LogManager.Configuration = config;

            var log = LogManager.GetLogger("WorkerRole");
            log.Info("Logging Enabled to {0}", logDir);

            try
            {
                if (RoleEnvironment.IsAvailable)
                {
                    ConfigureAzureDiagnostics(logDir, log);
                }
                else
                {
                    log.Info("Skipping Azure Diagnostics, we aren't in Azure");
                }
            }
            catch (Exception ex)
            {
                log.InfoException("Skipping Azure Diagnostics, we got an exception trying to check if we are in Azure", ex);
            }

            _runner = LoadJobRunner(settings);
        }

        private void ConfigureAzureDiagnostics(string logDir, Logger log)
        {
            var config = DiagnosticMonitor.GetDefaultInitialConfiguration();
            config.ConfigurationChangePollInterval = TimeSpan.FromMinutes(5);

            config.DiagnosticInfrastructureLogs.ScheduledTransferLogLevelFilter = Microsoft.WindowsAzure.Diagnostics.LogLevel.Verbose;
            config.DiagnosticInfrastructureLogs.ScheduledTransferPeriod = TimeSpan.FromMinutes(1);
            config.DiagnosticInfrastructureLogs.BufferQuotaInMB = 100;

            var dumps = config.Directories.DataSources.Single(dir => String.Equals(dir.Container, "wad-crash-dumps"));
            config.Directories.BufferQuotaInMB = 2048;
            config.Directories.DataSources.Clear();
            config.Directories.DataSources.Add(dumps);
            config.Directories.DataSources.Add(new DirectoryConfiguration()
            {
                Container = "wad-joblogs",
                Path = Path.Combine(logDir, "Jobs"),
                DirectoryQuotaInMB = 100
            });
            config.Directories.DataSources.Add(new DirectoryConfiguration()
            {
                Container = "wad-worker",
                Path = Path.Combine(logDir, "Infrastructure"),
                DirectoryQuotaInMB = 100
            });
            config.Directories.ScheduledTransferPeriod = TimeSpan.FromMinutes(1);

            config.WindowsEventLog.DataSources.Add("Application");
            config.WindowsEventLog.BufferQuotaInMB = 100;

            log.Info("Enabling Azure Diagnostics");
            DiagnosticMonitor.Start("Microsoft.WindowsAzure.Plugins.Diagnostics.ConnectionString", config);
            log.Info("Enabled Azure Diagnostics");
        }

        private static void ConfigureFileTarget(FileTarget hostTarget)
        {
            hostTarget.FileAttributes = Win32FileAttributes.WriteThrough;
            hostTarget.Layout = "{ " +
                "index: ${counter}, " +
                "threadId: ${threadid}, " +
                "callSite: '${callsite}', " +
                "date: '${date:format=s}', " +
                "level: '${level}', " +
                "message: '${message:jsonEncode=true}', " +
                "exception: { " +
                "type: '${exception:format=Type}', " + 
                "message: '${exception:format=Message}', " +
                "method: '${exception:format=Method}, " +
                "stackTrace: '${exception:format=StackTrace}' " +
                "} " +
            "}";
            hostTarget.LineEnding = LineEndingMode.CRLF;
            hostTarget.Encoding = Encoding.UTF8;
            hostTarget.CreateDirs = true;
            hostTarget.ArchiveEvery = FileArchivePeriod.Day;
            hostTarget.ArchiveNumbering = ArchiveNumberingMode.Sequence;
            hostTarget.ConcurrentWrites = false;
        }

        public override bool OnStart()
        {
            return _runner.OnStart();
        }

        public override void OnStop()
        {
            _runner.OnStop();
        }

        public override void Run()
        {
            _runner.Run();
        }

        public void RunSingleJob(string jobName)
        {
            _runner.RunSingleJob(jobName);
        }

        public void RunSingleJobContinuously(string jobName)
        {
            _runner.RunSingleJobContinuously(jobName);
        }

        public void Stop()
        {
            _runner.Stop();
        }

        public static IEnumerable<string> GetJobList()
        {
            JobRunner runner = LoadJobRunner(new Settings());
            return runner.Jobs.Keys;
        }

        public static void Execute(string jobName, bool continuous, IDictionary<string, string> overrideSettings)
        {
            // Create the settings manager
            var settings = new Settings(overrideSettings);
            var worker = new WorkerRole(settings);
            
            // See which mode we're in
            if (String.IsNullOrWhiteSpace(jobName))
            {
                // Run ALL THE JOBS!
                worker.OnStart();
                worker.Run();
                Console.WriteLine("Worker is running. Press ENTER to stop");
                Console.ReadLine();
                worker.Stop();
                worker.OnStop();
            }
            else
            {
                // Run JUST ONE JOB!
                if (!continuous)
                {
                    worker.RunSingleJob(jobName);
                }
                else
                {
                    worker.RunSingleJobContinuously(jobName);
                }
            }
        }

        private static JobRunner LoadJobRunner(Settings settings)
        {
            AssemblyCatalog catalog = new AssemblyCatalog(typeof(WorkerRole).Assembly);
            var container = new CompositionContainer(catalog);

            // Load settings
            settings = settings ?? new Settings();
            container.ComposeExportedValue(settings);

            // Get the job runner
            return container.GetExportedValue<JobRunner>();
        }
    }
}
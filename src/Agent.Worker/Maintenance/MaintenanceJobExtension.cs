using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Threading.Tasks;
using System.IO;
using System.Threading;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Maintenance
{
    public interface IMaintenanceServiceProvider : IExtension
    {
        string MaintenanceDescription { get; }
        void RunMaintenanceOperation(IExecutionContext context);
    }

    public sealed class MaintenanceJobExtension : AgentService, IJobExtension
    {
        public Type ExtensionType => typeof(IJobExtension);
        public string HostType => "maintenance";
        public IStep PrepareStep { get; private set; }
        public IStep FinallyStep { get; private set; }

        public MaintenanceJobExtension()
        {
            PrepareStep = new JobExtensionRunner(
                runAsync: PrepareAsync,
                alwaysRun: false,
                continueOnError: false,
                critical: true,
                displayName: "Run Maintenance Job",
                enabled: true,
                @finally: false);

            FinallyStep = new JobExtensionRunner(
                runAsync: FinallyAsync,
                alwaysRun: false,
                continueOnError: false,
                critical: false,
                displayName: "Report Agent Status",
                enabled: true,
                @finally: true);
        }

        public string GetRootedPath(IExecutionContext context, string path)
        {
            return path;
        }

        public void ConvertLocalPath(IExecutionContext context, string localPath, out string repoName, out string sourcePath)
        {
            sourcePath = localPath;
            repoName = string.Empty;
        }

        private Task PrepareAsync()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(PrepareStep, nameof(PrepareStep));
            ArgUtil.NotNull(PrepareStep.ExecutionContext, nameof(PrepareStep.ExecutionContext));
            IExecutionContext executionContext = PrepareStep.ExecutionContext;
            var extensionManager = HostContext.GetService<IExtensionManager>();
            var maintenanceServiceProviders = extensionManager.GetExtensions<IMaintenanceServiceProvider>();
            if (maintenanceServiceProviders != null && maintenanceServiceProviders.Count > 0)
            {
                foreach (var maintenanceProvider in maintenanceServiceProviders)
                {
                    // all maintenance operations should be best effort.
                    executionContext.Section($"Start maintenance service: {maintenanceProvider.MaintenanceDescription}");
                    try
                    {
                        maintenanceProvider.RunMaintenanceOperation(executionContext);
                    }
                    catch (Exception ex)
                    {
                        executionContext.Error(ex);
                    }

                    executionContext.Section($"Finish maintenance service: {maintenanceProvider.MaintenanceDescription}");
                }
            }

            return Task.CompletedTask;
        }

        private async Task FinallyAsync()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(FinallyStep, nameof(FinallyStep));
            ArgUtil.NotNull(FinallyStep.ExecutionContext, nameof(FinallyStep.ExecutionContext));
            IExecutionContext executionContext = FinallyStep.ExecutionContext;

            string workDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
            string pathRoot = Path.GetPathRoot(workDirectory);

            if (!Directory.Exists(workDirectory))
            {
                executionContext.Output($"'{workDirectory}' does not exist.");
                return;
            }

            DriveInfo driveInfo = new DriveInfo(pathRoot);
            executionContext.Output($"AvailableFreeSpace: {driveInfo.AvailableFreeSpace}");

            long workDirectorySize = await CalculateDirectorySizeAsync(executionContext, workDirectory);
            executionContext.Output($"'{workDirectory}': {workDirectorySize}");
        }

        private async Task<long> CalculateDirectorySizeAsync(IExecutionContext context, string directoryPath)
        {
            long directorySize = 0;
            fileProcessCounter = 0;
            DirectoryInfo workDirectoryInfo = new DirectoryInfo(directoryPath);
            CancellationTokenSource progressReportCancellationToken = new CancellationTokenSource();
            Task progressReportTask = FileScanReportAsync(context, progressReportCancellationToken.Token);

            foreach (var fileInfo in workDirectoryInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                Interlocked.Increment(ref fileProcessCounter);
                directorySize += fileInfo.Length;
            }

            progressReportCancellationToken.Cancel();
            await progressReportTask;
            return directorySize;
        }

        private async Task FileScanReportAsync(IExecutionContext context, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                context.Output($"Scaned {fileProcessCounter}");
                try
                {
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Trace.Error(ex);
                    }
                }
            }
        }

        private int fileProcessCounter = 0;
    }
}
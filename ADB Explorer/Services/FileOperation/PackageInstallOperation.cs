﻿using ADB_Explorer.Controls;
using ADB_Explorer.Helpers;
using ADB_Explorer.Models;

namespace ADB_Explorer.Services;

public class PackageInstallOperation : FileOperation
{
    private Task operationTask;
    private CancellationTokenSource cancelTokenSource;
    private readonly ObservableList<Package> packageList;
    private bool pushPackage;
    private bool isPackagePath;

    private string packageName;
    public string PackageName
    {
        get => packageName;
        set
        {
            if (Set(ref packageName, value))
            {
                OnPropertyChanged(nameof(IsUninstall));
                OnPropertyChanged(nameof(Tooltip));
                OnPropertyChanged(nameof(OpIcon));
            }
        }
    }

    public bool IsUninstall => !string.IsNullOrEmpty(PackageName);

    public override string Tooltip => IsUninstall ? "Uninstall" : "Install";

    public override object OpIcon => IsUninstall ? new UninstallIcon() : new InstallIcon();

    public PackageInstallOperation(Dispatcher dispatcher,
                                   ADBService.AdbDevice adbDevice,
                                   FilePath path = null,
                                   string packageName = null,
                                   ObservableList<Package> packageList = null,
                                   bool pushPackage = false,
                                   bool isPackagePath = false) : base(dispatcher, adbDevice, path)
    {
        OperationName = OperationType.Install;
        PackageName = packageName;
        this.packageList = packageList;
        this.pushPackage = pushPackage;
        this.isPackagePath = isPackagePath;
    }

    public override void Start()
    {
        if (Status == OperationStatus.InProgress)
        {
            throw new Exception("Cannot start an already active operation!");
        }

        Status = OperationStatus.InProgress;
        cancelTokenSource = new CancellationTokenSource();
        var args = new string[1];
        int index = 0;

        if (IsUninstall)
        {
            args = new string[2];
            args[0] = "uninstall";
            args[1] = PackageName;
            index = 1;
        }
        // install (pm / adb)
        else
        {
            if (!pushPackage)
            {
                args = new string[4];
                args[0] = "install";
                args[1] = "-r";
                args[2] = "-d";
                index = 3;
            }
            args[index] = FilePath.FullPath;
        }

        args[index] = ADBService.EscapeAdbShellString(args[index]);
        operationTask = Task.Run(() =>
        {
            return pushPackage
                ? ADBService.ExecuteDeviceAdbCommand(Device.ID, "install", out _, out _, args)
                : ADBService.ExecuteDeviceAdbShellCommand(Device.ID, "pm", out _, out _, args);
        });

        operationTask.ContinueWith((t) =>
        {
            var operationStatus = ((Task<int>)t).Result == 0 ? OperationStatus.Completed : OperationStatus.Failed;
            Status = operationStatus;
            StatusInfo = null;

            if (operationStatus is OperationStatus.Completed)
            {
                Dispatcher.Invoke(() =>
                {
                    if (packageList is not null)
                        packageList.RemoveAll(pkg => pkg.Name == packageName);
                    else if (pushPackage && isPackagePath)
                        Data.FileActions.RefreshPackages = true;
                });
            }
        }, TaskContinuationOptions.OnlyOnRanToCompletion);

        operationTask.ContinueWith((t) =>
        {
            Status = OperationStatus.Canceled;
            StatusInfo = null;
        }, TaskContinuationOptions.OnlyOnCanceled);

        operationTask.ContinueWith((t) =>
        {
            Status = OperationStatus.Failed;
            StatusInfo = t.Exception.InnerException.Message;
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public override void Cancel()
    {
        if (Status != OperationStatus.InProgress)
        {
            throw new Exception("Cannot cancel a deactivated operation!");
        }

        cancelTokenSource.Cancel();
    }
}

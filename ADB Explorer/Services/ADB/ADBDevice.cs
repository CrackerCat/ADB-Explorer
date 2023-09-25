﻿using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;
using static ADB_Explorer.Models.AbstractFile;

namespace ADB_Explorer.Services;

public partial class ADBService
{
    private const string GET_PROP = "getprop";
    private const string ANDROID_VERSION = "ro.build.version.release";
    private const string BATTERY = "dumpsys battery";
    private const string MMC_PROP = "vold.microsd.uuid";
    private const string OTG_PROP = "vold.otgstorage.uuid";

    /// <summary>
    /// First partition of MMC block device 0 / 1
    /// </summary>
    private static readonly string[] MMC_BLOCK_DEVICES = { "/dev/block/mmcblk0p1", "/dev/block/mmcblk1p1" };

    private static readonly string[] EMULATED_DRIVES_GREP = { "|", "grep", "-E", "'/mnt/media_rw/|/storage/'" };

    public class AdbDevice : Device
    {
        public AdbDevice(DeviceViewModel other)
        {
            ID = other.ID;
        }

        private const string CURRENT_DIR = ".";
        private const string PARENT_DIR = "..";
        private static readonly string[] SPECIAL_DIRS = { CURRENT_DIR, PARENT_DIR };
        private static readonly char[] LINE_SEPARATORS = { '\n', '\r' };

        private enum UnixFileMode : UInt32
        {
            S_IFMT = 0b1111 << 12, // bit mask for the file type bit fields
            S_IFSOCK = 0b1100 << 12, // socket
            S_IFLNK = 0b1010 << 12, // symbolic link
            S_IFREG = 0b1000 << 12, // regular file
            S_IFBLK = 0b0110 << 12, // block device
            S_IFDIR = 0b0100 << 12, // directory
            S_IFCHR = 0b0010 << 12, // character device
            S_IFIFO = 0b0001 << 12  // FIFO
        }

        public void ListDirectory(string path, ref ConcurrentQueue<FileStat> output, CancellationToken cancellationToken)
        {
            // Execute adb ls to get file list
            var stdout = ExecuteDeviceAdbCommandAsync(ID, "ls", cancellationToken, EscapeAdbString(path));
            foreach (string stdoutLine in stdout)
            {
                var match = AdbRegEx.RE_LS_FILE_ENTRY.Match(stdoutLine);
                if (!match.Success)
                {
                    throw new Exception($"Invalid output for adb ls command: {stdoutLine}");
                }

                var name = match.Groups["Name"].Value;
                var size = UInt64.Parse(match.Groups["Size"].Value, System.Globalization.NumberStyles.HexNumber);
                var time = long.Parse(match.Groups["Time"].Value, System.Globalization.NumberStyles.HexNumber);
                var mode = UInt32.Parse(match.Groups["Mode"].Value, System.Globalization.NumberStyles.HexNumber);

                if (SPECIAL_DIRS.Contains(name))
                {
                    continue;
                }

                output.Enqueue(new FileStat
                (
                    fileName: name,
                    path: FileHelper.ConcatPaths(path, name),
                    type: (UnixFileMode)(mode & (UInt32)UnixFileMode.S_IFMT) switch
                    {
                        UnixFileMode.S_IFSOCK => FileType.Socket,
                        UnixFileMode.S_IFLNK => FileType.Unknown,
                        UnixFileMode.S_IFREG => FileType.File,
                        UnixFileMode.S_IFBLK => FileType.BlockDevice,
                        UnixFileMode.S_IFDIR => FileType.Folder,
                        UnixFileMode.S_IFCHR => FileType.CharDevice,
                        UnixFileMode.S_IFIFO => FileType.FIFO,
                        (UnixFileMode)0 => FileType.Unknown,
                        _ => throw new Exception($"Unexpected file type for \"{name}\" with mode: {mode}")
                    },
                    size: (mode != 0) ? size : new UInt64?(),
                    modifiedTime: (time > 0) ? DateTimeOffset.FromUnixTimeSeconds(time).DateTime.ToLocalTime() : new DateTime?(),
                    isLink: (mode & (UInt32)UnixFileMode.S_IFMT) == (UInt32)UnixFileMode.S_IFLNK
                ));
            }
        }

        public AdbSyncStatsInfo PullFile(
            string targetPath,
            string sourcePath,
            ref ObservableList<FileOpProgressInfo> progressUpdates,
            CancellationToken cancellationToken) =>
            DoFileSync("pull", "-a", targetPath, sourcePath, ref progressUpdates, cancellationToken);

        public AdbSyncStatsInfo PushFile(
            string targetPath,
            string sourcePath,
            ref ObservableList<FileOpProgressInfo> progressUpdates,
            CancellationToken cancellationToken) =>
            DoFileSync("push", "", targetPath, sourcePath, ref progressUpdates, cancellationToken);

        private AdbSyncStatsInfo DoFileSync(
            string opertation,
            string operationArgs,
            string targetPath,
            string sourcePath,
            ref ObservableList<FileOpProgressInfo> progressUpdates,
            CancellationToken cancellationToken)
        {
            // Execute adb file sync operation
            var stdout = ExecuteCommandAsync(
                Data.ProgressRedirectionPath,
                ADB_PATH,
                Encoding.Unicode,
                cancellationToken,
                "-s",
                ID,
                opertation,
                operationArgs,
                EscapeAdbString(sourcePath),
                EscapeAdbString(targetPath));
            
            // Each line should be a progress update (but sometimes the output can be weird)
            string lastStdoutLine = null;
            foreach (string stdoutLine in stdout)
            {
                lastStdoutLine = stdoutLine;
                if (string.IsNullOrWhiteSpace(lastStdoutLine))
                    continue;

                var progressMatch = AdbRegEx.RE_FILE_SYNC_PROGRESS.Match(stdoutLine);
                if (progressMatch.Success)
                    progressUpdates.Add(new AdbSyncProgressInfo(progressMatch));
                else
                {
                    var errorMatch = AdbRegEx.RE_FILE_SYNC_ERROR.Match(stdoutLine);
                    if (errorMatch.Success)
                    {
                        progressUpdates.Add(new SyncErrorInfo(errorMatch));
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(lastStdoutLine))
                return null;

            var match = AdbRegEx.RE_FILE_SYNC_STATS.Match(lastStdoutLine);
            if (!match.Success)
                return null;

            return new AdbSyncStatsInfo(match);
        }

        public string TranslateDevicePath(string path)
        {
            if (path.StartsWith('~'))
                path = path.Length == 1 ? "/" : path[1..];

            if (path.StartsWith("//"))
                path = path[1..];

            int exitCode = ExecuteDeviceAdbShellCommand(ID, "cd", out string stdout, out string stderr, EscapeAdbShellString(path), "&&", "pwd");
            if (exitCode != 0)
            {
                throw new Exception(stderr);
            }
            return stdout.TrimEnd(LINE_SEPARATORS);
        }

        public List<LogicalDrive> GetDrives()
        {
            List<LogicalDrive> drives = new();

            var root = ReadDrives(AdbRegEx.RE_EMULATED_STORAGE_SINGLE, "/");
            if (root is null)
                return null;
            else if (root.Any())
                drives.Add(root.First());

            var intStorage = ReadDrives(AdbRegEx.RE_EMULATED_STORAGE_SINGLE, "/sdcard");
            if (intStorage is null)
                return drives;
            else if (intStorage.Any())
                drives.Add(intStorage.First());

            var extStorage = ReadDrives(AdbRegEx.RE_EMULATED_ONLY, EMULATED_DRIVES_GREP);
            if (extStorage is null)
                return drives;
            else
            {
                Func<LogicalDrive, bool> predicate = drives.Any(drive => drive.Type is AbstractDrive.DriveType.Internal)
                    ? d => d.Type is not AbstractDrive.DriveType.Internal or AbstractDrive.DriveType.Root
                    : d => d.Type is not AbstractDrive.DriveType.Root;
                drives.AddRange(extStorage.Where(predicate));
            }

            if (!drives.Any(d => d.Type == AbstractDrive.DriveType.Internal))
            {
                drives.Insert(0, new(path: AdbExplorerConst.DEFAULT_PATH));
            }

            if (!drives.Any(d => d.Type == AbstractDrive.DriveType.Root))
            {
                drives.Insert(0, new(path: "/"));
            }

            return drives;
        }

        private IEnumerable<LogicalDrive> ReadDrives(Regex re, params string[] args)
        {
            int exitCode = ExecuteDeviceAdbShellCommand(ID, "df", out string stdout, out string stderr, args);
            if (exitCode != 0)
                return null;

            return re.Matches(stdout).Select(m => new LogicalDrive(m.Groups, isEmulator: Type is DeviceType.Emulator, forcePath: args[0] == "/" ? "/" : ""));
        }

        private Dictionary<string, string> props;
        public Dictionary<string, string> Props
        {
            get
            {
                if (props is null)
                {
                    int exitCode = ExecuteDeviceAdbShellCommand(ID, GET_PROP, out string stdout, out string stderr);
                    if (exitCode == 0)
                    {
                        props = stdout.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Where(
                            l => l[0] == '[' && l[^1] == ']').TryToDictionary(
                                line => line.Split(':')[0].Trim('[', ']', ' '),
                                line => line.Split(':')[1].Trim('[', ']', ' '));
                    }
                    else
                        props = new Dictionary<string, string>();

                }

                return props;
            }
        }

        public string MmcProp => Props.ContainsKey(MMC_PROP) ? Props[MMC_PROP] : null;
        public string OtgProp => Props.ContainsKey(OTG_PROP) ? Props[OTG_PROP] : null;

        public Task<string> GetAndroidVersion() => Task.Run(() =>
        {
            if (Props.ContainsKey(ANDROID_VERSION))
                return Props[ANDROID_VERSION];
            else
                return "";
        });

        public static Dictionary<string, string> GetBatteryInfo(LogicalDevice device)
        {
            if (ExecuteDeviceAdbShellCommand(device.ID, BATTERY, out string stdout, out string stderr) == 0)
            {
                return stdout.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Where(l => l.Contains(':')).ToDictionary(
                    line => line.Split(':')[0].Trim(),
                    line => line.Split(':')[1].Trim());
            }
            return null;
        }

        public static void Reboot(string deviceId, string arg)
        {
            if (ExecuteDeviceAdbCommand(deviceId, "reboot", out string stdout, out string stderr, arg) != 0)
                throw new Exception(string.IsNullOrEmpty(stderr) ? stdout : stderr);
        }

        public static bool GetDeviceIp(DeviceViewModel device)
        {
            if (ExecuteDeviceAdbShellCommand(device.ID, "ip", out string stdout, out _, new[] { "-f", "inet", "addr", "show", "wlan0" }) != 0)
                return false;

            var match = AdbRegEx.RE_DEVICE_WLAN_INET.Match(stdout);
            if (!match.Success)
                return false;

            device.SetIpAddress(match.Groups["IP"].Value);

            return true;
        }
    }
}

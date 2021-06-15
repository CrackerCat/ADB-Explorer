﻿using ADB_Explorer.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ADB_Explorer.Core.Services
{
    public class ADBService
    {
        private const string ADB_PATH = "adb";
        private const string PRODUCT_MODEL = "ro.product.model";
        private const string HOST_NAME = "net.hostname";
        private const string VENDOR = "ro.vendor.config.CID";
        private const string GET_PROP = "getprop";
        private static readonly char[] LINE_SEPARATORS = { '\n', '\r' };
        private const string LS_FILE_ENTRY_PATTERN = @"^(?<Mode>[0-9a-f]+) (?<Size>[0-9a-f]+) (?<Time>[0-9a-f]+) (?<Name>[^/]+?)$";

        enum UnixFileMode : UInt32
        {
            S_IFMT =   0b1111 << 12, // bit mask for the file type bit fields
            S_IFSOCK = 0b1100 << 12, // socket
            S_IFLNK =  0b1010 << 12, // symbolic link
            S_IFREG =  0b1000 << 12, // regular file
            S_IFBLK =  0b0110 << 12, // block device
            S_IFDIR =  0b0100 << 12, // directory
            S_IFCHR =  0b0010 << 12, // character device
            S_IFIFO =  0b0001 << 12  // FIFO
        }

        private static void InitProcess(Process cmdProcess)
        {
            cmdProcess.StartInfo.UseShellExecute = false;
            cmdProcess.StartInfo.RedirectStandardOutput = true;
            cmdProcess.StartInfo.RedirectStandardError = true;
            cmdProcess.StartInfo.CreateNoWindow = true;
        }

        public static int ExecuteAdbCommand(string cmd, out string stdout, out string stderr, params string[] args)
        {
            using var cmdProcess = new Process();
            InitProcess(cmdProcess);
            cmdProcess.StartInfo.FileName = ADB_PATH;
            cmdProcess.StartInfo.Arguments = string.Join(' ', new[] { cmd }.Concat(args));
            cmdProcess.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            cmdProcess.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
            cmdProcess.Start();

            using var stdoutTask = cmdProcess.StandardOutput.ReadToEndAsync();
            using var stderrTask = cmdProcess.StandardError.ReadToEndAsync();
            Task.WaitAll(stdoutTask, stderrTask);
            cmdProcess.WaitForExit();

            stdout = stdoutTask.Result;
            stderr = stderrTask.Result;
            return cmdProcess.ExitCode;
        }

        public static int ExecuteShellCommand(string cmd, out string stdout, out string stderr, params string[] args)
        {
            return ExecuteAdbCommand("shell", out stdout, out stderr, new[] { cmd }.Concat(args).ToArray());
        }

        public static string EscapeShellString(string str)
        {
            return string.Concat(str.Select(c =>
                c switch
                {
                    var ch when new[] { '(', ')', '<', '>', '|' ,';', '&', '*', '\\', '~', '"', '\'', ' ' }.Contains(ch) => "\\" + ch,
                    _ => new string(c, 1)
                }));
        }

        public static List<FileStat> ListDirectory(string path)
        {
            // Remove trailing '/' from given path to avoid possible issues
            path = path.TrimEnd('/');

            // Execute adb ls to get file list
            string stdout, stderr;
            int exitCode = ExecuteAdbCommand("ls", out stdout, out stderr, $"\"{EscapeShellString(path)}/\"");
            if (exitCode != 0)
            {
                throw new Exception($"{stderr} (Error Code: {exitCode})");
            }

            // Parse stdout into natural values
            var fileEntries = Regex.Matches(stdout, LS_FILE_ENTRY_PATTERN, RegexOptions.IgnoreCase | RegexOptions.Multiline).Select(match => new
            {
                Name = match.Groups["Name"].Value.TrimEnd('\r'),
                Size = UInt64.Parse(match.Groups["Size"].Value, System.Globalization.NumberStyles.HexNumber),
                Time = long.Parse(match.Groups["Time"].Value, System.Globalization.NumberStyles.HexNumber),
                Mode = UInt32.Parse(match.Groups["Mode"].Value, System.Globalization.NumberStyles.HexNumber)
            });

            // Convert parse results to FileStats
            var fileStats = fileEntries.Select(entry => new FileStat
            {
                Name = entry.Name,
                Path = path + '/' + entry.Name,
                Type = (entry.Mode & (UInt32)UnixFileMode.S_IFMT) switch
                {
                    (UInt32)UnixFileMode.S_IFDIR => FileStat.FileType.Folder,
                    (UInt32)UnixFileMode.S_IFREG => FileStat.FileType.File,
                    (UInt32)UnixFileMode.S_IFLNK => FileStat.FileType.Folder,
                    _ => throw new Exception($"Cannot handle file: {entry.Name}, mode: {entry.Mode}")
                },
                Size = entry.Size,
                ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(entry.Time).DateTime
            });

            // Add parent directory when needed
            if (path.LastIndexOf('/') is var index && index > 0)
            {
                fileStats = new[]
                {
                    new FileStat
                    {
                        Name = "..",
                        Path = path.Remove(index),
                        Type = FileStat.FileType.Parent
                    }
                }.Concat(fileStats);
            }

            return fileStats.ToList();
        }

        public static string GetDeviceName()
        {
            var stdout = GetProps();
            var hostName = GetPropsValue(stdout, HOST_NAME);

            if (string.IsNullOrEmpty(hostName))
            {
                stdout = GetPropsValue(stdout, PRODUCT_MODEL).Replace('_', ' ');
            }
            else
            {
                var vendor = GetPropsValue(stdout, VENDOR);
                stdout = string.IsNullOrEmpty(stdout)
                    ? GetPropsValue(stdout, PRODUCT_MODEL)
                    : $"{vendor} {hostName}";
            }

            return stdout;
        }

        private static string GetProps()
        {
            ExecuteShellCommand(GET_PROP, out string stdout, out string stderr);
            return stdout;
        }

        private static string GetPropsValue(string props, string key)
        {
            var value = props.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Where(s => s.Contains(key));
            return value.Any() ? value.First().Split('[', ']')[3] : "";
        }
    }
}
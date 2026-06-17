#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MSTestX.Console
{
    internal static class devicectl
    {
        public static async Task<bool> IsDeviceCtlInstalled()
        {
            try
            {
                var version = await DeviceCtl("--version", CancellationToken.None);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static Task<DeviceCtl.DeviceDetails> GetDeviceDetails(string deviceId)
        {
            return DeviceCtl<DeviceCtl.DeviceDetails>($"device info details --device \"{deviceId}\"", CancellationToken.None);
        }

        public static Task<DeviceCtl.Devices> GetConnectedAppleDevicesAsync()
        {
            return DeviceCtl<DeviceCtl.Devices>("list devices --timeout 5", CancellationToken.None);
        }

        public static async Task<string> InstallApp(string deviceId, string appPath)
        {
            var output = await DeviceCtl($"device install app --device {deviceId} {appPath}", CancellationToken.None);
            var idLine = output.Split(Environment.NewLine).Where(s => s.Contains("bundleID: ")).FirstOrDefault();
            if(string.IsNullOrEmpty(idLine))
            {
                throw new Exception("Failed to obtain bundle ID from install");
            }
            var idx = idLine.LastIndexOf("bundleID: ") + 10;
            var bundleId = idLine.Substring(idx).Trim();
            return bundleId;
        }

        public static Task LaunchApp(string deviceId, string appId, string? arguments = null, string? stdOutputFile = null, CancellationToken token = default)
        {
            return DeviceCtl($"device process launch --device {deviceId} --terminate-existing --console {appId} {arguments}", token, stdOutputFile);
        }

        private static async Task<T> DeviceCtl<T>(string arguments, CancellationToken cancellationToken)
        {
            var tmpPath = System.IO.Path.GetTempFileName();
            await DeviceCtl($"{arguments} --json-output \"{tmpPath}\"", cancellationToken);
            var json = System.IO.File.ReadAllText(tmpPath);
            File.Delete(tmpPath);
            return System.Text.Json.JsonSerializer.Deserialize<T>(json)!;
        }

        private static Task<string> DeviceCtl(string arguments, CancellationToken cancellationToken, string? stdOutputFile = null)
        {
            TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
            Process xcrun = new Process();
            if (cancellationToken.CanBeCanceled)
                cancellationToken.Register(() => { tcs.TrySetCanceled(); xcrun.Close(); });
            xcrun.StartInfo = new ProcessStartInfo("xcrun", "devicectl " + arguments);
            xcrun.EnableRaisingEvents = true;
            xcrun.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            xcrun.StartInfo.UseShellExecute = false;
            xcrun.StartInfo.RedirectStandardError = true;
            xcrun.StartInfo.RedirectStandardOutput = true;
            StringBuilder sb = new StringBuilder();
            object outputLock = new object();
            PrepareOutputFile(stdOutputFile);
            
            xcrun.Exited += (s, e) =>
            {
                var output = SnapshotProcessOutput(sb, outputLock);
                if(xcrun.ExitCode > 0)
                    tcs.TrySetException(new Exception(output));
                else
                    tcs.TrySetResult(output);
            };
            xcrun.ErrorDataReceived += (s, e) =>
            {
                if(e.Data is null)
                    return;
                AppendProcessOutput(sb, outputLock, stdOutputFile, e.Data);
            };
            xcrun.OutputDataReceived += (s,e) =>
            {
                Debug.WriteLine(e.Data);
                AppendProcessOutput(sb, outputLock, stdOutputFile, e.Data);
            };
            xcrun.Start();
            xcrun.BeginErrorReadLine();
            xcrun.BeginOutputReadLine();

            return tcs.Task;
        }

        private static void PrepareOutputFile(string? outputFile)
        {
            if (string.IsNullOrWhiteSpace(outputFile))
                return;

            var directory = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(outputFile, "");
        }

        private static void AppendProcessOutput(
            StringBuilder output,
            object outputLock,
            string? outputFile,
            string? data)
        {
            if (data is null)
                return;

            lock (outputLock)
            {
                output.AppendLine(data);
                if (!string.IsNullOrWhiteSpace(outputFile))
                    File.AppendAllText(outputFile, data + Environment.NewLine);
            }
        }

        private static string SnapshotProcessOutput(StringBuilder output, object outputLock)
        {
            lock (outputLock)
            {
                return output.ToString();
            }
        }

    }
}

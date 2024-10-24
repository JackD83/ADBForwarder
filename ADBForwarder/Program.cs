using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;

using SharpAdbClient;
using ICSharpCode.SharpZipLib.Zip;
using SharpAdbClient.DeviceCommands;

namespace ADBForwarder
{
    internal class Program
    {
        public const string VERSION = "0.2";

        private static string[] deviceNames = new string[0];

        private static readonly AdbClient client = new AdbClient();
        private static readonly AdbServer server = new AdbServer();
        private static readonly IPEndPoint endPoint = new IPEndPoint(IPAddress.Loopback, AdbClient.AdbServerPort);

        private static void Main()
        {
            readDeviceNames();

            Console.ResetColor();
            var currentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (currentDirectory == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Path error!");
                return;
            }

            var adbPath = "adb/platform-tools/{0}";
            var downloadUri = "https://dl.google.com/android/repository/platform-tools-latest-{0}.zip";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Console.WriteLine("Platform: Linux");

                adbPath = string.Format(adbPath, "adb");
                downloadUri = string.Format(downloadUri, "linux");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine("Platform: Windows");

                adbPath = string.Format(adbPath, "adb.exe");
                downloadUri = string.Format(downloadUri, "windows");
            }
            else
            {
                Console.WriteLine("Unsupported platform!");
                return;
            }

            var absoluteAdbPath = Path.Combine(currentDirectory, adbPath);
            if (!File.Exists(absoluteAdbPath))
            {
                Console.WriteLine("ADB not found, downloading in the background...");
                DownloadADB(downloadUri);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    SetExecutable(absoluteAdbPath);
            }

            Console.WriteLine("Starting ADB Server...");
            server.StartServer(absoluteAdbPath, false);

            client.Connect(endPoint);

            var monitor = new DeviceMonitor(new AdbSocket(endPoint));
            monitor.DeviceConnected += Monitor_DeviceConnected;
            monitor.DeviceDisconnected += Monitor_DeviceDisconnected;
            monitor.Start();

            while (true)
            {
                // Main thread needs to stay alive, 100ms is acceptable idle time
                Thread.Sleep(100);
            }
        }

        private static void Monitor_DeviceConnected(object sender, DeviceDataEventArgs e)
        {
            Console.ForegroundColor = ConsoleColor.DarkGreen;
            Console.WriteLine($"Connected device: {e.Device.Serial}");
            Forward(e.Device);
        }

        private static void Monitor_DeviceDisconnected(object sender, DeviceDataEventArgs e)
        {
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine($"Disconnected device: {e.Device.Serial}");
        }

        private static void readDeviceNames()
        {
            string filePath = "devices.conf";

            if (!File.Exists(filePath))
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine("devices.conf not found");
                return;
            }


            deviceNames = File.ReadAllLines(filePath)
                                       .Where(line => !line.Trim().StartsWith("//") && !string.IsNullOrWhiteSpace(line))
                                       .Select(line => line.Split(new[] { "//" }, StringSplitOptions.None)[0].Trim())
                                       .ToArray();
        }

        private static void Forward(DeviceData device)
        {
            // DeviceConnected calls without product set yet
            Thread.Sleep(1000);

            foreach (var deviceData in client.GetDevices().Where(deviceData => device.Serial == deviceData.Serial))
            {


                if (!deviceNames.Contains(deviceData.Product))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Skipped forwarding device: {(string.IsNullOrEmpty(deviceData.Product) ? deviceData.Serial : deviceData.Product)}");
                    return;
                }

                var receiver = new ConsoleOutputReceiver();


                if (File.Exists("../alvr_client_android.apk"))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("Trying to install latest ALVR Client");

                    try
                    {
                        client.ExecuteShellCommand(deviceData, "am force-stop alvr.client", receiver);
                        client.ExecuteShellCommand(deviceData, "pm clear alvr.client", receiver);
                    }

                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("Failed clear ALVR Client state");
                        return;
                    }


                    try
                    {
                        PackageManager manager = new PackageManager(client, deviceData);
                        manager.InstallPackage(@"alvr_client_android.apk", reinstall: true);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("Successfully Updated ALVR Client");

                        client.ExecuteShellCommand(deviceData, " pm grant alvr.client android.permission.RECORD_AUDIO", receiver);


                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("Failed to install ALVR Client");
                        return;
                    }
                }


                client.CreateForward(deviceData, 9943, 9943);
                client.CreateForward(deviceData, 9944, 9944);


                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Successfully forwarded device: {deviceData.Serial} [{deviceData.Product}]");

                try
                {
                    System.Threading.Thread.Sleep(1000);
                    client.ExecuteShellCommand(deviceData, "monkey -p 'alvr.client' 1", receiver);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Failed to run ALVR Client");
                    return;
                }


                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"USB Streaming to device {deviceData.Serial} ready!");

                return;
            }

           
        }

        private static void DownloadADB(string downloadUri)
        {
            using var web = new WebClient();
            web.DownloadFile(downloadUri, "adb.zip");
            Console.WriteLine("Download successful");

            var zip = new FastZip();
            zip.ExtractZip("adb.zip", "adb", null);
            Console.WriteLine("Extraction successful");

            File.Delete("adb.zip");
        }

        private static void SetExecutable(string fileName)
        {
            Console.WriteLine("Giving adb executable permissions");

            var args = $"chmod u+x {fileName}";
            var escapedArgs = args.Replace("\"", "\\\"");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{escapedArgs}\""
                }
            };

            process.Start();
            process.WaitForExit();
        }
    }
}
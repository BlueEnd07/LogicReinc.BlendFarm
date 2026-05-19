using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LogicReinc.BlendFarm.Server
{
    public class SystemInfo
    {
        public const string OS_LINUX64 = "linux64";
        public const string OS_WINDOWS64 = "windows64";
        public const string OS_MACOS = "macOS";
        public const string OS_MACOSARM64 = "macOS-arm64";


        /// <summary>
        /// Ignore actual OS and override it with provided version.
        /// Mostly for testing
        /// </summary>
        public static string OverrideOS = null;

        /// <summary>
        /// Returns OS version in Blender formatted name
        /// </summary>
        /// <returns></returns>
        public static string GetOSName()
        {
            if (OverrideOS != null)
                return OverrideOS;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return OS_LINUX64;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return OS_WINDOWS64;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                return OS_MACOSARM64;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return OS_MACOS;
            else
                throw new NotImplementedException("Unknown OS");
        }

        public static string GetOSDescription()
        {
            return RuntimeInformation.OSDescription;
        }

        public static string GetArchitecture()
        {
            return RuntimeInformation.ProcessArchitecture.ToString();
        }

        public static string GetRuntimeDescription()
        {
            return RuntimeInformation.FrameworkDescription;
        }

        public static string GetProcessorName()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string registryProcessor = Microsoft.Win32.Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                        "ProcessorNameString",
                        null)?.ToString();
                    if (!string.IsNullOrWhiteSpace(registryProcessor))
                        return registryProcessor.Trim();

                    string wmicProcessor = ParseWmicValue(RunAndReadLines("wmic", "cpu get name /value"), "Name");
                    if (!string.IsNullOrWhiteSpace(wmicProcessor))
                        return wmicProcessor;

                    string processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
                    if (!string.IsNullOrWhiteSpace(processor))
                        return processor;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/proc/cpuinfo"))
                {
                    foreach (string line in File.ReadLines("/proc/cpuinfo"))
                    {
                        if (line.StartsWith("model name"))
                            return line.Split(':', 2)[1].Trim();
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    string brand = RunAndReadFirstLine("sysctl", "-n machdep.cpu.brand_string");
                    if (!string.IsNullOrWhiteSpace(brand))
                        return brand;
                }
            }
            catch { }

            return RuntimeInformation.ProcessArchitecture.ToString();
        }

        public static string GetGpuNames()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string[] names = ParseWmicValues(RunAndReadLines("wmic", "path win32_VideoController get name /value"), "Name");
                    string display = JoinDistinct(names);
                    if (!string.IsNullOrWhiteSpace(display))
                        return display;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    string display = JoinDistinct(RunAndReadLines("lspci", "")
                        .Where(line => line.IndexOf("VGA compatible controller", StringComparison.OrdinalIgnoreCase) >= 0
                            || line.IndexOf("3D controller", StringComparison.OrdinalIgnoreCase) >= 0
                            || line.IndexOf("Display controller", StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(line =>
                        {
                            int index = line.IndexOf(':');
                            return index >= 0 && index + 1 < line.Length ? line.Substring(index + 1).Trim() : line.Trim();
                        }));
                    if (!string.IsNullOrWhiteSpace(display))
                        return display;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    string display = JoinDistinct(RunAndReadLines("system_profiler", "SPDisplaysDataType")
                        .Select(line => line.Trim())
                        .Where(line => line.StartsWith("Chipset Model:", StringComparison.OrdinalIgnoreCase))
                        .Select(line => line.Substring("Chipset Model:".Length).Trim()));
                    if (!string.IsNullOrWhiteSpace(display))
                        return display;
                }
            }
            catch { }

            return null;
        }

        public static long GetMemoryBytes()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string wmicMemory = ParseWmicValue(RunAndReadLines("wmic", "computersystem get totalphysicalmemory /value"), "TotalPhysicalMemory");
                    if (long.TryParse(wmicMemory, out long windowsBytes))
                        return windowsBytes;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/proc/meminfo"))
                {
                    string memTotal = File.ReadLines("/proc/meminfo").FirstOrDefault(line => line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(memTotal))
                    {
                        string digits = new string(memTotal.Where(char.IsDigit).ToArray());
                        if (long.TryParse(digits, out long linuxKb))
                            return linuxKb * 1024;
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    string memSize = RunAndReadFirstLine("sysctl", "-n hw.memsize");
                    if (long.TryParse(memSize, out long macBytes))
                        return macBytes;
                }
            }
            catch
            {
            }

            try
            {
                return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            }
            catch
            {
                return 0;
            }
        }

        private static string RunAndReadFirstLine(string fileName, string arguments)
        {
            return RunAndReadLines(fileName, arguments).FirstOrDefault();
        }

        private static string[] RunAndReadLines(string fileName, string arguments)
        {
            using (var process = new System.Diagnostics.Process())
            {
                process.StartInfo.FileName = fileName;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.CreateNoWindow = true;
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(1500))
                    process.Kill();
                return output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToArray();
            }
        }

        private static string ParseWmicValue(string[] lines, string key)
        {
            return ParseWmicValues(lines, key).FirstOrDefault();
        }

        private static string[] ParseWmicValues(string[] lines, string key)
        {
            string prefix = key + "=";
            return lines
                .Where(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(line => line.Substring(prefix.Length).Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }

        private static string JoinDistinct(IEnumerable<string> values)
        {
            return string.Join(", ", values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct());
        }

        public static string RelativeToApplicationDirectory(string path)
        {
            if (!Path.IsPathRooted(path))
            {
                switch (GetOSName())
                {
                    case OS_WINDOWS64:
                        return path;
                    case OS_LINUX64:
                        return path;
                    case OS_MACOS:
                    case OS_MACOSARM64:
                        string userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        string appPath = Path.Combine(userDir, "Library/Application Support", "BlendFarm");
                        if (!Directory.Exists(appPath))
                            Directory.CreateDirectory(appPath);
                        return Path.Combine(appPath, path);
                    default:
                        return path;
                }
            }
            return path;
        }

        public static bool IsOS(string osName)
        {
            string name = null;
            try
            {
                name = GetOSName();
            }
            catch (NotImplementedException ex) { }
            return name == osName;
        }
    }
}

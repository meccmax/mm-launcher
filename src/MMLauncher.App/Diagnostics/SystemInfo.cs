using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using MMLauncher.Core.Diagnostics;

namespace MMLauncher.App.Diagnostics;

/// <summary>Basic PC details for problem reports: Windows version, CPU, graphics card + driver, RAM.</summary>
public static class SystemInfo
{
    public static SystemSummary Collect()
    {
        string windows = RuntimeInformation.OSDescription, cpu = "unknown", gpu = "unknown";
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var product = key?.GetValue("ProductName") as string ?? "Windows";
                var display = key?.GetValue("DisplayVersion") as string ?? "";
                var build = key?.GetValue("CurrentBuildNumber") as string ?? "";
                // Windows 11 still says "Windows 10" in ProductName; the build number tells them apart.
                if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
                windows = $"{product} {display} (build {build})".Replace("  ", " ").Trim();
            }
            catch { }
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                cpu = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? cpu;
                cpu += $" ({Environment.ProcessorCount} threads)";
            }
            catch { }
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController");
                var cards = new List<string>();
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        var name = mo["Name"] as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var driver = mo["DriverVersion"] as string;
                        cards.Add(driver != null ? $"{name} (driver {driver})" : name);
                    }
                }
                if (cards.Count > 0) gpu = string.Join("; ", cards);
            }
            catch { }
        }
        else
        {
            cpu = $"{RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} threads";
        }

        var ramBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var ram = ramBytes > 0 ? $"{ramBytes / 1024.0 / 1024 / 1024:0.#} GB" : "unknown";
        return new SystemSummary(windows, cpu, gpu, ram);
    }
}

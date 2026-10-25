using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CSweet.Office.Runtime.Core;

/// <summary>Prevents a drive-letter reassignment from starting Office against replacement storage.</summary>
public static class OfficeStorageGuard
{
    public static void VerifyWindowsVolume()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\CSweet\Office\Storage");
        if (key is null) return; // An existing installation predating drive selection.
        var expected = key.GetValue("VolumeId") as string;
        var install = key.GetValue("InstallRoot") as string;
        var data = key.GetValue("DataRoot") as string;
        if (key.GetValue("SchemaVersion") is not int schema || schema != 1 ||
            string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(install) || string.IsNullOrWhiteSpace(data))
            throw new InvalidOperationException("Office storage registration is incomplete.");
        foreach (var path in new[] { install, data })
        {
            var name = new StringBuilder(1024);
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null || !GetVolumeNameForVolumeMountPoint(root, name, name.Capacity) ||
                !string.Equals(expected, name.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The registered Office volume is missing or has been replaced.");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder name, int length);
}

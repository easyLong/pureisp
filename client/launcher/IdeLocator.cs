using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

// Installation discovery is read-only; the user's selection lives outside the package.
public sealed class IdeLocator
{
    private readonly string settingsPath;
    private readonly Func<IEnumerable<string>> discover;

    public IdeLocator(string settingsPath, Func<IEnumerable<string>> discover)
    {
        this.settingsPath = settingsPath;
        this.discover = discover;
    }

    public static IdeLocator ForCurrentUser()
    {
        return new IdeLocator(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"PureISP\ide-path.txt"), DiscoverInstalled);
    }

    public static bool IsIdeExecutable(string path)
    {
        if (String.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string name = Path.GetFileName(path);
            return (String.Equals(name, "Antigravity IDE.exe", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "Antigravity.exe", StringComparison.OrdinalIgnoreCase)) && File.Exists(path);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    public string Resolve()
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                string saved = File.ReadAllText(settingsPath, Encoding.UTF8).Trim();
                if (IsIdeExecutable(saved)) return Path.GetFullPath(saved);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        foreach (string candidate in discover())
            if (IsIdeExecutable(candidate)) return Path.GetFullPath(candidate);
        return null;
    }

    public void Remember(string executable)
    {
        if (!IsIdeExecutable(executable)) throw new ArgumentException("请选择已安装的 Antigravity IDE.exe 或 Antigravity.exe。");
        string fullPath = Path.GetFullPath(executable);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
        string temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, fullPath, new UTF8Encoding(false));
            if (File.Exists(settingsPath)) File.Replace(temporary, settingsPath, null);
            else File.Move(temporary, settingsPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IEnumerable<string> DiscoverInstalled()
    {
        List<string> candidates = new List<string>();
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (string name in new[] { "Antigravity IDE.exe", "Antigravity.exe" })
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + name))
                {
                    if (key != null)
                    {
                        string value = key.GetValue(null) as string;
                        if (!String.IsNullOrWhiteSpace(value)) candidates.Add(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')));
                    }
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.Programs,
            Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.CommonDesktopDirectory })
        foreach (string shortcut in FindShortcuts(Environment.GetFolderPath(folder)))
        {
            string target = ReadShortcutTarget(shortcut);
            if (!String.IsNullOrEmpty(target)) candidates.Add(target);
        }

        foreach (string root in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        foreach (string folder in new[] { "Antigravity IDE", "Antigravity" })
        foreach (string name in new[] { "Antigravity IDE.exe", "Antigravity.exe" })
            if (!String.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, folder, name));
        return candidates;
    }

    private static IEnumerable<string> FindShortcuts(string root)
    {
        List<string> found = new List<string>();
        Stack<string> pending = new Stack<string>();
        if (!String.IsNullOrEmpty(root) && Directory.Exists(root)) pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            try
            {
                foreach (string file in Directory.GetFiles(directory, "*.lnk"))
                    if (Path.GetFileName(file).IndexOf("Antigravity", StringComparison.OrdinalIgnoreCase) >= 0) found.Add(file);
                foreach (string child in Directory.GetDirectories(directory))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found;
    }

    public static string ReadShortcutTarget(string path)
    {
        object shell = null;
        object shortcut = null;
        try
        {
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            shell = Activator.CreateInstance(type);
            shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            return shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
        }
        catch (COMException) { return null; }
        catch (TargetInvocationException) { return null; }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}

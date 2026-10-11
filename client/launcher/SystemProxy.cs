using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

public sealed class ProxySettings
{
    public int Flags;
    public string Server;
    public string Bypass;
    public string AutoConfig;

    public bool SameAs(ProxySettings other)
    {
        return other != null && Flags == other.Flags &&
            String.Equals(Server ?? "", other.Server ?? "", StringComparison.OrdinalIgnoreCase) &&
            String.Equals(Bypass ?? "", other.Bypass ?? "", StringComparison.Ordinal) &&
            String.Equals(AutoConfig ?? "", other.AutoConfig ?? "", StringComparison.Ordinal);
    }
}

// Use WinINet's supported per-connection API instead of writing registry values.
public static class WindowsSystemProxy
{
    [StructLayout(LayoutKind.Sequential)]
    private struct OptionList
    {
        public int Size;
        public IntPtr Connection;
        public int Count;
        public int Error;
        public IntPtr Options;
    }

    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    private static extern bool Query(IntPtr handle, int option, ref OptionList list, ref int size);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    private static extern bool Set(IntPtr handle, int option, ref OptionList list, int size);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    private static extern bool Notify(IntPtr handle, int option, IntPtr buffer, int size);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    // The native union contains FILETIME (8 bytes), including on 32-bit Windows.
    private static readonly int OptionSize = IntPtr.Size == 8 ? 16 : 12;
    private static readonly int ValueOffset = IntPtr.Size == 8 ? 8 : 4;

    private static OptionList Allocate(int flagOption)
    {
        OptionList list = new OptionList();
        list.Size = Marshal.SizeOf(typeof(OptionList));
        list.Count = 4;
        list.Options = Marshal.AllocHGlobal(4 * OptionSize);
        Marshal.Copy(new byte[4 * OptionSize], 0, list.Options, 4 * OptionSize);
        int[] options = { flagOption, 2, 3, 4 };
        for (int i = 0; i < 4; i++) Marshal.WriteInt32(list.Options, i * OptionSize, options[i]);
        return list;
    }

    public static ProxySettings Read()
    {
        try { return ReadFlags(10); }
        catch (Win32Exception) { return ReadFlags(1); }
    }

    private static ProxySettings ReadFlags(int flagOption)
    {
        OptionList list = Allocate(flagOption);
        try
        {
            int size = list.Size;
            if (!Query(IntPtr.Zero, 75, ref list, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new ProxySettings {
                Flags = Marshal.ReadInt32(list.Options, ValueOffset),
                Server = Marshal.PtrToStringUni(Marshal.ReadIntPtr(list.Options, OptionSize + ValueOffset)),
                Bypass = Marshal.PtrToStringUni(Marshal.ReadIntPtr(list.Options, 2 * OptionSize + ValueOffset)),
                AutoConfig = Marshal.PtrToStringUni(Marshal.ReadIntPtr(list.Options, 3 * OptionSize + ValueOffset))
            };
        }
        finally
        {
            for (int i = 1; i < 4; i++)
            {
                IntPtr value = Marshal.ReadIntPtr(list.Options, i * OptionSize + ValueOffset);
                if (value != IntPtr.Zero) GlobalFree(value);
            }
            Marshal.FreeHGlobal(list.Options);
        }
    }

    public static void Write(ProxySettings settings)
    {
        OptionList list = Allocate(1);
        IntPtr[] strings = new IntPtr[3];
        try
        {
            Marshal.WriteInt32(list.Options, ValueOffset, settings.Flags);
            string[] values = { settings.Server, settings.Bypass, settings.AutoConfig };
            for (int i = 0; i < 3; i++)
            {
                strings[i] = values[i] == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(values[i]);
                Marshal.WriteIntPtr(list.Options, (i + 1) * OptionSize + ValueOffset, strings[i]);
            }
            if (!Set(IntPtr.Zero, 75, ref list, list.Size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Notify(IntPtr.Zero, 39, IntPtr.Zero, 0);
            Notify(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }
        finally
        {
            foreach (IntPtr value in strings) if (value != IntPtr.Zero) Marshal.FreeHGlobal(value);
            Marshal.FreeHGlobal(list.Options);
        }
    }
}

public sealed class SystemProxyLease : IDisposable
{
    private readonly Func<ProxySettings> read;
    private readonly Action<ProxySettings> write;
    private readonly string backupPath;
    private ProxySettings original;
    private readonly object gate = new object();

    public static ProxySettings Desired()
    {
        return new ProxySettings {
            Flags = 3,
            Server = "127.0.0.1:7890",
            Bypass = "localhost;127.*;[::1];10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*",
            AutoConfig = ""
        };
    }

    public SystemProxyLease(Func<ProxySettings> read, Action<ProxySettings> write, string backupPath)
    {
        this.read = read; this.write = write; this.backupPath = backupPath;
    }

    public static SystemProxyLease ForCurrentUser()
    {
        return new SystemProxyLease(WindowsSystemProxy.Read, WindowsSystemProxy.Write,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"PureISP\system-proxy.backup"));
    }

    public void RecoverStale()
    {
        lock (gate)
        {
            if (!File.Exists(backupPath)) return;
            int owner;
            ProxySettings saved = Load(out owner);
            try
            {
                using (Process process = Process.GetProcessById(owner))
                    if (!process.HasExited && process.ProcessName == "PureISP")
                        throw new InvalidOperationException("另一个 PureISP 正在管理系统代理，请先退出它。");
            }
            catch (ArgumentException) { }
            // A different proxy tool may have taken over since the crash.
            if (read().SameAs(Desired())) write(saved);
            File.Delete(backupPath);
        }
    }

    public void Enable()
    {
        lock (gate)
        {
            if (original != null) return;
            RecoverStale();
            ProxySettings saved = read();
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
            using (FileStream file = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(file))
            {
                writer.Write("PureISP.Proxy.1");
                writer.Write(Process.GetCurrentProcess().Id);
                writer.Write(saved.Flags);
                writer.Write(saved.Server ?? "");
                writer.Write(saved.Bypass ?? "");
                writer.Write(saved.AutoConfig ?? "");
                writer.Flush();
                file.Flush(true);
            }
            original = saved;
            try { write(Desired()); }
            catch
            {
                write(saved);
                File.Delete(backupPath);
                original = null;
                throw;
            }
        }
    }

    private ProxySettings Load(out int owner)
    {
        using (BinaryReader reader = new BinaryReader(File.OpenRead(backupPath)))
        {
            if (reader.ReadString() != "PureISP.Proxy.1") throw new InvalidDataException("系统代理备份格式错误。");
            owner = reader.ReadInt32();
            return new ProxySettings { Flags = reader.ReadInt32(), Server = reader.ReadString(), Bypass = reader.ReadString(), AutoConfig = reader.ReadString() };
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (original == null) return;
            // Do not overwrite changes made by the user or another proxy application.
            if (read().SameAs(Desired())) write(original);
            File.Delete(backupPath);
            original = null;
        }
    }
}

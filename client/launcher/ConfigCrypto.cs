using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

#if PROTECTED_CONFIG
internal static class NativeAesGcm
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AuthenticatedCipherModeInfo
    {
        public uint cbSize;
        public uint dwInfoVersion;
        public IntPtr pbNonce;
        public uint cbNonce;
        public IntPtr pbAuthData;
        public uint cbAuthData;
        public IntPtr pbTag;
        public uint cbTag;
        public IntPtr pbMacContext;
        public uint cbMacContext;
        public uint cbAAD;
        public ulong cbData;
        public uint dwFlags;
    }

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptOpenAlgorithmProvider(out IntPtr phAlgorithm, string pszAlgId, string pszImplementation, uint dwFlags);
    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptSetProperty(IntPtr hObject, string pszProperty, byte[] pbInput, int cbInput, uint dwFlags);
    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptGetProperty(IntPtr hObject, string pszProperty, byte[] pbOutput, int cbOutput, out int pcbResult, uint dwFlags);
    [DllImport("bcrypt.dll")]
    private static extern int BCryptGenerateSymmetricKey(IntPtr hAlgorithm, out IntPtr phKey, byte[] pbKeyObject, int cbKeyObject, byte[] pbSecret, int cbSecret, uint dwFlags);
    [DllImport("bcrypt.dll")]
    private static extern int BCryptDestroyKey(IntPtr hKey);
    [DllImport("bcrypt.dll")]
    private static extern int BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, uint dwFlags);
    [DllImport("bcrypt.dll")]
    private static extern int BCryptDecrypt(IntPtr hKey, byte[] pbInput, int cbInput, ref AuthenticatedCipherModeInfo pPaddingInfo, IntPtr pbIV, int cbIV, byte[] pbOutput, int cbOutput, out int pcbResult, uint dwFlags);

    private static void Check(int status, string operation)
    {
        if (status != 0) throw new CryptographicException(operation + " failed: 0x" + status.ToString("X8"));
    }

    public static byte[] Decrypt(byte[] ciphertext, byte[] key, byte[] nonce, byte[] tag)
    {
        if (key == null || key.Length != 32) throw new CryptographicException("AES-256 key must be 32 bytes.");
        IntPtr algorithm = IntPtr.Zero, keyHandle = IntPtr.Zero;
        byte[] keyObject = null;
        GCHandle nonceHandle = new GCHandle(), tagHandle = new GCHandle();
        try
        {
            Check(BCryptOpenAlgorithmProvider(out algorithm, "AES", null, 0), "BCryptOpenAlgorithmProvider");
            byte[] mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
            Check(BCryptSetProperty(algorithm, "ChainingMode", mode, mode.Length, 0), "BCryptSetProperty");
            byte[] length = new byte[4]; int written;
            Check(BCryptGetProperty(algorithm, "ObjectLength", length, length.Length, out written, 0), "BCryptGetProperty");
            keyObject = new byte[BitConverter.ToInt32(length, 0)];
            Check(BCryptGenerateSymmetricKey(algorithm, out keyHandle, keyObject, keyObject.Length, key, key.Length, 0), "BCryptGenerateSymmetricKey");
            nonceHandle = GCHandle.Alloc(nonce, GCHandleType.Pinned);
            tagHandle = GCHandle.Alloc(tag, GCHandleType.Pinned);
            AuthenticatedCipherModeInfo info = new AuthenticatedCipherModeInfo {
                cbSize = (uint)Marshal.SizeOf(typeof(AuthenticatedCipherModeInfo)),
                dwInfoVersion = 1,
                pbNonce = nonceHandle.AddrOfPinnedObject(), cbNonce = (uint)nonce.Length,
                pbTag = tagHandle.AddrOfPinnedObject(), cbTag = (uint)tag.Length
            };
            byte[] plaintext = new byte[ciphertext.Length]; int plaintextLength;
            Check(BCryptDecrypt(keyHandle, ciphertext, ciphertext.Length, ref info, IntPtr.Zero, 0, plaintext, plaintext.Length, out plaintextLength, 0), "BCryptDecrypt");
            if (plaintextLength != plaintext.Length) Array.Resize(ref plaintext, plaintextLength);
            return plaintext;
        }
        finally
        {
            if (nonceHandle.IsAllocated) nonceHandle.Free();
            if (tagHandle.IsAllocated) tagHandle.Free();
            if (keyHandle != IntPtr.Zero) BCryptDestroyKey(keyHandle);
            if (algorithm != IntPtr.Zero) BCryptCloseAlgorithmProvider(algorithm, 0);
            if (keyObject != null) Array.Clear(keyObject, 0, keyObject.Length);
        }
    }
}

internal static class ProtectedConfig
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PISPENC1");
    private const int NonceLength = 12;
    private const int TagLength = 16;

    public static string Prepare(string directory)
    {
        string source = Path.Combine(directory, "config.enc");
        if (!File.Exists(source)) throw new FileNotFoundException("缺少加密配置 config.enc，请完整解压客户包。", source);
        string runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PureISP", "protected-runtime");
        Directory.CreateDirectory(runtime);
        Cleanup(runtime);
        byte[] package = File.ReadAllBytes(source);
        if (package.Length <= Magic.Length + NonceLength + TagLength) throw new InvalidDataException("加密配置文件不完整。");
        for (int i = 0; i < Magic.Length; i++) if (package[i] != Magic[i]) throw new InvalidDataException("加密配置版本不受支持。");
        byte[] nonce = new byte[NonceLength], tag = new byte[TagLength], ciphertext = new byte[package.Length - Magic.Length - NonceLength - TagLength];
        Buffer.BlockCopy(package, Magic.Length, nonce, 0, nonce.Length);
        Buffer.BlockCopy(package, Magic.Length + nonce.Length, tag, 0, tag.Length);
        Buffer.BlockCopy(package, Magic.Length + nonce.Length + tag.Length, ciphertext, 0, ciphertext.Length);
        byte[] plaintext = NativeAesGcm.Decrypt(ciphertext, ConfigKeyProvider.Key, nonce, tag);
        string path = Path.Combine(runtime, "config-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllBytes(path, plaintext);
        Array.Clear(plaintext, 0, plaintext.Length);
        Array.Clear(ciphertext, 0, ciphertext.Length);
        Array.Clear(package, 0, package.Length);
        return path;
    }

    public static void Delete(string path)
    {
        if (String.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Cleanup(string runtime)
    {
        try { foreach (string path in Directory.GetFiles(runtime, "config-*.yaml")) Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
#endif

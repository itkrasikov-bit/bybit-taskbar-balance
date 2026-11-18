using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BybitTaskbar.Core;

/// <summary>
/// Шифрование секрета API через Windows DPAPI (CryptProtectData) — без NuGet-пакетов.
/// Расшифровать сможет только тот же пользователь на той же машине.
/// </summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var data = Encoding.UTF8.GetBytes(plain);
            var ptr = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, ptr, data.Length);
                var input = new DATA_BLOB { cbData = data.Length, pbData = ptr };
                var output = new DATA_BLOB();
                if (!CryptProtectData(ref input, "BybitTaskbar", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, ref output))
                    throw new InvalidOperationException("CryptProtectData failed: " + Marshal.GetLastWin32Error());

                try
                {
                    var bytes = new byte[output.cbData];
                    Marshal.Copy(output.pbData, bytes, 0, output.cbData);
                    return Convert.ToBase64String(bytes);
                }
                finally
                {
                    if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("DPAPI protect failed: " + ex);
            return "";
        }
    }

    public static string? TryUnprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return null;
        try
        {
            var data = Convert.FromBase64String(protectedBase64);
            var ptr = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, ptr, data.Length);
                var input = new DATA_BLOB { cbData = data.Length, pbData = ptr };
                var output = new DATA_BLOB();
                if (!CryptUnprotectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, ref output))
                    return null;

                try
                {
                    var bytes = new byte[output.cbData];
                    Marshal.Copy(output.pbData, bytes, 0, output.cbData);
                    return Encoding.UTF8.GetString(bytes);
                }
                finally
                {
                    if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("DPAPI unprotect failed: " + ex);
            return null;
        }
    }
}

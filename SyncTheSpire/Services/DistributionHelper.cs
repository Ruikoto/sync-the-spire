using System.Runtime.InteropServices;

namespace SyncTheSpire.Services;

/// <summary>
/// detects whether the app is running inside an MSIX package (Store / sideloaded) or as a loose exe.
/// </summary>
public static class DistributionHelper
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetCurrentPackageFullName(ref uint length,
        [MarshalAs(UnmanagedType.LPWStr)] char[]? fullName);

    private const int AppmodelErrorNoPackage = 15700;

    private static bool? _isMsixPackaged;

    public static bool IsMsixPackaged
    {
        get
        {
            if (_isMsixPackaged.HasValue) return _isMsixPackaged.Value;
            uint length = 0;
            _isMsixPackaged = GetCurrentPackageFullName(ref length, null) != AppmodelErrorNoPackage;
            return _isMsixPackaged.Value;
        }
    }
}

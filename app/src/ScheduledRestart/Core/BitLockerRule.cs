using System.Collections.Generic;
using System.Linq;

namespace ScheduledRestart.Core
{
    /// <summary>
    /// Decides whether an unattended restart will stop at a BitLocker pre-boot prompt (PIN, passphrase or
    /// startup key). TPM-only unlock restarts without anyone present, so it is not a reason to warn.
    /// </summary>
    public static class BitLockerRule
    {
        /// <summary>Win32_EncryptableVolume.GetConversionStatus: 0 = fully decrypted.</summary>
        public const int FullyDecrypted = 0;

        /// <summary>
        /// Key protector types that need someone at the machine before Windows starts:
        /// 2 external/startup key, 4 TPM+PIN, 5 TPM+startup key, 6 TPM+PIN+startup key, 8 passphrase.
        /// </summary>
        public static readonly int[] PreBootProtectorTypes = { 2, 4, 5, 6, 8 };

        /// <summary>
        /// True when the volume is not fully decrypted (encrypted, encrypting, decrypting or paused) and has
        /// at least one pre-boot protector. Protection status (on, off, suspended) deliberately does not matter.
        /// </summary>
        public static bool StopsAtPreBoot(int conversionStatus, IEnumerable<int> protectorTypes)
        {
            if (conversionStatus == FullyDecrypted || protectorTypes == null) return false;
            return protectorTypes.Any(t => PreBootProtectorTypes.Contains(t));
        }
    }
}

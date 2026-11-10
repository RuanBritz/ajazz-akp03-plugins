using System;
using Microsoft.Win32;

namespace AudioControls
{
    /// <summary>
    /// Windows camera privacy switch (Settings > Privacy &amp; security > Camera). It lives in the registry under
    /// CapabilityAccessManager\ConsentStore\webcam as "Allow" / "Deny", so no admin rights are needed.
    /// </summary>
    internal static class Camera
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
        const string ValueName = "Value";

        // HKCU "Let apps access your camera" plus its "Let desktop apps access your camera" sub-switch.
        // Desktop apps (Teams, Zoom, Discord...) are covered by the second one, so both are switched together.
        static readonly string[] UserKeys = { KeyPath, KeyPath + @"\NonPackaged" };

        /// <summary>True if apps can't use the camera, by the user switches or by the device-wide (HKLM) one.</summary>
        public static bool IsBlocked()
        {
            if (IsBlockedForDevice()) return true;
            foreach (string path in UserKeys)
                if (IsDeny(Registry.CurrentUser, path)) return true;
            return false;
        }

        /// <summary>"Camera access" for the whole device is off. Only an admin can turn it back on.</summary>
        public static bool IsBlockedForDevice()
        {
            return IsDeny(Registry.LocalMachine, KeyPath);
        }

        public static void SetBlocked(bool blocked)
        {
            string value = blocked ? "Deny" : "Allow";
            foreach (string path in UserKeys)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path))
                    key.SetValue(ValueName, value, RegistryValueKind.String);
            }
        }

        static bool IsDeny(RegistryKey root, string path)
        {
            using (RegistryKey key = root.OpenSubKey(path))
            {
                string value = key == null ? null : key.GetValue(ValueName) as string;
                return string.Equals(value, "Deny", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}

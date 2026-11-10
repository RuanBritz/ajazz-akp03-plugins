using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AudioControls
{
    /// <summary>Windows Core Audio: mic mute, output volume/mute, and default output device.</summary>
    internal static class Audio
    {
        const int eRender = 0;
        const int eCapture = 1;
        const int eConsole = 0;         // default device for most apps
        const int eMultimedia = 1;      // default device for music / video apps
        const int eCommunications = 2;  // default device for Teams, Zoom, Discord etc.
        const int DEVICE_STATE_ACTIVE = 1;
        const int STGM_READ = 0;
        const int CLSCTX_ALL = 23;
        const ushort VT_LPWSTR = 31;

        static readonly PropertyKey FriendlyName =
            new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant pv);

        // ---- Microphone (default capture device) ----

        public static bool IsMicMuted()
        {
            return GetMute(OpenVolume(eCapture, eConsole, null));
        }

        /// <summary>Applies to both the default and the default-communications mic (usually the same device).</summary>
        public static void SetMicMuted(bool mute)
        {
            foreach (int role in new[] { eConsole, eCommunications })
                SetMute(OpenVolume(eCapture, role, null), mute);
        }

        // ---- Output devices (an empty device id means the current default output) ----

        public static float GetVolume(string deviceId)
        {
            IAudioEndpointVolume vol = OpenVolume(eRender, eConsole, deviceId);
            try
            {
                float level;
                Marshal.ThrowExceptionForHR(vol.GetMasterVolumeLevelScalar(out level));
                return level;
            }
            finally
            {
                Marshal.ReleaseComObject(vol);
            }
        }

        public static void SetVolume(string deviceId, float level)
        {
            IAudioEndpointVolume vol = OpenVolume(eRender, eConsole, deviceId);
            try
            {
                Guid eventContext = Guid.Empty;
                Marshal.ThrowExceptionForHR(vol.SetMasterVolumeLevelScalar(level, ref eventContext));
            }
            finally
            {
                Marshal.ReleaseComObject(vol);
            }
        }

        public static bool IsMuted(string deviceId)
        {
            return GetMute(OpenVolume(eRender, eConsole, deviceId));
        }

        public static void SetMuted(string deviceId, bool mute)
        {
            SetMute(OpenVolume(eRender, eConsole, deviceId), mute);
        }

        /// <summary>Active output devices as (device id, friendly name).</summary>
        public static List<KeyValuePair<string, string>> ListOutputs()
        {
            var result = new List<KeyValuePair<string, string>>();
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDeviceCollection collection = null;
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out collection));
                uint count;
                Marshal.ThrowExceptionForHR(collection.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device;
                    if (collection.Item(i, out device) != 0) continue;
                    try
                    {
                        string id;
                        Marshal.ThrowExceptionForHR(device.GetId(out id));
                        result.Add(new KeyValuePair<string, string>(id, GetName(device)));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                if (collection != null) Marshal.ReleaseComObject(collection);
                Marshal.ReleaseComObject(enumerator);
            }
            return result;
        }

        /// <summary>Id of the current default output device, or "" if there is none.</summary>
        public static string DefaultOutputId()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device = null;
            try
            {
                string id;
                if (enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out device) != 0) return "";
                return device.GetId(out id) == 0 ? id : "";
            }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
        }

        public static string DefaultOutputName()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device = null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out device) != 0) return "";
                return GetName(device);
            }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
        }

        /// <summary>
        /// Makes a device the Windows default output, like "Set as default device" in Sound settings.
        /// Uses IPolicyConfig: undocumented, but it's what Windows' own UI and every audio switcher use.
        /// </summary>
        public static void SetDefaultOutput(string deviceId, bool includeCommunications)
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, eConsole));
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, eMultimedia));
                if (includeCommunications)
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, eCommunications));
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        }

        // ---- helpers ----

        static bool GetMute(IAudioEndpointVolume vol)
        {
            try
            {
                bool muted;
                Marshal.ThrowExceptionForHR(vol.GetMute(out muted));
                return muted;
            }
            finally
            {
                Marshal.ReleaseComObject(vol);
            }
        }

        static void SetMute(IAudioEndpointVolume vol, bool mute)
        {
            try
            {
                Guid eventContext = Guid.Empty;
                Marshal.ThrowExceptionForHR(vol.SetMute(mute, ref eventContext));
            }
            finally
            {
                Marshal.ReleaseComObject(vol);
            }
        }

        static string GetName(IMMDevice device)
        {
            IPropertyStore store;
            if (device.OpenPropertyStore(STGM_READ, out store) != 0) return "Unknown device";
            try
            {
                PropertyKey key = FriendlyName;
                PropVariant value;
                if (store.GetValue(ref key, out value) != 0) return "Unknown device";
                try
                {
                    return value.vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.pointer) : "Unknown device";
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        /// <summary>Volume control for a device id, or for the default device of the given role if the id is empty.</summary>
        static IAudioEndpointVolume OpenVolume(int flow, int role, string deviceId)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device = null;
            try
            {
                if (string.IsNullOrEmpty(deviceId))
                    Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(flow, role, out device));
                else
                    Marshal.ThrowExceptionForHR(enumerator.GetDevice(deviceId, out device));

                // GetDevice still returns unplugged/disabled devices; treat those as missing.
                int state;
                Marshal.ThrowExceptionForHR(device.GetState(out state));
                if (state != DEVICE_STATE_ACTIVE) throw new InvalidOperationException("Device is not active");

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object volume;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out volume));
                return (IAudioEndpointVolume)volume;
            }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VideoControls
{
    /// <summary>Key that blocks / allows the camera for all apps via the Windows camera privacy setting.</summary>
    internal class CameraAction : PluginAction
    {
        // Key states as defined in manifest.json: 0 = camera allowed, 1 = camera blocked.
        const int StateAllowed = 0;
        const int StateBlocked = 1;

        readonly HashSet<string> contexts = new HashSet<string>();
        readonly object contextsLock = new object();
        int lastState = -1;

        public override string Uuid { get { return "com.local.videocontrols.camera"; } }

        public override async Task Handle(string ev, string context, Dictionary<string, object> payload)
        {
            switch (ev)
            {
                case "willAppear":
                    lock (contextsLock) contexts.Add(context);
                    await StreamDock.SetState(context, SafeCurrentState());
                    break;

                case "willDisappear":
                    lock (contextsLock) contexts.Remove(context);
                    break;

                case "keyDown":
                    await Toggle(context);
                    break;
            }
        }

        async Task Toggle(string context)
        {
            int state = -1;
            try
            {
                if (Camera.IsBlockedForDevice())
                {
                    // Device-wide switch is off: the user switch can't override it, and turning it on needs admin.
                    StreamDock.Log("Camera access is turned off for this device; turn it on in Windows privacy settings.");
                }
                else
                {
                    Camera.SetBlocked(!Camera.IsBlocked());
                    state = Camera.IsBlocked() ? StateBlocked : StateAllowed;
                }
            }
            catch (Exception ex)
            {
                StreamDock.Log("Camera toggle failed: " + ex.Message);
            }

            if (state < 0)
            {
                await StreamDock.ShowAlert(context);
                return;
            }

            await ApplyState(state);
        }

        /// <summary>Keeps the key in sync if the camera setting is changed in Windows Settings.</summary>
        public override Task Poll()
        {
            int state;
            try
            {
                state = Camera.IsBlocked() ? StateBlocked : StateAllowed;
            }
            catch
            {
                return Task.FromResult(0);
            }

            if (state == lastState) return Task.FromResult(0);
            return ApplyState(state);
        }

        int SafeCurrentState()
        {
            try { return Camera.IsBlocked() ? StateBlocked : StateAllowed; }
            catch { return StateAllowed; }
        }

        /// <summary>Pushes the camera state to every key showing this action.</summary>
        async Task ApplyState(int state)
        {
            lastState = state;
            List<string> targets;
            lock (contextsLock) targets = new List<string>(contexts);
            foreach (string ctx in targets)
                await StreamDock.SetState(ctx, state);
        }
    }
}

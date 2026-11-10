using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AudioControls
{
    /// <summary>Key that toggles mute on the default Windows microphone.</summary>
    internal class MicMuteAction : PluginAction
    {
        // Key states as defined in manifest.json: 0 = mic live, 1 = mic muted.
        const int StateLive = 0;
        const int StateMuted = 1;

        readonly HashSet<string> contexts = new HashSet<string>();
        readonly object contextsLock = new object();
        int lastState = -1;

        public override string Uuid { get { return "com.local.audiocontrols.micmute"; } }

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
                Audio.SetMicMuted(!Audio.IsMicMuted());
                state = Audio.IsMicMuted() ? StateMuted : StateLive;
            }
            catch (Exception ex)
            {
                StreamDock.Log("Mic toggle failed: " + ex.Message);
            }

            if (state < 0)
            {
                await StreamDock.ShowAlert(context);
                return;
            }

            await ApplyState(state);
        }

        /// <summary>Keeps the key in sync if the mic is muted elsewhere (Teams, Windows settings, a headset button...).</summary>
        public override Task Poll()
        {
            int state;
            try
            {
                state = Audio.IsMicMuted() ? StateMuted : StateLive;
            }
            catch
            {
                return Task.FromResult(0); // no microphone right now; leave the key as is
            }

            if (state == lastState) return Task.FromResult(0);
            return ApplyState(state);
        }

        int SafeCurrentState()
        {
            try { return Audio.IsMicMuted() ? StateMuted : StateLive; }
            catch { return StateLive; }
        }

        /// <summary>Pushes a mute state to every key showing this action.</summary>
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

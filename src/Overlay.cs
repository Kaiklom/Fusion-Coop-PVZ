using FusionCoop.Net;
using UnityEngine;

namespace FusionCoop
{
    // IMGUI overlay. This build strips GUI.Button/TextField, so only Label/Box are used
    // and all input goes through hotkeys (see FusionCoopMod.HandleKeys).
    internal static class Overlay
    {
        internal static bool PanelOpen = true;
        internal static string IpDraft = "";

        private static string _toast;
        private static double _toastUntil;

        internal static void Toast(string text, double seconds = 2.5)
        {
            _toast = text;
            _toastUntil = Session.Now + seconds;
        }

        private static void DrawCenterMessages()
        {
            float w = 420, cx = Screen.width / 2f;
            if (Coop.SpeedSync.HostPaused)
            {
                GUI.Box(new Rect(cx - w / 2, Screen.height * 0.3f, w, 34), "");
                GUI.Label(new Rect(cx - w / 2 + 12, Screen.height * 0.3f + 7, w - 24, 22), "PAUSED BY HOST - waiting for the host to resume");
            }
            if (_toast != null && Session.Now < _toastUntil)
            {
                GUI.Box(new Rect(cx - w / 2, Screen.height * 0.3f + 44, w, 30), "");
                GUI.Label(new Rect(cx - w / 2 + 12, Screen.height * 0.3f + 49, w - 24, 22), _toast);
            }
        }

        internal static readonly Color[] PlayerColors =
        {
            new(0.35f, 0.75f, 1f), // P1
            new(1f, 0.55f, 0.35f), // P2
            new(0.55f, 1f, 0.45f), // P3
            new(1f, 0.45f, 0.9f),  // P4
        };

        internal static void Draw()
        {
            DrawCursors();
            DrawCenterMessages();
            if (!PanelOpen)
            {
                GUI.Label(new Rect(10, 10, 400, 22), $"FusionCoop: {Session.Status}   [F8] panel");
                return;
            }

            string role = Session.Role switch
            {
                Role.Host => "HOST (P1)",
                Role.Client => Session.LocalId != 0 ? $"CLIENT (P{Session.LocalId + 1})" : "CLIENT",
                _ => "-",
            };
            string rtt = Session.RttMs >= 0 ? $"{Session.RttMs:0} ms" : "-";
            string players = "";
            foreach (var kv in Session.Names) players += $"P{kv.Key + 1}:{kv.Value}  ";

            string text =
                "FusionCoop 0.6 (plant/shovel/glove/speed/pause sync test)\n" +
                $"status: {Session.Status}\n" +
                $"role: {role}    ping: {rtt}\n" +
                $"players: {players}\n" +
                $"join IP: {IpDraft}_   port {FusionCoopMod.Port}\n" +
                "type digits/. to edit IP, Backspace to delete\n" +
                "[F9] host   [F10] join   [F11] leave   [F8] hide";
            GUI.Box(new Rect(10, 10, 420, 150), "");
            GUI.Label(new Rect(20, 16, 400, 140), text);
        }

        private static void DrawCursors()
        {
            var cam = Camera.main;
            if (cam == null) return;
            Coop.PlantSync.DrawLabels(cam, PlayerColors);
            double now = Session.Now;
            foreach (var kv in Session.Cursors)
            {
                if (!kv.Value.EverActive) continue; // no real position yet
                var s = kv.Value.State;
                // Keepalive arrives every 1 s; silence beyond 3 s means the link is stalling.
                bool stale = now - kv.Value.LastSeen > 3;
                var sp = cam.WorldToScreenPoint(new Vector3(s.X, s.Y, 0));
                float gx = sp.x, gy = Screen.height - sp.y;

                var color = PlayerColors[kv.Key % PlayerColors.Length];
                if (!s.Active || stale) color = new Color(color.r * 0.5f + 0.25f, color.g * 0.5f + 0.25f, color.b * 0.5f + 0.25f, 0.6f);
                string name = Session.Names.TryGetValue(kv.Key, out var n) ? n : "";
                string cell = s.Col >= 0 && s.Row >= 0 ? $" c{s.Col} r{s.Row}" : "";
                string state = stale ? " (no signal)" : !s.Active ? " (away)" : "";

                var old = GUI.color;
                GUI.color = color;
                GUI.Box(new Rect(gx - 4, gy - 4, 8, 8), "");
                GUI.Label(new Rect(gx + 8, gy - 10, 260, 22), $"P{kv.Key + 1} {name}{cell}{state}");
                GUI.color = old;
            }
        }
    }
}

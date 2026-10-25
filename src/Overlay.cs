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

        // ---------- drawing helpers ----------

        private static bool _styled;

        // Bigger bold text, once. Some GUIStyle setters may be stripped in this build: then keep defaults.
        private static void ApplyStyle()
        {
            if (_styled) return;
            _styled = true;
            try
            {
                var label = GUI.skin.label;
                label.fontSize = 15;
                label.fontStyle = FontStyle.Bold;
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[ui] text style not available: {e.Message}");
            }
        }

        // GUI.Box is see-through; stacking it gives a near-solid dark panel that reads on any background.
        private static void Panel(Rect r)
        {
            var old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0f, 0f, 0f, 1f);
            for (int i = 0; i < 4; i++) GUI.Box(r, "");
            GUI.backgroundColor = old;
        }

        // Text with a dark outline so it stays readable even outside a panel.
        private static void Text(Rect r, string text, Color color)
        {
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, color.a);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text);
            GUI.Label(new Rect(r.x - 1, r.y + 1, r.width, r.height), text);
            GUI.Label(new Rect(r.x + 1, r.y - 1, r.width, r.height), text);
            GUI.Label(new Rect(r.x - 1, r.y - 1, r.width, r.height), text);
            GUI.color = color;
            GUI.Label(r, text);
            GUI.color = old;
        }

        private static readonly Color TextColor = new(1f, 1f, 1f, 1f);
        private static readonly Color HeadColor = new(1f, 0.85f, 0.3f, 1f);
        private static readonly Color HintColor = new(0.75f, 0.9f, 1f, 1f);

        private static void Message(float y, string text)
        {
            float w = 520, cx = Screen.width / 2f;
            Panel(new Rect(cx - w / 2, y, w, 34));
            Text(new Rect(cx - w / 2 + 12, y + 7, w - 24, 24), text, TextColor);
        }

        private static void DrawCenterMessages()
        {
            if (Coop.SpeedSync.HostPaused)
                Message(Screen.height * 0.3f, "PAUSED BY HOST - waiting for the host to resume");
            string rejoin = Coop.LevelSync.RejoinText();
            if (rejoin != null) Message(12, rejoin);
            string ready = Coop.LevelSync.ReadyText();
            if (ready != null) Message(12, ready);
            if (_toast != null && Session.Now < _toastUntil)
                Message(Screen.height * 0.3f + 44, _toast);
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
            ApplyStyle();
            DrawCursors();
            DrawCenterMessages();
            if (!PanelOpen)
            {
                string mini = $"FusionCoop: {Session.Status}   [F8] panel";
                Panel(new Rect(6, 6, 440, 30));
                Text(new Rect(14, 11, 430, 24), mini, TextColor);
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

            const float x = 6, y = 6, w = 500, line = 22;
            int rows = Coop.TestTools.Enabled ? 8 : 7;
            Panel(new Rect(x, y, w, 12 + line * rows));
            float ty = y + 6;
            void Row(string t, Color c)
            {
                Text(new Rect(x + 12, ty, w - 24, line + 4), t, c);
                ty += line;
            }
            Row("FusionCoop 0.12.2", HeadColor);
            Row($"status: {Session.Status}", TextColor);
            Row($"role: {role}    ping: {rtt}", TextColor);
            Row($"players: {players}", TextColor);
            Row($"join IP: {IpDraft}_   port {FusionCoopMod.Port}", TextColor);
            Row("type digits / . to edit IP, Backspace to delete", HintColor);
            Row("[F9] host   [F10] join   [F11] leave   [F8] hide", HintColor);
            if (Coop.TestTools.Enabled)
                Row($"test: [F5/F6] {Coop.TestTools.Selected}  [9] spawn  [F4] +sun", HeadColor);
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
                GUI.color = old;
                Text(new Rect(gx + 8, gy - 11, 320, 24), $"P{kv.Key + 1} {name}{cell}{state}", color);
            }
        }
    }
}

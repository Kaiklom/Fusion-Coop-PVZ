using System.Collections.Generic;
using FusionCoop.Net;
using UnityEngine;
using UnityEngine.UI;

namespace FusionCoop.Ui
{
    // "P2 Name is ready" while choosing cards: shown for 2 s at the top of the screen, between the seed bank and
    // the Main Menu button (design/ready_pickup.png). Several players are queued and shown one after the other,
    // in the order they got ready.
    internal static class ReadyBanner
    {
        private const float Seconds = 2f, Gap = 0.2f;
        // centre of the box as a share of the screen, from the left and from the top
        private const float AtX = 0.745f, AtY = 0.065f;

        private static readonly Queue<byte> _queue = new();
        private static GameObject _shown;
        private static double _until, _nextAt;

        internal static void Ready(byte id)
        {
            FusionCoopMod.Log.Msg($"[ui] ready banner: P{id + 1}");
            _queue.Enqueue(id);
        }

        internal static void Clear()
        {
            _queue.Clear();
            Hide();
        }

        private static void Hide()
        {
            if (_shown != null) Object.Destroy(_shown);
            _shown = null;
        }

        internal static void Update()
        {
            double now = Time.realtimeSinceStartup;
            if (_shown != null && now >= _until)
            {
                Hide();
                _nextAt = now + Gap;
            }
            if (!Session.Active || Il2Cpp.Board.Instance == null)
            {
                if (_queue.Count > 0 || _shown != null) Clear();
                return;
            }
            if (_shown != null || _queue.Count == 0 || now < _nextAt) return;
            byte id = _queue.Dequeue();
            try
            {
                Show(id);
                _until = now + Seconds;
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[ui] can't show the ready banner: {e.Message}");
                Hide();
            }
        }

        private static void Show(byte id)
        {
            var top = Il2Cpp.GameAPP.canvasUp;
            if (top == null || !GameUi.Load()) return;
            var area = top.GetComponent<RectTransform>().rect;
            var pos = new Vector2((AtX - 0.5f) * area.width, (0.5f - AtY) * area.height);
            var size = new Vector2(440, 58);

            _shown = GameUi.Empty(top, "FusionCoopReady", pos, size);
            var plate = _shown.AddComponent<Image>();
            plate.sprite = GameUi.BlankSprite;
            plate.color = new Color(0f, 0f, 0f, 0.6f);

            string name = Session.Names.TryGetValue(id, out var n) ? n : "";
            string you = id == Session.LocalId ? " (you)" : "";
            var color = Overlay.PlayerColors[id % Overlay.PlayerColors.Length];
            GameUi.Label(_shown.transform, $"P{id + 1} {name}{you} is ready", Vector2.zero, size - new Vector2(20, 0), 30, color);
        }
    }
}

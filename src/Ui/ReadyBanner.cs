using System.Collections.Generic;
using FusionCoop.Net;
using Il2CppTMPro;
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
            var size = new Vector2(400, 70);

            // the game's small stone button (as the Stone owner tags), stretched; nine-sliced when the sprite allows
            _shown = GameUi.Empty(top, "FusionCoopReady", pos, size);
            var plate = _shown.AddComponent<Image>();
            var stone = GameUi.StoneSprite;
            plate.sprite = stone != null ? stone : GameUi.BlankSprite;
            if (stone == null) plate.color = new Color(0f, 0f, 0f, 0.6f);
            else if (stone.border.sqrMagnitude > 0) plate.type = Image.Type.Sliced;

            string name = Session.Names.TryGetValue(id, out var n) ? n : "";
            string you = id == Session.LocalId ? " (you)" : "";
            var color = Overlay.PlayerColors[id % Overlay.PlayerColors.Length];
            var t = GameUi.Label(_shown.transform, $"P{id + 1} {name}{you} is ready", new Vector2(0, 2), size - new Vector2(40, 10), 28, color);
            t.fontStyle = FontStyles.Bold;
            t.outlineWidth = 0.28f;
            t.outlineColor = new Color32(30, 20, 10, 255);
        }
    }
}

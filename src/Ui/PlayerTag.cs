using FusionCoop.Net;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FusionCoop.Ui
{
    // "P1", "P2", ... of this window's player, on the game's stone plate, under the sun counter
    // (design/Player.png), while in a level in a room.
    internal static class PlayerTag
    {
        // centre of the plate as a share of the screen, from the left and from the top
        private const float AtX = 0.033f, AtY = 0.17f;

        private static GameObject _shown;
        private static int _shownId = -1;
        private static double _nextTry;

        internal static void Update()
        {
            bool want = Session.Active && Session.Ready && Il2Cpp.Board.Instance != null;
            if (!want)
            {
                Hide();
                return;
            }
            if (_shown != null && _shownId == Session.LocalId) return;
            if (Time.realtimeSinceStartup < _nextTry) return;
            _nextTry = Time.realtimeSinceStartup + 1;
            Hide();
            try
            {
                Show(Session.LocalId);
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[ui] can't show the player tag: {e.Message}");
                Hide();
            }
        }

        private static void Hide()
        {
            if (_shown != null) Object.Destroy(_shown);
            _shown = null;
            _shownId = -1;
        }

        private static void Show(byte id)
        {
            var top = Il2Cpp.GameAPP.canvasUp;
            if (top == null || !GameUi.Load()) return;
            var area = top.GetComponent<RectTransform>().rect;
            var pos = new Vector2((AtX - 0.5f) * area.width, (0.5f - AtY) * area.height);
            var size = new Vector2(96, 48);

            _shown = GameUi.Empty(top, "FusionCoopPlayerTag", pos, size);
            var plate = _shown.AddComponent<Image>();
            var stone = GameUi.StoneSprite;
            plate.sprite = stone != null ? stone : GameUi.BlankSprite;
            if (stone == null) plate.color = new Color(0f, 0f, 0f, 0.6f);
            else if (stone.border.sqrMagnitude > 0) plate.type = Image.Type.Sliced;

            var color = Overlay.PlayerColors[id % Overlay.PlayerColors.Length];
            var t = GameUi.Label(_shown.transform, $"P{id + 1}", new Vector2(0, 2), size - new Vector2(12, 6), 30, color);
            t.fontStyle = FontStyles.Bold;
            t.outlineWidth = 0.28f;
            t.outlineColor = new Color32(30, 20, 10, 255);
            _shownId = id;
            FusionCoopMod.Log.Msg($"[ui] player tag P{id + 1} shown");
        }
    }
}

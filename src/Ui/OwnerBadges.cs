using System.Collections.Generic;
using System.Text;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;

namespace FusionCoop.Ui
{
    // Who planted what: "P1" / "P2" tags in the player's colour, in outlined game font, on a plate the player
    // picks (Tab > Tag style): the game's small stone button, the gold edit box, or none.
    // - Tab > Show plant owners on: a tag in the bottom-right corner of every planted cell.
    // - Off: only the tag of the cell under the mouse.
    // Tags belong to the lawn cell, not the plant (plants' pivots differ by type), and are drawn at the top
    // of the plants' sorting layer: the plants of the row below don't cover them, the game's windows do. A cell with plants of several players
    // (a pumpkin round another's plant) lists them all.
    internal static class OwnerBadges
    {
        // Lawn grid in world units (Mouse.GetColumnFromX / GetRowFromY).
        internal const float CellW = 1.35f;
        private const float Left = -5.6f;
        private const float RowH = 1.67f, Top = 3.7f;           // 5 rows
        private const float RowH6 = 1.45f, Top6 = 4.0f;         // 6 rows, roof
        private static readonly Vector2 TagSize = new(0.5f, 0.26f);

        private sealed class Badge
        {
            internal GameObject Root;
            internal SpriteRenderer Plate;
            internal TextMeshPro Text;
            internal string Shown;
            internal int Count;
            internal int Style = -1;
        }

        private static readonly Dictionary<(int col, int row), Badge> Badges = new();
        private static readonly Dictionary<(int col, int row), int> CellOwners = new(); // bit per player
        private static readonly List<(int, int)> Gone = new();

        internal static void Update()
        {
            var board = Board.Instance;
            if (board == null || Coop.PlantSync.Owners.Count == 0)
            {
                Clear();
                return;
            }

            CellOwners.Clear();
            foreach (var o in Coop.PlantSync.Owners.Values)
            {
                var p = o.Plant;
                if (p == null) continue;
                var cell = (p.thePlantColumn, p.thePlantRow);
                CellOwners.TryGetValue(cell, out int bits);
                CellOwners[cell] = bits | (1 << (o.Owner & 7));
            }

            (int, int) hovered = (-99, -99);
            if (!CoopWindows.ShowOwners && MouseCell(board, out var mc) && CellOwners.ContainsKey(mc)) hovered = mc;

            Gone.Clear();
            foreach (var kv in Badges)
                if (!CellOwners.ContainsKey(kv.Key) || (!CoopWindows.ShowOwners && kv.Key != hovered)) Gone.Add(kv.Key);
            foreach (var cell in Gone) Remove(cell);

            foreach (var kv in CellOwners)
            {
                if (!CoopWindows.ShowOwners && kv.Key != hovered) continue;
                if (!Badges.TryGetValue(kv.Key, out var b) || b.Root == null)
                {
                    b = Make();
                    if (b == null) return;
                    Badges[kv.Key] = b;
                }
                Paint(b, kv.Value);
                var (right, bottom) = CellRect(board, kv.Key.Item1, kv.Key.Item2);
                b.Root.transform.position = new Vector3(right - Width(b) / 2 - 0.06f, bottom + TagSize.y / 2 + 0.06f, 0f);
            }
        }

        internal static void Clear()
        {
            foreach (var b in Badges.Values)
                if (b.Root != null) Object.Destroy(b.Root);
            Badges.Clear();
        }

        private static void Remove((int, int) cell)
        {
            if (Badges.TryGetValue(cell, out var b) && b.Root != null) Object.Destroy(b.Root);
            Badges.Remove(cell);
        }

        // ---------- grid ----------

        internal static bool SixRows(Board board) => board.rowNum == 6 || board.boardTag.isRoof;

        internal static float RowHeight(Board board) => SixRows(board) ? RowH6 : RowH;

        // right and bottom edges of a lawn cell
        internal static (float right, float bottom) CellRect(Board board, int col, int row)
        {
            float h = SixRows(board) ? RowH6 : RowH, top = SixRows(board) ? Top6 : Top;
            return (Left + CellW * (col + 1), top - h * (row + 1));
        }

        internal static bool MouseCell(Board board, out (int, int) cell)
        {
            cell = (-1, -1);
            var cam = Camera.main;
            if (cam == null) return false;
            var w = cam.ScreenToWorldPoint(Input.mousePosition);
            float h = SixRows(board) ? RowH6 : RowH, top = SixRows(board) ? Top6 : Top;
            if (w.x < Left || w.y > top) return false;
            int col = (int)((w.x - Left) / CellW), row = (int)((top - w.y) / h);
            if (col >= board.columnNum || row >= board.rowNum) return false;
            cell = (col, row);
            return true;
        }

        // ---------- drawing ----------

        // The plants' sorting layer, at the top of it: over every plant, under the game's windows
        // (Main Menu, pause, ...), which are on higher layers.
        private static int _plantLayer = int.MinValue;
        internal const int TagOrder = 32000; // sortingOrder tops out at 32767

        // Plants are sorted by their SortingGroup, on a layer per lawn row ("plant0", "plant1", ...; zombies
        // likewise). Tags and pings take the highest of those layers, so they are in front of every row, and
        // stay under the game's windows, which are on higher layers.
        internal static int PlantLayer
        {
            get
            {
                if (_plantLayer != int.MinValue) return _plantLayer;
                var layers = SortingLayer.layers;
                if (layers == null || layers.Length == 0) return 0;
                int best = int.MinValue, bestValue = int.MinValue;
                var names = new StringBuilder();
                foreach (var l in layers)
                {
                    names.Append(l.name).Append(' ');
                    string n = l.name != null ? l.name.ToLowerInvariant() : "";
                    if (!n.StartsWith("plant") && !n.StartsWith("zombie")) continue;
                    if (l.value > bestValue)
                    {
                        bestValue = l.value;
                        best = l.id;
                    }
                }
                _plantLayer = best != int.MinValue ? best : layers[layers.Length - 1].id;
                FusionCoopMod.Log.Msg($"[ui] sorting layers: {names}-> tags and pings on '{SortingLayer.IDToName(_plantLayer)}'");
                return _plantLayer;
            }
        }

        private static Badge Make()
        {
            try
            {
                var root = new GameObject("FusionCoopOwner");
                var b = new Badge { Root = root };
                var plate = new GameObject("Plate");
                plate.transform.SetParent(root.transform, false);
                b.Plate = plate.AddComponent<SpriteRenderer>();
                b.Plate.sortingLayerID = PlantLayer;
                b.Plate.sortingOrder = TagOrder;

                var text = new GameObject("Text");
                text.transform.SetParent(root.transform, false);
                b.Text = text.AddComponent<TextMeshPro>();
                if (GameUi.Font != null) b.Text.font = GameUi.Font;
                b.Text.fontSize = 2.2f;
                b.Text.alignment = TextAlignmentOptions.Center;
                b.Text.enableWordWrapping = false;
                b.Text.richText = true;
                b.Text.rectTransform.sizeDelta = new Vector2(1.2f, 0.4f);
                b.Text.fontStyle = FontStyles.Bold;
                b.Text.outlineWidth = 0.28f;
                b.Text.outlineColor = new Color32(30, 20, 10, 255);
                b.Text.sortingLayerID = PlantLayer;
                b.Text.sortingOrder = TagOrder + 1;
                return b;
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[ui] owner tag failed: {e.Message}");
                CoopWindows.ShowOwners = false;
                return null;
            }
        }

        private static float Width(Badge b) => TagSize.x + 0.36f * Mathf.Max(0, b.Count - 1);

        private static readonly StringBuilder Sb = new();

        private static void Paint(Badge b, int owners)
        {
            Sb.Clear();
            int count = 0;
            for (int i = 0; i < Overlay.PlayerColors.Length; i++)
            {
                if ((owners & (1 << i)) == 0) continue;
                var c = (Color32)Overlay.PlayerColors[i];
                Sb.Append($"<color=#{c.r:X2}{c.g:X2}{c.b:X2}>P{i + 1}</color>");
                count++;
            }
            string text = Sb.ToString();
            int style = FusionCoopMod.OwnerTagStyle;
            if (text == b.Shown && style == b.Style) return;
            b.Shown = text;
            b.Count = count;
            b.Style = style;
            b.Text.text = text;
            b.Text.color = Color.white;
            b.Plate.sprite = style == 0 ? GameUi.StoneSprite : style == 1 ? GameUi.GoldSprite : null;
            b.Plate.gameObject.SetActive(b.Plate.sprite != null);
            if (b.Plate.sprite != null)
            {
                var size = b.Plate.sprite.bounds.size;
                b.Plate.transform.localScale = new Vector3(Width(b) / Mathf.Max(size.x, 0.01f), TagSize.y / Mathf.Max(size.y, 0.01f), 1f);
            }
        }
    }
}

using System.Collections.Generic;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;

namespace FusionCoop.Ui
{
    // "P1" / "P2" tags under the plants players placed (Tab > Show plant owners), in the player's colour in
    // outlined game font, on a plate the player picks (Tab > Tag style): the game's small stone button, the gold
    // edit box, or none. They live in the game world and follow their plant, drawn just above it.
    internal static class OwnerBadges
    {
        private sealed class Badge
        {
            internal GameObject Root;
            internal SpriteRenderer Plate;
            internal TextMeshPro Text;
            internal byte Owner;
        }

        private static readonly Dictionary<int, Badge> Badges = new();
        private static readonly List<int> Gone = new();
        private static readonly Vector3 Offset = new(0f, -0.42f, 0f);

        internal static void Update()
        {
            if (!CoopWindows.ShowOwners || Board.Instance == null)
            {
                Clear();
                return;
            }
            Gone.Clear();
            foreach (var kv in Badges)
                if (!Coop.PlantSync.Owners.TryGetValue(kv.Key, out var o) || o.Plant == null) Gone.Add(kv.Key);
            foreach (var id in Gone) Remove(id);

            foreach (var kv in Coop.PlantSync.Owners)
            {
                var plant = kv.Value.Plant;
                if (plant == null) continue;
                if (!Badges.TryGetValue(kv.Key, out var b) || b.Root == null)
                {
                    b = Make(plant, kv.Value.Owner);
                    if (b == null) continue;
                    Badges[kv.Key] = b;
                }
                if (b.Owner != kv.Value.Owner) Paint(b, kv.Value.Owner);
                b.Root.transform.position = plant.transform.position + Offset;
            }
        }

        internal static void Clear()
        {
            if (Badges.Count == 0) return;
            foreach (var b in Badges.Values)
                if (b.Root != null) Object.Destroy(b.Root);
            Badges.Clear();
        }

        private static void Remove(int id)
        {
            if (Badges.TryGetValue(id, out var b) && b.Root != null) Object.Destroy(b.Root);
            Badges.Remove(id);
        }

        private static Badge Make(Plant plant, byte owner)
        {
            try
            {
                var root = new GameObject("FusionCoopOwner");
                var b = new Badge { Root = root };
                // draw over the plant: its own sorting layer, a higher order
                var pr = plant.GetComponentInChildren<SpriteRenderer>();
                int layer = pr != null ? pr.sortingLayerID : 0, order = (pr != null ? pr.sortingOrder : 0) + 500;

                var plate = new GameObject("Plate");
                plate.transform.SetParent(root.transform, false);
                b.Plate = plate.AddComponent<SpriteRenderer>();
                int style = FusionCoopMod.OwnerTagStyle;
                b.Plate.sprite = style == 0 ? GameUi.StoneSprite : style == 1 ? GameUi.GoldSprite : null;
                plate.SetActive(b.Plate.sprite != null);
                b.Plate.sortingLayerID = layer;
                b.Plate.sortingOrder = order;
                if (b.Plate.sprite != null)
                {
                    var size = b.Plate.sprite.bounds.size;
                    plate.transform.localScale = new Vector3(0.5f / Mathf.Max(size.x, 0.01f), 0.24f / Mathf.Max(size.y, 0.01f), 1f);
                }

                var text = new GameObject("Text");
                text.transform.SetParent(root.transform, false);
                b.Text = text.AddComponent<TextMeshPro>();
                if (GameUi.Font != null) b.Text.font = GameUi.Font;
                b.Text.fontSize = 2.2f;
                b.Text.alignment = TextAlignmentOptions.Center;
                b.Text.enableWordWrapping = false;
                b.Text.rectTransform.sizeDelta = new Vector2(1f, 0.4f);
                b.Text.fontStyle = FontStyles.Bold;
                b.Text.outlineWidth = 0.28f;
                b.Text.outlineColor = new Color32(30, 20, 10, 255);
                b.Text.sortingLayerID = layer;
                b.Text.sortingOrder = order + 1;
                Paint(b, owner);
                return b;
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[ui] owner tag failed: {e.Message}");
                CoopWindows.ShowOwners = false;
                return null;
            }
        }

        private static void Paint(Badge b, byte owner)
        {
            b.Owner = owner;
            b.Plate.color = Color.white;
            b.Text.text = $"P{owner + 1}";
            b.Text.color = Overlay.PlayerColors[owner % Overlay.PlayerColors.Length];
        }
    }
}

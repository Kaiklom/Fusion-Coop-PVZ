using System.Collections.Generic;
using FusionCoop.Net;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using UnityEngine;

namespace FusionCoop.Ui
{
    // Pings: show the other players a lawn cell and what you want done there.
    //
    // Right-click with nothing in hand (the game's own right-click only drops what is held) opens a wheel at
    // the mouse: Plant here / Look here / Dig up / Danger, Cancel in the middle (right-click again or Esc
    // closes it too). The choice is sent to the host, which shows it and passes it on to everyone.
    // On the cell: pulsing rings on the ground in the sender's colour, a round badge on a stalk with the
    // kind's sign, and a stone tag "P2 Plant here"; it lasts Life seconds. A new ping on the same cell
    // replaces the old one. Each player may send MaxBurst pings per Burst seconds.
    //
    // If another mod switched on the game's right-click menu (RightMenu.on: move/delete/upgrade a plant),
    // that menu keeps right-clicks on plants and pings use the empty cells.
    internal static class Pings
    {
        internal enum Kind : byte { Look = 0, Plant = 1, Dig = 2, Danger = 3 }

        private static readonly string[] Names = { "Look here", "Plant here", "Dig up", "Danger!" };
        private static readonly string[] Signs = { "!", "+", "X", "!!" };
        private static readonly Color[] SignColors =
        {
            new(1f, 0.95f, 0.6f), new(0.55f, 1f, 0.45f), new(1f, 0.8f, 0.45f), new(1f, 0.35f, 0.3f),
        };

        private const float Life = 3f, FadeTime = 0.5f;
        private const int MaxBurst = 3;
        private const double Burst = 2.0;

        internal static void Init()
        {
            Session.HostGameHandler += OnHostMessage;
            Session.ClientGameHandler += OnClientMessage;
        }

        internal static void Update()
        {
            var board = Board.Instance;
            if (board == null || !Session.Active || !Session.Ready)
            {
                CloseWheel();
                ClearShown();
                return;
            }
            UpdateWheel(board);
            Animate();
        }

        // ---------- the wheel ----------

        private static GameObject _wheel;
        private static (int col, int row) _wheelCell;
        private static readonly Queue<double> Sent = new();

        private static bool CanPing(Board board)
        {
            if (GameAPP.theGameStatus != GameStatus.InGame) return false;
            if (InitBoard.Instance != null && !InitBoard.Instance.ready) return false;
            var m = Mouse.Instance;
            if (m == null || m.theItemOnMouse != null || m.mouseItemType != MouseItemType.Nothing) return false;
            return GameAPP.canvasUp != null && GameUi.Load();
        }

        private static void UpdateWheel(Board board)
        {
            if (_wheel != null)
            {
                bool close = Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1) ||
                             GameAPP.theGameStatus != GameStatus.InGame;
                if (close) CloseWheel();
                return;
            }
            if (!Input.GetMouseButtonDown(1) || !CanPing(board)) return;
            if (!OwnerBadges.MouseCell(board, out var cell)) return;
            if (GameRightMenuOn() && HasPlants(cell.Item1, cell.Item2)) return;
            OpenWheel(cell);
        }

        private static bool GameRightMenuOn()
        {
            try { return Il2CppCore.RightMenu.RightMenu.on; }
            catch { return false; }
        }

        private static bool HasPlants(int col, int row)
        {
            var list = Lawnf.Get1x1Plants(col, row);
            return list != null && list.Count > 0;
        }

        private static void OpenWheel((int, int) cell)
        {
            _wheelCell = cell;
            var top = GameAPP.canvasUp;
            var area = top.GetComponent<RectTransform>().rect;
            var mouse = Input.mousePosition;
            float x = (mouse.x / Screen.width - 0.5f) * area.width, y = (mouse.y / Screen.height - 0.5f) * area.height;
            // keep the whole wheel on screen
            x = Mathf.Clamp(x, -area.width / 2 + 180, area.width / 2 - 180);
            y = Mathf.Clamp(y, -area.height / 2 + 120, area.height / 2 - 120);
            try
            {
                _wheel = GameUi.Empty(top, "FusionCoopPingWheel", new Vector2(x, y), new Vector2(10, 10));
                var blocker = GameUi.Blocker(_wheel.transform);
                blocker.GetComponent<UnityEngine.UI.Image>().color = new Color(0f, 0f, 0f, 0.12f);
                var size = new Vector2(170, 50);
                GameUi.Button(_wheel.transform, Names[(int)Kind.Plant], new Vector2(0, 85), size, false, () => Choose(Kind.Plant));
                GameUi.Button(_wheel.transform, Names[(int)Kind.Look], new Vector2(175, 0), size, false, () => Choose(Kind.Look));
                GameUi.Button(_wheel.transform, Names[(int)Kind.Dig], new Vector2(0, -85), size, false, () => Choose(Kind.Dig));
                GameUi.Button(_wheel.transform, Names[(int)Kind.Danger], new Vector2(-175, 0), size, false, () => Choose(Kind.Danger));
                GameUi.Button(_wheel.transform, "Cancel", Vector2.zero, new Vector2(120, 44), false, CloseWheel, 18);
                GameAPP.PlaySound(SoundType.Tap, 0.4f, 1f);
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[ping] can't open the wheel: {e.Message}");
                CloseWheel();
            }
        }

        private static void CloseWheel()
        {
            if (_wheel != null) Object.Destroy(_wheel);
            _wheel = null;
        }

        private static void Choose(Kind kind)
        {
            var cell = _wheelCell;
            CloseWheel();
            double now = Session.Now;
            while (Sent.Count > 0 && now - Sent.Peek() > Burst) Sent.Dequeue();
            if (Sent.Count >= MaxBurst) return;
            Sent.Enqueue(now);
            byte me = Session.LocalId;
            Show(me, kind, cell.Item1, cell.Item2);
            var frame = Build(me, kind, cell.Item1, cell.Item2);
            if (Session.Role == Role.Host) Session.SendToClients(frame);
            else Session.SendToHost(frame);
        }

        private static byte[] Build(byte from, Kind kind, int col, int row) =>
            Protocol.Build(Msg.CellPing, w =>
            {
                w.Write(from);
                w.Write((byte)kind);
                w.Write((sbyte)col);
                w.Write((sbyte)row);
            });

        // ---------- network ----------

        private static readonly Dictionary<byte, Queue<double>> HostSeen = new();

        private static void OnHostMessage(byte from, Packet pkt)
        {
            if (pkt.Type != Msg.CellPing) return;
            var r = pkt.Reader();
            r.ReadByte(); // the sender's own id: the host uses the connection's
            var kind = (Kind)r.ReadByte();
            int col = r.ReadSByte(), row = r.ReadSByte();
            if ((byte)kind > (byte)Kind.Danger || Board.Instance == null) return;
            if (!HostSeen.TryGetValue(from, out var q)) HostSeen[from] = q = new Queue<double>();
            double now = Session.Now;
            while (q.Count > 0 && now - q.Peek() > Burst) q.Dequeue();
            if (q.Count >= MaxBurst) return;
            q.Enqueue(now);
            Show(from, kind, col, row);
            Session.SendToClients(Build(from, kind, col, row));
        }

        private static void OnClientMessage(Packet pkt)
        {
            if (pkt.Type != Msg.CellPing) return;
            var r = pkt.Reader();
            byte from = r.ReadByte();
            var kind = (Kind)r.ReadByte();
            int col = r.ReadSByte(), row = r.ReadSByte();
            if (from == Session.LocalId || (byte)kind > (byte)Kind.Danger || Board.Instance == null) return; // own: shown already
            Show(from, kind, col, row);
        }

        // ---------- on the lawn ----------

        private sealed class Shown
        {
            internal GameObject Root;
            internal SpriteRenderer[] Rings;
            internal Transform Badge;
            internal SpriteRenderer[] BadgeParts;
            internal SpriteRenderer Stalk, Plate;
            internal TextMeshPro Sign, Text;
            internal Color Color;
            internal float BadgeY;
            internal float Start;
        }

        private static readonly Dictionary<(int, int), Shown> Active = new();
        private static readonly List<(int, int)> Ended = new();

        private static void Show(byte from, Kind kind, int col, int row)
        {
            var board = Board.Instance;
            if (board == null || col < 0 || row < 0 || col >= board.columnNum || row >= board.rowNum) return;
            if (Active.TryGetValue((col, row), out var old)) Remove(old);
            Active.Remove((col, row));
            try
            {
                var s = Make(board, from, kind, col, row);
                if (s != null) Active[(col, row)] = s;
                GameAPP.PlaySound(kind == Kind.Danger ? SoundType.Buzzer : SoundType.Bleep, 0.5f, 1f);
                FusionCoopMod.Log.Msg($"[ping] P{from + 1} {Names[(int)kind]} at c{col} r{row}");
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[ping] can't show a ping: {e.Message}");
            }
        }

        private static Shown Make(Board board, byte from, Kind kind, int col, int row)
        {
            var (right, bottom) = OwnerBadges.CellRect(board, col, row);
            float h = OwnerBadges.RowHeight(board);
            var color = Overlay.PlayerColors[from % Overlay.PlayerColors.Length];
            int layer = OwnerBadges.PlantLayer, order = OwnerBadges.TagOrder + 100;

            var s = new Shown { Color = color, Start = Time.realtimeSinceStartup };
            s.Root = new GameObject("FusionCoopPing");
            s.Root.transform.position = new Vector3(right - OwnerBadges.CellW / 2, bottom + 0.3f, 0f);

            // rings on the ground
            s.Rings = new SpriteRenderer[3];
            for (int i = 0; i < s.Rings.Length; i++)
                s.Rings[i] = Part(s.Root.transform, "Ring", RingSprite, layer, order, color);

            // stalk and badge above the cell
            float badgeY = h * 0.72f;
            s.Stalk = Part(s.Root.transform, "Stalk", GameUi.BlankSprite, layer, order + 1, color);
            Fit(s.Stalk, 0.05f, badgeY);
            s.Stalk.transform.localPosition = new Vector3(0f, badgeY / 2, 0f);

            var badge = new GameObject("Badge").transform;
            badge.SetParent(s.Root.transform, false);
            badge.localPosition = new Vector3(0f, badgeY, 0f);
            s.Badge = badge;
            s.BadgeY = badgeY;
            var edge = Part(badge, "Edge", DiscSprite, layer, order + 2, color);
            Fit(edge, 0.62f, 0.62f);
            var face = Part(badge, "Face", DiscSprite, layer, order + 3, new Color(0.36f, 0.38f, 0.44f));
            Fit(face, 0.5f, 0.5f);
            s.BadgeParts = new[] { edge, face };
            s.Sign = Text(badge, Signs[(int)kind], 4.2f, layer, order + 4);
            s.Sign.color = SignColors[(int)kind];
            s.Sign.transform.localPosition = new Vector3(0f, 0.02f, 0f);

            // "P2 Plant here" on a stone plate, beside the badge
            var tag = new GameObject("Tag").transform;
            tag.SetParent(badge, false);
            tag.localPosition = new Vector3(0f, 0.5f, 0f);
            s.Plate = Part(tag, "Plate", GameUi.StoneSprite, layer, order + 2, Color.white);
            if (s.Plate.sprite != null) Fit(s.Plate, 1.5f, 0.3f);
            var c = (Color32)color;
            s.Text = Text(tag, $"<color=#{c.r:X2}{c.g:X2}{c.b:X2}>P{from + 1}</color> {Names[(int)kind]}", 2f, layer, order + 3);
            return s;
        }

        private static SpriteRenderer Part(Transform parent, string name, Sprite sprite, int layer, int order, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = sprite;
            r.sortingLayerID = layer;
            r.sortingOrder = order;
            r.color = color;
            return r;
        }

        // Scale a sprite renderer to w x h world units.
        private static void Fit(SpriteRenderer r, float w, float h)
        {
            if (r.sprite == null) return;
            var size = r.sprite.bounds.size;
            r.transform.localScale = new Vector3(w / Mathf.Max(size.x, 0.0001f), h / Mathf.Max(size.y, 0.0001f), 1f);
        }

        private static TextMeshPro Text(Transform parent, string text, float size, int layer, int order)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshPro>();
            if (GameUi.Font != null) t.font = GameUi.Font;
            t.fontSize = size;
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            t.richText = true;
            t.rectTransform.sizeDelta = new Vector2(3f, 0.6f);
            t.fontStyle = FontStyles.Bold;
            t.outlineWidth = 0.28f;
            t.outlineColor = new Color32(30, 20, 10, 255);
            t.sortingLayerID = layer;
            t.sortingOrder = order;
            t.text = text;
            return t;
        }

        private static void Animate()
        {
            if (Active.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            Ended.Clear();
            foreach (var kv in Active)
            {
                var s = kv.Value;
                float age = now - s.Start;
                if (s.Root == null || age > Life)
                {
                    Ended.Add(kv.Key);
                    continue;
                }
                float fade = Mathf.Clamp01((Life - age) / FadeTime);
                for (int i = 0; i < s.Rings.Length; i++)
                {
                    var ring = s.Rings[i];
                    if (ring == null || ring.sprite == null) continue;
                    float p = (age * 1.1f + i / 3f) % 1f;
                    Fit(ring, 0.5f + 0.9f * p, (0.5f + 0.9f * p) * 0.42f);
                    ring.color = new Color(s.Color.r, s.Color.g, s.Color.b, (1f - p) * 0.9f * fade);
                }
                s.Badge.localPosition = new Vector3(0f, s.BadgeY + Mathf.Sin(age * 6f) * 0.04f, 0f);
                SetAlpha(s.Stalk, fade);
                foreach (var part in s.BadgeParts) SetAlpha(part, fade);
                SetAlpha(s.Plate, fade);
                s.Sign.alpha = fade;
                s.Text.alpha = fade;
            }
            foreach (var key in Ended)
            {
                Remove(Active[key]);
                Active.Remove(key);
            }
        }

        private static void SetAlpha(SpriteRenderer r, float a)
        {
            if (r == null) return;
            var c = r.color;
            r.color = new Color(c.r, c.g, c.b, a);
        }

        private static void Remove(Shown s)
        {
            if (s.Root != null) Object.Destroy(s.Root);
        }

        private static void ClearShown()
        {
            foreach (var s in Active.Values) Remove(s);
            Active.Clear();
        }

        // ---------- sprites made here: a ring and a disc ----------

        private static Sprite _ring, _disc;
        private static bool _spritesFailed;

        private static Sprite RingSprite => MakeSprites() ? _ring : null;
        private static Sprite DiscSprite => MakeSprites() ? _disc : GameUi.BlankSprite;

        private static bool MakeSprites()
        {
            if (_ring != null && _disc != null) return true;
            if (_spritesFailed) return false;
            try
            {
                _ring = Circle(0.80f);
                _disc = Circle(0f);
                return true;
            }
            catch (System.Exception e)
            {
                _spritesFailed = true;
                FusionCoopMod.Log.Warning($"[ping] can't make the ring sprites: {e.Message}");
                return false;
            }
        }

        // A white circle 1 world unit across; inner > 0 makes it a ring with that share of the radius hollow.
        private static Sprite Circle(float inner)
        {
            const int n = 128;
            var px = new Color32[n * n];
            float c = (n - 1) / 2f, outer = n / 2f - 1;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float a = Mathf.Clamp01(outer - d + 0.5f);
                    if (inner > 0) a *= Mathf.Clamp01(d - outer * inner + 0.5f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.SetPixels32(new Il2CppStructArray<Color32>(px));
            tex.Apply();
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var sprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }
    }
}

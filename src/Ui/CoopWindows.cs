using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FusionCoop.Net;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;

namespace FusionCoop.Ui
{
    // The co-op windows, built from the game's own UI (GameUi):
    // - main menu: a "Co-op" button next to Settings opens the room window (name, host IP, host / join /
    //   leave, status, players with ping, this PC's IPv4 addresses with copy buttons),
    // - in a level: Tab opens / closes the players window (who is choosing cards or ready, ping, kick,
    //   plant owner labels, force start, rejoin, leave).
    internal static class CoopWindows
    {
        internal static bool ShowOwners; // plant owner labels (off by default)

        private static double _nextScan;
        private static GameObject _menuButton;

        internal static void Init()
        {
            Session.Notice += Notify;
        }

        // Testing (UiTest pref): open the room window on the main menu, and the players window with the owner
        // badges in a level, once each, for screenshots.
        internal static bool TestOpen;
        private static bool _testRoomDone, _testTabDone;
        private static double _testTabAt = -1, _testCloseAt;

        private static void TestStep(bool inLevel)
        {
            if (!TestOpen) return;
            var notice = GameObject.Find("NoticePauseMenu(Clone)");
            if (notice != null) Object.Destroy(notice);
            if (!_testRoomDone && _menuButton != null && Time.realtimeSinceStartup > 30)
            {
                _testRoomDone = true;
                _testCloseAt = Time.realtimeSinceStartup + 20;
                OpenRoom();
            }
            if (_testCloseAt > 0 && Time.realtimeSinceStartup >= _testCloseAt)
            {
                _testCloseAt = 0;
                CloseRoom();
            }
            if (!_testTabDone && inLevel && Session.Active)
            {
                if (_testTabAt < 0) _testTabAt = Time.realtimeSinceStartup + 20;
                else if (Time.realtimeSinceStartup >= _testTabAt)
                {
                    _testTabDone = true;
                    ShowOwners = true;
                    if (Session.Role == Role.Host)
                        for (int i = 0; i < 2; i++)
                        {
                            var p = Coop.PlantSync.Replay(2 + i, 2, PlantType.Peashooter, null, true);
                            if (p != null) Coop.PlantSync.SetOwner(p, (byte)i);
                        }
                    OpenTab();
                }
            }
        }

        internal static void Update()
        {
            double now = Time.realtimeSinceStartup;
            if (now >= _nextScan)
            {
                _nextScan = now + 0.5;
                EnsureMenuButton();
            }
            bool inLevel = Board.Instance != null && MainMenuOpen() == null;
            if (_room != null && inLevel) CloseRoom();
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_rulesWindow != null) CloseRules();
                else if (_room != null) CloseRoom();
            }
            if (inLevel && Session.Active && Input.GetKeyDown(KeyCode.Tab))
            {
                if (_tab != null) CloseTab();
                else OpenTab();
            }
            if (_tab != null && (!inLevel || !Session.Active)) CloseTab();
            TestStep(inLevel);
            Coop.PlantSync.PruneOwners();
            OwnerBadges.Update();
            ReadyBanner.Update();
            if (_room != null) RefreshRoom();
            if (_tab != null) RefreshTab();
        }

        // ---------- notices ----------

        // Only losing the room (disconnected, kicked, rejected) is shown, in the game's message bar; other notices
        // (joined, left, connected) only go to the log: players found them too many.
        private static void Notify(string text)
        {
            FusionCoopMod.Log.Msg($"[notice] {text}");
            if (!text.StartsWith("Disconnected") && !text.StartsWith("rejected")) return;
            try
            {
                var bar = Il2CppCore.InGameText.Instance;
                if (bar != null)
                {
                    bar.ShowText(text, 3f, false, 400f);
                    return;
                }
            }
            catch (System.Exception)
            {
                // fall back below
            }
            Overlay.Alert(text);
        }

        // ---------- main menu button ----------

        private static MainMenu MainMenuOpen()
        {
            var m = Object.FindObjectOfType<MainMenu>();
            return m != null && m.gameObject.activeInHierarchy ? m : null;
        }

        private static RectTransform _optionsButton;

        private static void EnsureMenuButton()
        {
            if (_menuButton != null)
            {
                PlaceMenuButton(); // other mods (the translator) move the small buttons after the menu opens
                return;
            }
            var menu = MainMenuOpen();
            if (menu == null) return;
            var options = menu.transform.Find("Grave/OptionButton");
            if (options == null) return;
            var text = options.GetComponentInChildren<TMP_Text>(true);
            GameUi.TakeFont(text);
            if (!GameUi.Load()) return;
            _optionsButton = options.GetComponent<RectTransform>();
            var rt = _optionsButton;
            var b = GameUi.Button(options.parent, "Co-op", rt.anchoredPosition, rt.sizeDelta, false, OpenRoom,
                text != null ? text.fontSize : 16);
            b.Go.name = "FusionCoopButton";
            // keep the original's anchors (it sits in the menu's layout, not at the centre)
            var mine = b.Go.GetComponent<RectTransform>();
            mine.anchorMin = rt.anchorMin;
            mine.anchorMax = rt.anchorMax;
            mine.pivot = rt.pivot;
            _menuButton = b.Go;
            PlaceMenuButton();
            FusionCoopMod.Log.Msg("[ui] Co-op button added to the main menu");
        }

        // On top of the column of small buttons (Settings, Changelog, Languages, ...) the Settings button is in.
        private static void PlaceMenuButton()
        {
            if (_optionsButton == null) return;
            var mine = _menuButton.transform;
            var parent = _optionsButton.parent;
            // local positions: the buttons use different anchors, so anchoredPosition can't be compared
            var start = _optionsButton.localPosition;
            float h = _optionsButton.sizeDelta.y, top = start.y;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (c == mine || !c.gameObject.activeSelf || c.GetComponent<UIButton>() == null) continue;
                var crt = c.GetComponent<RectTransform>();
                if (crt == null || Mathf.Abs(c.localPosition.x - start.x) > 30 || Mathf.Abs(crt.sizeDelta.y - h) > 10) continue;
                top = Mathf.Max(top, c.localPosition.y);
            }
            var want = new Vector3(start.x, top + h, start.z);
            if ((mine.localPosition - want).sqrMagnitude > 1) mine.localPosition = want;
        }

        // ---------- room window (main menu) ----------

        private static GameObject _room;
        private static TMP_Text _status;
        private static readonly TMP_Text[] _players = new TMP_Text[Protocol.MaxPlayers];
        private static GameUi.Btn _host, _join, _leave;
        private static TMP_InputField _ipField;

        private static Transform Top() => GameAPP.canvasUp != null ? GameAPP.canvasUp : null;

        private static void OpenRoom()
        {
            if (_room != null || Top() == null || !GameUi.Load()) return;
            try
            {
                BuildRoom();
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Error($"[ui] can't build the co-op window: {e}");
                CloseRoom();
            }
        }

        private static void BuildRoom()
        {
            _room = GameUi.Empty(Top(), "FusionCoopRoom", Vector2.zero, new Vector2(10, 10));
            var root = _room.transform;
            GameUi.Blocker(root);
            var size = GameUi.BigFrame(root, Vector2.zero, 860);
            var frame = GameUi.Empty(root, "Content", Vector2.zero, size).transform;
            float top = size.y / 2;

            GameUi.Label(frame, "Co-op", new Vector2(0, top - 108), new Vector2(400, 56), 44, GameUi.Gold);

            GameUi.Label(frame, "Name", new Vector2(-270, top - 165), new Vector2(150, 40), 24, GameUi.Cream, TextAlignmentOptions.Left);
            GameUi.Input(frame, new Vector2(40, top - 165), 440, FusionCoopMod.PlayerName, "Your name", 20, v =>
            {
                if (!string.IsNullOrWhiteSpace(v)) FusionCoopMod.PlayerName = v.Trim();
            });
            GameUi.Label(frame, "Host IP", new Vector2(-270, top - 213), new Vector2(150, 40), 24, GameUi.Cream, TextAlignmentOptions.Left);
            _ipField = GameUi.Input(frame, new Vector2(40, top - 213), 440, FusionCoopMod.LastIp, "e.g. 26.12.34.56", 45, _ => { });

            float by = top - 270;
            _host = GameUi.Button(frame, "Host room", new Vector2(-215, by), new Vector2(200, 52), true, FusionCoopMod.HostRoom, 22);
            _join = GameUi.Button(frame, "Join", new Vector2(0, by), new Vector2(200, 52), true, () =>
            {
                if (Coop.LevelSync.CanRejoin) Coop.LevelSync.RequestRejoin();
                else FusionCoopMod.JoinRoom((_ipField.text ?? "").Trim());
            }, 22);
            _leave = GameUi.Button(frame, "Leave room", new Vector2(215, by), new Vector2(200, 52), true, Session.Stop, 22);

            _status = GameUi.Label(frame, "", new Vector2(0, by - 46), new Vector2(640, 36), 24, GameUi.Gold);
            for (int i = 0; i < _players.Length; i++)
                _players[i] = GameUi.Label(frame, "", new Vector2(0, by - 78 - 26 * i), new Vector2(560, 30), 22, GameUi.Cream,
                    TextAlignmentOptions.Left);

            float iy = by - 78 - 26 * _players.Length - 6;
            GameUi.Label(frame, "This PC's IP - send it to the friend who joins:", new Vector2(0, iy), new Vector2(640, 30), 20,
                GameUi.Dim);
            foreach (var (name, ip) in LocalIps().Take(3))
            {
                iy -= 28;
                GameUi.Label(frame, $"{name}:  {ip}", new Vector2(-70, iy), new Vector2(460, 30), 20, GameUi.Cream,
                    TextAlignmentOptions.Left);
                string copy = ip;
                GameUi.Btn copyButton = null;
                copyButton = GameUi.Button(frame, "Copy", new Vector2(230, iy), new Vector2(90, 34), false, () =>
                {
                    GUIUtility.systemCopyBuffer = copy;
                    FusionCoopMod.Log.Msg($"[notice] Copied {copy}");
                    copyButton.SetText("Copied");
                }, 16);
            }

            GameUi.Button(frame, "Rules", new Vector2(-120, -top + 62), new Vector2(200, 52), true, OpenRules, 22);
            GameUi.Button(frame, "Close", new Vector2(120, -top + 62), new Vector2(200, 52), true, CloseRoom, 22);
            RefreshRoom();
        }

        private static void CloseRoom()
        {
            CloseRules();
            if (_room != null) Object.Destroy(_room);
            _room = null;
        }

        // ---------- rules page (the room window's "Rules" button) ----------

        // What only the host may do and other co-op rules, so the game doesn't have to pop up a message each time.
        private static readonly string[] Rules =
        {
            "Only the host chooses the level.",
            "The battle starts once every player has pressed start.",
            "The host can force start from the players window (Tab).",
            "Only the host can pause; the host's pause stops everyone.",
            "Only the host can restart the level.",
            "The host picks Odyssey buffs, blessings and Magnetar modules.",
            "The host collects the trophy at the end of a level.",
            "Joining a running level: choose your cards and press start.",
            "Main menu leaves the level but not the room; Rejoin brings you back.",
            "Puzzle (I, Zombie) and some challenges are single player only.",
            "In a level, Tab shows players, ping and plant owner tags.",
        };

        private static GameObject _rulesWindow;

        private static void OpenRules()
        {
            if (_rulesWindow != null || Top() == null) return;
            try
            {
                _rulesWindow = GameUi.Empty(Top(), "FusionCoopRules", Vector2.zero, new Vector2(10, 10));
                var root = _rulesWindow.transform;
                // the room window steps aside, so its buttons can't take the clicks
                if (_room != null) _room.SetActive(false);
                GameUi.Blocker(root);
                var size = GameUi.BigFrame(root, Vector2.zero, 860);
                var frame = GameUi.Empty(root, "Content", Vector2.zero, size).transform;
                float top = size.y / 2;
                GameUi.Label(frame, "Co-op rules", new Vector2(0, top - 108), new Vector2(400, 56), 44, GameUi.Gold);
                float y = top - 158, step = 30;
                foreach (var rule in Rules)
                {
                    GameUi.Label(frame, "-", new Vector2(-330, y), new Vector2(20, 28), 20, GameUi.Gold);
                    GameUi.Label(frame, rule, new Vector2(25, y), new Vector2(690, 28), 20, GameUi.Cream, TextAlignmentOptions.Left);
                    y -= step;
                }
                GameUi.Button(frame, "Back", new Vector2(0, -top + 62), new Vector2(200, 52), true, CloseRules, 22);
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Error($"[ui] can't build the rules page: {e}");
                CloseRules();
            }
        }

        private static void CloseRules()
        {
            if (_rulesWindow != null) Object.Destroy(_rulesWindow);
            _rulesWindow = null;
            if (_room != null) _room.SetActive(true);
        }

        private static void RefreshRoom()
        {
            bool offline = !Session.Active;
            bool rejoin = Coop.LevelSync.CanRejoin;
            _host.SetEnabled(offline);
            _join.SetText(rejoin ? "Rejoin level" : "Join");
            _join.SetEnabled(offline || rejoin);
            _leave.SetEnabled(!offline);
            _status.text = StatusText();
            int row = 0;
            foreach (var kv in Session.Names.OrderBy(k => k.Key))
            {
                if (row >= _players.Length) break;
                var t = _players[row++];
                t.text = PlayerLine(kv.Key, kv.Value);
                t.color = Overlay.PlayerColors[kv.Key % Overlay.PlayerColors.Length];
            }
            while (row < _players.Length) _players[row++].text = "";
        }

        private static string StatusText()
        {
            return Session.Role switch
            {
                Role.Host => $"Hosting a room - {Session.Names.Count} player{(Session.Names.Count == 1 ? "" : "s")}",
                Role.Client when Session.Ready => $"Connected as P{Session.LocalId + 1}",
                _ => Cap(Session.Status),
            };
        }

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? "" : char.ToUpper(s[0]) + s.Substring(1);

        private static string PlayerLine(byte id, string name)
        {
            string tag = id == 0 ? "  (host)" : "";
            string you = id == Session.LocalId ? "  - you" : "";
            return $"P{id + 1}  {name}{tag}{you}    {PingText(id)}";
        }

        private static string PingText(byte id)
        {
            if (id == 0) return "";
            double ms = id == Session.LocalId && Session.Role == Role.Client ? Session.RttMs
                : Session.Pings.TryGetValue(id, out var p) ? p : -1;
            return ms >= 0 ? $"{ms:0} ms" : "";
        }

        // This PC's IPv4 addresses with the adapter's name (Radmin VPN, ZeroTier, Tailscale, Wi-Fi, ...).
        private static List<(string name, string ip)> LocalIps()
        {
            var list = new List<(string, string)>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    // virtual adapters of the PC itself (WSL, Hyper-V, VirtualBox, VMware) are no use to a friend
                    string n = (nic.Name + " " + nic.Description).ToLowerInvariant();
                    if (n.Contains("vethernet") || n.Contains("hyper-v") || n.Contains("virtualbox") || n.Contains("vmware") || n.Contains("wsl"))
                        continue;
                    foreach (var a in nic.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                            list.Add((nic.Name, a.Address.ToString()));
                }
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[ui] can't list this PC's addresses: {e.Message}");
            }
            if (list.Count == 0) list.Add(("This PC", "127.0.0.1"));
            return list;
        }

        // ---------- players window (Tab, in a level) ----------

        private static GameObject _tab;
        private const float TabScale = 1.5f;
        private static string _tabLayout;
        private static readonly List<(byte id, TMP_Text name, TMP_Text state, TMP_Text ping)> _rows = new();

        private static string TabLayout()
        {
            string ids = string.Join(",", Session.Names.Keys.OrderBy(k => k));
            return $"{Session.Role}|{ids}|{Coop.LevelSync.Choosing}|{Coop.LevelSync.CanRejoin}";
        }

        private static void OpenTab()
        {
            if (Top() == null || !GameUi.Load()) return;
            try
            {
                BuildTab();
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Error($"[ui] can't build the players window: {e}");
                CloseTab();
            }
        }

        // The small dialog (gameover sprite) is stretched to fit, and its parts scale with it: the skull plate on
        // top (title), the dark screen from DarkTop to DarkBottom of the height (players and options), the stone
        // below it (buttons). The height is chosen so the dark screen holds every row.
        private const float DarkTop = 0.30f, DarkBottom = 0.66f, RowStep = 40f;

        private static void BuildTab()
        {
            _tabLayout = TabLayout();
            var ids = Session.Names.Keys.OrderBy(k => k).ToList();
            bool host = Session.Role == Role.Host;
            int rows = ids.Count + 1; // players + the owner tag options
            float h = Mathf.Max(300f, (RowStep * rows + 16f) / (DarkBottom - DarkTop));
            float scale = Mathf.Min(TabScale, 900f / h);
            _tab = GameUi.Empty(Top(), "FusionCoopPlayers", new Vector2(0, 520 - h * scale / 2), new Vector2(10, 10));
            _tab.transform.localScale = new Vector3(scale, scale, 1f);
            var frame = GameUi.Frame(_tab.transform, Vector2.zero, new Vector2(620, h)).transform;
            float top = h / 2;
            GameUi.Label(frame, "Players", new Vector2(0, top - 0.16f * h), new Vector2(300, 40), 28, GameUi.Gold);

            float y = top - DarkTop * h - RowStep / 2 - 4;
            _rows.Clear();
            foreach (var id in ids)
            {
                var color = Overlay.PlayerColors[id % Overlay.PlayerColors.Length];
                var name = GameUi.Label(frame, "", new Vector2(-135, y), new Vector2(200, 34), 20, color, TextAlignmentOptions.Left);
                var state = GameUi.Label(frame, "", new Vector2(30, y), new Vector2(110, 34), 18, GameUi.Cream);
                var ping = GameUi.Label(frame, "", new Vector2(120, y), new Vector2(70, 34), 18, GameUi.Dim);
                if (host && id != 0)
                {
                    byte kick = id;
                    GameUi.Button(frame, "Kick", new Vector2(198, y), new Vector2(70, 32), false, () => Session.Kick(kick), 16);
                }
                _rows.Add((id, name, state, ping));
                y -= RowStep;
            }

            GameUi.CheckBox(frame, new Vector2(-215, y), ShowOwners, on =>
            {
                ShowOwners = on;
                FusionCoopMod.OwnerTags = on;
            });
            GameUi.Label(frame, "Show plant owners", new Vector2(-90, y), new Vector2(210, 34), 18, GameUi.Cream, TextAlignmentOptions.Left);
            GameUi.Btn style = null;
            style = GameUi.Button(frame, StyleText(), new Vector2(150, y), new Vector2(170, 36), false, () =>
            {
                FusionCoopMod.OwnerTagStyle = (FusionCoopMod.OwnerTagStyle + 1) % 3;
                OwnerBadges.Clear();
                style.SetText(StyleText());
            }, 16);

            // buttons on the stone under the screen
            float by = top - (DarkBottom + 1f) / 2f * h;
            var buttons = new List<(string text, System.Action act)>();
            if (host && Coop.LevelSync.Choosing) buttons.Add(("Force start", Coop.LevelSync.ForceStart));
            if (Coop.LevelSync.CanRejoin) buttons.Add(("Rejoin", Coop.LevelSync.RequestRejoin));
            buttons.Add(("Leave room", () =>
            {
                CloseTab();
                Session.Stop();
            }));
            float bw = 150, gap = 20, bx = -(buttons.Count * bw + (buttons.Count - 1) * gap) / 2 + bw / 2;
            foreach (var (text, act) in buttons)
            {
                GameUi.Button(frame, text, new Vector2(bx, by), new Vector2(bw, 40), false, act, 16);
                bx += bw + gap;
            }
            RefreshTab();
        }

        private static string StyleText() => FusionCoopMod.OwnerTagStyle switch
        {
            0 => "Tag style: Stone",
            1 => "Tag style: Gold",
            _ => "Tag style: Text",
        };

        private static void CloseTab()
        {
            if (_tab != null) Object.Destroy(_tab);
            _tab = null;
        }

        private static void RefreshTab()
        {
            if (TabLayout() != _tabLayout)
            {
                CloseTab();
                OpenTab();
                return;
            }
            foreach (var (id, name, state, ping) in _rows)
            {
                string n = Session.Names.TryGetValue(id, out var s) ? s : "";
                name.text = $"P{id + 1}  {n}{(id == Session.LocalId ? " (you)" : "")}";
                state.text = Coop.LevelSync.PhaseOf(id) switch
                {
                    Coop.LevelSync.Phase.Ready => "ready",
                    Coop.LevelSync.Phase.Choosing => "choosing...",
                    _ => id == 0 ? "host" : "",
                };
                ping.text = PingText(id);
            }
        }
    }
}

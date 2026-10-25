using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FusionCoop.Net;
using Newtonsoft.Json;

namespace FusionCoop.Coop
{
    // Magnetar (a separate cheat / tool mod, optional): in a room its game-changing modules follow the host.
    //
    // Magnetar keeps each module's on/off state (Module.Active, Module.Toggle) and settings (Module.Settings),
    // and saves them to its profile with SaveLoad.SerializeSettingValue / RestoreSettingValue. The host sends
    // the state of the modules that change the game (categories Level, Tools, Plant, Zombie, plus Time Scale and
    // Custom Difficulty) whenever it changes; clients take it with Magnetar's own restore, put it back when the
    // player changes one of those modules, and get their own state back when they leave the room. Visual and
    // other per-window modules stay each player's own. Everything goes through reflection, so FusionCoop works
    // without Magnetar too (a host without it turns those modules off on clients).
    internal static class MagnetarSync
    {
        private static readonly HashSet<string> SyncedCategories = new() { "Level", "Tools", "Plant", "Zombie" };
        private static readonly HashSet<string> SyncedMisc = new(StringComparer.OrdinalIgnoreCase) { "Time Scale", "Custom Difficulty" };

        private sealed class Snap
        {
            public bool A;
            public Dictionary<string, object> S = new();
        }

        private static bool _looked, _present;
        private static IList _modules;
        private static PropertyInfo _name, _category, _active;
        private static FieldInfo _settings, _settingName;
        private static MethodInfo _toggle, _serialize, _restore;
        private static Type _categorySetting;

        private static string _lastSent;
        private static double _nextCheck, _resendAt;
        private static string _hostState;      // client: what the host sent last
        private static string _appliedLocal;   // client: our state right after applying it
        private static string _backup;         // client: our own state before the first apply
        private static double _toastAt;

        internal static void Init()
        {
            Session.ClientGameHandler += OnClientMessage;
            Session.PlayerJoined += _ => _resendAt = 0;
        }

        private static bool Find()
        {
            if (_looked) return _present;
            _looked = true;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Magnetar Client");
                if (asm == null) return false;
                var manager = asm.GetType("Magnetar_Client.Core.ModuleManager");
                var module = asm.GetType("Magnetar_Client.Modules.Module");
                var setting = asm.GetType("Magnetar_Client.UI.Setting.Setting");
                var saveLoad = asm.GetType("Magnetar_Client.Utils.SaveLoad");
                _categorySetting = asm.GetType("Magnetar_Client.UI.Setting.CategorySetting");
                _modules = manager.GetField("Modules", BindingFlags.Public | BindingFlags.Static).GetValue(null) as IList;
                _name = module.GetProperty("Name");
                _category = module.GetProperty("Category");
                _active = module.GetProperty("Active");
                _settings = module.GetField("Settings");
                _toggle = module.GetMethod("Toggle");
                _settingName = setting.GetField("Name");
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                _serialize = saveLoad.GetMethod("SerializeSettingValue", any);
                _restore = saveLoad.GetMethod("RestoreSettingValue", any);
                _present = _modules != null && _name != null && _active != null && _toggle != null && _serialize != null && _restore != null;
                FusionCoopMod.Log.Msg(_present ? "[magnetar] found Magnetar: its game-changing modules follow the host in a room"
                    : "[magnetar] Magnetar found but its layout is not the expected one: not linked");
            }
            catch (Exception e)
            {
                _present = false;
                FusionCoopMod.Log.Warning($"[magnetar] can't link Magnetar: {e.Message}");
            }
            return _present;
        }

        private static bool Synced(object module)
        {
            string cat = _category.GetValue(module)?.ToString() ?? "";
            return SyncedCategories.Contains(cat) || SyncedMisc.Contains((string)_name.GetValue(module));
        }

        private static string Key(object setting)
        {
            string n = (string)_settingName.GetValue(setting);
            return _categorySetting != null && _categorySetting.IsInstanceOfType(setting) ? n + "_Category" : n;
        }

        // The synced modules' state as JSON; "{}" without Magnetar.
        private static string Capture()
        {
            var all = new Dictionary<string, Snap>();
            if (!Find()) return "{}";
            foreach (var m in _modules)
            {
                if (m == null || !Synced(m)) continue;
                var snap = new Snap { A = (bool)_active.GetValue(m) };
                if (_settings.GetValue(m) is IList settings)
                    foreach (var s in settings)
                    {
                        if (s == null || string.IsNullOrEmpty((string)_settingName.GetValue(s))) continue;
                        if (_categorySetting != null && _categorySetting.IsInstanceOfType(s)) continue; // folded or not: the player's
                        var v = _serialize.Invoke(null, new[] { s });
                        if (v != null) snap.S[Key(s)] = v;
                    }
                all[(string)_name.GetValue(m)] = snap;
            }
            return JsonConvert.SerializeObject(all);
        }

        // Puts that state on the synced modules; a synced module missing from it is turned off.
        private static void Apply(string json)
        {
            if (!Find()) return;
            var all = JsonConvert.DeserializeObject<Dictionary<string, Snap>>(json) ?? new Dictionary<string, Snap>();
            foreach (var m in _modules)
            {
                if (m == null || !Synced(m)) continue;
                all.TryGetValue((string)_name.GetValue(m), out var snap);
                try
                {
                    if (snap != null && _settings.GetValue(m) is IList settings)
                        foreach (var s in settings)
                        {
                            if (s == null || string.IsNullOrEmpty((string)_settingName.GetValue(s))) continue;
                            if (snap.S.TryGetValue(Key(s), out var v)) _restore.Invoke(null, new[] { s, v });
                        }
                    bool want = snap != null && snap.A;
                    if ((bool)_active.GetValue(m) != want) _toggle.Invoke(m, null);
                }
                catch (Exception e)
                {
                    FusionCoopMod.Log.Warning($"[magnetar] '{_name.GetValue(m)}' failed: {e.InnerException?.Message ?? e.Message}");
                }
            }
        }

        internal static void Update()
        {
            double now = Session.Now;
            if (now < _nextCheck) return;
            _nextCheck = now + 0.5;
            try
            {
                if (Session.Role == Role.Host && Session.Ready)
                {
                    if (Session.Names.Count < 2) return;
                    string state = Capture();
                    if (state == _lastSent && now < _resendAt) return;
                    if (state.Length > 60000)
                    {
                        FusionCoopMod.Log.Warning($"[magnetar] module state too big to send ({state.Length} chars)");
                        _resendAt = now + 30;
                        return;
                    }
                    _lastSent = state;
                    _resendAt = now + 10;
                    Session.SendToClients(Protocol.Build(Msg.MagnetarState, w => w.Write(state)));
                    return;
                }
                if (Session.Role == Role.Client && Session.Ready)
                {
                    if (_hostState == null || !Find()) return;
                    string local = Capture();
                    if (local == _appliedLocal) return;
                    // the player changed a module the host decides on: back to the host's
                    Apply(_hostState);
                    _appliedLocal = Capture();
                    if (now >= _toastAt)
                    {
                        _toastAt = now + 3;
                        Overlay.Toast("In a room the host decides these Magnetar modules", 3);
                    }
                    return;
                }
                if (Session.Role == Role.None && _backup != null)
                {
                    // left the room: our own modules back
                    Apply(_backup);
                    _backup = null;
                    _hostState = _appliedLocal = null;
                    FusionCoopMod.Log.Msg("[magnetar] own module state restored");
                }
            }
            catch (Exception e)
            {
                FusionCoopMod.Log.Warning($"[magnetar] {e.Message}");
                _nextCheck = now + 10;
            }
        }

        private static void OnClientMessage(Packet pkt)
        {
            if (pkt.Type != Msg.MagnetarState) return;
            string state = pkt.Reader().ReadString();
            if (!Find()) return;
            try
            {
                _backup ??= Capture();
                _hostState = state;
                Apply(state);
                _appliedLocal = Capture();
                FusionCoopMod.Log.Msg("[magnetar] took the host's module state");
            }
            catch (Exception e)
            {
                FusionCoopMod.Log.Warning($"[magnetar] applying the host's modules failed: {e.Message}");
            }
        }
    }
}

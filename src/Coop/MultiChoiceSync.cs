using FusionCoop.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;
using UnityEngine.Events;

namespace FusionCoop.Coop
{
    // MultipleChoiceMenu (a generic "pick one" menu; e.g. Odyssey's starting blessing from
    // TravelLevelData.OnAnimUIOver): the host chooses for everyone.
    //
    // The level registers its options (RegisterOption: title, text, an action) in a fixed order, so an option
    // is named by its index in optionDatas on every window. Refresh deals them onto the windows at random;
    // clicking a window runs its clickEvent: the option's action, then OnSelect closes the menu.
    // - The host's deal is sent and clients show the same options on the same windows.
    // - Clients can't click, confirm, refresh or cancel; the host's pick / refresh / cancel runs on them too.
    internal static class MultiChoiceSync
    {
        private static bool _replaying;
        private static int[] _hostDeal;      // client: option index per window, from the host
        private static int _pendingPick = -1; // client: the host picked before our menu was up
        private static double _pendingAt;

        internal static void Init()
        {
            Session.ClientGameHandler += OnClientMessage;
        }

        private static bool IsClient => Session.Role == Role.Client && Session.Ready;
        private static bool IsHost => Session.Role == Role.Host && Session.Ready;
        private static bool ClientBlocks => IsClient && !_replaying;

        private static bool Refuse()
        {
            if (!ClientBlocks) return true;
            Overlay.Toast("The host chooses");
            return false;
        }

        private static MultipleChoiceMenu MenuOf(UIButton button)
        {
            if (button == null || button.TryCast<BaseWindow>() == null) return null;
            var menu = button.GetComponentInParent<MultipleChoiceMenu>();
            return menu != null && menu.windowOptionPairs != null && menu.windowOptionPairs.ContainsKey(button.Cast<BaseWindow>()) ? menu : null;
        }

        // ---------- client: no own choices ----------

        [HarmonyPatch(typeof(UIButton), "OnMouseUpAsButton")]
        internal static class WindowClickPatch
        {
            private static bool Prefix(UIButton __instance) => !ClientBlocks || MenuOf(__instance) == null || Refuse();
        }

        [HarmonyPatch(typeof(MultipleChoiceMenu), "SelectWindow")]
        internal static class SelectWindowPatch
        {
            private static bool Prefix() => Refuse();
        }

        [HarmonyPatch(typeof(MultipleChoiceMenu), "Confirm")]
        internal static class ConfirmPatch
        {
            private static bool Prefix() => Refuse();
        }

        [HarmonyPatch(typeof(MultipleChoiceMenu), "Cancel")]
        internal static class CancelPatch
        {
            private static bool Prefix() => Refuse();

            private static void Postfix(bool __runOriginal)
            {
                if (!__runOriginal || !IsHost) return;
                Session.SendToClients(Protocol.Build(Msg.MultiChoiceClosed));
                FusionCoopMod.Log.Msg("[choice] host closed the menu -> clients");
            }
        }

        // ---------- the deal ----------

        [HarmonyPatch(typeof(MultipleChoiceMenu), "Refresh")]
        internal static class RefreshPatch
        {
            private static bool Prefix(bool first) => first || Refuse();

            private static void Postfix(MultipleChoiceMenu __instance, bool first, bool __runOriginal)
            {
                if (!__runOriginal || __instance == null) return;
                if (IsClient)
                {
                    ApplyDeal(__instance);
                    return;
                }
                if (!IsHost) return;
                var windows = __instance.windows;
                var deal = new int[windows.Count];
                for (int i = 0; i < deal.Length; i++)
                {
                    var w = windows[i];
                    deal[i] = w != null && __instance.windowOptionPairs.TryGetValue(w, out var od) ? __instance.optionDatas.IndexOf(od) : -1;
                }
                Session.SendToClients(Protocol.Build(Msg.MultiChoiceShown, wr =>
                {
                    wr.Write(first);
                    wr.Write((byte)deal.Length);
                    foreach (var d in deal) wr.Write((short)d);
                }));
                FusionCoopMod.Log.Msg($"[choice] host's options ({(first ? "first" : "refresh")}): {string.Join(",", deal)} of {__instance.optionDatas.Count} -> clients");
            }
        }

        // Start pauses the game after the first deal, so a pick that came early is applied after it.
        [HarmonyPatch(typeof(MultipleChoiceMenu), "Start")]
        internal static class StartPatch
        {
            private static void Postfix(MultipleChoiceMenu __instance)
            {
                if (!IsClient || __instance == null || _pendingPick < 0) return;
                int pick = _pendingPick;
                _pendingPick = -1;
                if (Session.Now - _pendingAt < 15) Pick(__instance, pick);
            }
        }

        private static void ApplyDeal(MultipleChoiceMenu menu)
        {
            if (_hostDeal == null || menu.optionDatas == null || menu.optionDatas.Count == 0) return;
            var windows = menu.windows;
            for (int i = 0; i < windows.Count && i < _hostDeal.Length; i++)
            {
                int idx = _hostDeal[i];
                if (idx < 0 || idx >= menu.optionDatas.Count || windows[i] == null) continue;
                menu.UpdateWindow(windows[i], menu.optionDatas[idx]);
            }
        }

        // ---------- the host's pick ----------

        // A window's clickEvent is (re)built here: the option's action, then OnSelect. On the host one more
        // listener goes last and tells clients which option it was.
        [HarmonyPatch(typeof(MultipleChoiceMenu), "UpdateWindow")]
        internal static class UpdateWindowPatch
        {
            private static void Postfix(MultipleChoiceMenu __instance, BaseWindow window, MultipleChoiceMenu.OptionData optionData)
            {
                if (!IsHost || __instance == null || window == null || optionData == null || window.clickEvent == null) return;
                var menu = __instance;
                var od = optionData;
                window.clickEvent.AddListener((UnityAction)new System.Action(() => HostPicked(menu, od)));
            }
        }

        private static void HostPicked(MultipleChoiceMenu menu, MultipleChoiceMenu.OptionData od)
        {
            if (!IsHost || menu == null || menu.optionDatas == null) return;
            int idx = menu.optionDatas.IndexOf(od);
            if (idx < 0) return;
            Session.SendToClients(Protocol.Build(Msg.MultiChoicePicked, w => w.Write((short)idx)));
            FusionCoopMod.Log.Msg($"[choice] host picked option {idx} ({od.title}) -> clients");
            TravelSync.SendSnapshot();
        }

        private static void Pick(MultipleChoiceMenu menu, int idx)
        {
            if (idx < 0 || idx >= menu.optionDatas.Count) return;
            var od = menu.optionDatas[idx];
            BaseWindow window = null;
            foreach (var w in menu.windows)
                if (w != null && menu.windowOptionPairs.TryGetValue(w, out var o) && o != null && o.Pointer == od.Pointer)
                {
                    window = w;
                    break;
                }
            Run($"pick {idx}", () =>
            {
                if (window != null && window.clickEvent != null) window.clickEvent.Invoke();
                else
                {
                    if (od.call != null) od.call.Invoke();
                    menu.OnSelect();
                }
            });
            FusionCoopMod.Log.Msg($"[choice] host's pick {idx} ({od.title}) applied");
        }

        private static void Run(string what, System.Action action)
        {
            _replaying = true;
            try
            {
                action();
            }
            catch (System.Exception e)
            {
                FusionCoopMod.Log.Warning($"[choice] {what} failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
            }
        }

        private static MultipleChoiceMenu OpenMenu()
        {
            var menu = Object.FindObjectOfType<MultipleChoiceMenu>();
            return menu != null && menu.optionDatas != null && menu.optionDatas.Count > 0 ? menu : null;
        }

        private static void OnClientMessage(Packet pkt)
        {
            switch (pkt.Type)
            {
                case Msg.MultiChoiceShown:
                {
                    var r = pkt.Reader();
                    bool first = r.ReadBoolean();
                    var deal = new int[r.ReadByte()];
                    for (int i = 0; i < deal.Length; i++) deal[i] = r.ReadInt16();
                    _hostDeal = deal;
                    var menu = OpenMenu();
                    if (menu == null) return;
                    if (!first) Run("refresh", () => menu.Refresh(false));
                    else ApplyDeal(menu);
                    break;
                }
                case Msg.MultiChoicePicked:
                {
                    int idx = pkt.Reader().ReadInt16();
                    var menu = OpenMenu();
                    if (menu == null)
                    {
                        _pendingPick = idx;
                        _pendingAt = Session.Now;
                        FusionCoopMod.Log.Msg($"[choice] host picked option {idx}; the menu isn't up here yet");
                        return;
                    }
                    Pick(menu, idx);
                    _hostDeal = null;
                    break;
                }
                case Msg.MultiChoiceClosed:
                {
                    var menu = OpenMenu();
                    if (menu != null) Run("close", menu.Cancel);
                    _hostDeal = null;
                    break;
                }
            }
        }
    }
}

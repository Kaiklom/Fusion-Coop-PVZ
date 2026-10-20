using HarmonyLib;
using UnityEngine;
using Il2Cpp;

namespace FusionCoop.Probe
{
    // Step 0 probes: log the game calls the co-op layer will hook, to confirm the call flow.

    internal static class P
    {
        internal static int Sun => Board.Instance != null ? Board.Instance.theSun : -1;
        internal static void Log(string s) => FusionCoopMod.Log.Msg(s);
    }

    [HarmonyPatch(typeof(CreatePlant), nameof(CreatePlant.SetPlant))]
    internal static class SetPlantProbe
    {
        private static void Postfix(int newColumn, int newRow, PlantType theSeedType, bool isFreeSet, Plant __result)
        {
            P.Log($"[SetPlant] col={newColumn} row={newRow} type={theSeedType} free={isFreeSet} " +
                  $"ok={(__result != null)} sun={P.Sun}");
        }
    }

    [HarmonyPatch(typeof(Board), nameof(Board.UseSun))]
    internal static class UseSunProbe
    {
        private static void Prefix(float count) => P.Log($"[UseSun] -{count} before={P.Sun}");
    }

    [HarmonyPatch(typeof(Board), nameof(Board.GetSun))]
    internal static class GetSunProbe
    {
        private static void Prefix(float count, bool save) => P.Log($"[GetSun] +{count} save={save} before={P.Sun}");
    }

    [HarmonyPatch(typeof(Shovel), nameof(Shovel.Use))]
    internal static class ShovelProbe
    {
        private static void Prefix() => P.Log("[Shovel.Use]");
    }

    [HarmonyPatch(typeof(CoinSun), nameof(CoinSun.Die))]
    internal static class CoinSunDieProbe
    {
        private static void Prefix(CoinSun __instance) =>
            P.Log($"[CoinSun.Die] price={__instance.sunPrice} coinType={__instance.theCoinType} " +
                  $"moveType={__instance.theMoveType}");
    }

    // Picking a card up: LeftClickWithNothing / ClickOnCard -> ClickCard(card)
    [HarmonyPatch(typeof(Mouse), "ClickCard")]
    internal static class ClickCardProbe
    {
        private static void Postfix(CardUI card, bool __result)
        {
            if (card == null) return;
            P.Log($"[ClickCard] type={card.thePlantType} cost={card.theSeedCost} cd={card.CD}/{card.fullCD} picked={__result}");
        }
    }

    // Player planting: LeftClickWithSomeThing -> TryToSetPlantByCard -> SetPlant + UseSun
    [HarmonyPatch(typeof(Mouse), "TryToSetPlantByCard")]
    internal static class TryToSetPlantByCardProbe
    {
        private static void Prefix(Mouse __instance)
        {
            var c = __instance.theCardOnMouse;
            P.Log($"[TryToSetPlantByCard] >> col={__instance.theMouseColumn} row={__instance.theMouseRow} " +
                  $"held={__instance.thePlantTypeOnMouse} card={(c != null ? c.thePlantType.ToString() : "-")} sun={P.Sun}");
        }

        private static void Postfix() => P.Log($"[TryToSetPlantByCard] << sun={P.Sun}");
    }

    // Sun pickup: CoinSun flies to the counter, then MoveToPosition -> Board.GetSun
    [HarmonyPatch(typeof(CoinSun), "MoveToPosition")]
    internal static class CoinSunMoveProbe
    {
        private static readonly System.Collections.Generic.HashSet<int> Seen = new();

        private static void Prefix(CoinSun __instance)
        {
            // MoveToPosition runs every frame while flying; log each object once.
            int id = __instance.GetInstanceID();
            if (!Seen.Add(id)) return;
            P.Log($"[CoinSun.Move] id={id} price={__instance.sunPrice} coinType={__instance.theCoinType} " +
                  $"moveType={__instance.theMoveType}");
        }
    }
}

using FusionCoop.Net;
using Il2Cpp;
using UnityEngine;

namespace FusionCoop.Coop
{
    // Testing hotkeys (pref TestTools), during a battle:
    // - F5 / F6: previous / next zombie type in the test list
    // - 9 (host): spawn that zombie at the right edge of the lane under the cursor; it reaches
    //   clients through the normal zombie sync
    // - F4: +1000 sun to your own wallet
    internal static class TestTools
    {
        internal static bool Enabled;

        private static readonly (ZombieType type, string name)[] Zombies =
        {
            (ZombieType.DollDiamond, "Diamond box (-> gold -> silver -> plain)"),
            (ZombieType.Gargantuar, "Gargantuar"),
            (ZombieType.RedGargantuar, "Red Gargantuar"),
            (ZombieType.IronGargantuar, "Iron Gargantuar"),
            (ZombieType.ImpZombie, "Imp"),
            (ZombieType.JacksonZombie, "Dancing Zombie"),
            (ZombieType.DriverZombie, "Zomboni"),
            (ZombieType.SuperDriver, "Super Zomboni"),
            (ZombieType.CatapultZombie, "Catapult"),
            (ZombieType.LadderZombie, "Ladder"),
            (ZombieType.MachineNutZombie, "Machine Nut"),
            (ZombieType.TallNutFootballZombie, "Tall-nut Football (drops a helmet)"),
            (ZombieType.BucketNutZombie, "Bucket Nut (drops a bucket)"),
            (ZombieType.WallNutZombie, "Wall-nut Zombie"),
            (ZombieType.BucketZombie, "Buckethead"),
            (ZombieType.DoorZombie, "Screen Door"),
            (ZombieType.FootballZombie, "Football"),
            (ZombieType.PolevaulterZombie, "Pole Vaulter"),
            (ZombieType.PogoZombie, "Pogo"),
            (ZombieType.JackboxZombie, "Jack-in-the-Box"),
            (ZombieType.BalloonZombie, "Balloon"),
            (ZombieType.MinerZombie, "Digger"),
            (ZombieType.BungiZombie, "Bungee"),
        };

        private static int _index;

        internal static string Selected => Zombies[_index].name;

        internal static bool InBattle =>
            Board.Instance != null && (InitBoard.Instance == null || InitBoard.Instance.ready);

        internal static void HandleKeys()
        {
            if (!Enabled) return;
            if (!InBattle)
            {
                bool pressed = Input.GetKeyDown(KeyCode.F4) || Input.GetKeyDown(KeyCode.F5) || Input.GetKeyDown(KeyCode.F6) ||
                               Input.GetKeyDown(KeyCode.Alpha9) || Input.GetKeyDown(KeyCode.Keypad9);
                if (pressed && Board.Instance != null) Overlay.Toast("Test keys work once the battle has started");
                return;
            }
            if (Input.GetKeyDown(KeyCode.F5)) Select(-1);
            if (Input.GetKeyDown(KeyCode.F6)) Select(+1);
            if (Input.GetKeyDown(KeyCode.Alpha9) || Input.GetKeyDown(KeyCode.Keypad9)) Spawn();
            if (Input.GetKeyDown(KeyCode.F4))
            {
                Board.Instance.GetSun(1000, false);
                Overlay.Toast("test: +1000 sun");
            }
        }

        private static void Select(int step)
        {
            _index = (_index + step + Zombies.Length) % Zombies.Length;
            Overlay.Toast($"test zombie: {Selected}   [9] spawn in the lane under the cursor");
        }

        private static void Spawn()
        {
            if (Session.Role == Role.Client)
            {
                Overlay.Toast("Only the host can spawn test zombies");
                return;
            }
            var mouse = Mouse.Instance;
            int row = mouse != null ? mouse.theMouseRow : -1;
            if (row < 0 || row >= Board.Instance.rowNum)
            {
                Overlay.Toast("Point the cursor at a lane, then press 9");
                return;
            }
            var (type, name) = Zombies[_index];
            var z = CreateZombie.Instance != null ? CreateZombie.Instance.SetZombie(row, type, 9.9f, false) : null;
            Overlay.Toast(z != null ? $"test: {name} in lane {row}" : $"test: could not spawn {name} in lane {row}");
            FusionCoopMod.Log.Msg($"[test] spawn {type} row {row} -> {(z != null ? "ok" : "FAILED")}");
        }
    }
}

using System.Text;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FusionCoop
{
    // Research (UiDump pref): writes the game's own UI to UserData/ui_dump.txt once the main menu is up:
    // the live canvases and some window prefabs (UI/Prefabs/<UIType>), with each object's components,
    // sprites, texts and sizes, to find buttons, frames and input fields the co-op windows can reuse.
    internal static class UiDump
    {
        internal static bool Enabled;
        private static bool _done;
        private static double _at = -1;

        private static readonly string[] Prefabs =
        {
            "MainMenu", "PauseMenu", "ConfirmMenu", "ReNameMenu", "CustomOnlineMenu", "OptionMenu",
            "PlayerSaveMenu", "InputKeyMenu", "LevelSaveMenu", "NoticePauseMenu", "CustomMenu",
        };

        internal static void Update()
        {
            if (!Enabled || _done) return;
            if (_at < 0) _at = Time.realtimeSinceStartup + 25;
            if (Time.realtimeSinceStartup < _at) return;
            _done = true;
            var sb = new StringBuilder();
            try
            {
                sb.AppendLine("==== live canvases");
                foreach (var c in Object.FindObjectsOfType<Canvas>())
                    if (c != null && c.isRootCanvas) Dump(sb, c.transform, 0);
                foreach (var name in Prefabs)
                {
                    sb.AppendLine($"==== prefab UI/Prefabs/{name}");
                    var go = Resources.Load<GameObject>("UI/Prefabs/" + name);
                    if (go == null) sb.AppendLine("  (not found)");
                    else Dump(sb, go.transform, 0);
                }
            }
            catch (System.Exception e)
            {
                sb.AppendLine($"!! {e}");
            }
            string path = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "ui_dump.txt");
            System.IO.File.WriteAllText(path, sb.ToString());
            FusionCoopMod.Log.Msg($"[ui] dumped the game's UI to {path}");
        }

        private static void Dump(StringBuilder sb, Transform t, int depth)
        {
            if (depth > 10) return;
            var go = t.gameObject;
            sb.Append(' ', depth * 2).Append(go.name);
            if (!go.activeSelf) sb.Append(" [off]");
            var rt = t.TryCast<RectTransform>();
            if (rt != null) sb.Append($" size={rt.rect.width:0}x{rt.rect.height:0} pos={rt.anchoredPosition.x:0},{rt.anchoredPosition.y:0}");
            sb.Append(" :");
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                string type = c.GetIl2CppType().Name;
                if (type == "RectTransform" || type == "Transform" || type == "CanvasRenderer") continue;
                sb.Append(' ').Append(type);
                var img = c.TryCast<Image>();
                if (img != null && img.sprite != null) sb.Append($"(sprite={img.sprite.name} type={img.type})");
                var sr = c.TryCast<SpriteRenderer>();
                if (sr != null && sr.sprite != null) sb.Append($"(sprite={sr.sprite.name})");
                var tmp = c.TryCast<TMP_Text>();
                if (tmp != null) sb.Append($"(text=\"{Short(tmp.text)}\" font={(tmp.font != null ? tmp.font.name : "-")} size={tmp.fontSize:0})");
                var txt = c.TryCast<Text>();
                if (txt != null) sb.Append($"(text=\"{Short(txt.text)}\" font={(txt.font != null ? txt.font.name : "-")} size={txt.fontSize})");
            }
            sb.AppendLine();
            for (int i = 0; i < t.childCount; i++) Dump(sb, t.GetChild(i), depth + 1);
        }

        private static string Short(string s)
        {
            if (s == null) return "";
            s = s.Replace("\n", "\\n");
            return s.Length > 40 ? s.Substring(0, 40) + "…" : s;
        }
    }
}

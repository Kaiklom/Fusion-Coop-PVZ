using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace FusionCoop.Ui
{
    // Builds co-op windows out of the game's own UI pieces, so they look and click like the game's:
    // - buttons: the small button of ConfirmMenu (UIButton + BoxCollider2D, clicked through the mouse
    //   ray like every game button), with the big button sprite of PauseMenu when asked,
    // - text: the TextMeshPro text of ConfirmMenu, in the font the main menu uses (the translator's),
    // - input fields: the name box of ReNameMenu (editbox + TMP_InputField),
    // - check boxes: the options_checkbox sprites of OptionMenu on a button,
    // - frames: the dialog of ConfirmMenu (gameover sprite).
    // Everything is positioned from the parent's centre (anchors and pivot in the middle).
    internal static class GameUi
    {
        private static Transform _button, _text, _input, _bigFrame;
        private static Sprite _bigButton, _frame, _check0, _check1;
        private static TMP_FontAsset _font;
        private static Sprite _blank; // a plain white sprite (UISprite of the scroll bars)
        private static bool _loaded, _failed;

        internal static Sprite BlankSprite => _blank;
        internal static Sprite StoneSprite => _button != null ? _button.GetComponent<Image>().sprite : null;
        internal static Sprite GoldSprite => _input != null ? _input.GetComponent<Image>().sprite : null;
        internal static TMP_FontAsset Font => _font;

        internal static readonly Color Gold = new(1f, 0.85f, 0.3f);
        internal static readonly Color Cream = new(1f, 0.96f, 0.86f);
        internal static readonly Color Dim = new(0.75f, 0.75f, 0.7f);

        internal static bool Load()
        {
            if (_loaded) return true;
            if (_failed) return false;
            try
            {
                var confirm = Resources.Load<GameObject>("UI/Prefabs/ConfirmMenu");
                var rename = Resources.Load<GameObject>("UI/Prefabs/ReNameMenu");
                var pause = Resources.Load<GameObject>("UI/Prefabs/PauseMenu");
                var option = Resources.Load<GameObject>("UI/Prefabs/OptionMenu");
                _button = confirm.transform.Find("Image");
                _text = confirm.transform.Find("window/text");
                _frame = confirm.transform.Find("window").GetComponent<Image>().sprite;
                _input = rename.transform.Find("Bg");
                var notice = Resources.Load<GameObject>("UI/Prefabs/NoticePauseMenu");
                _bigFrame = notice != null ? notice.transform.Find("BigMenuBank") : null;
                _bigButton = pause.transform.Find("restart").GetComponent<Image>().sprite;
                foreach (var img in option.GetComponentsInChildren<Image>(true))
                {
                    if (img.sprite == null) continue;
                    if (img.sprite.name == "UISprite") _blank = img.sprite;
                    if (img.sprite.name == "options_checkbox0") _check0 = img.sprite;
                    if (img.sprite.name == "options_checkbox1") _check1 = img.sprite;
                }
                if (_button == null || _text == null || _input == null || _frame == null)
                    throw new System.Exception("a template is missing");
                _loaded = true;
                FusionCoopMod.Log.Msg("[ui] game UI templates loaded");
            }
            catch (System.Exception e)
            {
                _failed = true;
                FusionCoopMod.Log.Error($"[ui] can't load the game's UI templates: {e.Message}");
            }
            return _loaded;
        }

        // The main menu's font (the translator swaps in its own); Chinese "Dynamic" otherwise.
        internal static void TakeFont(TMP_Text from)
        {
            if (from != null && from.font != null) _font = from.font;
        }

        // ---------- pieces ----------

        internal static RectTransform Place(GameObject go, Transform parent, Vector2 pos, Vector2 size)
        {
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one;
            return rt;
        }

        internal static GameObject Empty(Transform parent, string name, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            go.layer = parent.gameObject.layer;
            Place(go, parent, pos, size);
            return go;
        }

        internal static Image Frame(Transform parent, Vector2 pos, Vector2 size)
        {
            var go = Empty(parent, "Frame", pos, size);
            var img = go.AddComponent<Image>();
            img.sprite = _frame;
            return img;
        }

        // The big stone dialog of the game's notice (Disclaimer) window: 20 pieces, scaled to `width` and
        // centred on `pos`. Returns its size in the parent's units. Falls back to the small dialog.
        internal static Vector2 BigFrame(Transform parent, Vector2 pos, float width)
        {
            if (_bigFrame == null)
            {
                var size = new Vector2(width, width * 0.9f);
                Frame(parent, pos, size);
                return size;
            }
            var holder = Empty(parent, "BigFrame", pos, new Vector2(10, 10)).transform;
            var bank = Object.Instantiate(_bigFrame.gameObject).transform;
            bank.SetParent(holder, false);
            bank.localPosition = Vector3.zero;
            // bounds of the pieces in the holder's space (RectTransformUtility's helper is stripped in this build)
            var b = new Bounds();
            bool any = false;
            var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            foreach (var rt in bank.GetComponentsInChildren<RectTransform>(true))
            {
                if (rt.transform == bank) continue;
                rt.GetWorldCorners(corners);
                for (int c = 0; c < 4; c++)
                {
                    var p = holder.InverseTransformPoint(corners[c]);
                    if (!any) b = new Bounds(p, Vector3.zero);
                    else b.Encapsulate(p);
                    any = true;
                }
            }
            if (!any || b.size.x <= 0) b = new Bounds(Vector3.zero, new Vector3(width, width * 0.9f, 0));
            float k = width / b.size.x;
            bank.localScale = bank.localScale * k;
            bank.localPosition = new Vector3(-b.center.x * k, -b.center.y * k, 0);
            return new Vector2(b.size.x * k, b.size.y * k);
        }

        // A see-through dark sheet over the whole screen that also takes the clicks meant for what is behind.
        internal static GameObject Blocker(Transform parent)
        {
            // takes the clicks meant for the menu behind (the tint doesn't show on this canvas yet)
            var go = Empty(parent, "Blocker", Vector2.zero, new Vector2(3000, 2000));
            var img = go.AddComponent<Image>();
            img.sprite = _blank != null ? _blank : _frame;
            img.type = Image.Type.Simple;
            img.color = new Color(0f, 0f, 0f, 0.5f);
            var col = go.AddComponent<BoxCollider2D>();
            col.size = new Vector2(3000, 2000);
            // behind the window's buttons for the mouse ray (same depth = the game may pick either collider)
            var lp = go.transform.localPosition;
            go.transform.localPosition = new Vector3(lp.x, lp.y, lp.z + 100f);
            return go;
        }

        internal static TMP_Text Label(Transform parent, string text, Vector2 pos, Vector2 size, float fontSize,
            Color color, TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            var go = Object.Instantiate(_text.gameObject);
            go.name = "Label";
            Place(go, parent, pos, size);
            var t = go.GetComponent<TMP_Text>();
            if (_font != null) t.font = _font;
            t.enableAutoSizing = false;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = align;
            t.text = text;
            return t;
        }

        internal sealed class Btn
        {
            internal GameObject Go;
            internal UIButton Button;
            internal TMP_Text[] Texts;

            internal void SetText(string text)
            {
                foreach (var t in Texts) t.text = text;
            }

            internal void SetEnabled(bool on)
            {
                Button.Interactable = on;
                var img = Go.GetComponent<Image>();
                if (img != null) img.color = on ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);
                foreach (var t in Texts) t.alpha = on ? 1f : 0.4f;
            }
        }

        internal static Btn Button(Transform parent, string text, Vector2 pos, Vector2 size, bool big, System.Action onClick,
            float fontSize = 20)
        {
            var go = Object.Instantiate(_button.gameObject);
            go.name = "Button " + text;
            Place(go, parent, pos, size);
            var img = go.GetComponent<Image>();
            if (big && _bigButton != null) img.sprite = _bigButton;
            var col = go.GetComponent<BoxCollider2D>();
            if (col != null)
            {
                col.size = size;
                col.offset = Vector2.zero;
            }
            var texts = go.GetComponentsInChildren<TMP_Text>(true);
            foreach (var t in texts)
            {
                var trt = t.GetComponent<RectTransform>();
                trt.sizeDelta = size;
                if (_font != null) t.font = _font;
                t.enableAutoSizing = false;
                t.fontSize = fontSize;
                t.enableWordWrapping = false;
                t.text = text;
            }
            var b = go.GetComponent<UIButton>();
            b.clickEvent = new UnityEvent();
            b.clickEvent.AddListener((UnityAction)new System.Action(() =>
            {
                try
                {
                    onClick();
                }
                catch (System.Exception e)
                {
                    FusionCoopMod.Log.Warning($"[ui] '{text}' failed: {e.Message}");
                }
            }));
            return new Btn { Go = go, Button = b, Texts = texts };
        }

        internal static TMP_InputField Input(Transform parent, Vector2 pos, float width, string value, string placeholder,
            int limit, System.Action<string> onChange)
        {
            var go = Object.Instantiate(_input.gameObject);
            go.name = "Input";
            Place(go, parent, pos, new Vector2(width, 44));
            var field = go.GetComponentInChildren<TMP_InputField>(true);
            var frt = field.GetComponent<RectTransform>();
            frt.anchorMin = frt.anchorMax = frt.pivot = new Vector2(0.5f, 0.5f);
            frt.anchoredPosition = Vector2.zero;
            frt.sizeDelta = new Vector2(width - 24, 30);
            foreach (var t in field.GetComponentsInChildren<TMP_Text>(true))
            {
                var trt = t.GetComponent<RectTransform>();
                trt.anchorMin = Vector2.zero;
                trt.anchorMax = Vector2.one;
                trt.offsetMin = trt.offsetMax = Vector2.zero;
                if (_font != null) t.font = _font;
                t.enableAutoSizing = false;
                t.fontSize = 26;
                t.alignment = TextAlignmentOptions.Left;
                t.enableWordWrapping = false;
            }
            var area = field.textViewport;
            if (area != null)
            {
                area.anchorMin = Vector2.zero;
                area.anchorMax = Vector2.one;
                area.offsetMin = area.offsetMax = Vector2.zero;
            }
            if (field.placeholder != null)
            {
                var ph = field.placeholder.TryCast<TMP_Text>();
                if (ph != null) ph.text = placeholder;
            }
            field.characterLimit = limit;
            field.text = value ?? "";
            field.onValueChanged.AddListener((UnityAction<string>)new System.Action<string>(v => onChange(v)));
            return field;
        }

        internal sealed class Check
        {
            internal Btn Box;
            internal bool On;
            internal void Show() => Box.Go.GetComponent<Image>().sprite = On ? _check1 : _check0;
        }

        internal static Check CheckBox(Transform parent, Vector2 pos, bool value, System.Action<bool> onChange)
        {
            var c = new Check { On = value };
            c.Box = Button(parent, "", pos, new Vector2(36, 36), false, () =>
            {
                c.On = !c.On;
                c.Show();
                onChange(c.On);
            });
            foreach (var t in c.Box.Texts) t.gameObject.SetActive(false);
            if (_check0 != null) c.Show();
            return c;
        }
    }
}

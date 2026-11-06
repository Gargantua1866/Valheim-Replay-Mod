using UnityEngine;

namespace ReplayMod
{
    // Opaque IMGUI styles: the default skin's window is too see-through over the game.
    internal static class UiStyle
    {
        public static readonly Color Panel = new Color(0.08f, 0.08f, 0.10f, 0.97f);
        public static readonly Color Track = new Color(0.20f, 0.20f, 0.24f, 1f);
        public static readonly Color Played = new Color(0.77f, 0.63f, 0.36f, 1f);
        public static readonly Color Tick = new Color(1f, 1f, 1f, 0.25f);

        private static GUIStyle _window, _label;
        private static Texture2D _panelTex;

        public static Texture2D PanelTex => _panelTex ? _panelTex : (_panelTex = Solid(Panel));

        public static GUIStyle Window
        {
            get
            {
                if (_window == null || !_window.normal.background)
                {
                    _window = new GUIStyle(GUI.skin.window);
                    _window.normal.background = _window.onNormal.background = PanelTex;
                    _window.focused.background = _window.onFocused.background = PanelTex;
                    _window.normal.textColor = _window.onNormal.textColor = Played;
                    _window.fontStyle = FontStyle.Bold;
                }
                return _window;
            }
        }

        public static GUIStyle Label => _label ?? (_label = new GUIStyle(GUI.skin.label) { richText = true });

        private static GUIStyle _big, _small;
        public static GUIStyle Big => _big ?? (_big = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, wordWrap = false, richText = true, normal = { textColor = Color.white } });
        public static GUIStyle Small => _small ?? (_small = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = false, normal = { textColor = new Color(0.85f, 0.85f, 0.85f) } });

        public static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}

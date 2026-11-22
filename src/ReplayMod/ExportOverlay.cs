using UnityEngine;
using UnityEngine.Rendering;

namespace ReplayMod
{
    // A small progress box in the bottom-right corner while exporting. The video is captured from the screen at
    // the end of each frame, so anything drawn the usual way would be in it: this draws straight to the screen
    // after the capture, and the next frame's render covers it again before its own capture.
    internal static class ExportOverlay
    {
        private const int FontSize = 14, Pad = 8, Bar = 3, Margin = 16;
        private static Material _solid;
        private static Font _font;

        // Call after the frame's capture (after WaitForEndOfFrame).
        public static void Draw(string text, float progress)
        {
            if (!Ready()) return;
            text += "   F10 to cancel";
            _font.RequestCharactersInTexture(text, FontSize);
            float width = 0f;
            foreach (char c in text)
            {
                if (_font.GetCharacterInfo(c, out CharacterInfo ci, FontSize)) width += ci.advance;
            }
            float w = width + Pad * 2, h = FontSize + Pad * 2 + Bar;
            float x = Screen.width - w - Margin, y = Margin; // pixel space, origin bottom-left

            RenderTexture.active = null;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, Screen.width, 0f, Screen.height);
            _solid.SetPass(0);
            GL.Begin(GL.QUADS);
            Quad(x, y, w, h, new Color(0f, 0f, 0f, 0.55f));
            Quad(x, y, w, Bar, new Color(1f, 1f, 1f, 0.15f));
            Quad(x, y, w * Mathf.Clamp01(progress), Bar, new Color(1f, 0.25f, 0.25f, 0.9f));
            GL.End();
            _font.material.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(new Color(1f, 1f, 1f, 0.9f));
            float pen = x + Pad, baseLine = y + Bar + Pad + 3f;
            foreach (char c in text)
            {
                if (!_font.GetCharacterInfo(c, out CharacterInfo ci, FontSize)) continue;
                GL.TexCoord(ci.uvBottomLeft); GL.Vertex3(pen + ci.minX, baseLine + ci.minY, 0f);
                GL.TexCoord(ci.uvTopLeft); GL.Vertex3(pen + ci.minX, baseLine + ci.maxY, 0f);
                GL.TexCoord(ci.uvTopRight); GL.Vertex3(pen + ci.maxX, baseLine + ci.maxY, 0f);
                GL.TexCoord(ci.uvBottomRight); GL.Vertex3(pen + ci.maxX, baseLine + ci.minY, 0f);
                pen += ci.advance;
            }
            GL.End();
            GL.PopMatrix();
        }

        private static void Quad(float x, float y, float w, float h, Color c)
        {
            GL.Color(c);
            GL.Vertex3(x, y, 0f);
            GL.Vertex3(x, y + h, 0f);
            GL.Vertex3(x + w, y + h, 0f);
            GL.Vertex3(x + w, y, 0f);
        }

        private static bool Ready()
        {
            if (!_solid)
            {
                Shader shader = Shader.Find("Hidden/Internal-Colored");
                if (!shader) return false;
                _solid = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _solid.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                _solid.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                _solid.SetInt("_Cull", (int)CullMode.Off);
                _solid.SetInt("_ZWrite", 0);
                _solid.SetInt("_ZTest", (int)CompareFunction.Always);
            }
            if (!_font)
            {
                try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
                catch (System.Exception) { }
                if (!_font) _font = Font.CreateDynamicFontFromOSFont("Arial", FontSize);
            }
            return _font && _font.material;
        }
    }
}

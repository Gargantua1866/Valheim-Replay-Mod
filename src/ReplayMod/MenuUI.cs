using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GUIFramework;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ReplayMod
{
    // A "Replays" tab next to Start Game and Join Game. Its button and page are clones of Valheim's own,
    // so font, colours and frames match the game. The IMGUI window is the fallback if a game update
    // changes the menu so that the tab can't be built.
    internal static class MenuUI
    {
        private static bool _tabBuilt;
        private static GameObject _tabObject, _page; // removed again if building fails halfway
        private static RectTransform _root;
        private static Button _play, _delete;
        private static float _step;
        private static string _selected;
        private static readonly List<GameObject> _rows = new List<GameObject>();

        // After FejdStartup.Start: every return to the main menu reloads the scene, so this runs each time.
        public static void Install(FejdStartup fs)
        {
            _tabBuilt = false;
            _detailFor = null;
            _rows.Clear();
            _selected = null;
            try
            {
                Build(fs);
                _tabBuilt = true;
            }
            catch (Exception e)
            {
                if (_tabObject) UnityEngine.Object.Destroy(_tabObject);
                if (_page) UnityEngine.Object.Destroy(_page);
                Plugin.Log.LogWarning($"couldn't add the Replays tab, using a window instead: {e}");
            }
        }

        private static void Build(FejdStartup fs)
        {
            TabHandler tabs = fs.m_startGamePanel.transform.GetChild(0).GetComponent<TabHandler>();
            if (!tabs || tabs.m_tabs.Count < 2) throw new InvalidOperationException("no world/server tabs");
            TabHandler.Tab world = tabs.m_tabs[0], join = tabs.m_tabs[1];
            Transform origPage = world.m_page;
            if (!fs.m_worldListRoot.IsChildOf(origPage) || !fs.m_worldStart.transform.IsChildOf(origPage)) throw new InvalidOperationException("world list isn't on the world tab's page");

            // Tab button, one step further along than Join Game is from Start Game.
            GameObject tabObject = UnityEngine.Object.Instantiate(join.m_button.gameObject, join.m_button.transform.parent);
            _tabObject = tabObject;
            tabObject.name = "ReplayModReplaysTab";
            var joinRect = (RectTransform)join.m_button.transform;
            ((RectTransform)tabObject.transform).anchoredPosition = joinRect.anchoredPosition + (joinRect.anchoredPosition - ((RectTransform)world.m_button.transform).anchoredPosition);
            // Beside Join Game in the draw order too: last would draw it over the frame's gold stripe.
            tabObject.transform.SetSiblingIndex(join.m_button.transform.GetSiblingIndex() + 1);
            SetLabel(tabObject, "Replays");
            Button tabButton = tabObject.GetComponent<Button>();
            tabButton.onClick = new Button.ButtonClickedEvent();

            // Page: a copy of the world page, kept to the list, title and a few buttons.
            GameObject page = UnityEngine.Object.Instantiate(origPage.gameObject, origPage.parent);
            _page = page;
            page.name = "ReplayModReplaysPage";
            page.SetActive(false);
            _root = (RectTransform)Twin(fs.m_worldListRoot, origPage, page.transform);
            _step = fs.m_worldListElementStep;
            foreach (Transform old in _root) UnityEngine.Object.Destroy(old.gameObject);

            _play = (Button)Twin(fs.m_worldStart, origPage, page.transform);
            _delete = (Button)Twin(fs.m_worldRemove, origPage, page.transform);
            Button folder = FindByHandler(origPage, page.transform, "OnWorldNew");
            Button back = FindByHandler(origPage, page.transform, "OnStartGameBack");

            var keep = new HashSet<Selectable> { _play, _delete };
            if (folder) keep.Add(folder);
            if (back) keep.Add(back);
            ScrollRect scroll = _root.GetComponentInParent<ScrollRect>(true);
            TMP_Text title = TitleOf(page.transform, scroll);
            foreach (Selectable s in page.GetComponentsInChildren<Selectable>(true))
            {
                if (!keep.Contains(s) && (!scroll || !s.transform.IsChildOf(scroll.transform)) && (!scroll || s != scroll.verticalScrollbar)) s.gameObject.SetActive(false);
            }
            foreach (TMP_Text t in page.GetComponentsInChildren<TMP_Text>(true))
            {
                if (t == title || t.GetComponentInParent<Selectable>(true) || (scroll && t.transform.IsChildOf(scroll.transform))) continue;
                t.gameObject.SetActive(false);
            }

            if (title) SetText(title, "Select Replay");
            Wire(_play, "Play", Play);
            Wire(_delete, "Delete", AskDelete);
            if (folder) Wire(folder, "Open Folder", () => Application.OpenURL("file:///" + Plugin.ReplayDir.Replace('\\', '/')));
            if (title)
            {
                // The tab works without these, so a game update that breaks them shouldn't take it down.
                try { BuildDetail(fs, title, page.transform); }
                catch (Exception e)
                {
                    _picture = null;
                    Plugin.Log.LogWarning($"couldn't add the replay details: {e}");
                }
            }

            var tab = new TabHandler.Tab { m_button = tabButton, m_page = (RectTransform)page.transform, m_onClick = new UnityEvent() };
            tab.m_onClick.AddListener(Refresh);
            tabs.m_tabs.Add(tab);
            // TabHandler.Init adds its own listener when it starts later; a second call for the same tab does nothing.
            tabButton.onClick.AddListener(() => tabs.SetActiveTab(tabs.m_tabs.IndexOf(tab)));
            Refresh();
        }

        // The same object in the cloned hierarchy, walking the sibling indices from the page down.
        private static T Twin<T>(T original, Transform origPage, Transform clonePage) where T : Component
        {
            var path = new Stack<int>();
            for (Transform t = original.transform; t != origPage; t = t.parent) path.Push(t.GetSiblingIndex());
            Transform at = clonePage;
            while (path.Count > 0) at = at.GetChild(path.Pop());
            return at.GetComponent<T>();
        }

        // A button on the page, recognised by the FejdStartup method it calls.
        private static Button FindByHandler(Transform origPage, Transform clonePage, string method)
        {
            foreach (Button b in origPage.GetComponentsInChildren<Button>(true))
            {
                for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                {
                    if (b.onClick.GetPersistentMethodName(i) == method) return Twin(b, origPage, clonePage);
                }
            }
            return null;
        }

        // The page's heading: the topmost text that isn't on a control or in the list.
        private static TMP_Text TitleOf(Transform page, ScrollRect scroll)
        {
            TMP_Text best = null;
            float bestY = float.MinValue;
            var corners = new Vector3[4];
            foreach (TMP_Text t in page.GetComponentsInChildren<TMP_Text>(true))
            {
                if (t.GetComponentInParent<Selectable>(true) || (scroll && t.transform.IsChildOf(scroll.transform))) continue;
                t.rectTransform.GetWorldCorners(corners);
                if (corners[1].y > bestY)
                {
                    bestY = corners[1].y;
                    best = t;
                }
            }
            return best;
        }

        private static void Wire(Button b, string label, UnityAction action)
        {
            SetLabel(b.gameObject, label);
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(action);
            UITooltip tip = b.GetComponent<UITooltip>();
            if (tip) UnityEngine.Object.Destroy(tip);
        }

        private static void SetLabel(GameObject go, string text)
        {
            foreach (TMP_Text t in go.GetComponentsInChildren<TMP_Text>(true)) SetText(t, text);
        }

        // Localize would turn the text back into the original's on Start.
        private static void SetText(TMP_Text t, string text)
        {
            t.text = text;
            foreach (Localize l in t.GetComponentsInParent<Localize>(true)) UnityEngine.Object.DestroyImmediate(l);
        }

        // A clip finished saving: if the list is on screen, show it.
        public static void ListChanged()
        {
            if (_page && _page.activeInHierarchy) Refresh();
        }

        private static void Refresh()
        {
            if (!_root) return;
            FileInfo[] files = Files();
            if (_selected == null || !File.Exists(_selected)) _selected = files.FirstOrDefault()?.FullName;
            _play.interactable = _delete.interactable = _selected != null;
            ShowDetail(false);
            FejdStartup fs = FejdStartup.instance;
            _root.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(_root.rect.height, Mathf.Max(files.Length, 1) * _step));
            while (_rows.Count < Mathf.Max(files.Length, 1))
            {
                GameObject row = UnityEngine.Object.Instantiate(fs.m_worldListElement, _root);
                row.SetActive(true);
                foreach (string hide in new[] { "source_cloud", "source_local", "source_legacy" }) row.transform.Find(hide)?.gameObject.SetActive(false);
                _rows.Add(row);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                GameObject row = _rows[i];
                row.SetActive(i < Mathf.Max(files.Length, 1));
                ((RectTransform)row.transform).anchoredPosition = new Vector2(0f, -i * _step);
                FileInfo f = i < files.Length ? files[i] : null;
                RowText(row, "name", f != null ? DisplayName(f) : "No recordings yet. In game, Ctrl+F12 starts and stops.");
                RowText(row, "modifiers", f != null ? Length(f) : "");
                RowText(row, "seed", f != null ? $"{f.Length / 1048576f:0.0} MB" : "");
                row.transform.Find("selected")?.gameObject.SetActive(f != null && string.Equals(f.FullName, _selected, StringComparison.OrdinalIgnoreCase));
                Button button = row.GetComponent<Button>();
                button.onClick.RemoveAllListeners();
                if (f == null) continue;
                string path = f.FullName;
                button.onClick.AddListener(() =>
                {
                    _selected = path;
                    Refresh();
                });
            }
        }

        private static void RowText(GameObject row, string child, string text)
        {
            TMP_Text t = row.transform.Find(child)?.GetComponent<TMP_Text>();
            if (t) t.text = text;
        }

        private static FileInfo[] Files() => new DirectoryInfo(Plugin.ReplayDir).GetFiles("*.replay").OrderByDescending(f => f.LastWriteTime).ToArray();

        // "2026-11-05_14-10-53_Riverheim" reads as "Riverheim  2026-11-05 14:10".
        private static string DisplayName(FileInfo f)
        {
            string name = Path.GetFileNameWithoutExtension(f.Name);
            Match m = Regex.Match(name, @"^(\d{4}-\d{2}-\d{2})_(\d{2})-(\d{2})-\d{2}_(.+)$");
            return m.Success ? $"{m.Groups[4].Value}  {m.Groups[1].Value} {m.Groups[2].Value}:{m.Groups[3].Value}" : name;
        }

        // From the stats file written next to the recording.
        private static string Length(FileInfo f)
        {
            try
            {
                Match m = Regex.Match(File.ReadAllText(Path.ChangeExtension(f.FullName, ".json")), "\"duration_s\":\\s*([0-9.]+)");
                if (m.Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s))
                {
                    return $"{(int)s / 60}:{(int)s % 60:00}";
                }
            }
            catch (IOException) { /* no stats file: crashed recording */ }
            return "";
        }

        private static void Play()
        {
            if (_selected == null) return;
            ReplaySession.Start(_selected);
            if (string.IsNullOrEmpty(ReplaySession.LastError)) return;
            UnifiedPopup.Push(new WarningPopup("Replay", ReplaySession.LastError, () => UnifiedPopup.Pop(), localizeText: false));
        }

        private static readonly string[] ReplayFiles = { ".replay", ".json", ".camera.txt", ".path.json", ".jpg" };

        // The panel beside the list, where the world page shows its details: picture, name and replay
        // buffer. Controls are clones of Valheim's, like the rest of the tab.
        private static RawImage _picture;
        private static TMP_Text _noPicture;
        private static GuiInputField _nameField;
        private static Button _bufferButton;
        private static TMP_Text _minutes, _hint;
        private static GameObject _typing;
        private static int _typingUntil;
        private static string _detailFor;
        private static readonly float[] KeepMinutes = { 0.5f, 1f, 2f, 3f, 5f, 10f, 15f, 20f, 30f };

        private static void BuildDetail(FejdStartup fs, TMP_Text title, Transform page)
        {
            // The dark panel the heading sits on: the first parent big enough to hold all of this.
            Transform panel = title.transform.parent;
            while (panel != page && ((RectTransform)panel).rect.height < 300f) panel = panel.parent;
            if (panel == page) panel = title.transform.parent;
            float width = Mathf.Max(((RectTransform)panel).rect.width - 32f, 200f);
            float pictureHeight = width * 9f / 16f;
            float top = 60f;

            var pictureObject = new GameObject("ReplayModPicture", typeof(RectTransform), typeof(CanvasRenderer));
            pictureObject.transform.SetParent(panel, false);
            _picture = pictureObject.AddComponent<RawImage>();
            _picture.raycastTarget = false;
            Place(pictureObject.transform, top, pictureHeight, 0f, 1f);

            _noPicture = CloneText(title, panel, 16f);
            SetText(_noPicture, "No picture yet: it's made when the replay is opened");
            Place(_noPicture.transform, top, pictureHeight, 0f, 1f);
            top += pictureHeight + 10f;

            _nameField = UnityEngine.Object.Instantiate(fs.m_newWorldName.gameObject, panel).GetComponent<GuiInputField>();
            _nameField.gameObject.SetActive(true);
            _nameField.onValueChanged = new TMP_InputField.OnChangeEvent();
            _nameField.onEndEdit = new TMP_InputField.SubmitEvent();
            _nameField.onSubmit = new TMP_InputField.SubmitEvent();
            _nameField.characterLimit = 0;
            _nameField.contentType = TMP_InputField.ContentType.Standard;
            _nameField.lineType = TMP_InputField.LineType.SingleLine;
            _nameField.onSubmit.AddListener(_ => Rename());
            Place(_nameField.transform, top, 34f, 0f, 1f);
            // Enter renames. The page's buttons have hotkeys (Enter is Play's), which wait while typing.
            _typing = new GameObject("ReplayModTyping");
            _typing.transform.SetParent(panel, false);
            _typing.SetActive(false);
            foreach (UIGamePad pad in page.GetComponentsInChildren<UIGamePad>(true))
            {
                if (pad.m_blockingElements == null) pad.m_blockingElements = new List<GameObject>();
                pad.m_blockingElements.Add(_typing);
            }
            top += 42f;

            Wire(Clone(panel, top, 0f, 0.5f), "Rename", Rename);
            Wire(Clone(panel, top, 0.5f, 1f), "Copy", CopySelected);
            top += 46f;
            _bufferButton = Clone(panel, top, 0f, 0.55f);
            Wire(_bufferButton, "", () =>
            {
                Plugin.ReplayBuffer.Value = !Plugin.ReplayBuffer.Value;
                ShowDetail(true);
            });
            Wire(Clone(panel, top, 0.55f, 0.68f), "-", () => StepMinutes(-1));
            _minutes = CloneText(title, panel, 16f);
            Place(_minutes.transform, top, 40f, 0.68f, 0.87f);
            Wire(Clone(panel, top, 0.87f, 1f), "+", () => StepMinutes(1));
            top += 46f;

            _hint = CloneText(title, panel, 14f);
            Place(_hint.transform, top, 50f, 0f, 1f);
        }

        // The setting's change refreshes the panel (see Plugin).
        private static void StepMinutes(int dir)
        {
            float now = Plugin.ReplayBufferMinutes.Value;
            float next = dir > 0 ? KeepMinutes.FirstOrDefault(m => m > now + 0.01f) : KeepMinutes.LastOrDefault(m => m < now - 0.01f);
            if (next > 0f) Plugin.ReplayBufferMinutes.Value = next;
        }

        // Stays on a frame longer: the field may hear Enter before or after the hotkeys check for it.
        public static void Tick()
        {
            if (!_typing) return;
            if (_nameField.isFocused) _typingUntil = Time.frameCount + 1;
            _typing.SetActive(Time.frameCount <= _typingUntil);
        }

        private static Button Clone(Transform panel, float top, float from, float to)
        {
            Button b = UnityEngine.Object.Instantiate(_delete.gameObject, panel).GetComponent<Button>();
            b.gameObject.SetActive(true);
            b.interactable = true;
            UIGamePad pad = b.GetComponent<UIGamePad>();
            if (pad)
            {
                // Not the hotkey of the button it was cloned from.
                pad.m_keyCode = KeyCode.None;
                pad.m_zinputKey = "";
            }
            Place(b.transform, top, 40f, from, to);
            return b;
        }

        private static TMP_Text CloneText(TMP_Text original, Transform panel, float size)
        {
            TMP_Text t = UnityEngine.Object.Instantiate(original.gameObject, panel).GetComponent<TMP_Text>();
            t.enableAutoSizing = false;
            t.fontSize = size;
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.raycastTarget = false;
            t.gameObject.SetActive(true);
            return t;
        }

        // A row in the panel, measured from its top, across the given fraction of its width.
        private static void Place(Transform t, float top, float height, float from, float to)
        {
            var r = (RectTransform)t;
            r.anchorMin = new Vector2(from, 1f);
            r.anchorMax = new Vector2(to, 1f);
            r.pivot = new Vector2(0.5f, 1f);
            r.offsetMin = new Vector2(from == 0f ? 16f : 4f, -(top + height));
            r.offsetMax = new Vector2(to == 1f ? -16f : -4f, -top);
        }

        private static void ShowDetail(bool force)
        {
            if (!_picture) return;
            float minutes = Plugin.ReplayBufferMinutes.Value;
            SetLabel(_bufferButton.gameObject, $"Replay Buffer: {(Plugin.ReplayBuffer.Value ? "On" : "Off")}");
            SetText(_minutes, $"{minutes:0.#} min");
            SetText(_hint, $"In the game, Ctrl+Shift+F12 keeps the last {minutes:0.#} minutes, and Ctrl+Alt+F12 turns it on or off.");
            if (!force && _detailFor == _selected) return;
            _detailFor = _selected;
            _nameField.text = _selected != null ? Path.GetFileNameWithoutExtension(_selected) : "";
            if (_picture.texture) UnityEngine.Object.Destroy(_picture.texture);
            _picture.texture = null;
            string jpg = _selected != null ? Path.ChangeExtension(_selected, ".jpg") : null;
            if (jpg != null && File.Exists(jpg))
            {
                var tex = new Texture2D(2, 2);
                // net48 can't bind Unity 6's ReadOnlySpan overload: pick the byte[] one (see the modding guide).
                try
                {
                    typeof(ImageConversion).GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) }).Invoke(null, new object[] { tex, File.ReadAllBytes(jpg) });
                    _picture.texture = tex;
                }
                catch (IOException e) { Plugin.Log.LogWarning($"reading {jpg}: {e.Message}"); }
            }
            _picture.enabled = _picture.texture;
            _noPicture.gameObject.SetActive(!_picture.texture);
        }

        private static void Warn(string text) => UnifiedPopup.Push(new WarningPopup("Replay", text, () => UnifiedPopup.Pop(), localizeText: false));

        // A copy beside it, with the camera keys and everything else saved with it.
        private static void CopySelected()
        {
            if (_selected == null) return;
            string stem = Path.GetFileNameWithoutExtension(_selected);
            string target = Path.Combine(Plugin.ReplayDir, stem + " copy.replay");
            for (int i = 2; File.Exists(target); i++) target = Path.Combine(Plugin.ReplayDir, $"{stem} copy {i}.replay");
            try
            {
                foreach (string ext in ReplayFiles)
                {
                    string from = Path.ChangeExtension(_selected, ext);
                    if (File.Exists(from)) File.Copy(from, Path.ChangeExtension(target, ext));
                }
                _selected = target;
                Refresh();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Warn($"Couldn't copy the replay: {e.Message}");
            }
        }

        // Moves the replay and everything saved beside it. An error leaves the rest as they are.
        private static void Rename()
        {
            string name = _nameField.text.Trim();
            if (_selected == null || name.Length == 0) return;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Warn("That name has a character files can't have.");
                return;
            }
            string target = Path.Combine(Plugin.ReplayDir, name + ".replay");
            if (string.Equals(target, _selected, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(target))
            {
                Warn("There's already a replay with that name.");
                return;
            }
            try
            {
                foreach (string ext in ReplayFiles)
                {
                    string from = Path.ChangeExtension(_selected, ext);
                    if (File.Exists(from)) File.Move(from, Path.ChangeExtension(target, ext));
                }
                _selected = target;
                Refresh();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Warn($"Couldn't rename the replay: {e.Message}");
            }
        }

        private static void AskDelete()
        {
            string path = _selected;
            if (path == null) return;
            UnifiedPopup.Push(new YesNoPopup("Delete replay", $"Permanently delete {Path.GetFileNameWithoutExtension(path)} and its camera keys?", () =>
            {
                UnifiedPopup.Pop();
                foreach (string ext in ReplayFiles)
                {
                    try
                    {
                        File.Delete(Path.ChangeExtension(path, ext));
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        Plugin.Log.LogWarning($"couldn't delete {ext} of {path}: {e.Message}");
                    }
                }
                _selected = null;
                Refresh();
            }, () => UnifiedPopup.Pop(), localizeText: false));
        }

        // Fallback window, only if the tab couldn't be built.
        private static Rect _rect = new Rect(20, 20, 460, 420);
        private static Vector2 _scroll;
        private static FileInfo[] _files;
        private static float _nextRefresh;

        public static void OnGUI()
        {
            FejdStartup fs = FejdStartup.instance;
            if (_tabBuilt || fs == null || ReplaySession.Active || fs.m_startGamePanel == null || !fs.m_startGamePanel.activeInHierarchy) return;
            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + 2f;
                _files = Files();
            }
            _rect = GUILayout.Window(0x4D554F, _rect, Window, "Replay Mod", UiStyle.Window);
        }

        private static void Window(int id)
        {
            GUILayout.Label("Plays with the character selected here; nothing is saved to it.");
            if (!string.IsNullOrEmpty(ReplaySession.LastError)) GUILayout.Label(ReplaySession.LastError);
            _scroll = GUILayout.BeginScrollView(_scroll);
            if (_files == null || _files.Length == 0) GUILayout.Label("No recordings yet. In game, press Ctrl+F12 to start and stop.");
            foreach (FileInfo f in _files ?? new FileInfo[0])
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{Path.GetFileNameWithoutExtension(f.Name)}  ({f.Length / 1048576f:0.0} MB)");
                if (GUILayout.Button("Play", GUILayout.Width(60))) ReplaySession.Start(f.FullName);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Open replay folder")) Application.OpenURL("file:///" + Plugin.ReplayDir.Replace('\\', '/'));
            GUI.DragWindow();
        }
    }
}

using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// Island main HUD layout in panel space (1125×2436 design, same as Boluo InGameControlView).
    /// Icon sizes stay at design units; PanelSettings Match Height / Expand handles physical scale.
    /// Do not scale by Min(panelW, panelH)/1125 — under Match Height landscape short edge is 2436 and would ~2× icons.
    /// </summary>
    public sealed class IslandMainHudLayout
    {
        const float RefH = 2436f;
        const float RefIcon = 108f;
        const float RefCorner = 120f;
        const float RefIconGap = 156f;
        const float RefLeftX = 102f;
        const float RefRightX = 102f;
        const float RefTopY0 = 228f;
        // Corner icons share the same horizontal center as left/right columns.
        const float RefCornerX = 102f;
        const float RefCornerY = 180f;
        const float RefActivityW = 108f;
        const float RefActivityH = 132f;

        VisualElement _root;
        VisualElement _timeBackground2;
        VisualElement _weather;
        VisualElement _music;
        VisualElement _volume;
        VisualElement _profile;
        VisualElement _timer;
        VisualElement _preset;
        VisualElement _health;
        VisualElement _activity;
        VisualElement _timeHand;

        public void Bind(VisualElement root)
        {
            _root = root;
            if (root == null)
                return;

            _timeBackground2 = root.Q("timeBackground2");
            _weather = root.Q("weatherBackground");
            _music = root.Q("musicBackground");
            _volume = root.Q("volumeBackground");
            _profile = root.Q("profile");
            _timer = root.Q("timerBackground");
            _preset = root.Q("presetIcon");
            _health = root.Q("defaultState2");
            _activity = root.Q("activityEntry");
            _timeHand = root.Q("timeHand");
        }

        public void Apply()
        {
            if (_root == null)
                return;

            float h = _root.resolvedStyle.height;
            if (h < 1f) h = RefH;

            // Fixed design sizes (Boluo sizeDelta 108/120); only Y tracks panel height ratio.
            float icon = RefIcon;
            float corner = RefCorner;
            float gap = RefIconGap;
            float leftX = RefLeftX;
            float rightX = RefRightX;
            float topY0 = RefTopY0 * (h / RefH);
            float cornerX = RefCornerX;
            float cornerY = RefCornerY * (h / RefH);

            PlaceLeft(_timeBackground2, leftX, topY0, icon, icon);
            PlaceLeft(_weather, leftX, topY0 + gap, icon, icon);
            PlaceLeft(_music, leftX, topY0 + gap * 2f, icon, icon);
            PlaceLeft(_volume, leftX, topY0 + gap * 3f, icon, icon);

            PlaceRight(_profile, rightX, topY0, icon, icon);
            PlaceRight(_timer, rightX, topY0 + gap, icon, icon);
            PlaceRight(_activity, rightX, topY0 + gap * 2f, RefActivityW, RefActivityH);

            PlaceCorner(_preset, true, cornerX, cornerY, corner);
            PlaceCorner(_health, false, cornerX, cornerY, corner);

            ScaleHudIconInner(_weather, icon);
            ScaleHudIconInner(_music, icon);
            ScaleHudIconInner(_volume, icon);
            ScaleHudIconInner(_profile, icon);
            ScaleHudIconInner(_timer, icon);
            ScaleClockHand(1f);
            ScaleCornerInner(_preset, corner);
            ScaleCornerInner(_health, corner);
        }

        /// <summary>x/y are center (Boluo anchoredPosition with pivot 0.5).</summary>
        static void PlaceLeft(VisualElement el, float x, float y, float width, float height)
        {
            if (el == null)
                return;
            el.style.position = Position.Absolute;
            el.style.left = x - width * 0.5f;
            el.style.right = StyleKeyword.Auto;
            el.style.top = y - height * 0.5f;
            el.style.bottom = StyleKeyword.Auto;
            el.style.marginLeft = 0;
            el.style.marginTop = 0;
            el.style.marginRight = 0;
            el.style.marginBottom = 0;
            el.style.width = width;
            el.style.height = height;
        }

        /// <summary>xFromRight/y are center from right/top edges.</summary>
        static void PlaceRight(VisualElement el, float xFromRight, float y, float width, float height)
        {
            if (el == null)
                return;
            el.style.position = Position.Absolute;
            el.style.left = StyleKeyword.Auto;
            el.style.right = xFromRight - width * 0.5f;
            el.style.top = y - height * 0.5f;
            el.style.bottom = StyleKeyword.Auto;
            el.style.marginLeft = 0;
            el.style.marginTop = 0;
            el.style.marginRight = 0;
            el.style.marginBottom = 0;
            el.style.width = width;
            el.style.height = height;
        }

        /// <summary>x/yFromBottom are center (same pivot as PlaceLeft/PlaceRight).</summary>
        static void PlaceCorner(VisualElement el, bool left, float x, float yFromBottom, float size)
        {
            if (el == null)
                return;
            el.style.position = Position.Absolute;
            el.style.top = StyleKeyword.Auto;
            el.style.bottom = yFromBottom - size * 0.5f;
            el.style.marginLeft = 0;
            el.style.marginTop = 0;
            el.style.marginRight = 0;
            el.style.marginBottom = 0;
            el.style.width = size;
            el.style.height = size;
            if (left)
            {
                el.style.left = x - size * 0.5f;
                el.style.right = StyleKeyword.Auto;
            }
            else
            {
                el.style.left = StyleKeyword.Auto;
                el.style.right = x - size * 0.5f;
            }
        }

        static void ScaleHudIconInner(VisualElement host, float size)
        {
            if (host == null)
                return;
            VisualElement plate = host.Q("plate");
            VisualElement glyph = host.Q("glyph");
            VisualElement musicPause = host.Q("musicPause");
            VisualElement musicPlaying = host.Q("musicPlaying");
            VisualElement volumeMarks = host.Q("volumeMarks");
            VisualElement timerActive = host.Q("timerActive");
            VisualElement timerIdle = host.Q("timerIdle");
            StretchSquare(plate, size);
            StretchSquare(glyph, size);
            StretchSquare(musicPause, size);
            StretchSquare(musicPlaying, size);
            StretchSquare(volumeMarks, size);
            StretchSquare(timerActive, size);
            StretchSquare(timerIdle, size);

            float s = size / RefIcon;
            // Equalizer bars are mesh (HudEqualizerBars) — no ScaleChild layout anim.
            ScaleChild(host.Q("volumeTrack"), 0f, 0f, 108f, 108f, s);
            // Fills: full track width; volume anim uses scale.x from left origin.
            ScaleChild(host.Q("volumeTrack1"), 24f, 33f, 60f, 12f, s);
            ScaleChild(host.Q("volumeTrack2"), 24f, 63f, 60f, 12f, s);
            // Dots: park at minX (24); volume anim uses translate.
            ScaleChild(host.Q("effectvolumeyuan"), 24f, 58f, 22f, 22f, s);
            ScaleChild(host.Q("musicvolumeyuan"), 24f, 28f, 22f, 22f, s);
            ScaleChild(host.Q("timerFill"), 0f, 0f, 108f, 108f, s);
            ScaleChild(host.Q("topRule"), 41.85f, 20.25f, 24.3f, 8.1f, s);
            ScaleChild(host.Q("topRightDecor"), 74f, 33f, 12f, 13f, s);
            ScaleChild(host.Q("dialSpin"), 27f, 32.4f, 54f, 54f, s);
            ScaleChild(host.Q("badge"), 72f, 18f, 18f, 18f, s);
        }

        void ScaleClockHand(float s)
        {
            if (_timeHand == null)
                return;
            float size = RefIcon * s;
            _timeHand.style.width = size;
            _timeHand.style.height = size;
            _timeHand.style.left = Length.Percent(50);
            _timeHand.style.top = Length.Percent(50);
            _timeHand.style.marginLeft = -size * 0.5f;
            _timeHand.style.marginTop = -size * 0.5f;

            ScaleChild(_timeHand.Q("colorSwatch"), 6.35f, 23.4f, 25.22f, 61.1f, s);
            ScaleChild(_timeHand.Q("hand"), 15.88f, 41.29f, 50.82f, 25.41f, s);
            VisualElement face = _timeBackground2?.Q("timeBackground22");
            StretchSquare(face, size);
        }

        static void ScaleCornerInner(VisualElement host, float corner)
        {
            if (host == null)
                return;
            float s = corner / RefCorner;
            float inner = 96f * s;
            VisualElement a = host.Q("presetIcon2") ?? host.Q("defaultState22");
            if (a != null)
            {
                a.style.width = inner;
                a.style.height = inner;
                a.style.left = Length.Percent(50);
                a.style.top = Length.Percent(50);
                a.style.marginLeft = -inner * 0.5f;
                a.style.marginTop = -inner * 0.5f;
            }

            VisualElement bg = host.Q("background") ?? host.Q("background2");
            StretchSquare(bg, corner);
            VisualElement loading = host.Q("pressureLoading");
            StretchSquare(loading, corner);
        }

        static void StretchSquare(VisualElement el, float size)
        {
            if (el == null)
                return;
            el.style.left = 0;
            el.style.top = 0;
            el.style.marginLeft = 0;
            el.style.marginTop = 0;
            el.style.width = size;
            el.style.height = size;
        }

        static void ScaleChild(VisualElement el, float x, float y, float w, float h, float s)
        {
            if (el == null)
                return;
            el.style.left = x * s;
            el.style.top = y * s;
            el.style.marginLeft = 0;
            el.style.marginTop = 0;
            el.style.width = w * s;
            el.style.height = h * s;
        }
    }
}

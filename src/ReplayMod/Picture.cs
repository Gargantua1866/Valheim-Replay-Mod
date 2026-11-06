using UnityEngine;
using UnityEngine.PostProcessing;
using UnityStandardAssets.ImageEffects;

namespace ReplayMod
{
    // The replay's look, on top of the game's own post-processing: an offset or a multiple of each value,
    // so the neutral settings show the game as it is. The profile is a shared asset that outlives the
    // replay, so its values go back when the replay ends.
    internal static class Picture
    {
        public static float Brightness, Exposure, Temperature, Saturation = 1f, Contrast = 1f, Bloom = 1f, Shafts = 1f;
        public static bool LensDirt = true;

        private static PostProcessingProfile _profile;
        private static ColorGradingModel.Settings _grading;
        private static BloomModel.Settings _bloom;
        private static bool _gradingOn, _bloomOn;
        private static SunShafts _sun;
        private static float _sunIntensity;
        private static bool _sunOn;
        private static (float, float, float, float, float, float, float, bool) _applied;

        private static (float, float, float, float, float, float, float, bool) Now => (Brightness, Exposure, Temperature, Saturation, Contrast, Bloom, Shafts, LensDirt);
        private static readonly (float, float, float, float, float, float, float, bool) Neutral = (0f, 0f, 0f, 1f, 1f, 1f, 1f, true);

        public static void ResetValues()
        {
            Brightness = Exposure = Temperature = 0f;
            Saturation = Contrast = Bloom = Shafts = 1f;
            LensDirt = true;
        }

        // Called every frame; writes only when a value changed (color grading rebuilds its table on a write).
        public static void Apply(Camera cam)
        {
            SunShafts sun = cam.GetComponent<SunShafts>();
            if (sun != _sun)
            {
                _sun = sun;
                if (sun)
                {
                    _sunIntensity = sun.sunShaftIntensity;
                    _sunOn = sun.enabled;
                }
                _applied = Neutral;
            }
            PostProcessingProfile profile = cam.GetComponent<PostProcessingBehaviour>()?.profile;
            if (profile != _profile)
            {
                Restore();
                _profile = profile;
                if (profile)
                {
                    _grading = profile.colorGrading.settings;
                    _gradingOn = profile.colorGrading.enabled;
                    _bloom = profile.bloom.settings;
                    _bloomOn = profile.bloom.enabled;
                }
                _applied = Neutral;
            }
            if (_applied == Now) return;
            _applied = Now;
            bool neutral = Now == Neutral;
            if (_profile)
            {
                ColorGradingModel.Settings grading = _grading;
                grading.basic.postExposure += Exposure;
                grading.basic.temperature = Mathf.Clamp(grading.basic.temperature + Temperature, -100f, 100f);
                grading.basic.saturation *= Saturation;
                grading.basic.contrast *= Contrast;
                grading.colorWheels.linear.gamma.a += Brightness; // midtones, with black and white staying put
                _profile.colorGrading.settings = grading;
                _profile.colorGrading.enabled = _gradingOn || !neutral;
                BloomModel.Settings bloom = _bloom;
                bloom.bloom.intensity *= Bloom;
                if (!LensDirt) bloom.lensDirt.intensity = 0f;
                _profile.bloom.settings = bloom;
                _profile.bloom.enabled = Bloom != 1f ? Bloom > 0f : _bloomOn;
            }
            if (_sun)
            {
                _sun.sunShaftIntensity = _sunIntensity * Shafts;
                _sun.enabled = Shafts != 1f ? Shafts > 0f : _sunOn;
            }
        }

        // Replay over: back to the game's values. The sliders keep theirs for the next replay.
        public static void Restore()
        {
            if (_profile)
            {
                _profile.colorGrading.settings = _grading;
                _profile.colorGrading.enabled = _gradingOn;
                _profile.bloom.settings = _bloom;
                _profile.bloom.enabled = _bloomOn;
            }
            _profile = null;
            _sun = null; // part of the replay's scene, which is gone
            _applied = Neutral;
        }
    }
}

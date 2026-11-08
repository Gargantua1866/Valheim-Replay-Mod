using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ReplayMod
{
    // shortcut: diagnostic for the frame rate staying low after playing through a far teleport (cause not
    // found yet); logs what is alive a few seconds after a replay starts and after each far load. Remove
    // once the cause is fixed.
    internal static class PerfProbe
    {
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> Instances =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");
        private static readonly AccessTools.FieldRef<ZoneSystem, IDictionary> Zones =
            AccessTools.FieldRefAccess<ZoneSystem, IDictionary>("m_zones");

        public static void After(string what, float seconds)
        {
            if (Plugin.Instance) Plugin.Instance.StartCoroutine(Snapshot(what, seconds));
        }

        private static IEnumerator Snapshot(string what, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (!ReplaySession.Active) yield break;
            // Average frame time over 3 s of real time.
            int frames = 0;
            float from = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - from < 3f)
            {
                frames++;
                yield return null;
            }
            if (!ReplaySession.Active) yield break;
            float ms = (Time.realtimeSinceStartup - from) * 1000f / Mathf.Max(frames, 1);
            var sb = new StringBuilder();
            sb.Append($"perf {delay:0} s {what}: {ms:0.0} ms/frame ({1000f / ms:0} fps), paused {ReplaySession.Paused}, timeScale {Time.timeScale}, loading {ReplaySession.Loading}, T {ReplaySession.T:0.0}");
            sb.Append($", instances {(ZNetScene.instance ? Instances(ZNetScene.instance).Count : -1)}, zones {(ZoneSystem.instance ? Zones(ZoneSystem.instance)?.Count ?? -1 : -1)}, kept areas {ReplaySession.KeptAreas.Count}");

            var ps = Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
            sb.Append($"\n  particle systems {ps.Length}, playing {ps.Count(p => p.isPlaying)}, particles {ps.Sum(p => p.particleCount)}; most: ");
            sb.Append(string.Join(", ", ps.OrderByDescending(p => p.particleCount).Take(6).Select(p => $"{Path(p.transform)} {p.particleCount}")));

            var bodies = Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None).Where(b => !b.isKinematic && !b.IsSleeping()).ToList();
            sb.Append($"\n  awake bodies {bodies.Count}: ");
            sb.Append(string.Join(", ", bodies.GroupBy(b => b.transform.root.name).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key} x{g.Count()}")));
            sb.Append($"\n  lights on {Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Count(l => l.isActiveAndEnabled)}, sounds playing {Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None).Count(a => a.isPlaying)}, animators {Object.FindObjectsByType<Animator>(FindObjectsSortMode.None).Length}, renderers {Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).Length}");
            var roots = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t => t.parent == null).GroupBy(t => t.name).OrderByDescending(g => g.Count()).Take(8);
            sb.Append("\n  most common root objects: " + string.Join(", ", roots.Select(g => $"{g.Key} x{g.Count()}")));
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static string Path(Transform t) => t.parent ? $"{t.root.name}/{t.name}" : t.name;
    }
}

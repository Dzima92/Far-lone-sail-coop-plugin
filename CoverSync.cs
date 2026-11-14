using System;
using System.Reflection;
using HarmonyLib;
using Okomotive.FarLoneSails;
using UnityEngine;

namespace FarCoopMod
{
    /// <summary>
    /// The vehicle hides its front cover (so you see inside) when "a character" enters it, and shows it
    /// when "a character" leaves. With other players' bodies around, one of THEM leaving made the hull
    /// opaque for you while you were still inside. Here the cover follows only YOUR own character.
    /// </summary>
    public static class CoverSync
    {
        private static bool lastByZoom;
        private static bool applying;
        private static float mismatchSince = -1f;
        private static Vehicle vehicle;
        private static float nextFind;

        public static void Install(Harmony h)
        {
            try
            {
                foreach (var m in typeof(Vehicle).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "SetFrontCover" || m.GetParameters().Length != 2) continue;
                    h.Patch(m, prefix: new HarmonyMethod(typeof(CoverSync).GetMethod(nameof(Pre2), BindingFlags.Static | BindingFlags.Public)));
                }
                FarCoopPlugin.Logger.LogInfo("CoverSync: watching Vehicle.SetFrontCover");
            }
            catch (Exception e) { FarCoopPlugin.Logger.LogWarning("CoverSync: patch failed: " + e.Message); }
        }

<<<<<<< Updated upstream
=======
        private static readonly Dictionary<IntPtr, bool> inVehicleCache = new Dictionary<IntPtr, bool>();
        private static float nextBlockLog;
        public static int Blocked;

        /// <summary>Does this trigger call SetFrontCover / SetInsideLights (looked up in its UnityEvents)?</summary>
        private static bool IsCoverTrigger(Component inst)
        {
            bool cover = false;
            var names = new List<string>();
            try
            {
                var tg = inst.TryCast<Okomotive.Toolset.TriggerGeneric>();
                var evs = tg != null ? tg.events : null;
                if (evs != null)
                {
                    for (int i = 0; i < evs.Length; i++)
                    {
                        var cb = evs[i] != null ? evs[i].callback : null;
                        if (cb == null) continue;
                        // read the stored calls straight from the fields (Unity's own getter methods
                        // are stripped from this game build)
                        var group = cb.m_PersistentCalls;
                        var calls = group != null ? group.m_Calls : null;
                        if (calls == null) continue;
                        var items = calls._items;
                        int n = calls._size;
                        for (int j = 0; j < n && items != null && j < items.Length; j++)
                        {
                            string m = items[j] != null ? items[j].m_MethodName : null;
                            if (string.IsNullOrEmpty(m)) continue;
                            names.Add(m);
                            if (m.Contains("FrontCover") || m.Contains("InsideLight")) cover = true;
                        }
                    }
                }
            }
            catch (Exception e) { names.Add("(could not read: " + e.Message + ")"); }
            // fallback by name: the vehicle's "Entrance..." zones are the ones that open the hull view
            try { if (!cover && names.Count == 0 || names.Exists(x => x.StartsWith("(could not read"))) { if (inst.name.StartsWith("Entrance")) { cover = true; names.Add("by name"); } } } catch { }
            try { FarCoopPlugin.Logger.LogInfo($"CoverSync: trigger '{inst.name}' calls [{string.Join(", ", names)}] -> partner bodies {(cover ? "IGNORED (hull switch)" : "allowed")}"); } catch { }
            return cover;
        }

        public static bool SkipPuppetInVehicleTrigger(Component __instance, object __0)
        {
            try
            {
                if (!PartnerInput.Active || __instance == null || __0 == null) return true;
                Collider col = __0 as Collider;
                if (col == null && __0 is Collision c) col = c.collider;
                if (col == null) return true;
                var ch = col.GetComponentInParent(Il2CppType.Of<Character>())?.TryCast<Character>();
                if (ch == null || !PartnerInput.IsPuppet(ch.Pointer)) return true;
                // only the trigger that switches the hull (front cover / inside lights) ignores partner
                // bodies. Every other trigger - the ramp that opens when someone walks up, lifts,
                // doors - must keep working for them.
                if (!inVehicleCache.TryGetValue(__instance.Pointer, out bool coverTrigger))
                {
                    coverTrigger = IsCoverTrigger(__instance);
                    inVehicleCache[__instance.Pointer] = coverTrigger;
                }
                if (!coverTrigger) return true;
                Blocked++;
                if (Time.unscaledTime >= nextBlockLog)
                {
                    nextBlockLog = Time.unscaledTime + 10f;
                    FarCoopPlugin.Logger.LogInfo($"CoverSync: partner body ignored by vehicle trigger '{__instance.name}' ({Blocked} so far)");
                }
                return false;
            }
            catch { return true; }
        }

>>>>>>> Stashed changes
        // the 2-argument overload tells us whether the camera zoom caused it (leave those alone)
        public static void Pre2(bool state, bool triggerdByZoom)
        {
            if (!applying) lastByZoom = triggerdByZoom;
        }

        /// <summary>Call every frame while other players are in the game.</summary>
        public static void Tick()
        {
            if (!GameAccess.Alive(vehicle))
            {
                if (Time.unscaledTime < nextFind) return;
                nextFind = Time.unscaledTime + 2f;
                var go = GameObject.Find("Vehicle");
                vehicle = go != null ? GameAccess.GetComp<Vehicle>(go) : null;
                if (vehicle == null) return;
            }
            var me = GameAccess.LocalCharacter;
            if (me == null || lastByZoom) { mismatchSince = -1f; return; }
            bool want = !GameAccess.CharacterInsideVehicle(me);   // cover on while I am outside
            bool active;
            try { active = vehicle.IsFrontCoverActive(); } catch { return; }
            if (active == want) { mismatchSince = -1f; return; }
            // give the game's own trigger a moment (my own enter/exit fires it slightly earlier/later)
            if (mismatchSince < 0f) { mismatchSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - mismatchSince < 0.3f) return;
            mismatchSince = -1f;
            applying = true;
            try { vehicle.SetFrontCover(want); }
            catch (Exception e) { FarCoopPlugin.Logger.LogDebug("CoverSync: " + e.Message); }
            finally { applying = false; }
        }
    }
}

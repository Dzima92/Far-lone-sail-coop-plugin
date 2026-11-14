using System;
using System.Collections.Generic;
using System.Reflection;
using UnhollowerRuntimeLib;
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

                // the real switch: a trigger box on the vehicle (Okomotive TriggerGeneric) fires
                // "cover off" when a character enters and "cover on" when one leaves. Other players'
                // bodies must not fire the vehicle's triggers - only real local characters do.
                int n = 0;
                foreach (var m in typeof(Okomotive.Toolset.TriggerGeneric).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.GetParameters().Length != 1) continue;
                    if (m.Name != "OnTriggerEnter" && m.Name != "OnTriggerExit" && m.Name != "OnTriggerStay" &&
                        m.Name != "OnCollisionEnter" && m.Name != "OnCollisionExit" && m.Name != "OnCollisionStay") continue;
                    h.Patch(m, prefix: new HarmonyMethod(typeof(CoverSync).GetMethod(nameof(SkipPuppetInVehicleTrigger), BindingFlags.Static | BindingFlags.Public)));
                    n++;
                }
                FarCoopPlugin.Logger.LogInfo($"CoverSync: partner bodies ignored by {n} vehicle trigger callbacks");
            }
            catch (Exception e) { FarCoopPlugin.Logger.LogWarning("CoverSync: patch failed: " + e.Message); }
        }

        private static readonly Dictionary<IntPtr, bool> inVehicleCache = new Dictionary<IntPtr, bool>();
        private static float nextBlockLog;
        public static int Blocked;

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
                // only triggers that belong to the vehicle (cover, lights, camera...); level triggers stay
                if (!inVehicleCache.TryGetValue(__instance.Pointer, out bool onVehicle))
                {
                    onVehicle = __instance.GetComponentInParent(Il2CppType.Of<Vehicle>()) != null;
                    inVehicleCache[__instance.Pointer] = onVehicle;
                }
                if (!onVehicle) return true;
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

        // the 2-argument overload tells us whether the camera zoom caused it (leave those alone)
        public static void Pre2(bool state, bool triggerdByZoom)
        {
            if (!applying) lastByZoom = triggerdByZoom;
            Telemetry.Event($"vehicle cover {(state ? "ON" : "off")}{(triggerdByZoom ? " (zoom)" : "")}");
        }

        /// <summary>Call every frame while other players are in the game.</summary>
        public static void Tick()
        {
            return;   // v5.0.1: superseded by the trigger filter above (kept for reference)
#pragma warning disable CS0162
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
#pragma warning restore CS0162
        }
    }
}

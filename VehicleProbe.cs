using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Okomotive.FarLoneSails;
using UnhollowerRuntimeLib;
using UnityEngine;

namespace FarCoopMod
{
    /// <summary>
    /// Debug telemetry of the vehicle itself, once per second, on every PC (host and clients):
    ///  VEH  line: fuel, steam, speed, brakes, health, distance, engine button, sails, every module's
    ///             health / fire state.
    ///  MOVE line: which parts of the vehicle moved during the last second (relative to the vehicle),
    ///             biggest movers first.
    /// Compare the host's file with a client's file at the same second: anything that differs is
    /// something the mod does not sync yet.
    /// </summary>
    public static class VehicleProbe
    {
        private static float nextTick, nextFind, nextRescan;
        private static Vehicle veh;
        private static VehicleMotor motor;
        private static readonly List<VehicleModule> modules = new List<VehicleModule>();
        private static readonly List<VehicleSail> sails = new List<VehicleSail>();

        private static readonly List<Transform> parts = new List<Transform>();
        private static readonly List<string> names = new List<string>();
        private static readonly List<bool> isBody = new List<bool>();
        private static Vector3[] lastPos = new Vector3[0];
        private static Quaternion[] lastRot = new Quaternion[0];
        private static bool[] have = new bool[0];
        private static IntPtr rootPtr;

        private static string F(float v, string fmt = "0.00") => float.IsNaN(v) ? "-" : v.ToString(fmt, CultureInfo.InvariantCulture);

        public static void Tick(VehicleSync vs)
        {
            if (!Telemetry.Enabled || Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 1f;
            vs.Scan();
            var root = vs.Root;
            if (!GameAccess.Alive(root)) return;

            if (rootPtr != root.Pointer || Time.unscaledTime >= nextFind)
            {
                nextFind = Time.unscaledTime + 10f;
                try { veh = GameAccess.GetComp<Vehicle>(root) ?? GameAccess.GetCompInChildren<Vehicle>(root); } catch { veh = null; }
                try { motor = GameAccess.GetCompInChildren<VehicleMotor>(root); } catch { motor = null; }
                modules.Clear(); sails.Clear();
                try { modules.AddRange(GameAccess.GetCompsInChildren<VehicleModule>(root)); } catch { }
                try { sails.AddRange(GameAccess.GetCompsInChildren<VehicleSail>(root)); } catch { }
            }

            try { Telemetry.Line("VEH  " + State(vs)); } catch (Exception e) { Telemetry.Line("VEH  error " + e.Message); }
            try { Moving(vs, root); } catch (Exception e) { Telemetry.Line("MOVE error " + e.Message); }
        }

        private static string State(VehicleSync vs)
        {
            var sb = new StringBuilder(400);
            sb.Append("fuel=").Append(F(vs.Energy, "0.0"));
            if (GameAccess.Alive(motor))
            {
                try { sb.Append(" steam=").Append(F(motor.GetSteam().GetValue())); } catch { }
                try { sb.Append(" speed=").Append(F(motor.GetSpeed())); } catch { }
                try { sb.Append(" vel=").Append(F(motor.GetVelocity())); } catch { }
                try { sb.Append(" brakes=").Append(motor.IsOnBrakes() ? 1 : 0); } catch { }
                try { sb.Append(" dist=").Append(F(motor.GetTacho().GetDistance(), "0.0")); } catch { }
                try { var b = motor.GetButtonEngine(); if (b != null) sb.Append(" engineBtn=").Append(b.state.ToString()); } catch { }
            }
            if (GameAccess.Alive(veh))
            {
                try { sb.Append(" health=").Append(F(veh.GetHealth().GetValue())); } catch { }
                try { sb.Append(" moving=").Append(veh.IsMoving() ? 1 : 0); } catch { }
                try { sb.Append(" angle=").Append(F(veh.GetVehicleAngle(), "0.0")); } catch { }
                try { sb.Append(" cover=").Append(veh.IsFrontCoverActive() ? 1 : 0); } catch { }
            }
            var ch = vs.Chassis;
            if (ch != null && GameAccess.Alive(ch))
            {
                try { sb.Append(" kinematic=").Append(ch.isKinematic ? 1 : 0); } catch { }
            }
            foreach (var s in sails)
            {
                if (!GameAccess.Alive(s)) continue;
                try { sb.Append($" | sail {s.name}: open={(s.IsOpen() ? 1 : 0)} hooked={(s.IsHooked() ? 1 : 0)} blocked={(s.IsBlocked() ? 1 : 0)}"); } catch { }
            }
            foreach (var m in modules)
            {
                if (!GameAccess.Alive(m)) continue;
                try
                {
                    sb.Append(" | mod ").Append(m.name).Append(':');
                    try { sb.Append(" on=").Append(m.IsActive() ? 1 : 0); } catch { }
                    try { var h = m.GetHealth(); if (h != null) sb.Append(" hp=").Append(F(h.GetValue())).Append('/').Append(h.GetState().ToString()); } catch { }
                    try { var f = m.GetFire(); if (f != null) sb.Append(" fire=").Append(F(f.GetValue())).Append('/').Append(f.GetState().ToString()); } catch { }
                }
                catch { }
            }
            return sb.ToString();
        }

        private static void Rescan(VehicleSync vs, GameObject root)
        {
            rootPtr = root.Pointer;
            parts.Clear(); names.Clear(); isBody.Clear();
            var bodies = new HashSet<IntPtr>();
            foreach (var rb in vs.Bodies) if (GameAccess.Alive(rb)) bodies.Add(rb.transform.Pointer);
            var all = GameAccess.GetCompsInChildren<Transform>(root);
            var tChar = Il2CppType.Of<Character>();
            var tItem = Il2CppType.Of<Item>();
            foreach (var t in all)
            {
                if (t == null || parts.Count >= 4000) continue;
                try
                {
                    // players and loose items are logged elsewhere
                    if (t.GetComponentInParent(tChar) != null || t.GetComponentInParent(tItem) != null) continue;
                    string n = t.name;
                    if (n.StartsWith("FarCoop")) continue;
                    var p = t.parent;
                    parts.Add(t);
                    names.Add(p != null ? p.name + "/" + n : n);
                    isBody.Add(bodies.Contains(t.Pointer));
                }
                catch { }
            }
            lastPos = new Vector3[parts.Count];
            lastRot = new Quaternion[parts.Count];
            have = new bool[parts.Count];
        }

        private struct Mover { public string Name; public float Dist, Angle; }

        private static void Moving(VehicleSync vs, GameObject root)
        {
            if (rootPtr != root.Pointer || Time.unscaledTime >= nextRescan)
            {
                nextRescan = Time.unscaledTime + 15f;
                int before = parts.Count;
                Rescan(vs, root);
                if (before != parts.Count) Telemetry.Line($"MOVE watching {parts.Count} vehicle parts");
                // first pass after a rescan only records poses
            }
            var ch = vs.Chassis;
            Transform frame = ch != null && GameAccess.Alive(ch) ? ch.transform : root.transform;
            Vector3 fp = frame.position; Quaternion finv = Quaternion.Inverse(frame.rotation);

            var movers = new List<Mover>();
            for (int i = 0; i < parts.Count; i++)
            {
                var t = parts[i];
                Vector3 p; Quaternion q;
                try
                {
                    if (isBody[i]) { p = finv * (t.position - fp); q = finv * t.rotation; }   // physics part: relative to the chassis
                    else { p = t.localPosition; q = t.localRotation; }                         // animated part: relative to its parent
                }
                catch { continue; }
                if (have[i])
                {
                    float d = (p - lastPos[i]).magnitude;
                    float a = Quaternion.Angle(q, lastRot[i]);
                    if (d > 0.002f || a > 0.5f) movers.Add(new Mover { Name = names[i], Dist = d, Angle = a });
                }
                lastPos[i] = p; lastRot[i] = q; have[i] = true;
            }
            if (movers.Count == 0) { return; }
            movers.Sort((x, y) => (y.Dist * 100f + y.Angle).CompareTo(x.Dist * 100f + x.Angle));
            var sb = new StringBuilder(600);
            sb.Append("MOVE ").Append(movers.Count).Append(" parts moved:");
            for (int i = 0; i < movers.Count && i < 25; i++)
            {
                var m = movers[i];
                sb.Append(' ').Append(m.Name).Append('(');
                if (m.Dist > 0.002f) sb.Append(F(m.Dist)).Append('m');
                if (m.Dist > 0.002f && m.Angle > 0.5f) sb.Append(' ');
                if (m.Angle > 0.5f) sb.Append(F(m.Angle, "0")).Append("deg");
                sb.Append(')');
            }
            if (movers.Count > 25) sb.Append(" ...");
            Telemetry.Line(sb.ToString());
        }
    }
}

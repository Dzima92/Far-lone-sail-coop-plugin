using System;
using System.Collections.Generic;
using System.IO;
using FarCoopMod.Net;
using Okomotive.FarLoneSails;
using UnhollowerRuntimeLib;
using UnityEngine;

namespace FarCoopMod
{
    /// <summary>
    /// "Live sync": instead of teaching the mod about every lever, needle and piston one by one, the
    /// host WATCHES the whole vehicle. Any part that moves (relative to its parent) is announced to the
    /// clients and its pose streamed from then on; clients simply show it. Plus the vehicle's numbers
    /// (steam, health, every module's health and fire).
    /// Physics parts, players and boxes are not touched here - they have their own sync.
    /// </summary>
    public static class LiveSync
    {
        public static bool Enabled => FarCoopPlugin.CfgLiveSync != null && FarCoopPlugin.CfgLiveSync.Value;

        // ======================= host =======================

        private sealed class Part
        {
            public Transform T;
            public int Id = -1;
            public string Path;
            public bool HaveScan;
            public Vector3 ScanPos, SentPos;
            public Quaternion ScanRot, SentRot;
        }

        private static readonly Dictionary<IntPtr, Part> known = new Dictionary<IntPtr, Part>();
        private static readonly List<Part> idle = new List<Part>();      // watched, never moved yet
        private static readonly List<Part> active = new List<Part>();    // moved at least once: streamed
        private static readonly List<Part> newlyActive = new List<Part>();
        private static IntPtr rootPtr;
        private static float nextRescan, nextDiscover, nextSend, nextFull, nextValues;
        private static int nextId;
        public static int ActiveCount => active.Count;
        public static int WatchedCount => known.Count;

        private static void Rescan(VehicleSync vs, GameObject root)
        {
            if (rootPtr != root.Pointer) { known.Clear(); idle.Clear(); active.Clear(); newlyActive.Clear(); nextId = 0; rootPtr = root.Pointer; }
            var tChar = Il2CppType.Of<Character>();
            var tItem = Il2CppType.Of<Item>();
            var tBody = Il2CppType.Of<Rigidbody>();
            var all = GameAccess.GetCompsInChildren<Transform>(root);
            foreach (var t in all)
            {
                if (t == null || known.Count >= 5000) continue;
                try
                {
                    if (known.ContainsKey(t.Pointer)) continue;
                    if (t.Pointer == root.transform.Pointer) continue;
                    if (t.GetComponent(tBody) != null) continue;                                   // physics part: VehicleSync
                    if (t.GetComponentInParent(tChar) != null || t.GetComponentInParent(tItem) != null) continue;
                    if (t.name.StartsWith("FarCoop")) continue;
                    var p = new Part { T = t };
                    known[t.Pointer] = p;
                    idle.Add(p);
                }
                catch { }
            }
        }

        /// <summary>Host, every send tick. Returns messages to broadcast (may be empty).</summary>
        public static List<byte[]> HostTick(VehicleSync vs)
        {
            var result = new List<byte[]>();
            if (!Enabled) return result;
            var root = vs.Root;
            if (!GameAccess.Alive(root)) return result;
            float now = Time.unscaledTime;

            if (rootPtr != root.Pointer || now >= nextRescan)
            {
                nextRescan = now + 15f;
                Rescan(vs, root);
            }

            // discover: has an idle part started to move? (2x per second, cheap enough for thousands)
            if (now >= nextDiscover)
            {
                nextDiscover = now + 0.5f;
                for (int i = idle.Count - 1; i >= 0; i--)
                {
                    var p = idle[i];
                    try
                    {
                        if (!GameAccess.Alive(p.T)) { known.Remove(p.T.Pointer); idle.RemoveAt(i); continue; }
                        Vector3 lp = p.T.localPosition; Quaternion lr = p.T.localRotation;
                        if (p.HaveScan && ((lp - p.ScanPos).sqrMagnitude > 0.000001f || Quaternion.Angle(lr, p.ScanRot) > 0.5f))
                        {
                            if (nextId >= 65000) continue;
                            p.Id = nextId++;
                            p.Path = GameAccess.GetPath(p.T);
                            idle.RemoveAt(i);
                            active.Add(p);
                            newlyActive.Add(p);
                        }
                        p.ScanPos = lp; p.ScanRot = lr; p.HaveScan = true;
                    }
                    catch { idle.RemoveAt(i); }
                }
            }

            if (newlyActive.Count > 0)
            {
                result.Add(DefineMessage(newlyActive));
                if (newlyActive.Count <= 5)
                    foreach (var p in newlyActive) FarCoopPlugin.Logger.LogInfo("LiveSync: now syncing " + p.Path);
                else FarCoopPlugin.Logger.LogInfo($"LiveSync: now syncing {newlyActive.Count} more moving parts ({active.Count} total)");
                newlyActive.Clear();
            }

            // stream the active ones at 20 Hz (only what changed; everything every 2 s)
            if (now >= nextSend && active.Count > 0)
            {
                nextSend = now + 0.05f;
                bool full = now >= nextFull;
                if (full) nextFull = now + 2f;
                var w = Proto.Begin(MsgType.LiveParts, out var ms);
                long countPos = ms.Position;
                w.Write((ushort)0);
                int n = 0;
                foreach (var p in active)
                {
                    try
                    {
                        if (!GameAccess.Alive(p.T)) continue;
                        Vector3 lp = p.T.localPosition; Quaternion lr = p.T.localRotation;
                        if (!full && (lp - p.SentPos).sqrMagnitude <= 0.000001f && Quaternion.Angle(lr, p.SentRot) <= 0.3f) continue;
                        p.SentPos = lp; p.SentRot = lr;
                        w.Write((ushort)p.Id);
                        w.Write(lp);
                        w.WriteQuatC(lr);
                        n++;
                    }
                    catch { }
                }
                if (n > 0)
                {
                    w.Flush();
                    long end = ms.Position;
                    ms.Position = countPos;
                    w.Write((ushort)n);
                    w.Flush();
                    ms.Position = end;
                    result.Add(Proto.End(w, ms));
                }
            }

            if (now >= nextValues)
            {
                nextValues = now + 0.25f;
                var v = ValuesMessage(root);
                if (v != null) result.Add(v);
            }
            return result;
        }

        private static byte[] DefineMessage(List<Part> parts)
        {
            var w = Proto.Begin(MsgType.LiveDefine, out var ms);
            w.Write((ushort)parts.Count);
            foreach (var p in parts) { w.Write((ushort)p.Id); w.Write(p.Path ?? ""); }
            return Proto.End(w, ms);
        }

        /// <summary>Host: everything announced so far, for a player who just joined.</summary>
        public static byte[] AllDefines()
        {
            if (!Enabled || active.Count == 0) return null;
            nextFull = 0f;   // and a full pose refresh right away
            return DefineMessage(active);
        }

        // ---- numbers ----
        private static Vehicle veh;
        private static VehicleMotor motor;
        private static readonly List<VehicleModule> modules = new List<VehicleModule>();
        private static float nextFind;
        private static IntPtr valuesRoot;

        private static void FindParts(GameObject root)
        {
            if (valuesRoot == root.Pointer && Time.unscaledTime < nextFind) return;
            valuesRoot = root.Pointer;
            nextFind = Time.unscaledTime + 10f;
            try { veh = GameAccess.GetComp<Vehicle>(root) ?? GameAccess.GetCompInChildren<Vehicle>(root); } catch { veh = null; }
            try { motor = GameAccess.GetCompInChildren<VehicleMotor>(root); } catch { motor = null; }
            modules.Clear();
            try { modules.AddRange(GameAccess.GetCompsInChildren<VehicleModule>(root)); } catch { }
        }

        private static byte[] ValuesMessage(GameObject root)
        {
            FindParts(root);
            float steam = float.NaN, health = float.NaN;
            try { if (GameAccess.Alive(motor)) steam = motor.GetSteam().GetValue(); } catch { }
            try { if (GameAccess.Alive(veh)) health = veh.GetHealth().GetValue(); } catch { }
            var w = Proto.Begin(MsgType.LiveValues, out var ms);
            w.Write(steam);
            w.Write(health);
            w.Write((byte)Math.Min(modules.Count, 255));
            for (int i = 0; i < modules.Count && i < 255; i++)
            {
                float hp = float.NaN, fire = float.NaN;
                try { var h = modules[i].GetHealth(); if (h != null) hp = h.GetValue(); } catch { }
                try { var f = modules[i].GetFire(); if (f != null) fire = f.GetValue(); } catch { }
                w.Write(hp);
                w.Write(fire);
            }
            return Proto.End(w, ms);
        }

        // ======================= client =======================

        private sealed class CPart
        {
            public Transform T;
            public string Path;
            public bool Has, Init;
            public Vector3 Cur, Target;
            public Quaternion CurR = Quaternion.identity, TargetR = Quaternion.identity;
        }
        private static readonly Dictionary<int, CPart> cparts = new Dictionary<int, CPart>();
        private static readonly List<CPart> cactive = new List<CPart>();
        private static float nextResolve;
        public static int ClientParts => cactive.Count;
        public static int Unresolved { get; private set; }

        public static void ReadDefine(BinaryReader r)
        {
            int n = r.ReadUInt16();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadUInt16();
                string path = r.ReadString();
                if (!cparts.TryGetValue(id, out var p)) { p = new CPart(); cparts[id] = p; cactive.Add(p); }
                p.Path = path;
                p.T = Resolve(path);
                p.Init = false;
            }
            Count();
        }

        private static Transform Resolve(string path)
        {
            try { var go = GameAccess.FindByPath(path); return go != null ? go.transform : null; }
            catch { return null; }
        }

        private static void Count()
        {
            int u = 0;
            foreach (var p in cactive) if (!GameAccess.Alive(p.T)) u++;
            if (u != Unresolved && u > 0) FarCoopPlugin.Logger.LogInfo($"LiveSync: {cactive.Count} synced parts, {u} not found in this game");
            Unresolved = u;
        }

        public static void ReadParts(BinaryReader r)
        {
            int n = r.ReadUInt16();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadUInt16();
                Vector3 lp = r.ReadVector3();
                Quaternion lr = r.ReadQuatC();
                if (!cparts.TryGetValue(id, out var p)) continue;
                p.Target = lp; p.TargetR = lr; p.Has = true;
            }
        }

        /// <summary>Client, LateUpdate: after the game's own scripts and animations, show the host's poses.</summary>
        public static void Apply(float dt)
        {
            if (!Enabled || cactive.Count == 0) return;
            if (Unresolved > 0 && Time.unscaledTime >= nextResolve)
            {
                nextResolve = Time.unscaledTime + 3f;
                foreach (var p in cactive) if (!GameAccess.Alive(p.T) && p.Path != null) p.T = Resolve(p.Path);
                Count();
            }
            float k = 1f - Mathf.Exp(-20f * dt);
            foreach (var p in cactive)
            {
                if (!p.Has || !GameAccess.Alive(p.T)) continue;
                try
                {
                    if (!p.Init || (p.Cur - p.Target).sqrMagnitude > 0.25f) { p.Cur = p.Target; p.CurR = p.TargetR; p.Init = true; }
                    else { p.Cur = Vector3.Lerp(p.Cur, p.Target, k); p.CurR = Quaternion.Slerp(p.CurR, p.TargetR, k); }
                    p.T.localPosition = p.Cur;
                    p.T.localRotation = p.CurR;
                }
                catch { }
            }
        }

        public static void ReadValues(BinaryReader r, VehicleSync vs)
        {
            float steam = r.ReadSingle();
            float health = r.ReadSingle();
            int n = r.ReadByte();
            if (!Enabled) return;
            var root = vs.Root;
            if (!GameAccess.Alive(root)) return;
            FindParts(root);
            try
            {
                if (!float.IsNaN(steam) && GameAccess.Alive(motor))
                {
                    var s = motor.GetSteam();
                    if (s != null && Mathf.Abs(s.GetValue() - steam) > 0.005f) s.SetValue(steam);
                }
            }
            catch { }
            try
            {
                if (!float.IsNaN(health) && GameAccess.Alive(veh))
                {
                    var h = veh.GetHealth();
                    if (h != null && Mathf.Abs(h.GetValue() - health) > 0.005f) h.SetValue(health);
                }
            }
            catch { }
            for (int i = 0; i < n; i++)
            {
                float hp = r.ReadSingle();
                float fire = r.ReadSingle();
                if (n != modules.Count || i >= modules.Count || !GameAccess.Alive(modules[i])) continue;   // different vehicle setup: leave alone
                try
                {
                    if (!float.IsNaN(hp)) { var h = modules[i].GetHealth(); if (h != null && Mathf.Abs(h.GetValue() - hp) > 0.01f) h.SetValue(hp, false); }
                }
                catch { }
                try
                {
                    if (!float.IsNaN(fire)) { var f = modules[i].GetFire(); if (f != null && Mathf.Abs(f.GetValue() - fire) > 0.01f) f.SetValue(fire); }
                }
                catch { }
            }
        }

        public static void Reset()
        {
            cparts.Clear(); cactive.Clear(); Unresolved = 0;
            known.Clear(); idle.Clear(); active.Clear(); newlyActive.Clear();
            nextId = 0; rootPtr = IntPtr.Zero; nextRescan = nextDiscover = nextSend = nextFull = nextValues = 0f;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using SEAerospace.Frames;
using SEAerospace.Orbital;

namespace SEAerospace.Api
{
    /// <summary>
    /// THE API'S FRAME SNAPSHOT (OrbitalApi's Peek* members): a copy of every frame - id, body, elements, berth, anchor, flags,
    /// members - that readers on any thread read WITHOUT the frame lock and without allocating. The frame lock is held for a
    /// whole server step, so the lock-taking members (GetFrames, TryGetFrameOrbit ...) can stall a caller for milliseconds;
    /// this one never blocks a reader.
    ///
    /// A sequence lock: the writer makes the count odd, writes, makes it even; a reader copies what it needs between two reads
    /// of the count and tries again when they differ (or one was odd). The arrays are only ever replaced by LONGER ones, and
    /// every read is bounds-checked against the array it holds, so a reader racing a writer reads stale numbers - which the
    /// count then rejects - never throws. One writer at a time (Refresh takes its own lock; the caller holds the frame lock
    /// so the registry holds still while it is copied). Engine-free: the offline ApiTests drive it.
    /// </summary>
    public sealed class FrameTable
    {
        /// <summary>Doubles per frame in the elements array: a, e, i, raan, argp, nu, epoch, mu.</summary>
        public const int ElementStride = 8;

        int _seq;
        readonly object _write = new object();

        long[] _ids = new long[16];
        string[] _body = new string[16];
        double[] _el = new double[16 * ElementStride];
        double[] _berth = new double[16 * 3];
        long[] _anchor = new long[16];
        byte[] _flags = new byte[16];
        int[] _mStart = new int[16], _mCount = new int[16];
        long[] _members = new long[64];
        int _count;
        double _time = double.NaN;
        int _setVersion;
        long _setHash;

        const byte FlagEncounter = 1, FlagVoxel = 2;

        /// <summary>One frame as copied (what a reader gets out).</summary>
        public struct Row
        {
            public long Id, Anchor;
            public string Body;
            public KeplerianElements Elements;
            public Vector3D Berth;
            public int MemberCount;
            public bool IsEncounter, VoxelAnchored;
        }

        /// <summary>Frames in the table.</summary>
        public int Count => Volatile.Read(ref _count);
        /// <summary>The universe time the table was copied at (NaN: never).</summary>
        public double Time
        {
            get
            {
                var sw = new SpinWait();
                while (true)
                {
                    int s = Volatile.Read(ref _seq);
                    if ((s & 1) == 0)
                    {
                        double t = _time;
                        Interlocked.MemoryBarrier();
                        if (Volatile.Read(ref _seq) == s) return t;
                    }
                    sw.SpinOnce();
                }
            }
        }
        /// <summary>Bumped whenever the set of frames or of any frame's members changed between two refreshes.</summary>
        public int SetVersion => Volatile.Read(ref _setVersion);

        /// <summary>Copy the registry (the caller holds the frame lock). A null registry empties the table.</summary>
        public void Refresh(FrameRegistry reg, double now)
        {
            lock (_write)
            {
                int n = 0, m = 0;
                if (reg != null)
                {
                    // (the registry's own dictionary walked with its struct enumerator: through IEnumerable it boxed one per call)
                    var frames = reg.Frames;
                    if (frames is Dictionary<long, ProximityFrame>.ValueCollection vc) foreach (var f in vc) { n++; m += f.Members.Count; }
                    else foreach (var f in frames) { n++; m += f.Members.Count; }
                }
                Interlocked.Increment(ref _seq);   // odd: writing (a full fence - nothing below moves above it)
                Grow(n, m);   // (inside the odd window: a reader that saw the new arrays sees the count move, and reads again)
                int i = 0, k = 0; long hash = 17;
                if (reg != null)
                {
                    var frames = reg.Frames;
                    if (frames is Dictionary<long, ProximityFrame>.ValueCollection vc) foreach (var f in vc) Put(f, ref i, ref k, ref hash);
                    else foreach (var f in frames) Put(f, ref i, ref k, ref hash);
                }
                hash = hash * 31 + i;
                _count = i; _time = now;
                if (hash != _setHash) { _setHash = hash; _setVersion++; }
                Interlocked.Increment(ref _seq);   // even: done (a full fence - nothing above moves below it)
            }
        }

        void Put(ProximityFrame f, ref int i, ref int k, ref long hash)
        {
            if (i >= _ids.Length) return;   // (the registry grew under us - the caller's lock says it cannot; never overrun)
            _ids[i] = f.Id; _body[i] = f.ParentBodyName; _anchor[i] = f.AnchorEntityId;
            var el = f.Elements; int o = i * ElementStride;
            _el[o] = el.SemiMajorAxis; _el[o + 1] = el.Eccentricity; _el[o + 2] = el.Inclination; _el[o + 3] = el.Raan;
            _el[o + 4] = el.ArgPeriapsis; _el[o + 5] = el.TrueAnomaly; _el[o + 6] = el.Epoch; _el[o + 7] = el.Mu;
            _berth[3 * i] = f.BerthCenter.X; _berth[3 * i + 1] = f.BerthCenter.Y; _berth[3 * i + 2] = f.BerthCenter.Z;
            _flags[i] = (byte)((f.IsEncounter ? FlagEncounter : 0) | (f.VoxelAnchored ? FlagVoxel : 0));
            _mStart[i] = k;
            hash = hash * 31 + f.Id;
            var mem = f.Members;
            for (int j = 0; j < mem.Count && k < _members.Length; j++) { _members[k++] = mem[j]; hash = hash * 31 + mem[j]; }
            _mCount[i] = k - _mStart[i];
            hash = hash * 31 + _mCount[i];
            i++;
        }

        void Grow(int n, int m)
        {
            if (n > _ids.Length)
            {
                int c = Math.Max(n, _ids.Length * 2);
                _ids = new long[c]; _body = new string[c]; _el = new double[c * ElementStride]; _berth = new double[c * 3];
                _anchor = new long[c]; _flags = new byte[c]; _mStart = new int[c]; _mCount = new int[c];
            }
            if (m > _members.Length) _members = new long[Math.Max(m, _members.Length * 2)];
        }

        // ── readers (any thread; lock-free, allocation-free) ──

        /// <summary>A frame's row. False: no such frame in the table.</summary>
        public bool TryGet(long frameId, out Row row)
        {
            var sw = new SpinWait();
            while (true)
            {
                int s = Volatile.Read(ref _seq);
                if ((s & 1) == 0)
                {
                    bool found = TryCopy(frameId, out row);
                    Interlocked.MemoryBarrier();
                    if (Volatile.Read(ref _seq) == s) return found;
                }
                sw.SpinOnce();
            }
        }

        bool TryCopy(long frameId, out Row row)
        {
            row = default;
            var ids = _ids; int n = Math.Min(_count, ids.Length);
            for (int i = 0; i < n; i++)
            {
                if (ids[i] != frameId) continue;
                var el = _el; var berth = _berth; var body = _body; var anchor = _anchor; var flags = _flags; var mc = _mCount;
                if ((i + 1) * ElementStride > el.Length || 3 * i + 2 >= berth.Length || i >= body.Length || i >= anchor.Length
                    || i >= flags.Length || i >= mc.Length) return false;
                int o = i * ElementStride;
                row.Id = frameId; row.Body = body[i]; row.Anchor = anchor[i]; row.MemberCount = mc[i];
                row.Elements = new KeplerianElements
                {
                    SemiMajorAxis = el[o], Eccentricity = el[o + 1], Inclination = el[o + 2], Raan = el[o + 3],
                    ArgPeriapsis = el[o + 4], TrueAnomaly = el[o + 5], Epoch = el[o + 6], Mu = el[o + 7],
                };
                row.Berth = new Vector3D(berth[3 * i], berth[3 * i + 1], berth[3 * i + 2]);
                row.IsEncounter = (flags[i] & FlagEncounter) != 0; row.VoxelAnchored = (flags[i] & FlagVoxel) != 0;
                return true;
            }
            return false;
        }

        /// <summary>Every frame id into the caller's buffer (as many as fit, table order). Returns how many frames there are -
        /// more than ids.Length: grow it and ask again.</summary>
        public int CopyIds(long[] ids)
        {
            var sw = new SpinWait();
            while (true)
            {
                int s = Volatile.Read(ref _seq);
                if ((s & 1) == 0)
                {
                    var src = _ids; int n = Math.Min(_count, src.Length);
                    if (ids != null) for (int i = 0; i < n && i < ids.Length; i++) ids[i] = src[i];
                    Interlocked.MemoryBarrier();
                    if (Volatile.Read(ref _seq) == s) return n;
                }
                sw.SpinOnce();
            }
        }

        /// <summary>A frame's members into the caller's buffer (as many as fit). Returns its member count, or -1: no such frame.</summary>
        public int CopyMembers(long frameId, long[] into)
        {
            var sw = new SpinWait();
            while (true)
            {
                int s = Volatile.Read(ref _seq);
                if ((s & 1) == 0)
                {
                    int r = -1;
                    var ids = _ids; int n = Math.Min(_count, ids.Length);
                    var st = _mStart; var mc = _mCount; var mem = _members;
                    for (int i = 0; i < n; i++)
                    {
                        if (ids[i] != frameId) continue;
                        if (i >= st.Length || i >= mc.Length) break;
                        int a = st[i], c = mc[i];
                        if (a < 0 || c < 0 || a + c > mem.Length) break;
                        if (into != null) for (int j = 0; j < c && j < into.Length; j++) into[j] = mem[a + j];
                        r = c; break;
                    }
                    Interlocked.MemoryBarrier();
                    if (Volatile.Read(ref _seq) == s) return r;
                }
                sw.SpinOnce();
            }
        }

        /// <summary>The frame a member (grid member id or player id) is in, or -1.</summary>
        public long FrameOf(long memberId)
        {
            var sw = new SpinWait();
            while (true)
            {
                int s = Volatile.Read(ref _seq);
                if ((s & 1) == 0)
                {
                    long r = -1; bool hit = false;
                    var ids = _ids; int n = Math.Min(_count, ids.Length);
                    var st = _mStart; var mc = _mCount; var mem = _members;
                    for (int i = 0; i < n && !hit; i++)
                    {
                        if (i >= st.Length || i >= mc.Length) break;
                        int a = st[i], c = mc[i];
                        for (int j = 0; j < c && a + j < mem.Length; j++) if (mem[a + j] == memberId) { r = ids[i]; hit = true; break; }
                    }
                    Interlocked.MemoryBarrier();
                    if (Volatile.Read(ref _seq) == s) return r;
                }
                sw.SpinOnce();
            }
        }

        /// <summary>The frame whose berth (a sphere of radius slotRadius about its centre) holds a world point - the nearest
        /// such - or false.</summary>
        public bool TryBerthAt(Vector3D world, double slotRadius, out Row row)
        {
            row = default;
            var sw = new SpinWait();
            while (true)
            {
                int s = Volatile.Read(ref _seq);
                if ((s & 1) == 0)
                {
                    long best = 0; bool any = false; double bd = slotRadius * slotRadius;
                    var ids = _ids; var berth = _berth; int n = Math.Min(_count, ids.Length);
                    for (int i = 0; i < n && 3 * i + 2 < berth.Length; i++)
                    {
                        double dx = world.X - berth[3 * i], dy = world.Y - berth[3 * i + 1], dz = world.Z - berth[3 * i + 2];
                        double d = dx * dx + dy * dy + dz * dz;
                        if (d <= bd) { bd = d; best = ids[i]; any = true; }
                    }
                    bool found = any && TryCopy(best, out row);
                    Interlocked.MemoryBarrier();
                    if (Volatile.Read(ref _seq) == s) return found;
                }
                sw.SpinOnce();
            }
        }
    }
}

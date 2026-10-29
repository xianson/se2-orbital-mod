using System.Reflection;
using System.Reflection.Emit;
using Keen.VRage.Library.Mathematics;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace OrbitalMod;

/// <summary>
/// A procedural voxel shape written in the mod and run by the game's own planet pipeline (2026-10-26, Britannica).
///
/// A planet's voxels are composed per chunk as: its heightmap shape, MAX every fill shape, MINUS every remove shape
/// (PlanetCompositeDetailProvider.ReadContentRange). Every one of them is an ICompositeShape - the heightmap planet
/// itself is one (PlanetShapeProvider). The game asks a shape only for chunks its box touches (a bounding-volume tree),
/// at the chunk's level of detail, from worker threads. So a mod shape is a "subclassed planet": any size, any number,
/// computed on demand, no files, nothing saved into the world (the mod adds them again on every load).
///
/// Keen.VRage.Voxels is not on the script whitelist, so a script cannot name ICompositeShape; System.Reflection.Emit
/// is. ProcShapeBridge emits ONE class at runtime that implements ICompositeShape and forwards each member through
/// delegates (BCL / engine-math types only) to a <see cref="ProcShape"/>; its ComputeContent copies the shape's
/// density rows into the game's StorageData with StorageData's own ComputeLinear / Content (IL emitted here).
/// The composition calls are internal and reached by reflection: CompositeShapeData { Shape, Usage, Box, Parent,
/// Intent, Id } -> PlanetCompositeDetailComponent.Provider.AddToComposition, then ForceSignalCheckPoint (which adds the
/// shape to the tree and invalidates only its box). Parent is a CompositePlanetShapeComponent the checkpoint calls
/// SetId on: an uninitialized instance stands in (SetId only writes its _data.Id).
/// CompositePlanetShapeComponent is ISkipReplicate: the SAME shape must be added on the server's planet (collision,
/// the server's voxels) and on the client's (what is drawn) - the 2026-10-26 cut test drew nothing until it did both.
/// </summary>
public static class ProcShapeBridge
{
    public static string Status = "-";
    static Type _shapeType;              // the emitted ICompositeShape class (one per mod load)
    static Type _iShape, _iMaterial, _iMatReader, _materialDef, _storageData, _shapeData, _usage, _intent, _parentType, _detailComp, _planetComp;
    static FieldInfo _fC, _fS, _fF, _fI, _fBox, _fLod, _fMat, _fM;

    static Type Find(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName, false);
            if (t != null) return t;
        }
        throw new Exception("type not found: " + fullName);
    }

    /// <summary>Resolves the engine types and emits the shape class, once per mod load.</summary>
    public static bool Ready()
    {
        if (_shapeType != null) return true;
        try
        {
            _iShape = Find("Keen.VRage.Voxels.Procedural.ICompositeShape");
            _iMaterial = Find("Keen.VRage.Voxels.Procedural.ICompositeMaterial");
            _iMatReader = Find("Keen.VRage.Voxels.Procedural.ICompositeMaterialReader");
            _materialDef = Find("Keen.VRage.Voxels.Definitions.VoxelMaterialDefinition");
            _storageData = Find("Keen.VRage.Voxels.Storage.StorageData");
            _shapeData = Find("Keen.VRage.Voxels.Storage.Planet.Composite.CompositeShapeData");
            _usage = Find("Keen.VRage.Voxels.Components.ShapeUsageType");
            _intent = Find("Keen.VRage.Voxels.Storage.Planet.Composite.CompositeCheckpointIntent");
            _parentType = Find("Keen.VRage.Voxels.Components.CompositePlanetShapeComponent");
            _detailComp = Find("Keen.VRage.Voxels.Components.PlanetCompositeDetailComponent");
            _planetComp = Find("Keen.VRage.Voxels.Components.VoxelPlanetComponent");
            _shapeType = Emit();
            _fC = _shapeType.GetField("C"); _fS = _shapeType.GetField("S"); _fF = _shapeType.GetField("F");
            _fI = _shapeType.GetField("I"); _fBox = _shapeType.GetField("Box"); _fLod = _shapeType.GetField("Lod"); _fMat = _shapeType.GetField("Mat"); _fM = _shapeType.GetField("M");
            Status = "ready: " + _shapeType.FullName;
            return true;
        }
        catch (Exception e) { Status = "not ready: " + e.GetType().Name + ": " + e.Message; Log.Default.WriteLine("[PROCSHAPE] " + Status); return false; }
    }

    static Type Emit()
    {
        var asm = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OrbitalProcShapes_" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var mod = asm.DefineDynamicModule("OrbitalProcShapes");
        var tb = mod.DefineType("OrbitalProcShape", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, typeof(object), new[] { _iShape, _iMaterial, _iMatReader });
        var fC = tb.DefineField("C", typeof(Func<BoundingBox, BoundingSphere, int, ContainmentType>), FieldAttributes.Public);
        var fS = tb.DefineField("S", typeof(Func<Vector3, int, float>), FieldAttributes.Public);
        var fF = tb.DefineField("F", typeof(Func<int, Vector3I, Vector3I, int, byte[]>), FieldAttributes.Public);
        var fI = tb.DefineField("I", typeof(Func<LineD, BoundingBoxD, int, double[]>), FieldAttributes.Public);
        var fBox = tb.DefineField("Box", typeof(BoundingBox), FieldAttributes.Public);
        var fLod = tb.DefineField("Lod", typeof(int), FieldAttributes.Public);
        var fMat = tb.DefineField("Mat", _materialDef, FieldAttributes.Public);
        var fM = tb.DefineField("M", typeof(Func<int, Vector3I, Vector3I, int, byte[]>), FieldAttributes.Public);
        // ICompositeMaterialReader.ComputeMaterial (ref StorageData, int lodIndex, Vector3I min, Vector3I max, int lodVoxelSize):
        // the shape's own material per voxel (2026-10-26: one biome per island), written as ComputeContent writes content
        {
            var cm = _iMatReader.GetMethod("ComputeMaterial");
            var cps = cm.GetParameters();
            var mbm = tb.DefineMethod(cm.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                CallingConventions.HasThis, cm.ReturnType, null, null, cps.Select(q => q.ParameterType).ToArray(), cps.Select(q => q.GetRequiredCustomModifiers()).ToArray(), cps.Select(q => q.GetOptionalCustomModifiers()).ToArray());
            EmitRows(mbm.GetILGenerator(), fM, _storageData.GetMethod("Material", new[] { typeof(int), typeof(byte) }));
            tb.DefineMethodOverride(mbm, cm);
        }
        // ICompositeMaterial.Material: the shape's voxels get this material (2026-10-26: without one they kept the
        // planet's material there - none, high above its surface - and near chunks meshed into shards)
        var mg = _iMaterial.GetProperty("Material").GetGetMethod();
        var mb0 = tb.DefineMethod(mg.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.SpecialName,
            mg.ReturnType, Type.EmptyTypes);
        var ilm = mb0.GetILGenerator(); ilm.Emit(OpCodes.Ldarg_0); ilm.Emit(OpCodes.Ldfld, fMat); ilm.Emit(OpCodes.Ret);
        tb.DefineMethodOverride(mb0, mg);
        tb.DefineDefaultConstructor(MethodAttributes.Public);

        foreach (var im in _iShape.GetMethods())
        {
            if (!im.IsAbstract) continue;   // (Valid: a default interface member)
            var ps = im.GetParameters();
            var mb = tb.DefineMethod(im.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                CallingConventions.HasThis, im.ReturnType, im.ReturnParameter.GetRequiredCustomModifiers(), im.ReturnParameter.GetOptionalCustomModifiers(),
                ps.Select(p => p.ParameterType).ToArray(), ps.Select(p => p.GetRequiredCustomModifiers()).ToArray(), ps.Select(p => p.GetOptionalCustomModifiers()).ToArray());
            var il = mb.GetILGenerator();
            switch (im.Name)
            {
                case "get_LodSize":
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fLod); il.Emit(OpCodes.Ret); break;
                case "GetBoundingBox":
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fBox); il.Emit(OpCodes.Ret); break;
                case "Contains":   // (ref BoundingBox, ref BoundingSphere, int)
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fC);
                    il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldobj, typeof(BoundingBox));
                    il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldobj, typeof(BoundingSphere));
                    il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Callvirt, fC.FieldType.GetMethod("Invoke")); il.Emit(OpCodes.Ret); break;
                case "SignedDistance":   // (ref Vector3, int)
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fS);
                    il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldobj, typeof(Vector3));
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Callvirt, fS.FieldType.GetMethod("Invoke")); il.Emit(OpCodes.Ret); break;
                case "Intersect":   // (in LineD, in BoundingBoxD, int, out double, out double) -> bool
                {
                    var r = il.DeclareLocal(typeof(double[])); var has = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fI);
                    il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldobj, typeof(LineD));
                    il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldobj, typeof(BoundingBoxD));
                    il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Callvirt, fI.FieldType.GetMethod("Invoke")); il.Emit(OpCodes.Stloc, r);
                    il.Emit(OpCodes.Ldloc, r); il.Emit(OpCodes.Brtrue, has);
                    il.Emit(OpCodes.Ldarg_S, (byte)4); il.Emit(OpCodes.Ldc_R8, 0.0); il.Emit(OpCodes.Stind_R8);
                    il.Emit(OpCodes.Ldarg_S, (byte)5); il.Emit(OpCodes.Ldc_R8, 0.0); il.Emit(OpCodes.Stind_R8);
                    il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
                    il.MarkLabel(has);
                    il.Emit(OpCodes.Ldarg_S, (byte)4); il.Emit(OpCodes.Ldloc, r); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldelem_R8); il.Emit(OpCodes.Stind_R8);
                    il.Emit(OpCodes.Ldarg_S, (byte)5); il.Emit(OpCodes.Ldloc, r); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ldelem_R8); il.Emit(OpCodes.Stind_R8);
                    il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
                    break;
                }
                case "ComputeContent":   // (ref StorageData, int lodIndex, Vector3I min, Vector3I max, int lodVoxelSize, bool tryCacheReads)
                    EmitRows(il, fF, _storageData.GetMethod("Content", new[] { typeof(int), typeof(byte) })); break;
                default:   // (DebugDraw, Close)
                    if (im.ReturnType != typeof(void)) throw new Exception("unhandled ICompositeShape member " + im.Name);
                    il.Emit(OpCodes.Ret); break;
            }
            tb.DefineMethodOverride(mb, im);
        }
        return tb.CreateType();
    }

    /// <summary>a = F(lod, min, max, lodVoxelSize) - the density rows, x fastest; then, as PlanetShapeProvider writes them:
    /// for z, y: idx = storage.ComputeLinear(new Vector3I(0, y, z)); for x: storage.Content(idx, a[i++]); idx += StepLinear (1).</summary>
    static void EmitRows(ILGenerator il, FieldInfo fF, MethodInfo content)
    {
        var computeLinear = _storageData.GetMethod("ComputeLinear", new[] { typeof(Vector3I).MakeByRefType() });
        var ctor = typeof(Vector3I).GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });
        var fx = typeof(Vector3I).GetField("X"); var fy = typeof(Vector3I).GetField("Y"); var fz = typeof(Vector3I).GetField("Z");
        var a = il.DeclareLocal(typeof(byte[]));
        var i = il.DeclareLocal(typeof(int)); var x = il.DeclareLocal(typeof(int)); var y = il.DeclareLocal(typeof(int)); var z = il.DeclareLocal(typeof(int));
        var nx = il.DeclareLocal(typeof(int)); var ny = il.DeclareLocal(typeof(int)); var nz = il.DeclareLocal(typeof(int));
        var idx = il.DeclareLocal(typeof(int)); var v = il.DeclareLocal(typeof(Vector3I));
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fF);
        il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Ldarg_S, (byte)4); il.Emit(OpCodes.Ldarg_S, (byte)5);
        il.Emit(OpCodes.Callvirt, fF.FieldType.GetMethod("Invoke")); il.Emit(OpCodes.Stloc, a);
        // n = max - min + 1, per axis
        foreach (var (f, n) in new[] { (fx, nx), (fy, ny), (fz, nz) })
        {
            il.Emit(OpCodes.Ldarga_S, (byte)4); il.Emit(OpCodes.Ldfld, f);
            il.Emit(OpCodes.Ldarga_S, (byte)3); il.Emit(OpCodes.Ldfld, f);
            il.Emit(OpCodes.Sub); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, n);
        }
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, i);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, z);
        Label lz = il.DefineLabel(), lzEnd = il.DefineLabel(), ly = il.DefineLabel(), lyEnd = il.DefineLabel(), lx = il.DefineLabel(), lxEnd = il.DefineLabel();
        il.MarkLabel(lz);
        il.Emit(OpCodes.Ldloc, z); il.Emit(OpCodes.Ldloc, nz); il.Emit(OpCodes.Bge, lzEnd);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, y);
        il.MarkLabel(ly);
        il.Emit(OpCodes.Ldloc, y); il.Emit(OpCodes.Ldloc, ny); il.Emit(OpCodes.Bge, lyEnd);
        il.Emit(OpCodes.Ldloca, v); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldloc, y); il.Emit(OpCodes.Ldloc, z); il.Emit(OpCodes.Call, ctor);
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloca, v); il.Emit(OpCodes.Call, computeLinear); il.Emit(OpCodes.Stloc, idx);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, x);
        il.MarkLabel(lx);
        il.Emit(OpCodes.Ldloc, x); il.Emit(OpCodes.Ldloc, nx); il.Emit(OpCodes.Bge, lxEnd);
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloc, idx); il.Emit(OpCodes.Ldloc, a); il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Ldelem_U1);
        il.Emit(OpCodes.Call, content);
        il.Emit(OpCodes.Ldloc, i); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, i);
        il.Emit(OpCodes.Ldloc, idx); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, idx);
        il.Emit(OpCodes.Ldloc, x); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, x);
        il.Emit(OpCodes.Br, lx);
        il.MarkLabel(lxEnd);
        il.Emit(OpCodes.Ldloc, y); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, y);
        il.Emit(OpCodes.Br, ly);
        il.MarkLabel(lyEnd);
        il.Emit(OpCodes.Ldloc, z); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, z);
        il.Emit(OpCodes.Br, lz);
        il.MarkLabel(lzEnd);
        il.Emit(OpCodes.Ret);
    }

    static MethodInfo _tagOf, _tryGetByTag;

    /// <summary>A component of an entity by runtime type: Entity.TryGet(DefaultTag.TryGet(type)) - the generic TryGet&lt;T&gt;
    /// scripts call is an extension method, not on Entity (found 2026-10-26 in game).</summary>
    static object Component(object entity, Type t)
    {
        if (_tagOf == null)
        {
            _tagOf = Find("Keen.VRage.DCS.Components.DefaultTag").GetMethod("TryGet", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type) }, null)
                     ?? throw new Exception("no DefaultTag.TryGet(Type)");
            _tryGetByTag = entity.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "TryGet" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == _tagOf.ReturnType)
                ?? throw new Exception("no Entity.TryGet(StringId)");
        }
        return _tryGetByTag.Invoke(entity, new[] { _tagOf.Invoke(null, new object[] { t }) });
    }

    /// <summary>The planet's storage-space centre: a voxel's position relative to the planet's centre is
    /// voxel * 2^lod - Center (PlanetShapeProvider.ConvertToLocalPosition).</summary>
    public static Vector3 PlanetCenter(object planetEntity)
    {
        if (!Ready()) throw new Exception(Status);
        var vpc = Component(planetEntity, _planetComp) ?? throw new Exception("no VoxelPlanetComponent");
        var provider = _planetComp.GetField("Provider", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(vpc);
        var shape = provider.GetType().GetProperty("Shape", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(provider);
        return (Vector3)shape.GetType().GetProperty("Center").GetValue(shape);
    }

    /// <summary>Adds the shape to a planet entity's composition (server or client planet) as a fill. Returns the
    /// emitted shape instance (kept to remove it), or throws.</summary>
    /// (signal false: the caller adds many and calls <see cref="Signal"/> once - one checkpoint per shape made the server
    ///  re-process each box in turn: 551 islands froze it 34 s, 2026-10-26)
    public static object Add(object planetEntity, ProcShape shape, bool signal = true)
    {
        if (!Ready()) throw new Exception(Status);
        var detail = Component(planetEntity, _detailComp) ?? throw new Exception("no PlanetCompositeDetailComponent");
        var provider = _detailComp.GetProperty("Provider", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(detail);
        // (the material: the composition's own fallback rock until the shapes pick per-voxel materials)
        var inst = MakeInstance(shape, provider.GetType().GetField("_defaultMaterial", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(provider));
        object data = Activator.CreateInstance(_shapeData);
        _shapeData.GetField("Shape").SetValue(data, inst);
        _shapeData.GetField("Usage").SetValue(data, Enum.Parse(_usage, "Additive"));
        _shapeData.GetField("Box").SetValue(data, shape.Box);
        _shapeData.GetField("Parent").SetValue(data, System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(_parentType));
        _shapeData.GetField("Intent").SetValue(data, Enum.Parse(_intent, "Add"));
        _shapeData.GetField("Id").SetValue(data, -1);
        provider.GetType().GetMethod("AddToComposition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { _shapeData }, null)
            .Invoke(provider, new[] { data });
        if (signal) _detailComp.GetMethod("ForceSignalCheckPoint").Invoke(detail, null);
        return inst;
    }

    /// <summary>The emitted shape's instance for a ProcShape: its delegates, box, lod size and material.</summary>
    static object MakeInstance(ProcShape shape, object material)
    {
        var inst = Activator.CreateInstance(_shapeType);
        _fC.SetValue(inst, new Func<BoundingBox, BoundingSphere, int, ContainmentType>(shape.Contains));
        _fS.SetValue(inst, new Func<Vector3, int, float>(shape.SignedDistanceStorage));
        _fF.SetValue(inst, new Func<int, Vector3I, Vector3I, int, byte[]>(shape.Fill));
        _fI.SetValue(inst, new Func<LineD, BoundingBoxD, int, double[]>(shape.Intersect));
        _fBox.SetValue(inst, shape.Box);
        _fLod.SetValue(inst, shape.LodSize);
        _fM.SetValue(inst, new Func<int, Vector3I, Vector3I, int, byte[]>(shape.FillMaterial));
        _fMat.SetValue(inst, material);
        return inst;
    }

    /// <summary>Many shapes straight into the planet's composition - what ProcessCheckpoint does for each (the fill-shape
    /// tree, the material check) under the composition's write lock - WITHOUT Storage.NotifyChanged: nothing has read
    /// their boxes yet at load, and on the SERVER each notification made the voxel physics re-process the shape's whole
    /// box (2026-10-26: 551 islands froze the server 48 s in one checkpoint). Use where no cell of those boxes is built
    /// yet; a shape near a player goes through <see cref="Add"/> instead. Returns how many were added.</summary>
    public static int AddDirect(object planetEntity, IReadOnlyList<ProcShape> shapes)
    {
        if (!Ready()) throw new Exception(Status);
        if (shapes.Count == 0) return 0;
        var detail = Component(planetEntity, _detailComp) ?? throw new Exception("no PlanetCompositeDetailComponent");
        var provider = _detailComp.GetProperty("Provider", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(detail);
        var pt = provider.GetType();
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        var lk = (System.Threading.ReaderWriterLockSlim)(pt.GetField("_rwLockImmutable", F)?.GetValue(provider) ?? throw new Exception("no _rwLockImmutable"));
        var curF = pt.GetField("_currentImmutable", F) ?? throw new Exception("no _currentImmutable");
        var check = pt.GetMethod("CheckOverrideMaterials", F, null, new[] { curF.FieldType, _shapeType.GetInterfaces().First(i => i.Name == "ICompositeShape") }, null)
                    ?? throw new Exception("no CheckOverrideMaterials(Immutable, ICompositeShape)");
        object material = pt.GetField("_defaultMaterial", F)?.GetValue(provider);
        var insts = new List<object>(shapes.Count);
        foreach (var shape in shapes) insts.Add(MakeInstance(shape, material));
        lk.EnterWriteLock();
        try
        {
            object cur = curF.GetValue(provider), imm = cur;
            var it = cur.GetType();
            bool swap = (bool)it.GetProperty("IsUsed", F).GetValue(cur);
            if (swap) imm = it.GetMethod("Clone", F).Invoke(cur, null);
            object tree = it.GetField("FillShapes", F).GetValue(imm);
            // (DynamicBoundingVolumeTree3D<T>.Add(in BoundingBoxD, T, bool reBalance = true))
            var add = tree.GetType().GetMethod("Add", F, null, new[] { typeof(BoundingBoxD).MakeByRefType(), _shapeType.GetInterfaces().First(i => i.Name == "ICompositeShape"), typeof(bool) }, null)
                      ?? throw new Exception("no FillShapes.Add(in BoundingBoxD, ICompositeShape, bool)");
            for (int k = 0; k < insts.Count; k++)
            {
                var box = shapes[k].Box; box.Inflate(16f);   // (as the checkpoint)
                add.Invoke(tree, new object[] { (BoundingBoxD)box, insts[k], true });
            }
            check.Invoke(provider, new[] { imm, insts[0] });   // (the one material they share)
            if (swap) { curF.SetValue(provider, imm); it.GetMethod("MarkForDispose", F).Invoke(cur, null); }
        }
        finally { lk.ExitWriteLock(); }
        return insts.Count;
    }

    /// <summary>A voxel material (its definition Guid) as the planet storage's own index - what StorageData holds: the
    /// composition's remapping of the material's global index (PlanetCompositeDetailProvider.Immutable.RemappingCache, as
    /// it remaps an ICompositeMaterial's). 255 when the planet has no such material.</summary>
    public static byte MaterialIndex(object planetEntity, Guid material)
    {
        if (!Ready()) throw new Exception(Status);
        object inst = Keen.VRage.Library.Definitions.DefinitionManager.Instance;
        MethodInfo tryGet = null;
        foreach (var m in inst.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == "TryGetDefinition" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(Guid)) { tryGet = m; break; }
        var args = new object[] { material, null };
        if (tryGet == null || !(bool)tryGet.MakeGenericMethod(_materialDef).Invoke(inst, args) || args[1] == null) return 255;
        byte index = (byte)_materialDef.GetProperty("Index").GetValue(args[1]);
        var detail = Component(planetEntity, _detailComp);
        var provider = _detailComp.GetProperty("Provider", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(detail);
        var imm = provider.GetType().GetField("_currentImmutable", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(provider);
        var remap = imm?.GetType().GetField("RemappingCache", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(imm) as byte[];
        return remap != null && index < remap.Length ? remap[index] : (byte)255;
    }

    /// <summary>Shapes still waiting in the planet's composition queue (the checkpoint was not open when added).</summary>
    public static int Queued(object planetEntity)
    {
        var detail = Component(planetEntity, _detailComp);
        var provider = _detailComp.GetProperty("Provider", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(detail);
        var q = provider.GetType().GetField("_shapeQueue", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(provider);
        return q is System.Collections.ICollection c ? c.Count : -1;
    }

    /// <summary>Retries the checkpoint (it processes the queue only while the planet's checkpoint is open).</summary>
    public static void Signal(object planetEntity) => _detailComp.GetMethod("ForceSignalCheckPoint").Invoke(Component(planetEntity, _detailComp), null);
}

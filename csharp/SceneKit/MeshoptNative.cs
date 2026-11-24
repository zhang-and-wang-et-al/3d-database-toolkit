using System.Runtime.InteropServices;

namespace SceneKit;

/// <summary>Loader and thin span wrappers for the meshoptimizer native library (MIT, built by tools/build_meshoptimizer.ps1).
/// Loading never throws: when the library is missing or older than 1.0, <see cref="Available"/> is false and
/// <see cref="Unavailable"/> says why. The wrappers must only be called when <see cref="Available"/> is true.</summary>
internal static unsafe class MeshoptNative
{
    // meshopt_Simplify* option bits and the vertex_lock flag (meshoptimizer.h, v1.x)
    public const uint LockBorder = 1u, ErrorAbsolute = 4u, Prune = 8u, Regularize = 16u, Permissive = 32u;
    public const byte VertexLock = (byte)1;

    const int MaxAttributes = 32;   // meshopt_simplifyWithAttributes accepts at most 32 attribute floats
    const nuint PosStride = 12;     // positions are always tightly packed float xyz

    static readonly delegate* unmanaged[Cdecl]<uint*, uint*, nuint, float*, nuint, nuint, nuint, float, uint, float*, nuint> simplify;
    static readonly delegate* unmanaged[Cdecl]<uint*, uint*, nuint, float*, nuint, nuint, float*, nuint, float*, nuint, byte*, nuint, float, uint, float*, nuint> simplifyWithAttributes;
    static readonly delegate* unmanaged[Cdecl]<uint*, uint*, nuint, float*, nuint, nuint, byte*, nuint, float, float*, nuint> simplifySloppy;
    static readonly delegate* unmanaged[Cdecl]<uint*, uint*, nuint, float*, nuint, nuint, float, nuint> simplifyPrune;
    static readonly delegate* unmanaged[Cdecl]<float*, nuint, nuint, float> simplifyScale;

    static readonly bool loaded;
    static readonly string reason = "";

    /// <summary>Tests only: pretend the library is missing.</summary>
    internal static bool ForceUnavailable
    {
        get => forceUnavailable.Value;
        set => forceUnavailable.Value = value;
    }
    static readonly AsyncLocal<bool> forceUnavailable = new();   // per test flow, so parallel test classes do not see each other's flag

    public static bool Available => loaded && !ForceUnavailable;

    /// <summary>Why meshoptimizer cannot be used ("" when it can).</summary>
    public static string Unavailable =>
        Available ? "" : reason != "" ? reason : NotFound(LibraryName());

    static string LibraryName() =>
        OperatingSystem.IsWindows() ? "meshoptimizer.dll" : OperatingSystem.IsMacOS() ? "libmeshoptimizer.dylib" : "libmeshoptimizer.so";

    static string NotFound(string name) => $"meshoptimizer is not available on this computer ({name} not found next to the program)";

    static MeshoptNative()
    {
        string name = LibraryName();
        try
        {
            // the bundled DLL is win-x64 only
            if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture != Architecture.X64) { reason = NotFound(name); return; }
            if (!NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, name), out var h)
                && !NativeLibrary.TryLoad(name, typeof(MeshoptNative).Assembly, null, out h)) { reason = NotFound(name); return; }
            if (!NativeLibrary.TryGetExport(h, "meshopt_simplify", out var pSimplify)
                || !NativeLibrary.TryGetExport(h, "meshopt_simplifyWithAttributes", out var pAttr)
                || !NativeLibrary.TryGetExport(h, "meshopt_simplifySloppy", out var pSloppy)
                || !NativeLibrary.TryGetExport(h, "meshopt_simplifyPrune", out var pPrune)
                || !NativeLibrary.TryGetExport(h, "meshopt_simplifyScale", out var pScale)
                || !NativeLibrary.TryGetExport(h, "meshopt_simplifyWithUpdate", out _))   // added in 1.0
            {
                reason = $"meshoptimizer is not available on this computer ({name} is older than 1.0)";
                return;
            }
            simplify = (delegate* unmanaged[Cdecl]<uint*, uint*, nuint, float*, nuint, nuint, nuint, float, uint, float*, nuint>)pSimplify;
            simplifyWithAttributes = (delegate* unmanaged[Cdecl]<uint*, uint*, nuint, float*, nuint, nuint, float*, nuint, float*, nuint, byte*, nuint, float, uint, float*, nuint>)pAttr;
            simplifySloppy = (delegate* unmanaged[Cdecl]<uint*, uint*, nuint, float*, nuint, nuint, byte*, nuint, float, float*, nuint>)pSloppy;
            simplifyPrune = (delegate* unmanaged[Cdecl]<uint*, uint*, nuint, float*, nuint, nuint, float, nuint>)pPrune;
            simplifyScale = (delegate* unmanaged[Cdecl]<float*, nuint, nuint, float>)pScale;
            loaded = true;
        }
        catch (Exception)   // never throw from the type initializer
        {
            loaded = false;
            reason = NotFound(name);
        }
    }

    static void Check(Span<uint> dst, ReadOnlySpan<uint> idx, ReadOnlySpan<float> pos, int vertexCount)
    {
        if (!Available) throw new InvalidOperationException(Unavailable);
        if (idx.Length % 3 != 0) throw new ArgumentException("index count must be a multiple of 3", nameof(idx));
        if (dst.Length < idx.Length) throw new ArgumentException("destination is shorter than the index list", nameof(dst));
        if (vertexCount < 0 || pos.Length < vertexCount * 3) throw new ArgumentException("fewer positions than vertices", nameof(pos));
        foreach (uint i in idx)   // the release library has no assert: an index past the vertices reads out of bounds
            if (i >= (uint)vertexCount) throw new ArgumentException($"index {i} is not below the vertex count {vertexCount}", nameof(idx));
    }

    static void CheckTarget(ReadOnlySpan<uint> idx, int targetIndexCount)
    {
        if (targetIndexCount < 0 || targetIndexCount > idx.Length)
            throw new ArgumentOutOfRangeException(nameof(targetIndexCount), targetIndexCount, $"target must be within 0..{idx.Length}");
    }

    static void CheckLocks(ReadOnlySpan<byte> locks, int vertexCount)
    {
        if (!locks.IsEmpty && locks.Length < vertexCount) throw new ArgumentException("fewer lock flags than vertices", nameof(locks));
    }

    /// <summary>meshopt_simplify; returns the number of indices written to <paramref name="dst"/>.</summary>
    public static int Simplify(Span<uint> dst, ReadOnlySpan<uint> idx, ReadOnlySpan<float> pos, int vertexCount,
                               int targetIndexCount, float targetError, uint options, out float resultError)
    {
        Check(dst, idx, pos, vertexCount);
        CheckTarget(idx, targetIndexCount);
        float err = 0;
        nuint n;
        fixed (uint* d = dst) fixed (uint* i = idx) fixed (float* p = pos)
            n = simplify(d, i, (nuint)idx.Length, p, (nuint)vertexCount, PosStride, (nuint)targetIndexCount, targetError, options, &err);
        resultError = err;
        return (int)n;
    }

    /// <summary>meshopt_simplifyWithAttributes with <paramref name="attrCount"/> floats per vertex in <paramref name="attrs"/>
    /// and optional per-vertex <paramref name="locks"/> (<see cref="VertexLock"/>). Without attributes and locks it calls
    /// meshopt_simplify; with locks but no attributes it passes one zero-weight dummy channel.</summary>
    public static int SimplifyWithAttributes(Span<uint> dst, ReadOnlySpan<uint> idx, ReadOnlySpan<float> pos, int vertexCount,
                                             ReadOnlySpan<float> attrs, int attrCount, ReadOnlySpan<float> weights, ReadOnlySpan<byte> locks,
                                             int targetIndexCount, float targetError, uint options, out float resultError)
    {
        if (attrCount < 0 || attrCount > MaxAttributes)
            throw new ArgumentOutOfRangeException(nameof(attrCount), attrCount, $"meshoptimizer accepts 0..{MaxAttributes} attributes");
        Check(dst, idx, pos, vertexCount);
        CheckTarget(idx, targetIndexCount);
        CheckLocks(locks, vertexCount);
        if (attrCount == 0)
        {
            if (locks.IsEmpty) return Simplify(dst, idx, pos, vertexCount, targetIndexCount, targetError, options, out resultError);
            attrs = new float[vertexCount];
            weights = new float[1];
            attrCount = 1;
        }
        else
        {
            if (attrs.Length < vertexCount * attrCount) throw new ArgumentException("fewer attribute values than vertices x attrCount", nameof(attrs));
            if (weights.Length < attrCount) throw new ArgumentException("fewer weights than attributes", nameof(weights));
        }
        float err = 0;
        nuint n;
        fixed (uint* d = dst) fixed (uint* i = idx) fixed (float* p = pos) fixed (float* a = attrs) fixed (float* w = weights) fixed (byte* l = locks)
            n = simplifyWithAttributes(d, i, (nuint)idx.Length, p, (nuint)vertexCount, PosStride,
                                       a, (nuint)(4 * attrCount), w, (nuint)attrCount, l,
                                       (nuint)targetIndexCount, targetError, options, &err);
        resultError = err;
        return (int)n;
    }

    /// <summary>meshopt_simplifySloppy with optional per-vertex <paramref name="locks"/>.</summary>
    public static int SimplifySloppy(Span<uint> dst, ReadOnlySpan<uint> idx, ReadOnlySpan<float> pos, int vertexCount,
                                     ReadOnlySpan<byte> locks, int targetIndexCount, float targetError, out float resultError)
    {
        Check(dst, idx, pos, vertexCount);
        CheckTarget(idx, targetIndexCount);
        CheckLocks(locks, vertexCount);
        float err = 0;
        nuint n;
        fixed (uint* d = dst) fixed (uint* i = idx) fixed (float* p = pos) fixed (byte* l = locks)
            n = simplifySloppy(d, i, (nuint)idx.Length, p, (nuint)vertexCount, PosStride, l, (nuint)targetIndexCount, targetError, &err);
        resultError = err;
        return (int)n;
    }

    /// <summary>meshopt_simplifyPrune: removes disconnected parts smaller than <paramref name="targetError"/> (relative).</summary>
    public static int SimplifyPrune(Span<uint> dst, ReadOnlySpan<uint> idx, ReadOnlySpan<float> pos, int vertexCount, float targetError)
    {
        Check(dst, idx, pos, vertexCount);
        fixed (uint* d = dst) fixed (uint* i = idx) fixed (float* p = pos)
            return (int)simplifyPrune(d, i, (nuint)idx.Length, p, (nuint)vertexCount, PosStride, targetError);
    }

    /// <summary>meshopt_simplifyScale: the factor that turns a relative error into an absolute one (the largest extent).</summary>
    public static float SimplifyScale(ReadOnlySpan<float> pos, int vertexCount)
    {
        if (!Available) throw new InvalidOperationException(Unavailable);
        if (vertexCount < 0 || pos.Length < vertexCount * 3) throw new ArgumentException("fewer positions than vertices", nameof(pos));
        fixed (float* p = pos) return simplifyScale(p, (nuint)vertexCount, PosStride);
    }
}

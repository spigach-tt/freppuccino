using System.Runtime.CompilerServices;
using Freppuccino.Csg;
using Freppuccino.Display;
using Freppuccino.Meshing;
using Freppuccino.Scene;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.UI;

namespace Freppuccino.Selection;

/// Owns left clicks that land on F-rep geometry in views showing the Freppuccino display mode (plan section 3).
/// Acts only when the cursor is not on a gumball handle and no command is running (or the running command opened an
/// explicit F-rep selection session, see <see cref="BeginSession"/>). Anything else, and every click that hits no
/// F-rep, is left to Rhino.
///   plain click: the nearest root whose final field the click ray hits; replaces the selection. If it hits no root
///                but does hit a control shape (an exposed cutter), that shape's root.
///   Shift+click: the nearest control instance (the leaf's own pre-warp shape under T × P, i.e. what its outline shows),
///                with the root and path it was reached through; replaces the selection. A Shift+click on nothing F-rep
///                is left to Rhino (add to selection). Coincident shapes from different roots (including one shape shared
///                by several roots, where no root's surface is hit first): no pick, a message.
/// The hit is decided on mouse-down (which is when Rhino's own pick has to be cancelled), the selection is made on
/// mouse-up, after Rhino has finished with the click.
public sealed class FrepPicker : MouseCallback
{
    static FrepPicker? instance;
    static int sessions;

    public static void Install() => instance ??= new FrepPicker { Enabled = true };

    /// While a session is open the picker also acts during a running command (FrepMesh's pick-blends prompt).
    public static IDisposable BeginSession()
    {
        sessions++;
        return new Session();
    }

    sealed class Session : IDisposable
    {
        bool done;
        public void Dispose()
        {
            if (done) return;
            done = true;
            sessions--;
        }
    }

    /// Raised after the picker changed the selection.
    public static event Action<RhinoDoc>? Picked;

    protected override void OnMouseDown(MouseCallbackEventArgs e)
    {
        pending = null;
        if (e.MouseButton != MouseButton.Left || e.View is not { } view) return;
        var doc = view.Document;
        if (doc == null || !IsFrepView(view.ActiveViewport)) return;
        if (e.IsOverGumball() != Rhino.UI.Gumball.GumballMode.None) return;
        if (Command.InCommand() && sessions == 0) return;
        if (e.CtrlKeyDown) return;

        var vp = view.ActiveViewport;
        var client = new Point2d(e.ViewportPoint.X, e.ViewportPoint.Y);
        if (e.ShiftKeyDown && sessions == 0)
        {
            // Leaf: the nearest control instance. A miss is left to Rhino (Shift+click = add to its selection).
            var leaf = PickOutline(doc, vp, client, rootsToo: false, out var ambiguous)?.Instance;
            if (leaf == null && !ambiguous) leaf = PickInstance(doc, vp, client, out ambiguous);
            if (leaf == null && !ambiguous) return;
            e.Cancel = true;
            pending = ambiguous ? new Pending(doc, null, null, true) : new Pending(doc, null, leaf, false);
            return;
        }

        var outline = PickOutline(doc, vp, client, rootsToo: true, out _);
        var hit = outline is { } o ? o.Root ?? o.Instance!.Root : PickRoot(doc, vp, client);
        if (hit == null)
        {
            // An exposed control (e.g. a cutter's face outside the result) selects the root it belongs to.
            var inst = PickInstance(doc, vp, client, out var ambiguous);
            if (inst != null) hit = inst.Root;
            else if (ambiguous)
            {
                e.Cancel = true;
                pending = new Pending(doc, null, null, true);
                return;
            }
        }
        if (hit is not { } root)
        {
            // Rhino deselects its own objects on an empty click, but node roots aren't Rhino objects. (Not in a session:
            // there an empty click goes to the running command.)
            if (sessions == 0) UiController_ClearNodes(doc);
            return;
        }
        e.Cancel = true;
        pending = new Pending(doc, root, null, false);
    }

    sealed record Pending(RhinoDoc Doc, FrepOperand? Root, FrepInstance? Leaf, bool Ambiguous);
    Pending? pending;

    protected override void OnMouseUp(MouseCallbackEventArgs e)
    {
        if (pending is not { } p || e.MouseButton != MouseButton.Left) return;
        pending = null;
        e.Cancel = true;
        if (p.Ambiguous)
        {
            RhinoApp.WriteLine("Freppuccino: several shapes coincide there (a shape shared by several blends); select it in the F-rep panel.");
            return;
        }
        if (p.Leaf != null) FrepSelection.SelectLeaf(p.Doc, p.Leaf, add: false);
        else if (p.Root is { } r)
        {
            // In a selection session clicks accumulate (a second click on a chosen blend unchooses it).
            if (sessions > 0 && FrepSelection.Roots(p.Doc).Contains(r)) FrepSelection.DeselectRoot(p.Doc, r);
            else FrepSelection.SelectRoot(p.Doc, r, add: sessions > 0);
        }
        Picked?.Invoke(p.Doc);
    }

    // Per scene snapshot: one single-primitive field per control instance (its canonical leaf under T × P).
    static readonly ConditionalWeakTable<FrepSceneData, List<(FrepInstance Instance, FrepField Field, (V3 Min, V3 Max) Box)>> instanceFields = new();

    static List<(FrepInstance, FrepField, (V3, V3))> InstanceFields(FrepSceneData scene) =>
        instanceFields.GetValue(scene, s =>
        {
            var result = new List<(FrepInstance, FrepField, (V3, V3))>();
            var (_, instances) = FrepInstances.Compute(s.Tree, s.Ids);
            var index = new Dictionary<Guid, int>();
            for (int i = 0; i < s.Ids.Count; i++) index[s.Ids[i]] = i;
            foreach (var inst in instances)
            {
                if (!index.TryGetValue(inst.Object, out var i)) continue;
                var prim = s.Primitives[i];
                // The leaf under its instance's root transforms T, by the same rules as a gumball edit of the leaf
                // (rigid parts fold into the values, the rest into SDF.Linear's basis).
                if (!inst.T.IsIdentity && !FrepScene.TryTransform(prim, inst.T, out prim)) continue;
                var f = FrepFieldBuilder.Single(prim);
                result.Add((inst, f, f.Bounds()));
            }
            return result;
        });

    /// Nearest control instance under the cursor. <paramref name="ambiguous"/>: the nearest hits tie between instances
    /// that aren't the same shape at the same place (coincident shapes from different roots): no pick then.
    public static FrepInstance? PickInstance(RhinoDoc doc, RhinoViewport vp, Point2d client, out bool ambiguous)
    {
        ambiguous = false;
        var scene = FrepScene.Get(doc);
        var ray = Ray(vp, client);
        var hits = new List<(FrepInstance Inst, double T)>();
        foreach (var (inst, field, box) in InstanceFields(scene))
        {
            if (doc.Objects.FindId(inst.Object) is not { IsLocked: false, IsHidden: false }) continue;
            if (March(field, box, ray) is { } t) hits.Add((inst, t));
        }
        if (hits.Count == 0) return null;
        hits.Sort((a, b) => a.T.CompareTo(b.T));
        var best = hits[0];
        double tieTol = 4 * (ray.EpsConst + ray.EpsSlope * best.T);
        var tied = hits.Where(h => h.T - best.T <= tieTol).ToList();
        // A click resolves the actual root (and instance): equal-depth hits from different roots, objects or transforms
        // are ambiguous, even for one shared shape at one place (agreed rule, #118). Within a root, instances are
        // already deduplicated by (object, T).
        if (tied.Any(h => h.Inst.Root != best.Inst.Root || h.Inst.Object != best.Inst.Object || !FrepInstances.Same(h.Inst.T, best.Inst.T)))
        {
            ambiguous = true;
            return null;
        }
        return best.Inst;
    }


    const double OutlinePixels = 3;
    static readonly Vector2d[] Ring = [.. Enumerable.Range(0, 8).Select(i => new Vector2d(Math.Cos(i * Math.PI / 4), Math.Sin(i * Math.PI / 4)))];

    public static (FrepOperand? Root, FrepInstance? Instance)? PickOutline(RhinoDoc doc, RhinoViewport vp, Point2d client, bool rootsToo, out bool ambiguous)
    {
        ambiguous = false;
        var scene = FrepScene.Get(doc);
        var rays = Ring.Select(d => Ray(vp, client + d * OutlinePixels)).Prepend(Ray(vp, client)).ToArray();
        (FrepOperand? Root, FrepInstance? Instance)? best = null;
        double bestT = double.PositiveInfinity;
        var instanceHits = new List<(FrepInstance Inst, double T)>();
        foreach (var (inst, field, box) in InstanceFields(scene))
        {
            if (doc.Objects.FindId(inst.Object) is not { IsLocked: false, IsHidden: false }) continue;
            if (OutlineHit(field, box, rays) is not { } t) continue;
            instanceHits.Add((inst, t));
            if (t < bestT) (bestT, best) = (t, (null, inst));
        }
        if (rootsToo && scene.Program != null)
            foreach (var (root, field, box) in RootFields(scene))
                if (OutlineHit(field, box, rays) is { } t && t < bestT) (bestT, best) = (t, (root, null));
        if (best is { Instance: { } chosen })
        {
            double tieTol = 4 * (rays[0].EpsConst + rays[0].EpsSlope * bestT);
            ambiguous = instanceHits.Any(h => h.T - bestT <= tieTol &&
                (h.Inst.Root != chosen.Root || h.Inst.Object != chosen.Object || !FrepInstances.Same(h.Inst.T, chosen.T)));
            if (ambiguous) return null;
        }
        return best;
    }

    static double? OutlineHit(FrepField f, (V3 Min, V3 Max) box, PickRay[] rays)
    {
        double? nearest = null;
        bool missed = false;
        foreach (var r in rays)
            if (March(f, box, r) is { } t) nearest = Math.Min(nearest ?? t, t);
            else missed = true;
        return missed ? nearest : null;
    }

    static void UiController_ClearNodes(RhinoDoc doc) => Ui.UiController.SetSelectedNodes(doc, []);

    public static bool IsFrepView(RhinoViewport vp) =>
        FreppuccinoDisplayMode.Id != Guid.Empty && vp.DisplayMode?.Id == FreppuccinoDisplayMode.Id;

    // Per scene snapshot (FrepScene.Get returns a new instance whenever anything changed): one field per root.
    static readonly ConditionalWeakTable<FrepSceneData, List<(FrepOperand Root, FrepField Field, (V3 Min, V3 Max) Box)>> fields = new();

    static List<(FrepOperand Root, FrepField Field, (V3 Min, V3 Max) Box)> RootFields(FrepSceneData scene) =>
        fields.GetValue(scene, s =>
        {
            var result = new List<(FrepOperand, FrepField, (V3, V3))>();
            FrepFieldBuilder.Build(s, null, out var roots);
            foreach (var r in roots)
            {
                var f = FrepFieldBuilder.Build(s, [r], out var got);
                if (got.Count == 1) result.Add((r, f, f.Bounds()));
            }
            return result;
        });

    /// Nearest root whose field the ray through <paramref name="client"/> (viewport client coordinates) hits.
    public static FrepOperand? PickRoot(RhinoDoc doc, RhinoViewport vp, Point2d client)
    {
        var scene = FrepScene.Get(doc);
        if (scene.Program == null) return null;
        var ray = Ray(vp, client);
        FrepOperand? best = null;
        double bestT = double.PositiveInfinity;
        foreach (var (root, field, box) in RootFields(scene))
            if (March(field, box, ray, bestT) is { } t && t < bestT)
            {
                bestT = t;
                best = root;
            }
        return best;
    }

    /// Ray origin/direction plus the shader's hit tolerance (half a pixel footprint: eps = c + slope · t).
    public readonly record struct PickRay(V3 Origin, V3 Dir, double EpsConst, double EpsSlope);

    public static PickRay Ray(RhinoViewport vp, Point2d client)
    {
        var line = vp.ClientToWorld(client);
        // ClientToWorld runs far → near; march away from the camera, from the near end.
        if (line.Direction * vp.CameraDirection < 0) line.Flip();
        var dir = line.Direction;
        dir.Unitize();
        vp.GetFrustum(out var l, out var r, out _, out _, out var near, out _);
        double slope = (r - l) / Math.Max(1, vp.Size.Width) * 0.5;
        if (vp.IsPerspectiveProjection)
        {
            var o = vp.CameraLocation;
            return new PickRay(new V3(o.X, o.Y, o.Z), new V3(dir.X, dir.Y, dir.Z), 0, slope / near);
        }
        // Parallel: start well behind the scene so nothing in front of the near plane is missed.
        var from = line.From;
        return new PickRay(new V3(from.X, from.Y, from.Z), new V3(dir.X, dir.Y, dir.Z), slope, 0);
    }

    /// Sphere-traces <paramref name="f"/> along the ray inside <paramref name="box"/>; distance along the ray or null.
    public static double? March(FrepField f, (V3 Min, V3 Max) box, PickRay ray, double tLimit = double.PositiveInfinity)
    {
        if (!Slab(box, ray.Origin, ray.Dir, out var t0, out var t1)) return null;
        double t = Math.Max(t0, 0), tMax = Math.Min(t1, tLimit);
        for (int i = 0; i < 512 && t <= tMax; i++)
        {
            double eps = ray.EpsConst + ray.EpsSlope * t;
            double d = f.Value(ray.Origin + ray.Dir * t);
            if (d < eps) return t;
            t += Math.Max(d, eps * 0.5);
        }
        return null;
    }

    static bool Slab((V3 Min, V3 Max) b, V3 o, V3 d, out double t0, out double t1)
    {
        t0 = double.NegativeInfinity;
        t1 = double.PositiveInfinity;
        // Empty bounds (Min > Max somewhere, e.g. a disjoint intersection) or non-finite: nothing to hit.
        if (!(b.Min.X <= b.Max.X && b.Min.Y <= b.Max.Y && b.Min.Z <= b.Max.Z) ||
            !double.IsFinite(b.Min.X + b.Min.Y + b.Min.Z + b.Max.X + b.Max.Y + b.Max.Z))
            return false;
        // Pad the box slightly: it bounds the surface, and the hit tolerance can reach just outside it.
        double pad = 1e-9 + 1e-6 * Math.Max(b.Max.X - b.Min.X, Math.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z));
        for (int a = 0; a < 3; a++)
        {
            double lo = (a == 0 ? b.Min.X : a == 1 ? b.Min.Y : b.Min.Z) - pad, hi = (a == 0 ? b.Max.X : a == 1 ? b.Max.Y : b.Max.Z) + pad;
            double oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z, da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            if (Math.Abs(da) < 1e-300)
            {
                if (oa < lo || oa > hi) return false;
                continue;
            }
            double ta = (lo - oa) / da, tb = (hi - oa) / da;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = Math.Max(t0, ta);
            t1 = Math.Min(t1, tb);
            if (t0 > t1) return false;
        }
        return true;
    }
}

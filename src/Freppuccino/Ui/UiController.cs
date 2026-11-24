using System.Text.Json;
using System.Text.Json.Nodes;
using Freppuccino.Ai;
using Freppuccino.Commands;
using Freppuccino.Csg;
using Freppuccino.Display;
using Freppuccino.Sdf;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Ui;

/// <summary>
/// Glue between Rhino and the side panel page: pushes scene / selection snapshots when they change
/// (coalesced on Idle) and handles requests from the page.
///
/// Scene = SDF primitives (Rhino objects) + the doc's CSG tree (<see cref="FrepTrees"/>). Operation nodes aren't
/// Rhino objects, so their selection lives here (<see cref="TakeSelectedNodes"/>).
/// </summary>
public sealed class UiController : IDisposable
{
    /// Commands the page may run. Keep this a whitelist: the page must not be able to run arbitrary scripts.
    static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "FBox", "FSphere", "FCylinder", "FCone", "FPyramid", "FEllipsoid", "FTorus",
        "FFunction", "FTwist", "FBend", "FRepeat", "Freppuccino",
    };

    /// Selected operation nodes per doc (runtime serial), in pick order.
    static readonly Dictionary<uint, List<int>> selectedNodes = [];

    /// Replace the panel selection with one operation node and show the gumball on its leaves.
    public static void SelectNode(RhinoDoc doc, int id)
    {
        doc.Objects.UnselectAll();
        ClearNodes(doc);
        NodesOf(doc).Add(id);
        var tree = FrepTrees.Get(doc);
        if (tree.Find(id) is { } node) SelectLeaves(doc, tree, node.Id, true);
        ShowGumball();
        NodeSelectionChanged?.Invoke();
    }

    /// Selected node ids; clears them (consumed like Rhino preselection by FUnion/FIntersect).
    public static IReadOnlyList<int> TakeSelectedNodes(RhinoDoc doc)
    {
        if (!selectedNodes.Remove(doc.RuntimeSerialNumber, out var ids)) return [];
        NodeSelectionChanged?.Invoke();
        return ids;
    }

    /// Selected node ids (panel order), without consuming them.
    public static IReadOnlyList<int> SelectedNodes(RhinoDoc doc) => selectedNodes.GetValueOrDefault(doc.RuntimeSerialNumber, []);

    /// Writes the panel's node selection (FrepSelection is the caller for scene clicks).
    public static void SetSelectedNodes(RhinoDoc doc, IEnumerable<int> ids)
    {
        var list = NodesOf(doc);
        var next = ids.Distinct().ToList();
        if (list.SequenceEqual(next)) return;
        list.Clear();
        list.AddRange(next);
        NodeSelectionChanged?.Invoke();
    }

    static event Action? NodeSelectionChanged;

    static List<int> NodesOf(RhinoDoc doc) =>
        selectedNodes.TryGetValue(doc.RuntimeSerialNumber, out var l) ? l : selectedNodes[doc.RuntimeSerialNumber] = [];

    readonly WebViewHost web;
    bool sceneDirty, selectionDirty;
    int pruning;
    /// Set by ReplaceRhinoObject. The delete that follows is the old object making way for its replacement, not a removal.
    Guid? replaceOldId;
    uint sentDocSerial;
    bool sentFrepMode;
    /// Open slider drag: the primitive as it was before the first preview tick. One undo step on release.
    ParamEdit? paramEdit;

    public UiController(WebViewHost web)
    {
        this.web = web;
        web.MessageReceived += Handle;

        RhinoDoc.SelectObjects += OnSelectionChanged;
        RhinoDoc.DeselectObjects += OnSelectionChanged;
        RhinoDoc.DeselectAllObjects += OnDeselectAll;
        RhinoDoc.AddRhinoObject += OnObjectChanged;
        RhinoDoc.DeleteRhinoObject += OnRhinoDeleted;
        RhinoDoc.UndeleteRhinoObject += OnObjectChanged;
        RhinoDoc.ReplaceRhinoObject += OnReplaceObject;
        RhinoDoc.ModifyObjectAttributes += OnModifyAttributes;
        FrepTrees.Changed += OnTreeChanged;
        NodeSelectionChanged += OnNodeSelectionChanged;
        RhinoApp.Idle += OnIdle;
        RhinoApp.EscapeKeyPressed += OnEscape;
        Command.BeginCommand += OnBeginCommand;
    }

    public void Dispose()
    {
        web.MessageReceived -= Handle;
        RhinoDoc.SelectObjects -= OnSelectionChanged;
        RhinoDoc.DeselectObjects -= OnSelectionChanged;
        RhinoDoc.DeselectAllObjects -= OnDeselectAll;
        RhinoDoc.AddRhinoObject -= OnObjectChanged;
        RhinoDoc.DeleteRhinoObject -= OnRhinoDeleted;
        RhinoDoc.UndeleteRhinoObject -= OnObjectChanged;
        RhinoDoc.ReplaceRhinoObject -= OnReplaceObject;
        RhinoDoc.ModifyObjectAttributes -= OnModifyAttributes;
        FrepTrees.Changed -= OnTreeChanged;
        NodeSelectionChanged -= OnNodeSelectionChanged;
        RhinoApp.Idle -= OnIdle;
        RhinoApp.EscapeKeyPressed -= OnEscape;
        Command.BeginCommand -= OnBeginCommand;
        CommitColor();
        FrepTrees.CommitAll();
        if (RhinoDoc.ActiveDoc is { } doc) EndParamGesture(doc);
    }

    // ─────────────── Rhino → page ───────────────

    void OnSelectionChanged(object? sender, RhinoObjectSelectionEventArgs e) => selectionDirty = true;

    void OnObjectChanged(object? sender, RhinoObjectEventArgs e)
    {
        sceneDirty = selectionDirty = true;
        // The add or undelete that finishes a replace. Undo restores the tree itself.
        if (replaceOldId is not Guid old) return;
        replaceOldId = null;
        if (e.TheObject is not { } obj || obj.Id == Guid.Empty || obj.Id == old) return;
        var doc = obj.Document ?? RhinoDoc.ActiveDoc;
        if (doc == null || doc.UndoActive || doc.RedoActive) return;
        var tree = FrepTrees.Get(doc);
        var next = tree.Retarget(old, obj.Id);
        if (!ReferenceEquals(next, tree)) FrepTrees.Set(doc, next, "Transform");
    }

    /// A viewport delete joins this tree edit to the same undo record. Our own delete already excised the id.
    void OnRhinoDeleted(object? sender, RhinoObjectEventArgs e)
    {
        sceneDirty = selectionDirty = true;
        if (e.TheObject is not { } obj) return;
        var doc = obj.Document ?? RhinoDoc.ActiveDoc;
        if (doc == null) return;
        // Move, rotate, and scale replace the object: this delete is followed by an add. Folding the
        // union here drops the node before the replacement exists.
        if (replaceOldId is Guid replacing && replacing == obj.Id) return;
        replaceOldId = null;
        PruneObjects(doc, [obj.Id]);
        // Panel deletes redraw once, after every object is gone. A viewport delete has to redraw here:
        // the scene is already dirty, but nothing else asks the view to paint.
        if (pruning == 0) doc.Views.Redraw();
    }

    void OnReplaceObject(object? sender, RhinoReplaceObjectEventArgs e)
    {
        sceneDirty = true;
        replaceOldId = e.ObjectId;
    }
    void OnModifyAttributes(object? sender, RhinoModifyObjectAttributesEventArgs e) => sceneDirty = true;
    void OnNodeSelectionChanged() => selectionDirty = true;

    // Rhino only raises DeselectAllObjects when Rhino objects were selected, so Esc with just nodes selected needs this.
    void OnEscape(object? sender, EventArgs e)
    {
        if (RhinoDoc.ActiveDoc is { } doc) ClearNodes(doc);
    }

    static void ClearNodes(RhinoDoc doc)
    {
        if (selectedNodes.Remove(doc.RuntimeSerialNumber, out var ids) && ids.Count > 0) NodeSelectionChanged?.Invoke();
    }

    // Deselect-all (Esc, click on empty space, end of an F-CSG command) also clears node selection.
    void OnDeselectAll(object? sender, RhinoDeselectAllObjectsEventArgs e)
    {
        selectedNodes.Remove(e.Document.RuntimeSerialNumber);
        selectionDirty = true;
    }

    void OnTreeChanged(RhinoDoc doc)
    {
        // Drop selected nodes that no longer exist (undo, delete).
        if (selectedNodes.TryGetValue(doc.RuntimeSerialNumber, out var ids))
            ids.RemoveAll(id => FrepTrees.Get(doc).Find(id) == null);
        sceneDirty = selectionDirty = true;
    }

    void OnIdle(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) return;
        // A replace that never got its add must not retarget whatever object shows up next.
        replaceOldId = null;

        if (doc.RuntimeSerialNumber != sentDocSerial)
        {
            SendState(doc);
            return;
        }
        if (sceneDirty)
        {
            PruneDangling(doc);
            sceneDirty = false;
            web.Send(new SceneMessage(BuildScene(doc)));
        }
        if (selectionDirty)
        {
            selectionDirty = false;
            web.Send(new SelectionMessage(SelectedIds(doc)));
        }
        if (FrepMode(doc) != sentFrepMode)
        {
            sentFrepMode = !sentFrepMode;
            web.Send(new ViewMessage(sentFrepMode));
        }
    }

    static bool FrepMode(RhinoDoc doc) =>
        FreppuccinoDisplayMode.Id != Guid.Empty && doc.Views.ActiveView?.ActiveViewport.DisplayMode?.Id == FreppuccinoDisplayMode.Id;

    void SendState(RhinoDoc doc)
    {
        FrepTrees.CommitAll(); // page reload / doc switch mid-drag
        CommitColor();
        EndParamGesture(doc);
        PruneDangling(doc);
        sentDocSerial = doc.RuntimeSerialNumber;
        sceneDirty = selectionDirty = false;
        sentFrepMode = FrepMode(doc);
        var theme = Rhino.Runtime.HostUtils.RunningInDarkMode ? "dark" : "light";
        web.Send(new StateMessage(BuildScene(doc), SelectedIds(doc), new UiSettings(doc.ModelUnitSystem.ToString()), theme, sentFrepMode));
    }

    static string NodeKey(int id) => FrepTree.NodeName(id);
    static string ObjectKey(Guid id) => id.ToString("D");

    /// "N3" → 3 if that node exists.
    static bool TryNode(FrepTree tree, string key, out int id)
    {
        if (key.StartsWith('N') && int.TryParse(key.AsSpan(1), out id) && tree.Find(id) != null) return true;
        id = 0;
        return false;
    }

    static IEnumerable<int> Nodes(FrepTree tree, IEnumerable<string> keys) =>
        keys.Select(k => TryNode(tree, k, out var n) ? n : 0).Where(n => n != 0);

    static FrepOperand? ParseOperand(FrepTree tree, string key) =>
        Guid.TryParse(key, out var g) ? FrepOperand.Object(g)
        : TryNode(tree, key, out var n) ? FrepOperand.Node(n)
        : null;

    static string ToKind(FrepOp op) => op switch
    {
        FrepOp.Union => OperationKind.Union,
        FrepOp.Intersect => OperationKind.Intersect,
        FrepOp.Difference => OperationKind.Subtract,
        FrepOp.Twist => OperationKind.Twist,
        FrepOp.Bend => OperationKind.Bend,
        FrepOp.Repeat => OperationKind.Repeat,
        FrepOp.Blend => OperationKind.Blend,
        FrepOp.Offset => OperationKind.Offset,
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    static FrepOp? ParseKind(string kind) => kind switch
    {
        OperationKind.Union => FrepOp.Union,
        OperationKind.Intersect => FrepOp.Intersect,
        OperationKind.Subtract => FrepOp.Difference,
        _ => null,
    };

    /// Roots: nodes nothing references (creation order), then unreferenced primitives (document order).
    /// Operands nest under their node; shared operands repeat under each parent.
    static List<SceneNode> BuildScene(RhinoDoc doc)
    {
        var tree = FrepTrees.Get(doc);
        var built = new Dictionary<int, SceneNode>();

        SceneNode Node(FrepNode n)
        {
            if (built.TryGetValue(n.Id, out var s)) return s;
            var children = n.Operands.Select(o => o.IsNode
                ? (tree.Find(o.NodeId) is { } child ? Node(child) : Missing(NodeKey(o.NodeId)))
                : Primitive(doc, o.ObjectId)).ToList();
            if (n.Op.IsWarp())
            {
                var names = n.Op.ParamNames();
                bool refOk = doc.Objects.FindId(n.Ref) is { IsDeleted: false } r && FrepRefs.Fits(n.Op, r.Geometry);
                return built[n.Id] = new SceneNode(
                    Id: NodeKey(n.Id), Kind: SceneNodeKind.Operation, Name: n.Name, Visible: !n.Hidden, Locked: false,
                    Op: ToKind(n.Op), Params: names.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => n.Param(x.i)),
                    K: null,
                    Children: children, Ref: ObjectKey(n.Ref), RefMissing: refOk ? null : true);
            }
            return built[n.Id] = new SceneNode(
                Id: NodeKey(n.Id), Kind: SceneNodeKind.Operation, Name: n.Name, Visible: !n.Hidden, Locked: false,
                Op: ToKind(n.Op), K: n.K, Children: children);
        }

        var roots = tree.Nodes.Where(n => !tree.IsReferenced(FrepOperand.Node(n.Id))).Select(Node).ToList();

        var settings = new ObjectEnumeratorSettings
        {
            HiddenObjects = true,
            LockedObjects = true,
            NormalObjects = true,
            IncludeLights = false,
            IncludeGrips = false,
        };
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (SdfUserText.ReadType(obj.Attributes) == null || tree.IsReferenced(FrepOperand.Object(obj.Id))) continue;
            roots.Add(Primitive(doc, obj));
        }
        return roots;
    }

    static SceneNode Missing(string id) =>
        new(id, SceneNodeKind.Primitive, "(deleted)", Visible: false, Locked: false, Missing: true);

    static SceneNode Primitive(RhinoDoc doc, Guid id) =>
        doc.Objects.FindId(id) is { IsDeleted: false } obj ? Primitive(doc, obj) : Missing(ObjectKey(id));

    static SceneNode Primitive(RhinoDoc doc, RhinoObject obj)
    {
        var attributes = obj.Attributes;
        var type = SdfUserText.ReadType(attributes);
        var parameters = ReadParams(attributes);
        UserFnInfo? userFn = null;
        if (UserFnView.IsUser(type))
        {
            // L and u.* travel in UserFn (with defaults filled in); Params keeps the box half-extents.
            parameters.Remove(UserFnView.LipschitzName);
            foreach (var key in parameters.Keys.Where(k => k.StartsWith(UserFnView.ParamPrefix, StringComparison.Ordinal)).ToList())
                parameters.Remove(key);
            userFn = UserFnView.Read(attributes);
        }
        return new SceneNode(
            Id: ObjectKey(obj.Id),
            Kind: SceneNodeKind.Primitive,
            Name: string.IsNullOrEmpty(attributes.Name) ? null : attributes.Name,
            Visible: !obj.IsHidden,
            Locked: obj.IsLocked,
            SdfType: type,
            Params: parameters,
            Color: SdfUserText.FormatColor(SdfUserText.ReadColorOrDefault(attributes)),
            Custom: SdfUserText.TryReadLinear(attributes, out _, out _, out _) ? true : null,
            UserFn: userFn);
    }

    static readonly HashSet<string> HeaderKeys = new(StringComparer.Ordinal)
    {
        SdfUserText.VersionKey, SdfUserText.TypeKey, SdfUserText.UnitsKey,
        SdfUserText.OriginKey, SdfUserText.XAxisKey, SdfUserText.YAxisKey, SdfUserText.ColorKey,
        SdfUserText.LinearXKey, SdfUserText.LinearYKey, SdfUserText.LinearZKey,
    };

    /// Every numeric SDF.&lt;param&gt; (SDF.s, SDF.b.x, ...), keyed without the prefix. Generic, so new primitives just show up.
    static Dictionary<string, double> ReadParams(ObjectAttributes attributes)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var strings = attributes.GetUserStrings();
        foreach (string? key in strings.AllKeys)
        {
            if (key == null || !key.StartsWith(SdfUserText.Prefix, StringComparison.Ordinal) || HeaderKeys.Contains(key)) continue;
            var name = key[SdfUserText.Prefix.Length..];
            if (SdfUserText.TryReadFloat(attributes, name, out var v)) result[name] = v;
        }
        return result;
    }

    internal static List<string> SelectedIds(RhinoDoc doc)
    {
        // Leaves selected only so the gumball can sit on a node's geometry stay out of the panel selection.
        var gumball = NodeLeaves(doc);
        var selected = doc.Objects.GetSelectedObjects(false, false).ToList();
        return selected
            .Where(o => SdfUserText.ReadType(o.Attributes) != null && !gumball.Contains(o.Id))
            .Select(o => ObjectKey(o.Id))
            // A shared shape picked through another blend's copy is selected via its instance carrier: its row too.
            .Concat(selected.Select(o => Selection.FrepCarriers.InstanceOf(doc, o)).OfType<Selection.FrepInstance>().Select(i => ObjectKey(i.Object)))
            .Concat(selectedNodes.GetValueOrDefault(doc.RuntimeSerialNumber, []).Select(NodeKey))
            .Distinct()
            .ToList();
    }

    /// Object ids under the selected operation nodes (the geometry the gumball transforms).
    static HashSet<Guid> NodeLeaves(RhinoDoc doc)
    {
        var leaves = new HashSet<Guid>();
        if (!selectedNodes.TryGetValue(doc.RuntimeSerialNumber, out var ids)) return leaves;
        var tree = FrepTrees.Get(doc);
        foreach (var id in ids)
            if (tree.Find(id) is { } node) CollectObjects(tree, node, leaves);
        return leaves;
    }

    static void CollectObjects(FrepTree tree, FrepNode node, HashSet<Guid> into)
    {
        foreach (var o in node.Operands)
        {
            if (!o.IsNode) into.Add(o.ObjectId);
            else if (tree.Find(o.NodeId) is { } child) CollectObjects(tree, child, into);
        }
    }

    // ─────────────── page → Rhino ───────────────

    void Handle(string type, JsonElement message)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) return;
        // Anything but a live blend or warp edit ends that gesture.
        if (type is not ("setOperation" or "setNodeParam")) FrepTrees.Commit(doc);
        if (type is not "setColor") CommitColor();
        // A preview setParam continues the open size drag; every other message closes it as one undo step.
        if (type != "setParam") EndParamGesture(doc);
        try
        {
            switch (type)
            {
                case "ready":
                    SendState(doc);
                    break;
                case "select":
                    Select(doc, Parse<SelectRequest>(message));
                    break;
                case "setVisible":
                    SetVisible(doc, Parse<SetVisibleRequest>(message));
                    break;
                case "runCommand":
                    RunCommand(Parse<RunCommandRequest>(message));
                    break;
                case "showFrepMode":
                    ShowFrepMode(doc);
                    break;
                case "setParam":
                    SetParam(doc, Parse<SetParamRequest>(message));
                    break;
                case "setColor":
                    SetColor(doc, Parse<SetColorRequest>(message));
                    break;
                case "applyOperation":
                    ApplyOperation(doc, Parse<ApplyOperationRequest>(message));
                    break;
                case "blendRepeat":
                    BlendRepeat(doc, Parse<BlendRepeatRequest>(message));
                    break;
                case "offsetNode":
                    OffsetNode(doc, Parse<OffsetNodeRequest>(message));
                    break;
                case "setOperation":
                    SetOperation(doc, Parse<SetOperationRequest>(message));
                    break;
                case "setNodeParam":
                    SetNodeParam(doc, Parse<SetNodeParamRequest>(message));
                    break;
                case "deleteNodes":
                    DeleteNodes(doc, Parse<DeleteNodesRequest>(message));
                    break;
                case "moveNode":
                    MoveNode(doc, Parse<MoveNodeRequest>(message));
                    break;
                case "aiTool":
                    RunAiTool(doc, Parse<AiToolRequest>(message));
                    break;
                case "aiSaveRun":
                    SaveAiRun(Parse<AiSaveRunRequest>(message));
                    break;
                default:
                    RhinoApp.WriteLine($"Freppuccino UI: unknown message '{type}'");
                    break;
            }
        }
        catch (JsonException ex)
        {
            RhinoApp.WriteLine($"Freppuccino UI: bad '{type}' message: {ex.Message}");
        }
    }

    static T Parse<T>(JsonElement message) =>
        message.Deserialize<T>(UiProtocol.Json) ?? throw new JsonException("empty payload");

    internal static void Select(RhinoDoc doc, SelectRequest request)
    {
        var tree = FrepTrees.Get(doc);
        var nodeIds = Nodes(tree, request.Ids).ToList();

        // While FUnion/FIntersect/FDifference wait for operands, a node click feeds the command (like typing "N3").
        if (FrepNodeCommand.Picking)
        {
            foreach (var n in nodeIds) RhinoApp.SendKeystrokes(FrepTree.NodeName(n), true);
            return;
        }

        if (request.Mode == "replace")
        {
            doc.Objects.UnselectAll();
            ClearNodes(doc); // OnDeselectAll doesn't fire if no Rhino object was selected
        }
        var nodes = NodesOf(doc);
        var rootSet = Selection.FrepSelection.Structure(doc).Roots.ToHashSet();
        foreach (var n in nodeIds)
        {
            bool select = request.Mode != "toggle" || !nodes.Contains(n);
            nodes.Remove(n);
            if (select) nodes.Add(n);
            // An inner node: select its primitives so Rhino draws the gumball on that geometry (each moves as a leaf).
            // A root: its blend carrier below, so the gumball transforms the whole blend.
            if (!rootSet.Contains(FrepOperand.Node(n))) SelectLeaves(doc, tree, n, select);
        }
        foreach (var key in request.Ids)
        {
            if (!Guid.TryParse(key, out var id) || doc.Objects.FindId(id) is not { } obj) continue;
            // IsSelected: 0 = not selected, 1 = selected, 2 = selected persistently
            bool select = request.Mode != "toggle" || obj.IsSelected(false) == 0;
            obj.Select(select);
        }
        // A root row selects that blend in the scene too: its carrier, which carries the gumball.
        foreach (var n in nodeIds.ToList())
        {
            if (!rootSet.Contains(FrepOperand.Node(n)) || Selection.FrepCarriers.Ensure(doc, n) is not { } carrier) continue;
            carrier.Select(NodesOf(doc).Contains(n), true);
        }
        ShowGumball();
        Selection.FrepSelection.RefreshGumball(); // a selection made from code shows no gumball until refreshed
        NodeSelectionChanged?.Invoke();
        doc.Views.Redraw();
    }

    static void SelectLeaves(RhinoDoc doc, FrepTree tree, int nodeId, bool on)
    {
        if (tree.Find(nodeId) is not { } node) return;
        var leaves = new HashSet<Guid>();
        CollectObjects(tree, node, leaves);
        if (!on)
        {
            var keep = new HashSet<Guid>();
            foreach (var other in NodesOf(doc))
                if (tree.Find(other) is { } n) CollectObjects(tree, n, keep);
            foreach (var id in leaves)
                if (!keep.Contains(id)) doc.Objects.FindId(id)?.Select(false);
            return;
        }
        foreach (var id in leaves)
            doc.Objects.FindId(id)?.Select(true);
    }

    /// Panel selection is a pre-pick; the gumball only appears for pre-picked objects when it is enabled.
    static void ShowGumball()
    {
        Rhino.ApplicationSettings.ModelAidSettings.AutoGumballEnabled = true;
#if RHINO9
        Rhino.ApplicationSettings.GumballSettings.EnableGumball = true;
#endif
    }

    static void SetVisible(RhinoDoc doc, SetVisibleRequest request)
    {
        var tree = FrepTrees.Get(doc);
        if (TryNode(tree, request.Id, out var n))
        {
            if (tree.Find(n)!.Hidden == !request.Visible) return;
            FrepTrees.Set(doc, tree.WithHidden(n, !request.Visible), $"{(request.Visible ? "Show" : "Hide")} {FrepTree.NodeName(n)}");
            return;
        }
        if (!Guid.TryParse(request.Id, out var id)) return;
        if (request.Visible) doc.Objects.Show(id, true);
        else doc.Objects.Hide(id, true);
        doc.Views.Redraw();
    }

    // Any command (incl. Undo) ends an open size drag or color gesture whose final message never came,
    // like FrepTrees does for nodes.
    void OnBeginCommand(object? sender, CommandEventArgs e)
    {
        CommitColor();
        EndParamGesture(e.Document);
    }

    void SetParam(RhinoDoc doc, SetParamRequest request)
    {
        if (!Guid.TryParse(request.Id, out var id) || doc.Objects.FindId(id) is not { IsDeleted: false } obj) return;
        if (SdfUserText.ReadType(obj.Attributes) is not { } type) return;

        bool sameGesture = paramEdit != null && paramEdit.Id == id && paramEdit.Name == request.Name;
        if (paramEdit != null && !sameGesture) EndParamGesture(doc);

        bool edited = PrimitiveProxy.TryEdit(type, obj.Attributes, request.Name, request.Value, doc.ModelAbsoluteTolerance, out var attrs, out var brep, out var reason);
        if (!edited || attrs == null || brep == null)
        {
            if (!request.Preview && reason != null) web.Send(new NoticeMessage("warning", reason));
            if (!request.Preview) EndParamGesture(doc);
            brep?.Dispose();
            return;
        }

        if (request.Preview)
        {
            if (paramEdit == null)
            {
                if (obj.Geometry is not Brep geometry)
                {
                    brep.Dispose();
                    return;
                }
                // Missing key (FFunction param at its default): NaN, so release always records the step.
                var original = SdfUserText.TryReadFloat(obj.Attributes, request.Name, out var v) ? v : double.NaN;
                paramEdit = new ParamEdit(id, request.Name, original, geometry.DuplicateBrep(), obj.Attributes.Duplicate());
            }
            // Live ticks stay out of the undo stack; release records the whole drag.
            ApplyParam(doc, obj, attrs, brep, undo: false);
            return;
        }

        if (paramEdit != null) ApplyParam(doc, obj, attrs, brep, undo: false);
        else RecordParam(doc, obj, attrs, brep);
        EndParamGesture(doc);
    }

    /// A typed value (no drag) is one ordinary undo record.
    static void RecordParam(RhinoDoc doc, RhinoObject obj, ObjectAttributes attrs, Brep brep)
    {
        uint record = doc.UndoRecordingIsActive ? 0 : doc.BeginUndoRecord("Edit F-rep parameter");
        ApplyParam(doc, obj, attrs, brep, undo: true);
        if (record != 0) doc.EndUndoRecord(record);
    }

    /// Attributes first: Replace keeps them, so the SDF values and the proxy land together.
    /// <paramref name="undo"/> false for live ticks, which must not push an undo step each move.
    static void ApplyParam(RhinoDoc doc, RhinoObject obj, ObjectAttributes attrs, Brep brep, bool undo)
    {
        bool enabled = doc.UndoRecordingEnabled;
        if (!undo) doc.UndoRecordingEnabled = false;
        try
        {
            bool selected = obj.IsSelected(false) != 0;
            bool ok = doc.Objects.ModifyAttributes(obj, attrs, true) && doc.Objects.Replace(obj.Id, brep);
            if (!ok) brep.Dispose();
            else if (selected) doc.Objects.Select(obj.Id);
        }
        finally
        {
            if (!undo) doc.UndoRecordingEnabled = enabled;
        }
        doc.Views.Redraw();
    }

    /// Closes the open size drag. No undo step if the value ended where it started.
    void EndParamGesture(RhinoDoc doc)
    {
        if (paramEdit == null) return;
        var edit = paramEdit;
        paramEdit = null;
        if (doc.Objects.FindId(edit.Id) is not { IsDeleted: false } obj)
        {
            edit.Brep.Dispose();
            return;
        }
        if (!SdfUserText.TryReadFloat(obj.Attributes, edit.Name, out var now) || Math.Abs(now - edit.Value) < 1e-9)
        {
            ApplyParam(doc, obj, edit.Attributes, edit.Brep, undo: false);
            return;
        }
        uint record = doc.UndoRecordingIsActive ? 0 : doc.BeginUndoRecord("Edit F-rep parameter");
        doc.AddCustomUndoEvent("Edit F-rep parameter", OnParamUndo, edit);
        if (record != 0) doc.EndUndoRecord(record);
    }

    static void OnParamUndo(object? sender, CustomUndoEventArgs e)
    {
        if (e.Tag is not ParamEdit prior) return;
        var doc = e.Document;
        if (doc.Objects.FindId(prior.Id) is not { IsDeleted: false, Geometry: Brep current } obj) return;
        var redo = new ParamEdit(prior.Id, prior.Name, prior.Value, current.DuplicateBrep(), obj.Attributes.Duplicate());
        doc.AddCustomUndoEvent(e.ActionDescription, OnParamUndo, redo);
        ApplyParam(doc, obj, prior.Attributes, prior.Brep, undo: false);
    }

    sealed class ParamEdit(Guid id, string name, double value, Brep brep, ObjectAttributes attributes)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public double Value { get; } = value;
        public Brep Brep { get; } = brep;
        public ObjectAttributes Attributes { get; } = attributes;
    }

    void ApplyOperation(RhinoDoc doc, ApplyOperationRequest request)
    {
        if (ParseKind(request.Op) is not { } op) throw new JsonException($"unknown op '{request.Op}'");
        var tree0 = FrepTrees.Get(doc);
        var operands = request.Operands.Select(k => ParseOperand(tree0, k)).OfType<FrepOperand>().Distinct().ToList();
        bool topLevel = operands.Count >= 2 && !operands.Any(tree0.IsReferenced);
        if (!topLevel || (op == FrepOp.Difference && operands.Count != 2))
        {
            web.Send(new NoticeMessage("warning", op == FrepOp.Difference
                ? "Subtract takes exactly two top-level nodes."
                : "Select two or more top-level nodes."));
            return;
        }
        var (tree, node) = tree0.Add(op, Math.Max(0, request.K), operands);
        FrepTrees.Set(doc, tree, $"F-rep {request.Op}");
        // Select the new node, like the page expects after "Apply".
        doc.Objects.UnselectAll();
        NodesOf(doc).Add(node.Id);
        NodeSelectionChanged?.Invoke();
        doc.Views.Redraw();
    }

    static void BlendRepeat(RhinoDoc doc, BlendRepeatRequest request)
    {
        var tree = FrepTrees.Get(doc);
        if (!TryNode(tree, request.Id, out var id) || tree.Find(id) is not { Op: FrepOp.Repeat }) return;
        var (next, blend) = tree.BlendOver(id);
        FrepTrees.Set(doc, next, "Blend repeat");
        doc.Objects.UnselectAll();
        SelectNode(doc, blend.Id);
        doc.Views.Redraw();
    }

    static void OffsetNode(RhinoDoc doc, OffsetNodeRequest request)
    {
        var tree = FrepTrees.Get(doc);
        if (!TryNode(tree, request.Id, out var id) || tree.Find(id) is not { } node) return;
        if (node.Op is not (FrepOp.Union or FrepOp.Intersect or FrepOp.Difference)) return;
        var (next, offset) = tree.WrapOver(FrepOp.Offset, 0, id);
        FrepTrees.Set(doc, next, "Offset");
        doc.Objects.UnselectAll();
        SelectNode(doc, offset.Id);
        doc.Views.Redraw();
    }

    static void SetOperation(RhinoDoc doc, SetOperationRequest request)
    {
        var tree = FrepTrees.Get(doc);
        if (!TryNode(tree, request.Id, out var id) || tree.Find(id) is not { } node) return;
        if (node.Op == FrepOp.Offset)
        {
            if (request.Op != OperationKind.Offset || !double.IsFinite(request.K)) return;
            if (node.K != request.K) FrepTrees.Preview(doc, tree.WithK(id, request.K), $"Edit {node.Name}");
            if (!request.Preview) FrepTrees.Commit(doc);
            return;
        }
        var k = Math.Max(0, request.K);
        if (node.Op == FrepOp.Blend)
        {
            // The slider only changes this node's fillet. It is not a union/subtract/intersect.
            if (request.Op != OperationKind.Blend) return;
            if (node.K != k) FrepTrees.Preview(doc, tree.WithK(id, k), $"Edit {node.Name}");
            if (!request.Preview) FrepTrees.Commit(doc);
            return;
        }
        if (ParseKind(request.Op) is not { } op) throw new JsonException($"unknown op '{request.Op}'");
        if (node.Op.IsWarp()) return;
        if (op == FrepOp.Difference && node.Op != FrepOp.Difference && node.Operands.Length != 2) return;
        // Previews apply live; the final message records the whole gesture as one undo step.
        if (node.Op != op || node.K != k) FrepTrees.Preview(doc, tree.WithOp(id, op, k), $"Edit {node.Name}");
        if (!request.Preview) FrepTrees.Commit(doc);
    }

    static void SetNodeParam(RhinoDoc doc, SetNodeParamRequest request)
    {
        var tree = FrepTrees.Get(doc);
        if (!TryNode(tree, request.Id, out var id) || tree.Find(id) is not { } node) return;
        int i = node.Op.ParamNames().ToList().IndexOf(request.Name);
        if (i < 0 || !double.IsFinite(request.Value)) throw new JsonException($"bad param '{request.Name}' for {node.Name}");
        if (node.Param(i) != request.Value) FrepTrees.Preview(doc, tree.WithParam(id, i, request.Value), $"Edit {node.Name}");
        if (!request.Preview) FrepTrees.Commit(doc);
    }

    /// Open color-picker gesture: the object and its SDF.Color before the gesture (null = it had none).
    (uint Doc, Guid Id, string? Before)? colorGesture;

    /// SDF.Color edit. Previews write the attributes without undo; the final one puts the original value back
    /// (silently) and then writes the new one in an undo record, so the whole drag is one undo step.
    void SetColor(RhinoDoc doc, SetColorRequest request)
    {
        if (!Guid.TryParse(request.Id, out var id) || doc.Objects.FindId(id) is not { IsDeleted: false } obj ||
            SdfUserText.ReadType(obj.Attributes) == null)
            return;
        if (!SdfUserText.TryParseColor(request.Color, out var color)) throw new JsonException($"bad color '{request.Color}'");

        // A different object starts a new gesture.
        if (colorGesture is { } g && (g.Doc != doc.RuntimeSerialNumber || g.Id != id)) CommitColor();

        if (request.Preview)
        {
            colorGesture ??= (doc.RuntimeSerialNumber, id, obj.Attributes.GetUserString(SdfUserText.ColorKey));
            WriteColor(doc, obj, SdfUserText.FormatColor(color), undo: false);
            return;
        }

        string? before = colorGesture is { } open ? open.Before : obj.Attributes.GetUserString(SdfUserText.ColorKey);
        colorGesture = null;
        var after = SdfUserText.FormatColor(color);
        if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
        {
            WriteColor(doc, obj, after, undo: false); // drag that ended where it started: just undo the previews
            return;
        }
        if (obj.Attributes.GetUserString(SdfUserText.ColorKey) != before)
            WriteColor(doc, doc.Objects.FindId(id), before, undo: false);
        WriteColor(doc, doc.Objects.FindId(id), after, undo: true);
    }

    /// Ends an open color gesture (its final message never came) as one undo step at the last previewed color.
    void CommitColor()
    {
        if (colorGesture is not { } g) return;
        colorGesture = null;
        if (RhinoDoc.FromRuntimeSerialNumber(g.Doc) is not { } doc || doc.Objects.FindId(g.Id) is not { IsDeleted: false } obj) return;
        var now = obj.Attributes.GetUserString(SdfUserText.ColorKey);
        if (now == g.Before) return;
        WriteColor(doc, obj, g.Before, undo: false);
        WriteColor(doc, doc.Objects.FindId(g.Id), now, undo: true);
    }

    /// Sets (or, for null, removes) SDF.Color. undo: wrap in its own undo record.
    static void WriteColor(RhinoDoc doc, RhinoObject? obj, string? value, bool undo)
    {
        if (obj == null) return;
        var attributes = obj.Attributes.Duplicate();
        if (value == null) attributes.DeleteUserString(SdfUserText.ColorKey);
        else attributes.SetUserString(SdfUserText.ColorKey, value);
        uint record = undo && !doc.UndoRecordingIsActive ? doc.BeginUndoRecord("F-rep color") : 0;
        doc.Objects.ModifyAttributes(obj, attributes, true);
        if (record != 0) doc.EndUndoRecord(record);
        doc.Views.Redraw();
    }

    void MoveNode(RhinoDoc doc, MoveNodeRequest request)
    {
        var tree = FrepTrees.Get(doc);
        if (ParseOperand(tree, request.Id) is not { } item) return;
        int? from = request.From is { } f ? TryNode(tree, f, out var fid) ? fid : throw new JsonException($"unknown node '{f}'") : null;
        int? to = request.To is { } t ? TryNode(tree, t, out var tid) ? tid : throw new JsonException($"unknown node '{t}'") : null;
        if (from == null && to == null) return;
        if (to is { } target && tree.Find(target)!.Op is not (FrepOp.Union or FrepOp.Intersect or FrepOp.Difference))
        {
            web.Send(new NoticeMessage("warning", "Drop onto a union, subtract, or intersect."));
            return;
        }
        FrepTree next;
        try
        {
            next = tree.Move(item, from, to, request.Index);
        }
        catch (ArgumentException ex)
        {
            web.Send(new NoticeMessage("warning", ex.Message));
            return;
        }
        if (!ReferenceEquals(next, tree)) FrepTrees.Set(doc, next, "Move in tree");
        doc.Views.Redraw();
    }

    /// Deletes the nodes and primitives in the request, then folds parents that no longer have enough children.
    void DeleteNodes(RhinoDoc doc, DeleteNodesRequest request)
    {
        var tree = FrepTrees.Get(doc);
        var dropNodes = new HashSet<int>();
        var dropObjects = new HashSet<Guid>();
        foreach (var key in request.Ids)
        {
            if (TryNode(tree, key, out var id)) dropNodes.Add(id);
            else if (Guid.TryParse(key, out var g)) dropObjects.Add(g);
        }
        if (dropNodes.Count == 0 && dropObjects.Count == 0) return;

        if (request.Deep)
            foreach (var id in dropNodes.ToArray())
                if (tree.Find(id) is { } node)
                    CollectSubtree(tree, node, dropNodes, dropObjects);

        // Hold pruning so the delete event doesn't record a second undo for ids we already removed.
        pruning++;
        uint record = doc.UndoRecordingIsActive ? 0 : doc.BeginUndoRecord("Delete");
        try
        {
            var next = tree.Excise(dropNodes, dropObjects);
            if (!ReferenceEquals(next, tree)) FrepTrees.Set(doc, next, "Delete");
            foreach (var id in dropObjects)
                if (doc.Objects.FindId(id) is { IsDeleted: false })
                    doc.Objects.Delete(id, true);
        }
        finally
        {
            if (record != 0) doc.EndUndoRecord(record);
            pruning--;
        }
        // Delete(quiet: true) does not repaint, and an earlier redraw still included these objects.
        doc.Views.Redraw();
    }

    /// Every operation under <paramref name="node"/>, and every primitive those operations use.
    /// The node itself is already in <paramref name="nodes"/>.
    static void CollectSubtree(FrepTree tree, FrepNode node, HashSet<int> nodes, HashSet<Guid> objects)
    {
        foreach (var o in node.Operands)
        {
            if (!o.IsNode) objects.Add(o.ObjectId);
            else if (tree.Find(o.NodeId) is { } child && nodes.Add(child.Id))
                CollectSubtree(tree, child, nodes, objects);
        }
    }

    /// Viewport delete of a primitive that is still an operand.
    void PruneObjects(RhinoDoc doc, IEnumerable<Guid> ids)
    {
        if (pruning > 0) return;
        var drop = ids.ToHashSet();
        if (drop.Count == 0) return;
        var tree = FrepTrees.Get(doc);
        if (!tree.Nodes.Any(n => n.Operands.Any(o => !o.IsNode && drop.Contains(o.ObjectId)))) return;
        pruning++;
        try
        {
            var next = tree.Excise(new HashSet<int>(), drop);
            if (!ReferenceEquals(next, tree)) FrepTrees.Set(doc, next, "Delete");
        }
        finally { pruning--; }
    }

    /// Operands whose object or node is already gone. Keeps a "(deleted)" row from sticking around.
    void PruneDangling(RhinoDoc doc)
    {
        if (pruning > 0) return;
        var tree = FrepTrees.Get(doc);
        var nodes = new HashSet<int>();
        var objects = new HashSet<Guid>();
        foreach (var n in tree.Nodes)
            foreach (var o in n.Operands)
            {
                if (o.IsNode)
                {
                    if (tree.Find(o.NodeId) == null) nodes.Add(o.NodeId);
                }
                else if (doc.Objects.FindId(o.ObjectId) is not { IsDeleted: false })
                    objects.Add(o.ObjectId);
            }
        if (nodes.Count == 0 && objects.Count == 0) return;
        pruning++;
        try
        {
            var next = tree.Excise(nodes, objects);
            if (!ReferenceEquals(next, tree)) FrepTrees.Set(doc, next, "Delete");
        }
        finally { pruning--; }
    }

    void RunCommand(RunCommandRequest request)
    {
        if (!AllowedCommands.Contains(request.Command))
        {
            web.Send(new NoticeMessage("error", $"Command '{request.Command}' is not allowed from the panel."));
            return;
        }
        // Fails (returns false) if another command is already running.
        if (!RhinoApp.RunScript("_" + request.Command, false))
            web.Send(new NoticeMessage("warning", $"Couldn't start {request.Command}; finish the current command first."));
    }

    void ShowFrepMode(RhinoDoc doc)
    {
        var mode = DisplayModeDescription.GetDisplayMode(FreppuccinoDisplayMode.Id);
        if (mode == null || doc.Views.ActiveView is not { } view)
        {
            web.Send(new NoticeMessage("error", "The Freppuccino display mode is missing."));
            return;
        }
        view.ActiveViewport.DisplayMode = mode;
        view.Redraw();
    }

    // ─────────────── AI agent (src/ui/src/ai, docs/ai.md) ───────────────

    void RunAiTool(RhinoDoc doc, AiToolRequest request)
    {
        AiToolResultMessage reply;
        AiTools.UndoPrefix = request.Source == "script" ? "Script: " : "AI: ";
        try
        {
            reply = new(request.CallId, true, AiTools.Run(doc, request.Tool, AiTools.ParseArgs(request.Args)));
        }
        catch (AiToolException ex)
        {
            reply = new(request.CallId, false, Error: ex.Message);
        }
        catch (Exception ex)
        {
            // Wrong argument types (a number where a string goes, ...) land here too; the model can fix those.
            RhinoApp.WriteLine($"Freppuccino AI: {request.Tool} failed: {ex}");
            reply = new(request.CallId, false, Error: $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            AiTools.UndoPrefix = "AI: ";
        }
        web.Send(reply);
    }

    static void SaveAiRun(AiSaveRunRequest request)
    {
        try
        {
            var transcript = JsonNode.Parse(request.Transcript.GetRawText())!;
            AiRunLog.Save(request.Title, transcript, [.. (request.Images ?? []).Select(i => (i.Name, i.DataUrl))]);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"Freppuccino AI: couldn't save run: {ex.Message}");
        }
    }

    void NotImplemented(string what)
    {
        RhinoApp.WriteLine($"Freppuccino UI: {what} is not implemented yet.");
        web.Send(new NoticeMessage("info", $"{what}: not implemented yet."));
    }
}

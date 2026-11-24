using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Freppuccino.Ui;

/// <summary>
/// JSON messages between the side panel (src/ui, React) and Rhino.
/// Mirror of src/ui/src/bridge/protocol.ts — change both together. Every message has a "type".
/// Docs: docs/side-panel.md.
/// </summary>
public static class UiProtocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// ───────────────────────────── Scene tree ─────────────────────────────

public static class SceneNodeKind
{
    /// A Rhino object carrying SDF.* user text (FBox, FSphere, ...). Id = Rhino object id ("D" format).
    public const string Primitive = "primitive";
    /// A CSG node (Csg/FrepTree). Id = node name, e.g. "N3".
    public const string Operation = "operation";
}

/// <summary>Combine operations. Each takes a blend k (model units, 0 = sharp).</summary>
public static class OperationKind
{
    public const string Union = "union";
    /// children[0] minus children[1..]
    public const string Subtract = "subtract";
    public const string Intersect = "intersect";
    /// Domain warps of one child (children[0]); Ref = line/box object id, Params = FrepOps.ParamNames → value.
    public const string Twist = "twist";
    public const string Bend = "bend";
    public const string Repeat = "repeat";
    /// One child, a Repeat. K fillets every copy (0 = sharp). Not a combine op.
    public const string Blend = "blend";
    /// One child, a union, intersection, or difference. K shifts the surface (positive grows). Not a combine op.
    public const string Offset = "offset";
}

/// <summary>
/// One row in the browser tree. Primitives are leaves; operations nest their operands as children.
/// The CSG graph is a DAG, so a shared operand appears under each parent.
/// </summary>
public sealed record SceneNode(
    string Id,
    string Kind,
    string? Name,
    bool Visible,
    bool Locked,
    string? SdfType = null,
    IReadOnlyDictionary<string, double>? Params = null,
    string? Op = null,
    double? K = null,
    IReadOnlyList<SceneNode>? Children = null,
    bool? Missing = null,
    string? Ref = null,
    bool? RefMissing = null,
    // Primitive: SDF.Color "#RRGGBB" (white if the object has none).
    string? Color = null,
    /// <summary>Primitive carries SDF.Linear.*: a scale or shear the parameters cannot express.</summary>
    bool? Custom = null,
    /// <summary>sdUser (FFunction) only: code, compile result, params. Params then holds just b.x/b.y/b.z.</summary>
    UserFnInfo? UserFn = null);

/// <summary>FFunction inspector data (SDF.Code / SDF.L / SDF.u.*), compiled with UserFnCompiler.</summary>
/// <param name="Hash">Program hash when <paramref name="Ok"/>.</param>
/// <param name="Params">Declared params in order; Value = SDF.u.&lt;name&gt; or the default. Empty when the code doesn't compile.</param>
/// <param name="Lipschitz">SDF.L (field = f / L); 1 when missing or invalid.</param>
/// <param name="Error">Non-code reason the object isn't drawn (invalid SDF.L), or null.</param>
public sealed record UserFnInfo(
    string Code,
    bool Ok,
    string? Hash,
    IReadOnlyList<UserFnDiagnosticInfo> Diagnostics,
    IReadOnlyList<UserFnParamInfo> Params,
    double Lipschitz,
    string? Error = null);

/// 1-based line / column.
public sealed record UserFnDiagnosticInfo(int Line, int Column, string Message);

public sealed record UserFnParamInfo(string Name, double Value, double Default);

public sealed record UiSettings(string Units);

// ───────────────────────────── Host → UI ─────────────────────────────

public abstract record HostMessage
{
    public abstract string Type { get; }
}

/// Full snapshot; sent in reply to "ready" and when the active document changes.
public sealed record StateMessage(IReadOnlyList<SceneNode> Scene, IReadOnlyList<string> Selection, UiSettings Settings, string Theme, bool FrepMode) : HostMessage
{
    public override string Type => "state";
}

public sealed record ViewMessage(bool FrepMode) : HostMessage
{
    public override string Type => "view";
}

public sealed record SceneMessage(IReadOnlyList<SceneNode> Scene) : HostMessage
{
    public override string Type => "scene";
}

/// Ids of the selected primitives (Rhino selection) and operation nodes (panel selection).
public sealed record SelectionMessage(IReadOnlyList<string> Ids) : HostMessage
{
    public override string Type => "selection";
}

/// Transient feedback shown in the panel ("info" | "warning" | "error").
public sealed record NoticeMessage(string Level, string Text) : HostMessage
{
    public override string Type => "notice";
}

/// Reply to an AiToolRequest with the same CallId: Result on success, Error (for the model) on failure.
public sealed record AiToolResultMessage(string CallId, bool Ok, JsonNode? Result = null, string? Error = null) : HostMessage
{
    public override string Type => "aiToolResult";
}

// ───────────────────────────── UI → Host ─────────────────────────────
// Incoming messages are dispatched on "type" in UiController.Handle.

/// mode: "replace" | "add" | "toggle"
public sealed record SelectRequest(string[] Ids, string Mode = "replace");

/// Primitive: Rhino show/hide. Operation node: FrepNode.Hidden (undoable).
public sealed record SetVisibleRequest(string Id, bool Visible);

/// Only commands in UiController.AllowedCommands run.
public sealed record RunCommandRequest(string Command);

/// Edit one SDF parameter of a primitive and rebuild its proxy, e.g. { id, name: "s", value: 2 }.
/// sdUser also takes "L" (Lipschitz, &gt; 0) and "u.&lt;param&gt;" (a declared param, any finite value).
/// Preview = live drag tick (no undo); the next non-preview edit ends the gesture as one undo step.
public sealed record SetParamRequest(string Id, string Name, double Value, bool Preview = false);

/// Set a primitive's SDF.Color ("#RRGGBB"). Preview = live picker tick (no undo); the next non-preview one ends the gesture as one undo step.
public sealed record SetColorRequest(string Id, string Color, bool Preview = false);

/// New CSG node over top-level operands. Union and intersect take any number ≥ 2; subtract takes exactly two (operands[0] is the target).
public sealed record ApplyOperationRequest(string[] Operands, string Op, double K);

/// Insert a blend node (k = 0) directly above a Repeat. Its slider fillets the copies.
public sealed record BlendRepeatRequest(string Id);

/// Insert an offset node (k = 0) directly above a union, intersection, or difference.
public sealed record OffsetNodeRequest(string Id);

/// Edit an existing operation node. Preview = live drag tick (no undo); the next non-preview edit ends the gesture as one undo step.
public sealed record SetOperationRequest(string Id, string Op, double K, bool Preview = false);

/// Edit one param of a warp node (FrepOps.ParamNames). Preview semantics as SetOperationRequest.
public sealed record SetNodeParamRequest(string Id, string Name, double Value, bool Preview = false);

/// Delete operation nodes or primitives. Without <see cref="Deep"/>, a node's children take its place.
/// With <see cref="Deep"/>, the node and every node and primitive under it are removed.
public sealed record DeleteNodesRequest(string[] Ids, bool Deep = false);

public sealed record MoveNodeRequest(string Id, string? From = null, string? To = null, int? Index = null);

/// AI agent tool call (Ai/AiTools.cs); answered with an AiToolResultMessage carrying the same CallId.
public sealed record AiToolRequest(string CallId, string Tool, JsonElement? Args = null, string? Source = null);

/// Saves a finished AI run for review (Ai/AiRunLog.cs). Images: { name: "01-perspective.png", dataUrl }.
public sealed record AiSaveRunRequest(string Title, JsonElement Transcript, AiImage[]? Images = null);

public sealed record AiImage(string Name, string DataUrl);

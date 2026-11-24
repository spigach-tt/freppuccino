import { ChevronRight, Eye, EyeOff, Lock, Trash2, Ungroup } from 'lucide-react'
import { useEffect, useState, type DragEvent, type MouseEvent } from 'react'
import { cn } from '@/lib/utils'
import { NodeIcon } from '@/components/brand-icon'
import { Input } from '@/components/ui/input'
import { send } from '@/bridge/host'
import { isDeform } from '@/lib/scene'
import type { Id, OperationKind, SceneNode, SelectMode } from '@/bridge/protocol'

/**
 * Browser tree (Fusion 360 browser / CATIA spec tree). Primitives are leaves; operation nodes
 * nest their operands. Click selects, Ctrl/Cmd+click toggles, Shift+click adds.
 * Shared operands (DAG) show under each parent and expand/collapse together.
 */
export function SceneTree(props: {
  nodes: SceneNode[]
  labels: Map<string, string>
  selection: Set<Id>
  onSelect: (ids: Id[], mode: SelectMode) => void
  onSetVisible: (id: Id, visible: boolean) => void
}) {
  // Collapsed rather than expanded, so new operation nodes start open.
  const [collapsed, setCollapsed] = useState<Set<Id>>(new Set())
  const toggle = (id: Id) =>
    setCollapsed((prev) => {
      const next = new Set(prev)
      if (!next.delete(id)) next.add(id)
      return next
    })

  const [drag, setDrag] = useState<Drag | null>(null)
  const [drop, setDrop] = useState<Drop | null>(null)
  const endDrag = () => {
    setDrag(null)
    setDrop(null)
  }
  const dnd: Dnd = { drag, drop, setDrag, setDrop, endDrag }

  const [menu, setMenu] = useState<{ x: number; y: number; id: Id; operation: boolean } | null>(null)
  useEffect(() => {
    if (!menu) return
    const close = () => setMenu(null)
    window.addEventListener('pointerdown', close)
    window.addEventListener('keydown', close)
    return () => {
      window.removeEventListener('pointerdown', close)
      window.removeEventListener('keydown', close)
    }
  }, [menu])

  if (props.nodes.length === 0)
    return <p className="type-small px-4 py-6 text-center text-muted-foreground">No F-rep objects yet. Create one below.</p>

  return (
    <>
      <ul role="tree" aria-multiselectable className="py-1">
        {props.nodes.map((node, i) => (
          <TreeRow
            key={node.id}
            node={node}
            depth={0}
            {...props}
            path={node.id}
            parent={null}
            index={i}
            dnd={dnd}
            collapsed={collapsed}
            onToggle={toggle}
            onDeleteMenu={setMenu}
          />
        ))}
      </ul>
      {drag?.from && (
        <div
          onDragOver={(e) => {
            e.preventDefault()
            e.dataTransfer.dropEffect = 'move'
            if (drop?.zone !== 'root') setDrop({ path: '', zone: 'root' })
          }}
          onDragLeave={() => setDrop(null)}
          onDrop={(e) => {
            e.preventDefault()
            send({ type: 'moveNode', id: drag.id, from: drag.from, to: null })
            endDrag()
          }}
          className={cn(
            'type-small mx-2 mb-2 grid h-9 place-items-center rounded-sm border border-dashed text-muted-foreground',
            drop?.zone === 'root' && 'border-primary bg-accent/50 text-foreground',
          )}
        >
          Move to top level
        </div>
      )}
      {menu && (
        <div
          role="menu"
          className="fixed z-50 min-w-36 rounded-md border border-input bg-popover p-1 text-popover-foreground shadow-float"
          style={{ left: menu.x, top: menu.y }}
          onPointerDown={(e) => e.stopPropagation()}
        >
          {menu.operation && (
            <button
              type="button"
              role="menuitem"
              className="type-small flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left hover:bg-accent"
              onClick={() => {
                send({ type: 'deleteNodes', ids: [menu.id] })
                setMenu(null)
              }}
            >
              <Ungroup className="size-3.5" /> Unwrap
            </button>
          )}
          <button
            type="button"
            role="menuitem"
            className="type-small flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left hover:bg-accent"
            onClick={() => {
              send({ type: 'deleteNodes', ids: [menu.id], deep: menu.operation })
              setMenu(null)
            }}
          >
            <Trash2 className="size-3.5" /> Delete
          </button>
        </div>
      )}
    </>
  )
}

type Drag = { id: Id; from: Id | null; index: number; inside: Set<Id> }
type Drop = { path: string; zone: 'before' | 'after' | 'into' | 'root' }
type Dnd = {
  drag: Drag | null
  drop: Drop | null
  setDrag: (d: Drag | null) => void
  setDrop: (d: Drop | null) => void
  endDrag: () => void
}

const isCombine = (n: SceneNode | null) => !n || n.op === 'union' || n.op === 'subtract' || n.op === 'intersect'

function subtreeIds(node: SceneNode, into = new Set<Id>()): Set<Id> {
  into.add(node.id)
  node.children?.forEach((c) => subtreeIds(c, into))
  return into
}

function TreeRow(props: {
  node: SceneNode
  depth: number
  path: string
  parent: SceneNode | null
  index: number
  dnd: Dnd
  labels: Map<string, string>
  selection: Set<Id>
  collapsed: Set<Id>
  onToggle: (id: Id) => void
  onSelect: (ids: Id[], mode: SelectMode) => void
  onSetVisible: (id: Id, visible: boolean) => void
  onDeleteMenu: (menu: { x: number; y: number; id: Id; operation: boolean }) => void
  /** subtract operands after the first are tools */
  role?: 'tool'
}) {
  const { node, depth, selection, collapsed, parent, index, dnd } = props
  const hasChildren = !!node.children?.length
  const open = hasChildren && !collapsed.has(node.id)
  const selected = selection.has(node.id)

  const canDelete = !node.missing

  const onClick = (e: MouseEvent) => {
    const mode: SelectMode = e.ctrlKey || e.metaKey ? 'toggle' : e.shiftKey ? 'add' : 'replace'
    props.onSelect([node.id], mode)
  }

  const zoneAt = (e: DragEvent<HTMLDivElement>, drag: Drag): Drop['zone'] | null => {
    const r = e.currentTarget.getBoundingClientRect()
    const y = (e.clientY - r.top) / r.height
    const canInto = node.kind === 'operation' && isCombine(node) && !node.missing && !drag.inside.has(node.id)
    const canBeside = isCombine(parent) && !(parent && drag.inside.has(parent.id)) && node.id !== drag.id
    if (canInto && (!canBeside || (y > 0.25 && y < 0.75))) return 'into'
    if (!canBeside) return null
    return y < 0.5 ? 'before' : 'after'
  }

  const onDrop = (zone: Drop['zone'], drag: Drag) => {
    if (zone === 'into') {
      if (drag.from !== node.id) send({ type: 'moveNode', id: drag.id, from: drag.from, to: node.id })
      return
    }
    const to = parent?.id ?? null
    if (to === null) {
      if (drag.from !== null) send({ type: 'moveNode', id: drag.id, from: drag.from, to: null })
      return
    }
    let at = zone === 'before' ? index : index + 1
    if (drag.from === to && drag.index < at) at--
    if (drag.from === to && drag.index === at) return
    send({ type: 'moveNode', id: drag.id, from: drag.from, to, index: at })
  }

  const dropZone = dnd.drop?.path === props.path ? dnd.drop.zone : null

  return (
    <li role="treeitem" aria-selected={selected} aria-expanded={hasChildren ? open : undefined}>
      <div
        draggable={!node.missing}
        onDragStart={(e) => {
          if ((e.target as HTMLElement).closest('input')) return e.preventDefault()
          e.stopPropagation()
          e.dataTransfer.effectAllowed = 'move'
          e.dataTransfer.setData('text/plain', node.id)
          dnd.setDrag({ id: node.id, from: parent?.id ?? null, index, inside: subtreeIds(node) })
        }}
        onDragEnd={dnd.endDrag}
        onDragOver={(e) => {
          const zone = dnd.drag && zoneAt(e, dnd.drag)
          if (!zone) {
            if (dropZone) dnd.setDrop(null)
            return
          }
          e.preventDefault()
          e.stopPropagation()
          e.dataTransfer.dropEffect = 'move'
          if (dropZone !== zone) dnd.setDrop({ path: props.path, zone })
        }}
        onDragLeave={(e) => {
          if (dropZone && !e.currentTarget.contains(e.relatedTarget as Node | null)) dnd.setDrop(null)
        }}
        onDrop={(e) => {
          e.preventDefault()
          e.stopPropagation()
          const zone = dnd.drag && zoneAt(e, dnd.drag)
          if (dnd.drag && zone) onDrop(zone, dnd.drag)
          dnd.endDrag()
        }}
        onClick={onClick}
        onContextMenu={(e) => {
          if (!canDelete || (e.target as HTMLElement).closest('input')) return
          e.preventDefault()
          props.onSelect([node.id], 'replace')
          props.onDeleteMenu({ x: e.clientX, y: e.clientY, id: node.id, operation: node.kind === 'operation' })
        }}
        className={cn(
          'group relative mx-2 flex h-7 cursor-default items-center gap-1.5 rounded-sm pr-1 select-none hover:bg-accent/50',
          selected && 'bg-accent font-bold hover:bg-accent',
          !node.visible && 'text-muted-foreground [&_[data-slot=brand-icon]]:opacity-40',
          node.missing && 'italic line-through',
          dnd.drag?.id === node.id && 'opacity-50',
          dropZone === 'into' && 'bg-accent/50 ring-2 ring-primary ring-inset',
        )}
        style={{ paddingLeft: 4 + depth * 16 }}
      >
        {(dropZone === 'before' || dropZone === 'after') && (
          <span
            aria-hidden
            className={cn('pointer-events-none absolute right-0 h-0.5 rounded-full bg-primary', dropZone === 'before' ? '-top-px' : '-bottom-px')}
            style={{ left: 4 + depth * 16 }}
          />
        )}
        <button
          type="button"
          aria-label={open ? 'Collapse' : 'Expand'}
          tabIndex={-1}
          onClick={(e) => (e.stopPropagation(), props.onToggle(node.id))}
          className={cn('grid size-4 place-items-center rounded-sm text-muted-foreground hover:bg-foreground/10', !hasChildren && 'invisible')}
        >
          <ChevronRight className={cn('size-3 transition-transform', open && 'rotate-90')} />
        </button>
        <NodeIcon node={node} className="size-5" />
        <span className={cn('min-w-0 flex-1 truncate', node.kind === 'operation' && 'font-extrabold')}>
          {props.role === 'tool' && <span className="text-muted-foreground">− </span>}
          {props.labels.get(node.id)}
        </span>
        <span className="ml-auto flex shrink-0 items-center gap-0.5">
          {node.kind === 'operation' && node.op && !isDeform(node.op) && node.op !== 'offset' && !node.missing && (
            <BlendField id={node.id} op={node.op} k={node.k ?? 0} />
          )}
          {node.locked && <Lock className="size-3.5 text-muted-foreground" />}
          {!node.missing && <button
            type="button"
            aria-label={node.visible ? 'Hide' : 'Show'}
            onClick={(e) => (e.stopPropagation(), props.onSetVisible(node.id, !node.visible))}
            className={cn(
              'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-foreground/10 focus-visible:ring-2 focus-visible:ring-ring',
              node.visible && 'opacity-0 group-hover:opacity-100 focus-visible:opacity-100',
            )}
          >
            {node.visible ? <Eye className="size-3.5" /> : <EyeOff className="size-3.5" />}
          </button>}
        </span>
      </div>
      {open && (
        <ul role="group">
          {node.children!.map((child, i) => (
            <TreeRow
              key={`${i}:${child.id}`}
              {...props}
              node={child}
              depth={depth + 1}
              path={`${props.path}/${i}:${child.id}`}
              parent={node}
              index={i}
              role={node.op === 'subtract' && i > 0 ? 'tool' : undefined}
            />
          ))}
        </ul>
      )}
    </li>
  )
}

/** Typed blend for an operation node. Commits on Enter or blur, same path as the inspector slider. */
function BlendField(props: { id: Id; op: OperationKind | 'blend'; k: number }) {
  const { id, op, k } = props
  const [text, setText] = useState(() => formatBlend(k))
  const [shown, setShown] = useState(k)
  if (k !== shown) {
    setShown(k)
    setText(formatBlend(k))
  }

  const commit = () => {
    const raw = text.trim()
    const v = Number(raw)
    if (raw === '' || !Number.isFinite(v) || v < 0 || v === k) {
      setText(formatBlend(k))
      return
    }
    setText(formatBlend(v))
    send({ type: 'setOperation', id, op, k: v })
  }

  return (
    <Input
      inputMode="decimal"
      aria-label="Blend"
      value={text}
      onChange={(e) => setText(e.target.value)}
      onBlur={commit}
      onMouseDown={(e) => e.stopPropagation()}
      onClick={(e) => e.stopPropagation()}
      onKeyDown={(e) => {
        if (e.key === 'Enter') e.currentTarget.blur()
      }}
      className="type-numeric h-5 w-14 px-1 text-right font-medium"
    />
  )
}

const formatBlend = (v: number) => String(Math.round(v * 1000) / 1000)

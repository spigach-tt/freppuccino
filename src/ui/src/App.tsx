import { Code2, Sparkles, X } from 'lucide-react'
import { useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent, type RefObject } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ScrollArea } from '@/components/ui/scroll-area'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { AiPanel } from '@/components/ai-panel'
import { BrandIcon } from '@/components/brand-icon'
import { Inspector } from '@/components/inspector'
import { ScriptPanel } from '@/components/script-panel'
import { SceneTree } from '@/components/scene-tree'
import { dismissNotice, send, useHost } from '@/bridge/host'
import { indexNodes, labelNodes } from '@/lib/scene'
import { cn } from '@/lib/utils'
import appIcon from '@/brand/app-icon.svg'
import wordmark from '@/brand/wordmark.svg?raw'

type Tab = 'scene' | 'script' | 'ai'

export default function App() {
  const [tab, setTab] = useState<Tab>('scene')
  const [editRequest, setEditRequest] = useState<{ id: string; seq: number }>()
  const host = useHost((s) => s.host)
  const synced = useHost((s) => s.synced)
  const scene = useHost((s) => s.scene)
  const selection = useHost((s) => s.selection)
  const theme = useHost((s) => s.theme)
  const frepMode = useHost((s) => s.frepMode)

  useEffect(() => {
    document.documentElement.classList.toggle('dark', theme === 'dark')
  }, [theme])

  // Esc in the panel deselects like Esc in the viewport (inputs keep Esc for themselves).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || (e.target as HTMLElement | null)?.closest('input, textarea')) return
      send({ type: 'select', ids: [], mode: 'replace' })
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const inspectorRef = useRef<HTMLDivElement>(null)
  const [inspectorHeight, setInspectorHeight] = useState<number | null>(() => {
    const saved = Number(localStorage.getItem(INSPECTOR_HEIGHT_KEY))
    return saved > 0 ? saved : null
  })
  useEffect(() => {
    if (inspectorHeight === null) localStorage.removeItem(INSPECTOR_HEIGHT_KEY)
    else localStorage.setItem(INSPECTOR_HEIGHT_KEY, String(Math.round(inspectorHeight)))
  }, [inspectorHeight])

  const labels = useMemo(() => labelNodes(scene), [scene])
  const byId = useMemo(() => indexNodes(scene), [scene])
  const roots = useMemo(() => new Set(scene.map((n) => n.id)), [scene])
  const selectedSet = useMemo(() => new Set(selection), [selection])
  const selected = useMemo(() => selection.map((id) => byId.get(id)).filter((n) => !!n), [selection, byId])

  return (
    <div className="relative flex h-dvh flex-col overflow-hidden">
      <header className="flex h-12 shrink-0 items-center gap-2 border-b px-4">
        <img src={appIcon} alt="" className="h-8 w-auto" />
        {/* Wordmark: ink = currentColor, "FRep" = --wordmark-accent (caramel / caramel-ink per theme) */}
        <span
          role="img"
          aria-label="FRepuccino"
          className="h-4 text-ink [&>svg]:h-full [&>svg]:w-auto"
          dangerouslySetInnerHTML={{ __html: wordmark }}
        />
        <span className="ml-auto flex items-center gap-1.5">
          {host === 'mock' && <Badge variant="new">Mock host</Badge>}
          {!synced && <span className="type-small text-muted-foreground">connecting…</span>}
          <ToggleGroup
            type="single"
            variant="outline"
            size="sm"
            spacing={0}
            value={tab}
            onValueChange={(v) => v && setTab(v as Tab)}
            aria-label="Panel"
          >
            <ToggleGroupItem value="scene" className="px-2">Scene</ToggleGroupItem>
            <ToggleGroupItem value="script" className="px-2">
              <Code2 /> Script
            </ToggleGroupItem>
            <ToggleGroupItem value="ai" className="px-2">
              <Sparkles /> AI
            </ToggleGroupItem>
          </ToggleGroup>
        </span>
      </header>

      {synced && !frepMode && (
        <div className="flex shrink-0 items-center gap-2 border-b bg-secondary px-4 py-2">
          <span className="type-small flex-1 text-secondary-foreground">This view isn't showing F-reps.</span>
          <Button size="sm" onClick={() => send({ type: 'showFrepMode' })}>
            <BrandIcon name="preview" /> F-rep display mode
          </Button>
        </div>
      )}

      {/* Both stay mounted so switching keeps scroll position and the chat draft. */}
      <div className={cn('flex min-h-0 flex-1 flex-col', tab !== 'scene' && 'hidden')}>
        <div className="flex h-9 shrink-0 items-center px-4 text-muted-foreground">
          <span className="type-label">Browser</span>
          <span className="type-numeric ml-auto">{byId.size}</span>
        </div>
        <ScrollArea className="min-h-24 flex-1">
          <SceneTree
            nodes={scene}
            labels={labels}
            selection={selectedSet}
            onSelect={(ids, mode) => send({ type: 'select', ids, mode })}
            onSetVisible={(id, visible) => send({ type: 'setVisible', id, visible })}
          />
        </ScrollArea>

        <Splitter height={inspectorHeight} setHeight={setInspectorHeight} pane={inspectorRef} />
        <div
          ref={inspectorRef}
          className={cn('shrink-0 overflow-y-auto bg-card', inspectorHeight === null && 'max-h-[65%]')}
          style={inspectorHeight === null ? undefined : { height: inspectorHeight, maxHeight: `calc(100% - ${RESERVED_ABOVE}px)` }}
        >
          <Inspector
            selected={selected}
            labels={labels}
            roots={roots}
            onEditCode={(id) => {
              setEditRequest((r) => ({ id, seq: (r?.seq ?? 0) + 1 }))
              setTab('script')
            }}
          />
        </div>
      </div>
      <div className={cn('flex min-h-0 flex-1 flex-col', tab !== 'script' && 'hidden')}>
        <ScriptPanel editRequest={editRequest} onOpenAi={() => setTab('ai')} />
      </div>
      <div className={cn('flex min-h-0 flex-1 flex-col', tab !== 'ai' && 'hidden')}>
        <AiPanel />
      </div>

      <NoticeToast />
    </div>
  )
}

const INSPECTOR_HEIGHT_KEY = 'freppuccino.inspectorHeight'
const MIN_PANE = 72
const RESERVED_ABOVE = 112

function Splitter(props: { height: number | null; setHeight: (h: number | null) => void; pane: RefObject<HTMLDivElement | null> }) {
  const start = useRef<{ y: number; h: number; max: number } | null>(null)

  const limits = () => {
    const pane = props.pane.current
    const column = pane?.parentElement
    if (!pane || !column) return null
    return { h: pane.offsetHeight, max: Math.max(MIN_PANE, column.clientHeight - RESERVED_ABOVE) }
  }

  const clamp = (h: number, max: number) => Math.min(Math.max(h, MIN_PANE), max)

  return (
    <div
      role="separator"
      aria-orientation="horizontal"
      aria-label="Resize browser and inspector"
      tabIndex={0}
      title="Drag to resize. Double-click to reset."
      onPointerDown={(e: PointerEvent<HTMLDivElement>) => {
        const l = limits()
        if (!l || e.button !== 0) return
        e.currentTarget.setPointerCapture(e.pointerId)
        start.current = { y: e.clientY, h: l.h, max: l.max }
      }}
      onPointerMove={(e) => {
        const s = start.current
        if (s) props.setHeight(clamp(s.h + s.y - e.clientY, s.max))
      }}
      onPointerUp={(e) => {
        start.current = null
        e.currentTarget.releasePointerCapture(e.pointerId)
      }}
      onDoubleClick={() => props.setHeight(null)}
      onKeyDown={(e: ReactKeyboardEvent<HTMLDivElement>) => {
        if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return
        const l = limits()
        if (!l) return
        e.preventDefault()
        props.setHeight(clamp(l.h + (e.key === 'ArrowUp' ? 16 : -16), l.max))
      }}
      className="group relative z-10 -my-1 h-2 shrink-0 cursor-row-resize touch-none outline-none"
    >
      <div className="pointer-events-none absolute inset-x-0 top-1/2 h-px -translate-y-1/2 bg-border transition-[height,background-color] group-hover:h-0.5 group-hover:bg-primary group-focus-visible:h-0.5 group-focus-visible:bg-ring group-active:h-0.5 group-active:bg-primary" />
    </div>
  )
}

function NoticeToast() {
  const notice = useHost((s) => s.notice)
  useEffect(() => {
    if (!notice) return
    const t = setTimeout(() => dismissNotice(notice.id), 4000)
    return () => clearTimeout(t)
  }, [notice])
  if (!notice) return null
  return (
    <div
      role="status"
      className={cn(
        'type-small absolute inset-x-3 bottom-3 flex items-center gap-2 rounded-full border-2 border-ink bg-popover py-1.5 pr-1.5 pl-4 text-popover-foreground shadow-float',
        notice.level === 'error' && 'border-destructive text-destructive',
      )}
    >
      <span className="flex-1">{notice.text}</span>
      <Button variant="ghost" size="icon-xs" className="rounded-full" aria-label="Dismiss" onClick={() => dismissNotice(notice.id)}>
        <X />
      </Button>
    </div>
  )
}
